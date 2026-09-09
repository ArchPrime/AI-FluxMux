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

        return "Off";
    }

    public static bool IsOff(string? value) => NormalizeMode(value) == "Off";

    public static bool IsOn(string? value) => NormalizeMode(value) == "On";

    public static bool IsAuto(string? value) => NormalizeMode(value) == "Auto";
}
