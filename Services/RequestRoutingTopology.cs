using System;

namespace FluxMux.Avalonia.Services;

public static class RequestRoutingTopology
{
    public const string DualHot = "DualHot";
    public const string LocalPrompted = "LocalPrompted";

    public static string Normalize(string? value)
    {
        if (IsLocalPrompted(value))
        {
            return LocalPrompted;
        }

        return DualHot;
    }

    public static bool IsDualHot(string? value)
        => Normalize(value) == DualHot;

    public static bool IsLocalPrompted(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Equals(LocalPrompted, StringComparison.OrdinalIgnoreCase)
            || text.Equals("LocalPromptedSwitching", StringComparison.OrdinalIgnoreCase)
            || text.Contains("prompted", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldKeepBothHot(bool routingEnabled, string? topology)
        => routingEnabled && IsDualHot(topology);

    public static string DescribeForUi(string? topology)
        => IsLocalPrompted(topology)
            ? "Local by default"
            : "Local and cloud both launched";

    public static string NormalizeDisplayOption(string? display)
        => IsLocalPromptedDisplay(display)
            ? LocalPrompted
            : DualHot;

    public static bool IsLocalPromptedDisplay(string? display)
    {
        var text = (display ?? string.Empty).Trim();
        return text.StartsWith("Local by default", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When routing is on, picks proxy mode from ready local/cloud legs and topology.
    /// </summary>
    public static string ResolveRoutingMode(bool routingEnabled, bool localReady, bool cloudReady, string? topology)
    {
        if (!routingEnabled || (!localReady && !cloudReady))
        {
            if (localReady)
            {
                return "local";
            }

            if (cloudReady)
            {
                return "cloud";
            }

            return "idle";
        }

        if (localReady && cloudReady && IsDualHot(topology))
        {
            return "route";
        }

        if (localReady)
        {
            return "local";
        }

        return "cloud";
    }
}
