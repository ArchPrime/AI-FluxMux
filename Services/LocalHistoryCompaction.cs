using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class LocalHistoryCompaction
{
    public const double HighWatermark = 0.85;
    public const int KeepTurns = 8;
    public const int ToolKeepTurns = 16;
    public const int Headroom = 256;
    public const int PreservedUserChars = 8000;

    public static bool IsNearLimit(int promptTokens, int contextTokens)
        => IsNearLimit(promptTokens, contextTokens, PortForwardingRules.Defaults);

    public static bool IsNearLimit(int promptTokens, int contextTokens, PortForwardingRules rules)
    {
        if (contextTokens <= 0 || promptTokens <= 0)
        {
            return false;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var watermark = (int)Math.Floor(contextTokens * live.CompactWatermark);
        if (promptTokens < watermark)
        {
            return false;
        }

        return promptTokens + 1 + live.CompactHeadroom <= contextTokens;
    }

    public static bool ShouldForwardCompact(
        bool compactEnabled,
        int promptTokens,
        int contextTokens,
        bool promptExceeds,
        bool fillingEstimate = false)
        => ShouldForwardCompact(
            compactEnabled,
            promptTokens,
            contextTokens,
            promptExceeds,
            fillingEstimate,
            PortForwardingRules.Defaults);

    public static bool ShouldForwardCompact(
        bool compactEnabled,
        int promptTokens,
        int contextTokens,
        bool promptExceeds,
        bool fillingEstimate,
        PortForwardingRules rules)
    {
        if (!compactEnabled)
        {
            return false;
        }

        // Filling uses the tighter 2-chars/token estimate. Compact used to wait
        // for 85% of the 4-chars/token count, so Cline/Harness 400'd first.
        return promptExceeds || fillingEstimate || IsNearLimit(promptTokens, contextTokens, rules);
    }

    public static bool IsDestinationTight(int destinationContext, int turnTokens)
        => IsDestinationTight(destinationContext, turnTokens, PortForwardingRules.Defaults);

    public static bool IsDestinationTight(int destinationContext, int turnTokens, PortForwardingRules rules)
    {
        if (destinationContext <= 0 || turnTokens <= 0)
        {
            return false;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        return destinationContext < turnTokens + live.CompactHeadroom;
    }

    public static bool TryCompactPayload(JsonObject payload, int keepTurns = KeepTurns, bool force = false)
        => TryCompactPayload(payload, PortForwardingRules.Defaults with { CompactKeepTurns = keepTurns }, force);

    public static bool TryCompactPayload(JsonObject payload, PortForwardingRules rules, bool force = false)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var compacted = CompactMessages(messages, rules, force);
        if (compacted is null)
        {
            return false;
        }

        payload["messages"] = compacted;
        return true;
    }

    public static JsonArray? CompactMessages(JsonArray messages, int keepTurns = KeepTurns, bool force = false)
        => CompactMessages(messages, PortForwardingRules.Defaults with { CompactKeepTurns = keepTurns }, force);

    public static JsonArray? CompactMessages(JsonArray messages, PortForwardingRules rules, bool force = false)
    {
        if (messages.Count < 4)
        {
            return null;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var keepTurns = live.CompactKeepTurns;
        var toolHistory = HasToolHistory(messages);
        keepTurns = Math.Clamp(toolHistory ? Math.Max(keepTurns, live.CompactToolKeepTurns) : keepTurns, 6, 24);
        keepTurns = Math.Min(keepTurns, Math.Max(live.CompactKeepTurns, messages.Count / 2));
        var conversational = 0;
        foreach (var node in messages.OfType<JsonObject>())
        {
            var role = RoleOf(node);
            if (role is "user" or "assistant")
            {
                conversational++;
            }
        }

        if (!force && (messages.Count < 10 || conversational < 6))
        {
            return null;
        }

        var split = Math.Min(keepTurns, Math.Max(1, messages.Count - 1));
        var keepStart = AlignKeepStart(messages, messages.Count - split);
        if (keepStart < 1)
        {
            return null;
        }

        var lastRealUserIndex = IndexOfLastRealUser(messages);
        JsonObject? preservedUser = null;
        if (lastRealUserIndex >= 0 && lastRealUserIndex < keepStart)
        {
            preservedUser = PreserveUserTask(messages[lastRealUserIndex] as JsonObject, live.CompactPreservedUserChars);
        }

        var summaryText = BuildForwardSummary(messages, keepStart);
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            return null;
        }

        if (!force && summaryText.Length < 180)
        {
            return null;
        }

        var compacted = new JsonArray();
        foreach (var node in messages.OfType<JsonObject>())
        {
            if (RoleOf(node) == "system")
            {
                compacted.Add(node.DeepClone());
            }
        }

        compacted.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = summaryText
        });

        if (preservedUser is not null)
        {
            compacted.Add(preservedUser);
        }

        for (var i = keepStart; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject tail)
            {
                continue;
            }

            if (RoleOf(tail) == "system")
            {
                continue;
            }

            compacted.Add(tail.DeepClone());
        }

        if (compacted.Count >= messages.Count)
        {
            return null;
        }

        return compacted;
    }

    private static bool HasToolHistory(JsonArray messages)
    {
        foreach (var node in messages.OfType<JsonObject>())
        {
            if (IsToolFollowUp(node) || node["tool_calls"] is JsonArray { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    private static int AlignKeepStart(JsonArray messages, int keepStart)
    {
        keepStart = Math.Clamp(keepStart, 1, Math.Max(1, messages.Count - 1));
        while (keepStart > 1 && IsToolFollowUp(messages[keepStart] as JsonObject))
        {
            keepStart--;
        }

        while (keepStart < messages.Count - 1 && IsToolFollowUp(messages[keepStart] as JsonObject))
        {
            keepStart++;
        }

        return keepStart;
    }

    private static int IndexOfLastRealUser(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject message && IsRealUserTask(message))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? BuildForwardSummary(JsonArray messages, int keepStart)
    {
        var lines = new List<string>
        {
            "Compressed prior conversation context (older turns):"
        };
        var alreadyRan = LocalToolResultClearing.CollectAlreadyRanLines(messages, keepStart);
        if (alreadyRan.Count > 0)
        {
            lines.Add(LocalToolResultClearing.AlreadyRanHeader);
            lines.AddRange(alreadyRan);
        }

        // A tool ledger already names dropped commands. Do not also paste
        // truncated file bodies — that is how Compact taught the model to
        // re-run the same read/shell call.
        if (alreadyRan.Count == 0)
        {
            var notes = CollectDroppedNotes(messages, keepStart);
            if (notes.Count > 0)
            {
                lines.Add("Notes:");
                lines.AddRange(notes);
            }
        }

        if (lines.Count <= 1)
        {
            return null;
        }

        var summaryText = string.Join("\n", lines);
        return summaryText.Length > 5000 ? summaryText[..5000] : summaryText;
    }

    private static List<string> CollectDroppedNotes(JsonArray messages, int keepStart)
    {
        var notes = new List<string>();
        for (var i = 0; i < keepStart && notes.Count < 8; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            if (!IsRealUserTask(message))
            {
                continue;
            }

            var raw = ExtractText(message["content"]);
            var text = CollapseWhitespace(raw);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Length > 120)
            {
                text = text[..120];
            }

            notes.Add("- user: " + text);
        }

        return notes;
    }

    private static string CollapseWhitespace(string text)
        => string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static JsonObject? PreserveUserTask(JsonObject? message, int preservedUserChars = PreservedUserChars)
    {
        if (message is null || !IsRealUserTask(message))
        {
            return null;
        }

        var clone = message.DeepClone() as JsonObject;
        if (clone is null)
        {
            return null;
        }

        var keepChars = preservedUserChars > 0 ? preservedUserChars : PreservedUserChars;
        if (clone["content"] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.Length > keepChars)
        {
            clone["content"] = text[..keepChars];
        }

        return clone;
    }

    private static bool IsRealUserTask(JsonObject message)
    {
        if (RoleOf(message) != "user" || IsToolFollowUp(message))
        {
            return false;
        }

        var text = ExtractText(message["content"]);
        return !text.StartsWith("Compressed prior conversation context", StringComparison.Ordinal);
    }

    private static bool IsToolFollowUp(JsonObject? message)
    {
        if (message is null)
        {
            return false;
        }

        var role = RoleOf(message);
        if (role is "tool" or "function")
        {
            return true;
        }

        if (role != "user")
        {
            return false;
        }

        var text = ExtractText(message["content"]).TrimStart();
        return text.StartsWith("<tool_response>", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool result", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool output", StringComparison.OrdinalIgnoreCase);
    }

    private static string RoleOf(JsonObject message)
        => (message["role"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

    private static string ExtractText(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value)
        {
            return value.ToString() ?? string.Empty;
        }

        if (content is JsonArray parts)
        {
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue textValue)
                {
                    builder.Append(textValue);
                    continue;
                }

                if (part is not JsonObject obj)
                {
                    continue;
                }

                var text = obj["text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Append(text);
                    builder.Append(' ');
                }
            }

            return builder.ToString().Trim();
        }

        if (content is JsonObject objContent)
        {
            return objContent["text"]?.ToString() ?? objContent.ToJsonString();
        }

        return content.ToString() ?? string.Empty;
    }
}
