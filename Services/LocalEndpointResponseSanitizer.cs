using System;
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
        return LocalReasoningLaunchPolicy.IsOn(mode)
            ? LocalThinkingContentFilter.StripEmptyThinkingFromCompletion(root)
            : LocalThinkingContentFilter.StripThinkingFromCompletion(root);
    }

    public static (byte[] Body, bool Changed) SanitizeJsonBody(byte[] body, string? reasoningMode)
    {
        if (body.Length == 0)
        {
            return (body, false);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            return (body, false);
        }

        if (root is null || !SanitizeCompletionJson(root, reasoningMode))
        {
            return (body, false);
        }

        return (Encoding.UTF8.GetBytes(root.ToJsonString()), true);
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
