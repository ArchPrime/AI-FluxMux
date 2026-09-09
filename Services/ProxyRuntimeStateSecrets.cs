using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Cloud API keys must stay in memory (and fluxmux_secrets.json). They must not
/// remain in Proxy_Runtime_State.json for Diagnostics, backups, or logs.
/// </summary>
public static class ProxyRuntimeStateSecrets
{
    public static readonly string[] SecretFields =
    [
        "gemini_key",
        "anthropic_key",
        "openai_key",
        "custom_key",
        "copilot_key"
    ];

    public static void Capture(JsonObject state, JsonObject destination)
    {
        foreach (var field in SecretFields)
        {
            var value = GetString(state, field);
            if (!string.IsNullOrWhiteSpace(value) && !IsRedacted(value))
            {
                destination[field] = value;
            }
        }
    }

    public static void Apply(JsonObject state, JsonObject? secrets)
    {
        if (secrets is null)
        {
            return;
        }

        foreach (var field in SecretFields)
        {
            var value = GetString(secrets, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                state[field] = value;
            }
        }
    }

    public static JsonObject RedactCopy(JsonObject state)
    {
        var copy = JsonNode.Parse(state.ToJsonString()) as JsonObject ?? new JsonObject();
        foreach (var field in SecretFields)
        {
            if (copy.ContainsKey(field))
            {
                copy[field] = string.Empty;
            }
        }

        return copy;
    }

    public static bool LooksLikeSecretDump(JsonObject? state)
    {
        if (state is null)
        {
            return false;
        }

        foreach (var field in SecretFields)
        {
            var value = GetString(state, field);
            if (!string.IsNullOrWhiteSpace(value) && !IsRedacted(value))
            {
                return true;
            }
        }

        return false;
    }

    public static void FillMissingFromStores(JsonObject state, JsonObject? configRoot, JsonObject? secretsRoot)
    {
        var provider = GetString(state, "provider");
        var isCustomCompat = CustomCloudProviderRegistry.IsCustomCompat(provider, configRoot);
        SetIfEmpty(state, "gemini_key", FirstNonEmpty(GetString(secretsRoot, "GeminiApiKey"), GetString(configRoot, "GeminiApiKey")));
        SetIfEmpty(state, "anthropic_key", FirstNonEmpty(GetString(secretsRoot, "AnthropicApiKey"), GetString(configRoot, "AnthropicApiKey")));
        SetIfEmpty(state, "openai_key", FirstNonEmpty(GetString(secretsRoot, "OpenAiApiKey"), GetString(configRoot, "OpenAiApiKey")));
        SetIfEmpty(state, "copilot_key", FirstNonEmpty(GetString(secretsRoot, "GitHubCopilotApiKey"), GetString(configRoot, "GitHubCopilotApiKey")));
        if (isCustomCompat)
        {
            SetIfEmpty(state, "custom_key", CustomCloudProviderRegistry.GetApiKey(secretsRoot, configRoot, provider));
        }
    }

    public static string FormatCloudLabel(string? provider, string? model)
    {
        var name = (provider ?? string.Empty).Trim();
        var id = (model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return id;
        }

        return string.IsNullOrWhiteSpace(id) ? name : name + " / " + id;
    }

    public static bool LastServedWasCloud(string? kind)
        => (kind ?? string.Empty).Trim().Equals("cloud", StringComparison.OrdinalIgnoreCase);

    private static void SetIfEmpty(JsonObject state, string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(GetString(state, field)))
        {
            return;
        }

        state[field] = value;
    }

    private static bool IsRedacted(string value)
        => value.Trim().Equals("********", StringComparison.Ordinal)
           || value.IndexOf('*', StringComparison.Ordinal) >= 0 && value.Replace("*", string.Empty, StringComparison.Ordinal).Length == 0;

    private static string GetString(JsonObject? root, string key)
        => root is null ? string.Empty : (root[key]?.ToString() ?? string.Empty).Trim();

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}
