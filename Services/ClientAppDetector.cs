using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Names the Client app that sent a chat request, from its own system prompt. Hail Mary acts on
/// the Client app whose chat failed, and the Client app dropdown is only setup notes: Cline and
/// Harness can share Port, and the operator who left the dropdown on Cline while chatting in
/// Harness was offered Cline's /smol and compaction-file rescue for a Harness chat (25 Nov).
/// Returns the dropdown's own names ("Cline", "DeepSeek Harness"), or null when the prompt is not
/// recognised, so callers fall back to the dropdown.
/// </summary>
public static class ClientAppDetector
{
    public const string Cline = "Cline";
    public const string Harness = "DeepSeek Harness";

    // Harness 0.2.x opens with "You are an AI agent powered by DeepSeek Harness."; Cline with
    // "You are Cline, a highly skilled software engineer…".
    private const string HarnessMarker = "powered by DeepSeek Harness";
    private const string ClineMarker = "You are Cline";

    public static string? FromPayload(JsonObject? payload)
    {
        if (payload?["messages"] is not JsonArray messages)
        {
            return null;
        }

        var system = messages.OfType<JsonObject>()
            .FirstOrDefault(m => string.Equals(m["role"]?.ToString(), "system", StringComparison.OrdinalIgnoreCase));
        return system is null ? null : FromSystemPrompt(TextOf(system["content"]));
    }

    public static string? FromSystemPrompt(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Only the opening matters, and a system prompt can be tens of KB.
        var head = text.Length > 2000 ? text[..2000] : text;
        if (head.Contains(HarnessMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Harness;
        }

        return head.Contains(ClineMarker, StringComparison.Ordinal) ? Cline : null;
    }

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue value => value.ToString(),
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["text"]?.ToString() ?? string.Empty)),
        _ => string.Empty,
    };
}
