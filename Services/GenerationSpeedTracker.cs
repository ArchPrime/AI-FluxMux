using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace FluxMux.Avalonia.Services;

public sealed record GenerationSpeedSnapshot(
    bool Active,
    string RouteKind,
    double TokensPerSecond,
    int CompletionTokens,
    string DisplayText,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Tracks reply generation speed from gateway chat responses. Disabled by default in UI until the operator turns it on.
/// </summary>
public sealed class GenerationSpeedTracker
{
    private readonly object _gate = new();
    private bool _enabled;
    private bool _active;
    private string _routeKind = string.Empty;
    private DateTimeOffset _firstTokenUtc;
    private DateTimeOffset _lastTokenUtc;
    private int _completionTokens;
    private double _liveTokensPerSecond;
    private double _lastTokensPerSecond;
    private int _lastCompletionTokens;
    private string _lastRouteKind = string.Empty;
    private DateTimeOffset _lastUpdatedUtc = DateTimeOffset.MinValue;

    public void SetEnabled(bool enabled)
    {
        Volatile.Write(ref _enabled, enabled);
        if (!enabled)
        {
            lock (_gate)
            {
                _active = false;
            }
        }
    }

    public bool IsEnabled => Volatile.Read(ref _enabled);

    public void Begin(string routeKind)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (_gate)
        {
            _active = true;
            _routeKind = NormalizeRouteKind(routeKind);
            _firstTokenUtc = default;
            _lastTokenUtc = default;
            _completionTokens = 0;
            _liveTokensPerSecond = 0;
        }
    }

    public void ProcessSseLine(string? line)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = line.Substring(5).Trim();
        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProcessJsonPayload(payload);
    }

    public void ProcessJsonBody(ReadOnlySpan<byte> body)
    {
        if (!IsEnabled || body.IsEmpty)
        {
            return;
        }

        try
        {
            ProcessJsonPayload(Encoding.UTF8.GetString(body));
        }
        catch
        {
        }
    }

    public void Complete()
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            FinalizeGenerationLocked();
            _active = false;
        }
    }

    public GenerationSpeedSnapshot? ReadSnapshot()
    {
        if (!IsEnabled)
        {
            return null;
        }

        lock (_gate)
        {
            if (_active)
            {
                RecalculateLiveRateLocked(DateTimeOffset.UtcNow, useLiveClock: true);
                return BuildSnapshotLocked(
                    active: true,
                    routeKind: _routeKind,
                    tokensPerSecond: _liveTokensPerSecond,
                    completionTokens: _completionTokens);
            }

            if (_lastUpdatedUtc == DateTimeOffset.MinValue)
            {
                return null;
            }

            return BuildSnapshotLocked(
                active: false,
                routeKind: _lastRouteKind,
                tokensPerSecond: _lastTokensPerSecond,
                completionTokens: _lastCompletionTokens);
        }
    }

    public static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, (text.Length + 3) / 4);
    }

    public static bool TryApplyTimings(JsonObject root, ref int completionTokens, ref double tokensPerSecond)
    {
        if (root["timings"] is not JsonObject timings)
        {
            return false;
        }

        var applied = false;
        if (TryReadInt(timings["predicted_n"], out var predictedN) && predictedN > 0)
        {
            completionTokens = Math.Max(completionTokens, predictedN);
            applied = true;
        }

        if (TryReadDouble(timings["predicted_per_second"], out var predictedPerSecond) && predictedPerSecond > 0)
        {
            tokensPerSecond = predictedPerSecond;
            applied = true;
        }

        return applied;
    }

    public static bool TryApplyUsage(JsonObject root, ref int completionTokens)
    {
        if (root["usage"] is not JsonObject usage)
        {
            return false;
        }

        if (TryReadInt(usage["completion_tokens"], out var completion) && completion >= 0)
        {
            completionTokens = Math.Max(completionTokens, completion);
            return true;
        }

        return false;
    }

    public static int CountDeltaTokens(JsonObject root)
    {
        if (root["choices"] is not JsonArray choices || choices.Count == 0)
        {
            return 0;
        }

        if (choices[0] is not JsonObject firstChoice)
        {
            return 0;
        }

        var total = 0;
        if (firstChoice["delta"] is JsonObject delta)
        {
            total += EstimateTokenCount(delta["content"]?.ToString());
            total += EstimateTokenCount(delta["reasoning_content"]?.ToString());
            total += EstimateTokenCount(delta["text"]?.ToString());
            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var callNode in toolCalls)
                {
                    if (callNode is not JsonObject call)
                    {
                        continue;
                    }

                    if (call["function"] is JsonObject function)
                    {
                        total += EstimateTokenCount(function["name"]?.ToString());
                        total += EstimateTokenCount(function["arguments"]?.ToString());
                    }
                }
            }
        }

        if (firstChoice["text"] is JsonValue textValue)
        {
            total += EstimateTokenCount(textValue.ToString());
        }

        if (firstChoice["message"] is JsonObject message)
        {
            total += EstimateTokenCount(message["content"]?.ToString());
        }

        return total;
    }

    private void ProcessJsonPayload(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return;
        }

        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var deltaTokens = CountDeltaTokens(root);
            if (deltaTokens > 0)
            {
                if (_firstTokenUtc == default)
                {
                    _firstTokenUtc = now;
                }

                _lastTokenUtc = now;
                _completionTokens += deltaTokens;
            }

            var liveRate = _liveTokensPerSecond;
            var completionTokens = _completionTokens;
            _ = TryApplyTimings(root, ref completionTokens, ref liveRate);
            _ = TryApplyUsage(root, ref completionTokens);
            _completionTokens = completionTokens;
            if (liveRate > 0)
            {
                _liveTokensPerSecond = liveRate;
            }
            else
            {
                RecalculateLiveRateLocked(now, useLiveClock: false);
            }
        }
    }

    private void FinalizeGenerationLocked()
    {
        var now = DateTimeOffset.UtcNow;
        RecalculateLiveRateLocked(now, useLiveClock: false);
        if (_liveTokensPerSecond > 0)
        {
            _lastTokensPerSecond = _liveTokensPerSecond;
        }

        _lastCompletionTokens = _completionTokens;
        _lastRouteKind = _routeKind;
        _lastUpdatedUtc = now;
    }

    private void RecalculateLiveRateLocked(DateTimeOffset now, bool useLiveClock)
    {
        if (_completionTokens <= 0 || _firstTokenUtc == default)
        {
            return;
        }

        var end = useLiveClock
            ? now
            : _lastTokenUtc == default ? now : _lastTokenUtc;
        var seconds = Math.Max(0.05, (end - _firstTokenUtc).TotalSeconds);
        _liveTokensPerSecond = _completionTokens / seconds;
    }

    private GenerationSpeedSnapshot BuildSnapshotLocked(
        bool active,
        string routeKind,
        double tokensPerSecond,
        int completionTokens)
    {
        var kindLabel = routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
        var rateText = tokensPerSecond > 0
            ? tokensPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s"
            : active ? "waiting for tokens…" : "—";
        var tokenText = completionTokens > 0
            ? completionTokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens"
            : string.Empty;
        var prefix = active ? "Generating" : "Last reply";
        var display = string.IsNullOrWhiteSpace(tokenText)
            ? $"{prefix} \u00b7 {rateText} \u00b7 {kindLabel}"
            : $"{prefix} \u00b7 {rateText} \u00b7 {kindLabel} \u00b7 {tokenText}";

        return new GenerationSpeedSnapshot(
            active,
            routeKind,
            tokensPerSecond,
            completionTokens,
            display,
            active ? DateTimeOffset.UtcNow : _lastUpdatedUtc);
    }

    private static string NormalizeRouteKind(string? routeKind)
        => string.IsNullOrWhiteSpace(routeKind) ? "local" : routeKind.Trim();

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
