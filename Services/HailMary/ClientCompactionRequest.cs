using System;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Recognises a Client app's own "summarise this chat" request and helps it succeed.
///
/// Most agent Client apps compact a chat themselves by sending its older part to the model with a
/// summarising instruction, then continuing from the reply — on their own when the chat overflows,
/// or when the person asks (a compact button, /compact). AI-FluxMux sees that request like any
/// other turn, which makes it the one rescue that works the same way for every Client app (26 Nov):
/// recognise the request by the app's instruction (<see cref="SummaryRequestClient"/>, one row per
/// app), add AI-FluxMux's own record of the chat to it so the app's summary keeps the facts
/// (<see cref="HandoverBrief.ClientSummaryRecord"/>, added to the request and to the summary the app gets back), and make it fit when the app sends more
/// than the Context holds.
///
/// Harness sends nearly the whole chat, so its request overflowed too and the chat stayed stuck;
/// the gateway shortens it to fit, or, when even that fails, answers it with AI-FluxMux's session
/// note in Harness's checkpoint layout. Cline sizes its own request to the model's input limit, but
/// its summary lost facts that lived in tool output: on the Live-Switch 10-fact chat (229K tokens)
/// Cline's own compaction kept 7 of 10, against 10 of 10 for Hail Mary's rebuild.
/// </summary>
public static class ClientCompactionRequest
{
    /// <summary>Start of Harness's compaction instruction (dsh-compaction-basic, dsh 0.2.3).</summary>
    public const string HarnessInstructionStart =
        "You are now acting as a compaction engine for this AI coding assistant.";

    /// <summary>Room kept for the summary when the request names no max_tokens.</summary>
    public const double DefaultReplyShare = 0.125;

    /// <summary>The reply room is never more than this share of the Context.</summary>
    public const double MaxReplyShare = 0.5;

    /// <summary>
    /// Start of Cline's summarising system prompt (agentic compaction, @cline/core 0.2.1 in Cline
    /// 4.1.27). Cline sends it as the system prompt of a two-message request whose user message
    /// holds the conversation text to fold.
    /// </summary>
    public const string ClineSummarizerStart =
        "Summarize the provided coding session into a concise continuation note";

    /// <summary>
    /// The Client app whose own summary request this is (a <see cref="ClientAppDetector"/> name), or
    /// null for any other request. Matched on the app's fixed instruction, not on the sender's
    /// headers: a summary request often carries neither the app's system prompt nor its User-Agent.
    /// </summary>
    public static string? SummaryRequestClient(JsonObject? payload)
    {
        if (payload?["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return null;
        }

        if (messages[0] is JsonObject first
            && string.Equals(first["role"]?.ToString(), "system", StringComparison.OrdinalIgnoreCase)
            && TextOf(first["content"]).TrimStart().StartsWith(ClineSummarizerStart, StringComparison.Ordinal))
        {
            return ClientAppDetector.Cline;
        }

        return IsHarnessSummaryRequest(messages) ? ClientAppDetector.Harness : null;
    }

    /// <summary>AI-FluxMux's record as it is added to a Client app's summary request.</summary>
    public static string RecordForRequest(string record)
        => "\n\nAI-FluxMux kept the points below from the whole conversation, including parts that may be shortened or "
           + "left out above. Keep every one that is still true in your summary, with its exact values, names and numbers.\n"
           + record;

    /// <summary>
    /// The model's summary with AI-FluxMux's record after it, as the Client app receives it and
    /// keeps it as the chat's compaction.
    /// </summary>
    public static string SummaryWithRecord(string summary, string record)
        => summary.TrimEnd() + "\n\n---\n" + HandoverBrief.ClientRecordHeading + " (exact values; "
           + "where anything here conflicts with the summary above, the later message wins):\n" + record;

    /// <summary>True when the payload is a Client app's own summary request.</summary>
    public static bool IsSummaryRequest(JsonObject? payload) => SummaryRequestClient(payload) is not null;

    private static bool IsHarnessSummaryRequest(JsonArray messages)
    {
        if (messages[messages.Count - 1] is not JsonObject last
            || !string.Equals(last["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TextOf(last["content"]).TrimStart().StartsWith(HarnessInstructionStart, StringComparison.Ordinal);
    }

    /// <summary>Tokens to leave free for the summary: the request's max_tokens, within limits.</summary>
    public static int ReplyTokens(JsonObject? payload, int contextTokens)
    {
        if (contextTokens <= 0)
        {
            return 0;
        }

        var cap = (int)(contextTokens * MaxReplyShare);
        var asked = ReadInt(payload, "max_completion_tokens") ?? ReadInt(payload, "max_tokens");
        return asked is > 0
            ? Math.Min(asked.Value, cap)
            : (int)(contextTokens * DefaultReplyShare);
    }

    /// <summary>
    /// A checkpoint in Harness's own section layout, built from AI-FluxMux's session note. Used
    /// only when the summary request cannot be made to fit. Harness requires non-empty text.
    /// </summary>
    public static string BuildFallbackCheckpoint(string? essenceBlock)
    {
        var note = string.IsNullOrWhiteSpace(essenceBlock)
            ? "(none: AI-FluxMux had no session note for this chat)"
            : essenceBlock.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("## Primary Request and Intent");
        sb.AppendLine("- See Critical Context: the session note kept by AI-FluxMux.");
        sb.AppendLine();
        sb.AppendLine("## Key Technical Concepts");
        sb.AppendLine("- (none)");
        sb.AppendLine();
        sb.AppendLine("## Files and Code");
        sb.AppendLine("- (none)");
        sb.AppendLine();
        sb.AppendLine("## Errors and Fixes");
        sb.AppendLine("- The chat grew too large for the loaded model's Context; the older part was condensed to this note.");
        sb.AppendLine();
        sb.AppendLine("## Pending Jobs");
        sb.AppendLine("- (none)");
        sb.AppendLine();
        sb.AppendLine("## Current Work");
        sb.AppendLine("- Continue from the most recent messages that follow.");
        sb.AppendLine();
        sb.AppendLine("## Next Step");
        sb.AppendLine("- (none)");
        sb.AppendLine();
        sb.AppendLine("## Critical Context");
        sb.Append(note);
        return sb.ToString();
    }

    /// <summary>Appends text to the last message (the instruction), whatever its content shape.</summary>
    public static void AppendToInstruction(JsonObject payload, string text)
    {
        if (payload["messages"] is not JsonArray messages || messages.Count == 0
            || messages[messages.Count - 1] is not JsonObject last)
        {
            return;
        }

        switch (last["content"])
        {
            case JsonArray parts:
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                break;
            default:
                last["content"] = (last["content"]?.ToString() ?? string.Empty) + text;
                break;
        }
    }

    private static int? ReadInt(JsonObject? payload, string key)
    {
        try
        {
            return payload?[key] is JsonValue value && value.TryGetValue<int>(out var n) ? n : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string TextOf(JsonNode? content)
    {
        if (content is JsonArray parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonObject obj && obj["text"] is JsonNode text)
                {
                    sb.Append(text.ToString());
                }
            }

            return sb.ToString();
        }

        return content is JsonValue ? content.ToString() : string.Empty;
    }
}
