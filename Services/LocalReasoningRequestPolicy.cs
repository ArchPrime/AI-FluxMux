using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Per-request thinking depth for a Quick Select slot. llama-server stays
/// reasoning-capable at launch; this only changes the next forwarded turn.
/// </summary>
public static class LocalReasoningRequestPolicy
{
    public static readonly string[] SlotOptions = ["Off", "Low", "Medium", "XHigh"];

    public static bool TryNormalize(string? value, out string level)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals("Low", StringComparison.OrdinalIgnoreCase))
        {
            level = "Low";
            return true;
        }

        if (text.Equals("Medium", StringComparison.OrdinalIgnoreCase))
        {
            level = "Medium";
            return true;
        }

        if (text.Equals("XHigh", StringComparison.OrdinalIgnoreCase)
            || text.Equals("High", StringComparison.OrdinalIgnoreCase))
        {
            level = "XHigh";
            return true;
        }

        level = "Off";
        return false;
    }

    public static string NormalizeLevel(string? value)
        => TryNormalize(value, out var level) ? level : "Off";

    public static bool IsThinkingLevel(string? value)
        => TryNormalize(value, out var level) && level is "Low" or "Medium" or "XHigh";

    public static string FromProfile(string? localReasoning)
    {
        if (TryNormalize(localReasoning, out var level))
        {
            return level;
        }

        var text = (localReasoning ?? string.Empty).Trim();
        if (text.Equals("On", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "Medium";
        }

        return "Off";
    }

    public static string? Effort(string? value)
    {
        if (!TryNormalize(value, out var level))
        {
            return null;
        }

        return level switch
        {
            "Low" => "low",
            "Medium" => "medium",
            "XHigh" => "xhigh",
            _ => null
        };
    }

    public static int? ThinkingBudget(string? mode, int maxTokens)
    {
        var normalized = LocalReasoningLaunchPolicy.NormalizeMode(mode);
        if (LocalReasoningLaunchPolicy.IsAuto(normalized))
        {
            return null;
        }

        if (LocalReasoningLaunchPolicy.IsOff(normalized)
            || !LocalReasoningLaunchPolicy.IsThinkingEnabled(normalized))
        {
            return 0;
        }

        var cap = maxTokens > 0 ? maxTokens : 16384;
        if (TryNormalize(normalized, out var level))
        {
            return level switch
            {
                "Low" => Math.Clamp(cap / 8, 256, 2048),
                "Medium" => Math.Clamp(cap / 4, 512, 4096),
                "XHigh" => null,
                _ => null
            };
        }

        if (LocalReasoningLaunchPolicy.IsOn(normalized))
        {
            return Math.Clamp(cap / 4, 512, 4096);
        }

        return null;
    }
}
