using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Removes thinking markup from llama-server responses before they reach the Client app,
/// so Cline and Harness do not store empty reasoning blocks in chat history.
/// </summary>
public static class LocalEndpointResponseSanitizer
{
    public static bool SanitizeCompletionJson(JsonObject root, string? reasoningMode)
    {
        var mode = LocalReasoningLaunchPolicy.NormalizeMode(reasoningMode);
        return LocalReasoningLaunchPolicy.IsThinkingEnabled(mode)
            ? LocalThinkingContentFilter.StripEmptyThinkingFromCompletion(root)
            : LocalThinkingContentFilter.StripThinkingFromCompletion(root);
    }

    public static (byte[] Body, bool Changed) SanitizeJsonBody(byte[] body, string? reasoningMode)
    {
        var (sanitized, thinkingChanged, _) = SanitizeJsonBody(body, reasoningMode, declaredTools: null);
        return (sanitized, thinkingChanged);
    }

    public static (byte[] Body, bool Changed, bool Healed) SanitizeJsonBody(
        byte[] body,
        string? reasoningMode,
        IReadOnlyCollection<string>? declaredTools,
        bool allowParallelToolCalls = true,
        LocalSessionArtifacts? artifacts = null)
    {
        if (body.Length == 0)
        {
            return (body, false, false);
        }

        var shouldThink = !string.IsNullOrWhiteSpace(reasoningMode);
        var shouldHeal = declaredTools is { Count: > 0 };
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            return (body, false, false);
        }

        if (root is null)
        {
            return (body, false, false);
        }

        var thinkingChanged = shouldThink && SanitizeCompletionJson(root, reasoningMode);
        var healed = shouldHeal
            && LocalToolCallHealing.HealCompletion(root, declaredTools!, allowParallelToolCalls, artifacts);
        var blocked = LocalSessionArtifactPolicy.FilterCompletion(root, artifacts) > 0;
        if (!thinkingChanged && !healed && !blocked)
        {
            return (body, false, false);
        }

        return (Encoding.UTF8.GetBytes(root.ToJsonString()), thinkingChanged, healed || blocked);
    }

    public static (string Line, bool Changed) SanitizeSseLine(string line, string? reasoningMode)
        => SanitizeSseLine(line, reasoningMode, streamSession: null);

    public static (string Line, bool Changed) SanitizeSseLine(
        string line,
        string? reasoningMode,
        LocalThinkingStreamSession? streamSession)
    {
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return (line, false);
        }

        var payload = line.Substring(5).TrimStart();
        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.Ordinal))
        {
            return (line, false);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject;
        }
        catch
        {
            return (line, false);
        }

        if (root is null)
        {
            return (line, false);
        }

        var changed = streamSession is not null
            ? streamSession.FilterCompletionJson(root)
            : SanitizeCompletionJson(root, reasoningMode);
        if (!changed)
        {
            return (line, false);
        }

        return ("data: " + root.ToJsonString(), true);
    }
}
