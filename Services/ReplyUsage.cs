using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// The token usage llama-server reports on a reply (the final stream chunk, or the JSON body):
/// what the request it was actually sent held, after AI-FluxMux's shortening and additions, and
/// the reply. This is the count a Client app's own context gauge shows (Cline: prompt plus
/// completion of the last request), so AI-FluxMux's gauge uses it too (27 Nov): it used to show
/// the chat as the Client app sent it, measured before compaction, Omit's stubs and the session
/// note, and the two gauges disagreed on every turn AI-FluxMux changed the request.
/// </summary>
public static class ReplyUsage
{
    /// <summary>Reads prompt and completion tokens from one SSE data line or a JSON body; false when it has no usage.</summary>
    public static bool TryRead(string? jsonOrSseLine, out int promptTokens, out int completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;
        if (string.IsNullOrWhiteSpace(jsonOrSseLine) || !jsonOrSseLine.Contains("\"usage\"", System.StringComparison.Ordinal))
        {
            return false;
        }

        var json = jsonOrSseLine.TrimStart();
        if (json.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
        {
            json = json[5..].Trim();
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["usage"] is not JsonObject usage)
            {
                return false;
            }

            promptTokens = ReadInt(usage["prompt_tokens"]);
            completionTokens = ReadInt(usage["completion_tokens"]);
            return promptTokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ReadInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var number) && number > 0 ? (int)number : 0;
}
