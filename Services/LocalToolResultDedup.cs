using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Replaces a tool result that is identical to an earlier one still shown in full in the
/// forwarded chat with a one-line pointer to it. Models re-read files they already have; in two
/// long Cline tasks the repeats were 7.4 % and 3.4 % of all tool-result text. Matching is on the
/// result text itself, so a file changed in between (by the editor or a shell command) never
/// matches.
///
/// Prompt-cache safe: the later copy is always the one replaced, and the same way on every turn
/// (the Client app resends the full chat, so the rewrite is recomputed identically), so the
/// forwarded prompt keeps the same start from turn to turn. Run it last, on the pack that is
/// actually sent — after Omit and compaction — so the earlier copy it points at is certain to
/// be there. If that copy was stubbed or compacted away, its text no longer matches and the
/// later copy stays in full.
/// </summary>
public static class LocalToolResultDedup
{
    /// <summary>Results shorter than this are left alone: the pointer would save little, and short
    /// outputs ("Build succeeded", an empty listing) legitimately repeat.</summary>
    public const int MinChars = 1_500;

    public const string Pointer =
        "[AI-FluxMux: this result is identical to the result of an earlier tool call in this chat, "
        + "which is still shown in full above, so it is not repeated here. Nothing in it has changed since then.]";

    /// <summary>Returns how many results were replaced.</summary>
    public static int Apply(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return 0;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var replaced = 0;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (!LocalToolResultClearing.IsToolResult(message)
                || !TryPlainText(message["content"], out var text)
                || text.Length < MinChars)
            {
                continue;
            }

            if (seen.Add(text))
            {
                continue;
            }

            message["content"] = message["content"] is JsonArray
                ? new JsonArray { new JsonObject { ["type"] = "text", ["text"] = Pointer } }
                : Pointer;
            replaced++;
        }

        return replaced;
    }

    // Text only: a result that carries a picture or any other non-text part is never replaced.
    private static bool TryPlainText(JsonNode? content, out string text)
    {
        text = string.Empty;
        switch (content)
        {
            case JsonValue value when value.TryGetValue<string>(out var s):
                text = s;
                return true;
            case JsonArray parts:
                var sb = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part is not JsonObject obj
                        || !string.Equals(obj["type"]?.ToString(), "text", StringComparison.Ordinal)
                        || obj["text"] is not JsonValue partText
                        || !partText.TryGetValue<string>(out var piece))
                    {
                        return false;
                    }

                    sb.Append(piece).Append('\n');
                }

                text = sb.ToString();
                return true;
            default:
                return false;
        }
    }
}
