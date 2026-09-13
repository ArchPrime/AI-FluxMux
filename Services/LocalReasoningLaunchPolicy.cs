using System;

namespace FluxMux.Avalonia.Services;

public static class LocalReasoningLaunchPolicy
{
    public static string NormalizeMode(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals("On", StringComparison.OrdinalIgnoreCase))
        {
            return "On";
        }

        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "Auto";
        }

        if (LocalReasoningRequestPolicy.TryNormalize(text, out var level))
        {
            return level;
        }

        return "Off";
    }

    public static bool IsOff(string? value) => NormalizeMode(value) == "Off";

    public static bool IsOn(string? value) => NormalizeMode(value) == "On";

    public static bool IsAuto(string? value) => NormalizeMode(value) == "Auto";

    public static bool IsThinkingEnabled(string? value)
    {
        var mode = NormalizeMode(value);
        return mode == "On" || LocalReasoningRequestPolicy.IsThinkingLevel(mode);
    }

    /// <summary>
    /// llama-server is launched reasoning-capable so On/Off can change on the
    /// next request without a reload. Per-request <c>chat_template_kwargs</c>
    /// and history strip still follow the loaded profile.
    /// </summary>
    public static string ProcessLaunchMode() => "Auto";
}
