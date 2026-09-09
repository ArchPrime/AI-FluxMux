using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class ProfileSettingsFingerprint
{
    public static string Local(JsonObject? settings)
    {
        settings ??= new JsonObject();
        return string.Join("|",
            Token(settings["OverrideContext"], "8192"),
            Token(settings["OverrideThreads"], "Auto"),
            Token(settings["LocalThreadsBatch"], "Auto"),
            Token(settings["LocalTemperature"], "0.3"),
            Token(settings["LocalGpuOffloadMode"], "Auto"),
            Token(settings["GpuLayers"], "Auto"),
            Token(settings["LocalFlashAttention"], "Auto"),
            Token(settings["LocalKvCacheTypeK"], "Auto"),
            Token(settings["LocalKvCacheTypeV"], "Auto"),
            Token(settings["LocalChatTemplate"], "Auto"),
            Token(settings["LocalMultiUserMode"], "Auto"),
            Token(settings["LocalUnbanTokensMode"], "Auto"),
            Token(settings["OverrideMaxTokens"], string.Empty),
            Token(settings["LocalBatchSize"], string.Empty),
            Token(settings["LocalUbatchSize"], string.Empty),
            Token(settings["LocalSpecType"], string.Empty),
            Token(settings["LocalCacheReuse"], string.Empty),
            Token(settings["LocalCacheRam"], string.Empty),
            Token(settings["LocalFit"], string.Empty),
            Token(settings["LocalSwaFull"], string.Empty),
            Token(settings["LocalReasoning"], string.Empty),
            Token(settings["LocalChatParser"], string.Empty),
            NormalizeVisionEnabled(settings["LocalVisionEnabled"]?.ToString()),
            Token(settings["LocalVisionProjectorPath"], string.Empty),
            Token(settings["LocalVisionMaxImageEdge"], string.Empty));
    }

    public static string Cloud(JsonObject? settings)
    {
        settings ??= new JsonObject();
        return string.Join("|",
            Token(settings["CloudTemperature"], "0.7"),
            Token(settings["CloudMaxTokens"], "2048"),
            Token(settings["CloudContextWindow"], "Auto"),
            Token(settings["CloudReasoningMode"], "Balanced"),
            Token(settings["CloudResponseFormat"], "Text"),
            Token(settings["OpenAiReasoningEffort"], "Auto"),
            Token(settings["CopilotReasoningEffort"], "Auto"),
            Token(settings["GeminiThinkingMode"], "Balanced"),
            Token(settings["GeminiThinkingBudget"], "0"),
            Token(settings["AnthropicThinkingMode"], "Standard"));
    }

    public static string NormalizeVisionEnabled(string? value)
        => NormalizeFlag(value, defaultEnabled: false);

    public static string AssignmentKey(string routeType, string model, string fingerprint)
        => string.Join("|",
            (routeType ?? string.Empty).Trim().ToLowerInvariant(),
            (model ?? string.Empty).Trim().ToLowerInvariant(),
            fingerprint ?? string.Empty);

    private static string Token(JsonNode? node, string fallback)
    {
        var text = node?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    }

    private static string NormalizeFlag(string? value, bool defaultEnabled)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultEnabled ? "enabled" : "disabled";
        }

        if (text.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            || text.Equals("On", StringComparison.OrdinalIgnoreCase)
            || text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return "enabled";
        }

        if (text.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Off", StringComparison.OrdinalIgnoreCase)
            || text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || text.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return "disabled";
        }

        return text.ToLowerInvariant();
    }
}
