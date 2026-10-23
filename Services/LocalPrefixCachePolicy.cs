using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Compact breaks the prompt prefix. Always skip llama-server cache_prompt on
/// that forwarded turn. This is a correctness guard, not a tuning knob: after a
/// compact the prefix has changed, so reusing the old KV cache would produce
/// wrong output. The session --no-cache-prompt flag stays on the profile.
/// </summary>
public static class LocalPrefixCachePolicy
{
    public static void ApplyAfterCompact(JsonObject payload, bool compactApplied, PortForwardingRules? rules = null)
    {
        if (payload is null || !compactApplied)
        {
            return;
        }

        // Always skip the prefix cache after a compact. The SkipPrefixCacheAfterCompact
        // port-rule is a no-op (always on) because reusing the cache after a compact
        // is never safe - the prefix has changed.
        payload["cache_prompt"] = false;
    }
}
