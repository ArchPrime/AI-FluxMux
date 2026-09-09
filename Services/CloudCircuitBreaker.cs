using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public sealed record CloudBreakerBlock(
    string Reason,
    DateTimeOffset OpenUntilUtc,
    string Message,
    string Hint,
    int RetryAfterSeconds);

public static class CloudCircuitBreaker
{
    private static readonly object FileLock = new();

    public static TimeSpan QuotaOpenDuration { get; } = TimeSpan.FromHours(4);

    public static TimeSpan DefaultRateLimitOpenDuration { get; } = TimeSpan.FromSeconds(60);

    public static TimeSpan TimeoutOpenDuration { get; } = TimeSpan.FromSeconds(30);

    public static TimeSpan OverloadedOpenDuration { get; } = TimeSpan.FromSeconds(45);

    public static bool TryGetBlock(
        string path,
        string provider,
        string model,
        out CloudBreakerBlock block)
    {
        block = null!;
        var entry = ReadEntry(path, provider, model);
        if (entry is null || !IsEntryOpen(entry, DateTimeOffset.UtcNow))
        {
            return false;
        }

        var reason = FirstNonEmpty(entry["reason"]?.ToString(), "cloud_breaker");
        var openUntil = ParseUtc(entry["openUntilUtc"]?.ToString());
        var retryAfter = Math.Max(1, (int)Math.Ceiling((openUntil - DateTimeOffset.UtcNow).TotalSeconds));
        block = new CloudBreakerBlock(
            reason,
            openUntil,
            BuildBlockMessage(provider, model, reason, openUntil),
            BuildBlockHint(reason),
            retryAfter);
        return true;
    }

    public static CloudBreakerBlock? ReadOpenBlock(string path, string provider, string model)
        => TryGetBlock(path, provider, model, out var block) ? block : null;

    public static void RecordFailure(
        string path,
        string provider,
        string model,
        string errorType,
        int? retryAfterSeconds = null)
    {
        var duration = ResolveOpenDuration(errorType, retryAfterSeconds);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (FileLock)
        {
            var root = LoadRoot(path);
            var entries = EnsureEntries(root);
            var key = BuildKey(provider, model);
            var existing = entries[key] as JsonObject;
            var reason = MapErrorTypeToReason(errorType);
            var openUntil = now.Add(duration);
            if (existing is not null
                && IsEntryOpen(existing, now)
                && existing["reason"]?.ToString()?.Equals("quota_exhausted", StringComparison.OrdinalIgnoreCase) == true
                && reason.Equals("quota_exhausted", StringComparison.OrdinalIgnoreCase))
            {
                var existingUntil = ParseUtc(existing["openUntilUtc"]?.ToString());
                if (existingUntil > openUntil)
                {
                    openUntil = existingUntil;
                }
            }

            entries[key] = new JsonObject
            {
                ["provider"] = provider ?? string.Empty,
                ["model"] = model ?? string.Empty,
                ["reason"] = reason,
                ["lastErrorType"] = errorType ?? string.Empty,
                ["openedUtc"] = now.ToString("o", CultureInfo.InvariantCulture),
                ["openUntilUtc"] = openUntil.ToString("o", CultureInfo.InvariantCulture)
            };
            SaveRoot(path, root);
        }
    }

    public static void RecordSuccess(string path, string provider, string model)
    {
        lock (FileLock)
        {
            var root = LoadRoot(path);
            var entries = EnsureEntries(root);
            var key = BuildKey(provider, model);
            if (entries[key] is not JsonObject existing)
            {
                return;
            }

            var reason = existing["reason"]?.ToString() ?? string.Empty;
            if (reason.Equals("quota_exhausted", StringComparison.OrdinalIgnoreCase)
                && IsEntryOpen(existing, DateTimeOffset.UtcNow))
            {
                return;
            }

            entries.Remove(key);
            SaveRoot(path, root);
        }
    }

    public static void Clear(string path, string provider, string model)
    {
        lock (FileLock)
        {
            var root = LoadRoot(path);
            var entries = EnsureEntries(root);
            var key = BuildKey(provider, model);
            if (!entries.ContainsKey(key))
            {
                return;
            }

            entries.Remove(key);
            SaveRoot(path, root);
        }
    }

    internal static string BuildKey(string provider, string model)
        => $"{NormalizeToken(provider)}::{NormalizeToken(model)}";

    internal static TimeSpan ResolveOpenDuration(string errorType, int? retryAfterSeconds)
    {
        return errorType switch
        {
            "quota_exhausted" => QuotaOpenDuration,
            "rate_limited" => TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds ?? (int)DefaultRateLimitOpenDuration.TotalSeconds, 15, 300)),
            "request_timeout" => TimeoutOpenDuration,
            "upstream_overloaded" => OverloadedOpenDuration,
            _ => TimeSpan.Zero
        };
    }

    private static string MapErrorTypeToReason(string errorType)
        => errorType switch
        {
            "quota_exhausted" => "quota_exhausted",
            "rate_limited" => "rate_limited",
            "request_timeout" => "request_timeout",
            "upstream_overloaded" => "upstream_overloaded",
            _ => errorType ?? string.Empty
        };

    private static bool IsEntryOpen(JsonObject entry, DateTimeOffset now)
    {
        var openUntil = ParseUtc(entry["openUntilUtc"]?.ToString());
        return openUntil > now;
    }

    private static JsonObject? ReadEntry(string path, string provider, string model)
    {
        lock (FileLock)
        {
            var root = LoadRoot(path);
            var entries = EnsureEntries(root);
            return entries[BuildKey(provider, model)] as JsonObject;
        }
    }

    private static JsonObject LoadRoot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new JsonObject();
            }

            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static JsonObject EnsureEntries(JsonObject root)
    {
        if (root["entries"] is not JsonObject entries)
        {
            entries = new JsonObject();
            root["entries"] = entries;
        }

        return entries;
    }

    private static void SaveRoot(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DateTimeOffset ParseUtc(string? text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTimeOffset.MinValue;
    }

    private static string BuildBlockMessage(string provider, string model, string reason, DateTimeOffset openUntil)
    {
        var providerName = string.IsNullOrWhiteSpace(provider) ? "Cloud provider" : provider;
        var modelName = string.IsNullOrWhiteSpace(model) ? "selected cloud model" : model;
        _ = openUntil;
        return reason switch
        {
            "quota_exhausted" =>
                $"{providerName} quota looks exhausted for {modelName}. Wait, or choose a local model.",
            "rate_limited" =>
                $"{providerName} is rate-limited for {modelName}. Wait a moment, then retry.",
            "request_timeout" =>
                $"{providerName} timed out for {modelName}. Retry, or choose a local model.",
            "upstream_overloaded" =>
                $"{providerName} is busy for {modelName}. Wait a moment, then retry.",
            _ =>
                $"{providerName} is paused for {modelName}. Wait, then retry."
        };
    }

    private static string BuildBlockHint(string reason)
        => reason switch
        {
            "quota_exhausted" => "Check the provider dashboard or billing, then Launch the cloud profile again when quota returns. Stay on local meanwhile.",
            "rate_limited" => "Wait for the pause to expire, reduce burst traffic, or stay on the loaded local model.",
            "request_timeout" => "Try a shorter prompt, wait for the pause to expire, or stay on local.",
            "upstream_overloaded" => "Retry after the pause or stay on local while the provider recovers.",
            _ => "Launch the cloud profile again after the pause, or stay on local."
        };

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string FirstNonEmpty(string? primary, string fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();
}
