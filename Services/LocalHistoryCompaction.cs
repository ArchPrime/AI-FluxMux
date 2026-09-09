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
    public const int Headroom = 256;

    public static bool IsNearLimit(int promptTokens, int contextTokens)
    {
        if (contextTokens <= 0 || promptTokens <= 0)
        {
            return false;
        }

        var watermark = (int)Math.Floor(contextTokens * HighWatermark);
        if (promptTokens < watermark)
        {
            return false;
        }

        return promptTokens + 1 + Headroom <= contextTokens;
    }

    public static bool ShouldForwardCompact(
        bool compactEnabled,
        int promptTokens,
        int contextTokens,
        bool promptExceeds)
    {
        if (!compactEnabled)
        {
            return false;
        }

        return promptExceeds || IsNearLimit(promptTokens, contextTokens);
    }

    public static bool IsDestinationTight(int destinationContext, int turnTokens)
    {
        if (destinationContext <= 0 || turnTokens <= 0)
        {
            return false;
        }

        return destinationContext < turnTokens + Headroom;
    }

    public static bool TryCompactPayload(JsonObject payload, int keepTurns = KeepTurns, bool force = false)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        var compacted = CompactMessages(messages, keepTurns, force);
        if (compacted is null)
        {
            return false;
        }

        payload["messages"] = compacted;
        return true;
    }

    public static JsonArray? CompactMessages(JsonArray messages, int keepTurns = KeepTurns, bool force = false)
    {
        if (messages.Count < 4)
        {
            return null;
        }

        keepTurns = Math.Clamp(keepTurns, 6, 16);
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
        var headCount = messages.Count - split;
        if (headCount < 1)
        {
            return null;
        }

        var summaryLines = new List<string>();
        for (var i = 0; i < headCount; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            var role = RoleOf(message);
            if (role is "system" or "")
            {
                continue;
            }

            var text = ExtractText(message["content"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (compact.Length > 220)
            {
                compact = compact[..220];
            }

            if (compact.Length > 0)
            {
                summaryLines.Add(role + ": " + compact);
            }
        }

        if (summaryLines.Count == 0)
        {
            return null;
        }

        var combined = string.Join("\n", summaryLines);
        if (!force && combined.Length < 1800)
        {
            return null;
        }

        var keptLines = summaryLines.Count <= 24 ? summaryLines : summaryLines.GetRange(summaryLines.Count - 24, 24);
        var summaryText = "Compressed prior conversation context (older turns):\n" + string.Join("\n", keptLines);
        if (summaryText.Length > 5000)
        {
            summaryText = summaryText[..5000];
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

        for (var i = headCount; i < messages.Count; i++)
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
