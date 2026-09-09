using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Removes hidden reasoning from chat payloads when the loaded local profile has Reasoning Off.
/// </summary>
public static class LocalThinkingContentFilter
{
    private static readonly Regex[] ThinkBlockPatterns =
    [
        new(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("\x3cthink\x3e" + @"[\s\S]*?" + "\x3c/think\x3e", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<thinking>[\s\S]*?</thinking>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<think_off\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<think_on\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex[] EmptyThinkBlockPatterns =
    [
        new(@"<think>\s*</think>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("\x3cthink\x3e" + @"\s*" + "\x3c/think\x3e", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<thinking>\s*</thinking>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static string StripThinkingFromText(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var result = content;
        foreach (var pattern in ThinkBlockPatterns)
        {
            result = pattern.Replace(result, string.Empty);
        }

        return CollapseExtraBlankLines(result);
    }

    public static string StripEmptyThinkingFromText(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var result = content;
        foreach (var pattern in EmptyThinkBlockPatterns)
        {
            result = pattern.Replace(result, string.Empty);
        }

        return CollapseExtraBlankLines(result);
    }

    private static string CollapseExtraBlankLines(string text)
    {
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        while (text.Contains("\r\n\r\n\r\n", StringComparison.Ordinal))
        {
            text = text.Replace("\r\n\r\n\r\n", "\r\n\r\n", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    public static bool StripThinkingFromMessage(JsonObject? message)
    {
        if (message is null)
        {
            return false;
        }

        var changed = false;
        if (message.ContainsKey("reasoning_content"))
        {
            message.Remove("reasoning_content");
            changed = true;
        }

        if (message.ContainsKey("reasoning"))
        {
            message.Remove("reasoning");
            changed = true;
        }

        if (message["content"] is JsonValue contentValue
            && contentValue.TryGetValue<string>(out var content)
            && content.Length > 0)
        {
            var stripped = StripThinkingFromText(content);
            if (!string.Equals(stripped, content, StringComparison.Ordinal))
            {
                message["content"] = stripped;
                changed = true;
            }
        }
        else if (message["content"] is JsonArray parts)
        {
            var partChanged = false;
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (part["text"] is JsonValue textValue
                    && textValue.TryGetValue<string>(out var text)
                    && text.Length > 0)
                {
                    var stripped = StripThinkingFromText(text);
                    if (!string.Equals(stripped, text, StringComparison.Ordinal))
                    {
                        part["text"] = stripped;
                        partChanged = true;
                    }
                }
            }

            if (partChanged)
            {
                changed = true;
            }
        }

        return changed;
    }

    public static bool StripThinkingFromCompletion(JsonObject root)
    {
        if (root["choices"] is not JsonArray choices)
        {
            return false;
        }

        var changed = false;
        foreach (var choiceNode in choices)
        {
            if (choiceNode is not JsonObject choice)
            {
                continue;
            }

            if (StripThinkingFromMessage(choice["delta"] as JsonObject))
            {
                changed = true;
            }

            if (StripThinkingFromMessage(choice["message"] as JsonObject))
            {
                changed = true;
            }
        }

        return changed;
    }

    public static bool StripEmptyThinkingFromCompletion(JsonObject root)
    {
        if (root["choices"] is not JsonArray choices)
        {
            return false;
        }

        var changed = false;
        foreach (var choiceNode in choices)
        {
            if (choiceNode is not JsonObject choice)
            {
                continue;
            }

            if (StripEmptyThinkingFromMessage(choice["delta"] as JsonObject))
            {
                changed = true;
            }

            if (StripEmptyThinkingFromMessage(choice["message"] as JsonObject))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static bool StripEmptyThinkingFromMessage(JsonObject? message)
    {
        if (message is null)
        {
            return false;
        }

        var changed = false;
        if (message["reasoning_content"] is JsonValue reasoningValue
            && reasoningValue.TryGetValue<string>(out var reasoning)
            && string.IsNullOrWhiteSpace(reasoning))
        {
            message.Remove("reasoning_content");
            changed = true;
        }

        if (message["reasoning"] is JsonValue legacyReasoning
            && legacyReasoning.TryGetValue<string>(out var legacy)
            && string.IsNullOrWhiteSpace(legacy))
        {
            message.Remove("reasoning");
            changed = true;
        }

        if (message["content"] is JsonValue contentValue
            && contentValue.TryGetValue<string>(out var content)
            && content.Length > 0)
        {
            var stripped = StripEmptyThinkingFromText(content);
            if (!string.Equals(stripped, content, StringComparison.Ordinal))
            {
                message["content"] = stripped;
                changed = true;
            }
        }
        else if (message["content"] is JsonArray parts)
        {
            var partChanged = false;
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (part["text"] is JsonValue textValue
                    && textValue.TryGetValue<string>(out var text)
                    && text.Length > 0)
                {
                    var stripped = StripEmptyThinkingFromText(text);
                    if (!string.Equals(stripped, text, StringComparison.Ordinal))
                    {
                        part["text"] = stripped;
                        partChanged = true;
                    }
                }
            }

            if (partChanged)
            {
                changed = true;
            }
        }

        return changed;
    }

    public static (JsonArray Messages, bool Changed) StripThinkingFromMessages(JsonArray messages)
    {
        var changed = false;
        var result = new JsonArray();
        foreach (var node in messages)
        {
            if (node is JsonObject message)
            {
                var clone = message.DeepClone() as JsonObject ?? new JsonObject();
                if (StripThinkingFromMessage(clone))
                {
                    changed = true;
                }

                result.Add(clone);
            }
            else
            {
                result.Add(node?.DeepClone());
            }
        }

        return (result, changed);
    }
}
