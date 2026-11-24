using System;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Recognises a Client app's own "summarise this chat" request and helps it succeed.
///
/// Harness compacts a chat (on its own after an overflow error, or when the user types
/// /compact) by sending the old part of the chat to the model with a fixed instruction as the
/// last user message, then swapping the reply in as a checkpoint — live, in the open chat. That
/// request is nearly as large as the chat, so it overflowed too and the chat stayed stuck. The
/// gateway now shortens it to fit, or, when even that fails, answers it with AI-FluxMux's own
/// session note in Harness's checkpoint layout, so Harness's compaction always completes.
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

    /// <summary>True when the payload's last message is Harness's compaction instruction.</summary>
    public static bool IsSummaryRequest(JsonObject? payload)
    {
        if (payload?["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return false;
        }

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
