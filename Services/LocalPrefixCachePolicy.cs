using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Compact breaks the prompt prefix. Skip llama-server cache_prompt on
/// that forwarded turn only. Session --no-cache-prompt stays on the profile.
/// </summary>
public static class LocalPrefixCachePolicy
{
    public static void ApplyAfterCompact(JsonObject payload, bool compactApplied, PortForwardingRules? rules = null)
    {
        if (payload is null || !compactApplied)
        {
            return;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.SkipPrefixCacheAfterCompact)
        {
            return;
        }

        payload["cache_prompt"] = false;
    }
}
