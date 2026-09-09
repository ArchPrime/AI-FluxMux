using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

public static class LocalChatPayloadSignals
{
    private static readonly Regex ImageUrlInTextRegex = new(
        @"https?://[^\s""'<>]+?\.(?:png|jpe?g|gif|webp|bmp|svg)(?:\?[^\s""'<>]*)?|data:image/|!\[[^\]]*\]\(\s*https?://|<img\b[^>]*\bsrc\s*=\s*[""']https?://|encrypted-tbn\d*\.gstatic\.com|googleusercontent\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool PayloadHasImage(JsonObject payload)
    {
        if (payload["messages"] is JsonArray messages)
        {
            foreach (var msg in messages.OfType<JsonObject>())
            {
                if (NodeHasImage(msg["content"]))
                {
                    return true;
                }
            }
        }

        return NodeHasImage(payload["images"]);
    }

    public static bool LatestUserTurnHasImage(JsonObject payload)
    {
        if (NodeHasImage(payload["images"]))
        {
            return true;
        }

        var lastUser = LastUserMessage(payload);
        return lastUser is not null && NodeHasImage(lastUser["content"]);
    }

    public static bool StripImagesFromPayload(JsonObject payload)
    {
        var changed = false;
        if (payload["images"] is not null)
        {
            payload.Remove("images");
            changed = true;
        }

        if (payload["messages"] is not JsonArray messages)
        {
            return changed;
        }

        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (!NodeHasImage(msg["content"]))
            {
                continue;
            }

            msg["content"] = StripImagesFromNode(msg["content"]);
            changed = true;
        }

        return changed;
    }

    public static bool PayloadWantsThinking(JsonObject payload)
    {
        if (payload["chat_template_kwargs"] is JsonObject kwargs
            && kwargs["enable_thinking"] is JsonValue thinkFlag
            && thinkFlag.TryGetValue<bool>(out var enabled)
            && enabled)
        {
            return true;
        }

        if (payload["enable_thinking"] is JsonValue topThink
            && topThink.TryGetValue<bool>(out var topEnabled)
            && topEnabled)
        {
            return true;
        }

        return false;
    }

    public static bool PayloadHasToolCalls(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        foreach (var node in messages)
        {
            if (node is not JsonObject msg)
            {
                continue;
            }

            if (msg["tool_calls"] is JsonArray { Count: > 0 })
            {
                return true;
            }

            var role = Str(msg, "role");
            if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var content = msg["content"]?.ToString() ?? string.Empty;
            if (content.Contains("tool_result", StringComparison.OrdinalIgnoreCase)
                || content.Contains("<tool", StringComparison.OrdinalIgnoreCase)
                || content.Contains("[tool", StringComparison.OrdinalIgnoreCase)
                || content.Contains("tool_call", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool PayloadLooksLikePatchOrPlan(JsonObject payload)
    {
        var user = LastUserText(payload).ToLowerInvariant();
        if (user.Length == 0)
        {
            return false;
        }

        return user.Contains("```diff")
            || user.Contains("@@ ")
            || user.Contains("run_command")
            || user.Contains("apply_patch")
            || user.Contains("write_to_file")
            || user.Contains("replace_in_file")
            || user.Contains("search_replace")
            || user.Contains("--- a/")
            || user.Contains("+++ b/");
    }

    public static string LastUserText(JsonObject payload)
    {
        var lastUser = LastUserMessage(payload);
        if (lastUser is not null)
        {
            return lastUser["content"]?.ToString() ?? string.Empty;
        }

        return payload["prompt"]?.ToString() ?? string.Empty;
    }

    private static JsonObject? LastUserMessage(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return null;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject msg)
            {
                continue;
            }

            if (Str(msg, "role").Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return msg;
            }
        }

        return null;
    }

    private static JsonNode? StripImagesFromNode(JsonNode? node)
    {
        if (node is JsonArray items)
        {
            var kept = new JsonArray();
            foreach (var item in items)
            {
                if (item is JsonObject part && PartIsImage(part))
                {
                    continue;
                }

                kept.Add(item?.DeepClone());
            }

            if (kept.Count == 0)
            {
                kept.Add(new JsonObject { ["type"] = "text", ["text"] = "[picture omitted]" });
            }

            return kept;
        }

        if (node is JsonObject obj && PartIsImage(obj))
        {
            return "[picture omitted]";
        }

        if (node is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            if (ImageUrlInTextRegex.IsMatch(text))
            {
                return ImageUrlInTextRegex.Replace(text, "[picture omitted]");
            }
        }

        return node?.DeepClone();
    }

    private static bool PartIsImage(JsonObject part)
    {
        var type = Str(part, "type").ToLowerInvariant();
        if (type.Contains("image")
            || part["image_url"] is not null
            || part["image"] is not null
            || part["input_image"] is not null)
        {
            return true;
        }

        if (part["source"] is JsonObject source
            && (source["data"] is not null
                || source["url"] is not null
                || Str(source, "media_type").Contains("image", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool NodeHasImage(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            var text = value.ToString() ?? string.Empty;
            return ImageUrlInTextRegex.IsMatch(text);
        }

        if (node is JsonObject part)
        {
            var type = Str(part, "type").ToLowerInvariant();
            if (type.Contains("image") || part["image_url"] is not null || part["image"] is not null || part["input_image"] is not null)
            {
                return true;
            }

            if (part["source"] is JsonObject source
                && (source["data"] is not null
                    || source["url"] is not null
                    || Str(source, "media_type").Contains("image", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (NodeHasImage(part["image_url"]) || NodeHasImage(part["url"]) || NodeHasImage(part["text"]) || NodeHasImage(part["content"]) || NodeHasImage(part["source"]))
            {
                return true;
            }
        }

        if (node is JsonArray items)
        {
            foreach (var item in items)
            {
                if (NodeHasImage(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Str(JsonObject obj, string key, string fallback = "")
        => obj[key]?.ToString() ?? fallback;
}
