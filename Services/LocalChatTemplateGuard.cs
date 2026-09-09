using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Qwen 3.5/3.8 Jinja raises when a tool loop has no remaining real user query
/// (only system, assistant, tool, or user rows wrapped as tool_response).
/// </summary>
public static class LocalChatTemplateGuard
{
    public const string SyntheticUserText = "Continue.";

    public static int EnsureUserQuery(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return 0;
        }

        if (HasRealUserQuery(messages))
        {
            return 0;
        }

        var insertAt = 0;
        if (messages[0] is JsonObject first
            && Role(first) is "system" or "developer")
        {
            insertAt = 1;
        }

        messages.Insert(insertAt, new JsonObject
        {
            ["role"] = "user",
            ["content"] = SyntheticUserText
        });
        return 1;
    }

    public static bool HasRealUserQuery(JsonArray messages)
    {
        foreach (var node in messages)
        {
            if (node is not JsonObject msg)
            {
                continue;
            }

            if (Role(msg) != "user")
            {
                continue;
            }

            if (!IsToolResponseWrapper(UserText(msg)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsToolResponseWrapper(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<tool_response>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Tool result", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Tool output", StringComparison.OrdinalIgnoreCase);
    }

    private static string Role(JsonObject msg)
        => msg["role"]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string UserText(JsonObject msg)
    {
        var content = msg["content"];
        if (content is JsonValue)
        {
            return content.ToString() ?? string.Empty;
        }

        if (content is not JsonArray parts)
        {
            return content?.ToString() ?? string.Empty;
        }

        var texts = new System.Collections.Generic.List<string>();
        foreach (var part in parts)
        {
            if (part is JsonValue)
            {
                texts.Add(part.ToString() ?? string.Empty);
            }
            else if (part is JsonObject block)
            {
                var text = block["text"]?.ToString() ?? block["content"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }
            }
        }

        return string.Join("\n", texts);
    }
}
