using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Named OpenAI-compatible cloud backends beyond the built-in Gemini/Anthropic/OpenAI/Copilot slots.
/// Config: CustomCloudProviders[name].Endpoint (+ optional path/auth fields).
/// Secrets: CustomCompatApiKeys[name]. Legacy CustomCompatEndpoint / CustomCompatApiKey still
/// map to <see cref="DefaultCustomProviderName"/>.
/// </summary>
public static class CustomCloudProviderRegistry
{
    public const string DefaultCustomProviderName = "Custom OpenAI-Compatible";
    public const string ProvidersConfigKey = "CustomCloudProviders";
    public const string ApiKeysSecretsKey = "CustomCompatApiKeys";
    public const string LegacyEndpointField = "CustomCompatEndpoint";
    public const string LegacyApiKeyField = "CustomCompatApiKey";
    public const string LegacyAuthModeField = "CustomCompatAuthMode";
    public const string LegacyApiKeyHeaderField = "CustomCompatApiKeyHeader";
    public const string LegacyModelsPathField = "CustomCompatModelsPath";
    public const string LegacyChatPathField = "CustomCompatChatPath";
    public const string LegacyApplyOpenAiTweaksField = "CustomCompatApplyOpenAiTweaks";

    public static readonly string[] BuiltInProviders =
    [
        "Gemini",
        "Anthropic",
        "OpenAI",
        "Copilot GitHub"
    ];

    /// <summary>
    /// Common OpenAI-compatible hosts seeded into CustomCloudProviders when missing.
    /// These are product built-ins for OpenAI-compatible APIs (separate from Gemini/Anthropic/OpenAI/Copilot).
    /// Bump <see cref="ReadyMadeSeedVersion"/> when adding new presets so existing installs pick them up
    /// without restoring any ready-made entry the operator already removed before that version.
    /// </summary>
    public const int ReadyMadeSeedVersion = 2;

    public const string ReadyMadeSeedVersionConfigKey = "CustomCloudProvidersReadyMadeSeedVersion";

    public static readonly (string Name, string Endpoint)[] ReadyMadeOpenAiCompatibleProviders =
    [
        ("Groq", "https://api.groq.com/openai/v1"),
        ("DeepSeek", "https://api.deepseek.com/v1"),
        ("OpenRouter", "https://openrouter.ai/api/v1"),
        ("Together", "https://api.together.xyz/v1"),
        ("Fireworks", "https://api.fireworks.ai/inference/v1"),
        ("Mistral", "https://api.mistral.ai/v1"),
        ("xAI", "https://api.x.ai/v1"),
        ("Cerebras", "https://api.cerebras.ai/v1"),
        ("Perplexity", "https://api.perplexity.ai"),
        ("Cohere", "https://api.cohere.ai/compatibility/v1"),
        ("DeepInfra", "https://api.deepinfra.com/v1/openai"),
        ("SambaNova", "https://api.sambanova.ai/v1"),
        ("NVIDIA", "https://integrate.api.nvidia.com/v1"),
        ("Hugging Face", "https://router.huggingface.co/v1"),
        ("Hyperbolic", "https://api.hyperbolic.xyz/v1"),
        ("Ollama", "http://127.0.0.1:11434/v1"),
        ("LM Studio", "http://127.0.0.1:1234/v1")
    ];

    public static bool IsBuiltIn(string? provider)
    {
        var name = NormalizeName(provider);
        return BuiltInProviders.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsReadyMade(string? provider)
    {
        var name = NormalizeName(provider);
        return ReadyMadeOpenAiCompatibleProviders.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCustomCompat(string? provider, JsonObject? configRoot)
    {
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name) || IsBuiltIn(name))
        {
            return false;
        }

        if (name.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return FindProviderEntry(configRoot, name) is not null;
    }

    public static string NormalizeName(string? provider) => (provider ?? string.Empty).Trim();

    /// <summary>
    /// Ensures CustomCloudProviders exists and lifts legacy single-slot fields into the default entry.
    /// Returns true when the config object was modified.
    /// </summary>
    public static bool EnsureMigrated(JsonObject configRoot, JsonObject? secretsRoot = null)
    {
        var changed = false;
        if (configRoot[ProvidersConfigKey] is not JsonObject providers)
        {
            providers = new JsonObject();
            configRoot[ProvidersConfigKey] = providers;
            changed = true;
        }

        var defaultKey = FindProviderKey(providers, DefaultCustomProviderName);
        if (string.IsNullOrWhiteSpace(defaultKey))
        {
            providers[DefaultCustomProviderName] = new JsonObject();
            changed = true;
            defaultKey = DefaultCustomProviderName;
        }

        var resolvedDefaultKey = defaultKey ?? DefaultCustomProviderName;
        if (providers[resolvedDefaultKey] is not JsonObject defaultEntry)
        {
            defaultEntry = new JsonObject();
            providers[resolvedDefaultKey] = defaultEntry;
            changed = true;
        }

        var legacyEndpoint = GetString(configRoot, LegacyEndpointField);
        if (!string.IsNullOrWhiteSpace(legacyEndpoint)
            && string.IsNullOrWhiteSpace(GetString(defaultEntry, "Endpoint")))
        {
            defaultEntry["Endpoint"] = legacyEndpoint;
            changed = true;
        }

        CopyLegacyStringIfMissing(configRoot, LegacyAuthModeField, defaultEntry, "AuthMode", ref changed);
        CopyLegacyStringIfMissing(configRoot, LegacyApiKeyHeaderField, defaultEntry, "ApiKeyHeader", ref changed);
        CopyLegacyStringIfMissing(configRoot, LegacyModelsPathField, defaultEntry, "ModelsPath", ref changed);
        CopyLegacyStringIfMissing(configRoot, LegacyChatPathField, defaultEntry, "ChatPath", ref changed);
        if (configRoot[LegacyApplyOpenAiTweaksField] is not null
            && defaultEntry["ApplyOpenAiTweaks"] is null)
        {
            defaultEntry["ApplyOpenAiTweaks"] = ParseBool(GetString(configRoot, LegacyApplyOpenAiTweaksField), true);
            changed = true;
        }

        if (secretsRoot is not null)
        {
            changed |= EnsureSecretsMigrated(secretsRoot);
        }

        // Keep legacy top-level endpoint in sync with the default custom entry.
        var syncedEndpoint = GetString(defaultEntry, "Endpoint");
        if (string.IsNullOrWhiteSpace(syncedEndpoint))
        {
            syncedEndpoint = GetString(configRoot, LegacyEndpointField);
        }

        if (!string.Equals(GetString(configRoot, LegacyEndpointField), syncedEndpoint, StringComparison.Ordinal))
        {
            configRoot[LegacyEndpointField] = syncedEndpoint;
            changed = true;
        }

        changed |= SeedReadyMadeProviders(configRoot, providers);

        return changed;
    }

    /// <summary>
    /// Adds any ready-made presets that are new for this seed version and not already registered.
    /// Does not overwrite an existing endpoint, and does not re-add a preset the operator removed
    /// after that seed version was applied.
    /// </summary>
    private static bool SeedReadyMadeProviders(JsonObject configRoot, JsonObject providers)
    {
        var currentVersion = 0;
        var versionText = GetString(configRoot, ReadyMadeSeedVersionConfigKey);
        _ = int.TryParse(versionText, out currentVersion);
        if (configRoot[ReadyMadeSeedVersionConfigKey] is JsonValue versionNode)
        {
            try
            {
                currentVersion = Math.Max(currentVersion, versionNode.GetValue<int>());
            }
            catch
            {
                // keep parsed text version
            }
        }

        if (currentVersion >= ReadyMadeSeedVersion)
        {
            return false;
        }

        foreach (var (name, endpoint) in ReadyMadeOpenAiCompatibleProviders)
        {
            if (FindProviderKey(providers, name) is not null)
            {
                continue;
            }

            providers[name] = new JsonObject { ["Endpoint"] = endpoint };
        }

        configRoot[ReadyMadeSeedVersionConfigKey] = ReadyMadeSeedVersion;
        return true;
    }

    public static bool EnsureSecretsMigrated(JsonObject secretsRoot)
    {
        var changed = false;
        if (secretsRoot[ApiKeysSecretsKey] is not JsonObject keys)
        {
            keys = new JsonObject();
            secretsRoot[ApiKeysSecretsKey] = keys;
            changed = true;
        }

        var legacyKey = GetString(secretsRoot, LegacyApiKeyField);
        if (!string.IsNullOrWhiteSpace(legacyKey)
            && string.IsNullOrWhiteSpace(GetKeyedString(keys, DefaultCustomProviderName)))
        {
            keys[DefaultCustomProviderName] = legacyKey;
            changed = true;
        }

        var defaultKey = GetKeyedString(keys, DefaultCustomProviderName);
        if (!string.Equals(GetString(secretsRoot, LegacyApiKeyField), defaultKey, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(defaultKey))
        {
            secretsRoot[LegacyApiKeyField] = defaultKey;
            changed = true;
        }

        return changed;
    }

    public static IReadOnlyList<string> ListCustomProviderNames(JsonObject? configRoot)
    {
        if (configRoot is null)
        {
            return Array.Empty<string>();
        }

        EnsureMigrated(configRoot);
        if (configRoot[ProvidersConfigKey] is not JsonObject providers)
        {
            return Array.Empty<string>();
        }

        var readyMadeOrder = ReadyMadeOpenAiCompatibleProviders
            .Select((p, index) => (p.Name, Index: index))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var names = providers
            .Select(e => NormalizeName(e.Key))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => !n.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase)
                        || ShouldListDefaultCustomSlot(configRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => IsReadyMade(n) ? 1 : 0)
            .ThenBy(n => n.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(n => readyMadeOrder.TryGetValue(n, out var index) ? index : int.MaxValue)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names;
    }

    /// <summary>
    /// Legacy unnamed slot only appears when it already has an endpoint or Model Profiles.
    /// New customs must use a real name via <see cref="TryRegisterNamedProvider"/>.
    /// </summary>
    public static bool ShouldListDefaultCustomSlot(JsonObject configRoot)
    {
        EnsureMigrated(configRoot);
        if (!string.IsNullOrWhiteSpace(GetEndpoint(configRoot, DefaultCustomProviderName)))
        {
            return true;
        }

        return HasCloudProfilesForProvider(configRoot, DefaultCustomProviderName);
    }

    public static string GetEndpoint(JsonObject configRoot, string? provider)
    {
        EnsureMigrated(configRoot);
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var entry = FindProviderEntry(configRoot, name);
        var fromEntry = GetString(entry, "Endpoint");
        if (!string.IsNullOrWhiteSpace(fromEntry))
        {
            return fromEntry.Trim();
        }

        if (name.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return GetString(configRoot, LegacyEndpointField).Trim();
        }

        return string.Empty;
    }

    public static void SetEndpoint(JsonObject configRoot, string? provider, string? endpoint)
    {
        EnsureMigrated(configRoot);
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name) || !IsCustomCompat(name, configRoot))
        {
            return;
        }

        if (configRoot[ProvidersConfigKey] is not JsonObject providers)
        {
            return;
        }

        var key = FindProviderKey(providers, name) ?? name;
        if (providers[key] is not JsonObject entry)
        {
            entry = new JsonObject();
            providers[key] = entry;
        }

        var trimmed = (endpoint ?? string.Empty).Trim();
        entry["Endpoint"] = trimmed;
        if (name.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            configRoot[LegacyEndpointField] = trimmed;
        }
    }

    public static string GetAuthMode(JsonObject configRoot, string? provider)
        => FirstNonEmpty(GetProviderField(configRoot, provider, "AuthMode"), GetString(configRoot, LegacyAuthModeField), "Bearer");

    public static string GetApiKeyHeader(JsonObject configRoot, string? provider)
        => FirstNonEmpty(GetProviderField(configRoot, provider, "ApiKeyHeader"), GetString(configRoot, LegacyApiKeyHeaderField), "Authorization");

    public static string GetModelsPath(JsonObject configRoot, string? provider)
        => FirstNonEmpty(GetProviderField(configRoot, provider, "ModelsPath"), GetString(configRoot, LegacyModelsPathField), "/models");

    public static string GetChatPath(JsonObject configRoot, string? provider)
        => FirstNonEmpty(GetProviderField(configRoot, provider, "ChatPath"), GetString(configRoot, LegacyChatPathField), "/chat/completions");

    public static bool GetApplyOpenAiTweaks(JsonObject configRoot, string? provider)
    {
        var entry = FindProviderEntry(configRoot, provider);
        if (entry?["ApplyOpenAiTweaks"] is JsonValue value)
        {
            try
            {
                return value.GetValue<bool>();
            }
            catch
            {
                // fall through
            }
        }

        return ParseBool(GetString(configRoot, LegacyApplyOpenAiTweaksField), fallback: true);
    }

    public static string GetApiKey(JsonObject? secretsRoot, JsonObject? configRoot, string? provider)
    {
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        if (secretsRoot is not null)
        {
            EnsureSecretsMigrated(secretsRoot);
            if (secretsRoot[ApiKeysSecretsKey] is JsonObject keys)
            {
                var fromMap = GetKeyedString(keys, name);
                if (!string.IsNullOrWhiteSpace(fromMap))
                {
                    return fromMap;
                }
            }

            if (name.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
            {
                var legacy = GetString(secretsRoot, LegacyApiKeyField);
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    return legacy;
                }
            }
        }

        if (configRoot is not null && name.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return GetString(configRoot, LegacyApiKeyField).Trim();
        }

        return string.Empty;
    }

    public static void SetApiKey(JsonObject secretsRoot, string? provider, string? apiKey)
    {
        EnsureSecretsMigrated(secretsRoot);
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (secretsRoot[ApiKeysSecretsKey] is not JsonObject keys)
        {
            keys = new JsonObject();
            secretsRoot[ApiKeysSecretsKey] = keys;
        }

        var trimmed = (apiKey ?? string.Empty).Trim();
        var existingKey = FindProviderKey(keys, name);
        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            keys.Remove(existingKey);
        }

        keys[name] = trimmed;
        if (name.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            secretsRoot[LegacyApiKeyField] = trimmed;
        }
    }

    public static bool TryAddProvider(JsonObject configRoot, string? name, string? endpoint, out string error)
        => TryRegisterNamedProvider(configRoot, name, endpoint, updateExisting: false, out error);

    /// <summary>
    /// Registers a named OpenAI-compatible provider in CustomCloudProviders.
    /// Every custom provider must have an operator-chosen name (not the legacy default slot label).
    /// </summary>
    public static bool TryRegisterNamedProvider(
        JsonObject configRoot,
        string? name,
        string? endpoint,
        bool updateExisting,
        out string error)
    {
        EnsureMigrated(configRoot);
        error = string.Empty;
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Enter a name for the custom OpenAI-compatible provider.";
            return false;
        }

        if (IsBuiltIn(normalized))
        {
            error = normalized + " is already a built-in cloud provider.";
            return false;
        }

        if (normalized.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            error = "Choose a specific name for this server (for example Work vLLM or Office Groq). \"Custom OpenAI-Compatible\" is only kept for older unnamed setups.";
            return false;
        }

        if (normalized.Contains("::", StringComparison.Ordinal))
        {
            error = "Custom provider names cannot contain \"::\".";
            return false;
        }

        var trimmedEndpoint = (endpoint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedEndpoint) || !Uri.TryCreate(trimmedEndpoint, UriKind.Absolute, out _))
        {
            error = "Enter a valid base URL for the custom provider, for example https://host:port/v1";
            return false;
        }

        if (configRoot[ProvidersConfigKey] is not JsonObject providers)
        {
            providers = new JsonObject();
            configRoot[ProvidersConfigKey] = providers;
        }

        var existingKey = FindProviderKey(providers, normalized);
        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            if (!updateExisting)
            {
                error = "A custom cloud provider named \"" + normalized + "\" already exists.";
                return false;
            }

            var resolvedExistingKey = existingKey!;
            if (providers[resolvedExistingKey] is not JsonObject entry)
            {
                entry = new JsonObject();
                providers[resolvedExistingKey] = entry;
            }

            entry["Endpoint"] = trimmedEndpoint;
            return true;
        }

        providers[normalized] = new JsonObject { ["Endpoint"] = trimmedEndpoint };
        return true;
    }

    public static bool TryRemoveProvider(JsonObject configRoot, JsonObject? secretsRoot, string? name, out string error)
    {
        EnsureMigrated(configRoot);
        error = string.Empty;
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Choose a custom cloud provider to remove.";
            return false;
        }

        if (normalized.Equals(DefaultCustomProviderName, StringComparison.OrdinalIgnoreCase))
        {
            error = "The legacy Custom OpenAI-Compatible slot cannot be removed. Clear its base URL, or Add a named custom provider and move profiles to that name.";
            return false;
        }

        if (IsReadyMade(normalized))
        {
            error = "\"" + normalized + "\" is a built-in OpenAI-compatible provider and cannot be removed. Leave its API key blank if you are not using it.";
            return false;
        }

        if (IsBuiltIn(normalized))
        {
            error = "Built-in cloud providers cannot be removed.";
            return false;
        }

        if (configRoot[ProvidersConfigKey] is not JsonObject providers)
        {
            error = "That custom cloud provider is not registered.";
            return false;
        }

        var key = FindProviderKey(providers, normalized);
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "That custom cloud provider is not registered.";
            return false;
        }

        providers.Remove(key);
        if (secretsRoot is not null)
        {
            EnsureSecretsMigrated(secretsRoot);
            if (secretsRoot[ApiKeysSecretsKey] is JsonObject keys)
            {
                var secretKey = FindProviderKey(keys, normalized);
                if (!string.IsNullOrWhiteSpace(secretKey))
                {
                    keys.Remove(secretKey);
                }
            }
        }

        return true;
    }

    public static bool HasCloudProfilesForProvider(JsonObject configRoot, string? provider)
    {
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name) || configRoot["CloudProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        return profiles.Any(entry =>
        {
            var parts = (entry.Key ?? string.Empty).Split("::", 2, StringSplitOptions.TrimEntries);
            return parts.Length >= 1 && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string GetProviderField(JsonObject configRoot, string? provider, string field)
    {
        EnsureMigrated(configRoot);
        return GetString(FindProviderEntry(configRoot, provider), field);
    }

    private static JsonObject? FindProviderEntry(JsonObject? configRoot, string? provider)
    {
        var name = NormalizeName(provider);
        if (string.IsNullOrWhiteSpace(name) || configRoot?[ProvidersConfigKey] is not JsonObject providers)
        {
            return null;
        }

        var key = FindProviderKey(providers, name);
        return string.IsNullOrWhiteSpace(key) ? null : providers[key] as JsonObject;
    }

    private static string? FindProviderKey(JsonObject map, string? name)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return map
            .Select(e => e.Key)
            .FirstOrDefault(k => NormalizeName(k).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyLegacyStringIfMissing(
        JsonObject configRoot,
        string legacyField,
        JsonObject entry,
        string entryField,
        ref bool changed)
    {
        var legacy = GetString(configRoot, legacyField);
        if (!string.IsNullOrWhiteSpace(legacy) && string.IsNullOrWhiteSpace(GetString(entry, entryField)))
        {
            entry[entryField] = legacy;
            changed = true;
        }
    }

    private static string GetKeyedString(JsonObject map, string name)
    {
        var key = FindProviderKey(map, name);
        return string.IsNullOrWhiteSpace(key) ? string.Empty : GetString(map, key);
    }

    private static string GetString(JsonObject? root, string key)
    {
        if (root is null || string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var node = root[key];
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return node.GetValue<string>()?.Trim() ?? node.ToString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return node.ToString()?.Trim() ?? string.Empty;
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static bool ParseBool(string? text, bool fallback)
    {
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }
}
