using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Turns HTTP error bodies into operator-facing text for Diagnostics and Health.
/// Avoids dumping raw JSON tails (which hide the message and show escape codes).
/// </summary>
public static class HttpErrorResponseFormatter
{
    public static string FormatHttpErrorDetail(int statusCode, string? reasonPhrase, string body, int maxLength = 480)
    {
        var prefix = "HTTP " + statusCode;
        if (!string.IsNullOrWhiteSpace(reasonPhrase))
        {
            prefix += " " + reasonPhrase.Trim();
        }

        var message = TryExtractErrorMessage(body);
        if (!string.IsNullOrWhiteSpace(message))
        {
            var hint = TryExtractErrorHint(body);
            var detail = string.IsNullOrWhiteSpace(hint)
                ? message.Trim()
                : message.Trim() + " " + hint.Trim();
            return TruncateHead(prefix + ". " + detail, maxLength);
        }

        var fallback = StripJsonNoise(body);
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return prefix + ".";
        }

        return TruncateHead(prefix + ". " + fallback.Trim(), maxLength);
    }

    private static string? TryExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
            {
                return null;
            }

            if (root["error"] is JsonValue errorText)
            {
                var asString = errorText.ToString();
                if (!string.IsNullOrWhiteSpace(asString))
                {
                    return asString.Trim();
                }
            }

            if (root["error"] is JsonObject errorObj)
            {
                var nested = ReadJsonString(errorObj, "message");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }

            var rootMessage = ReadJsonString(root, "message");
            if (!string.IsNullOrWhiteSpace(rootMessage))
            {
                return rootMessage;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryExtractErrorHint(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject errorObj)
            {
                return ReadJsonString(errorObj, "hint");
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ReadJsonString(JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue val)
        {
            try
            {
                return val.GetValue<string>()?.Trim() ?? string.Empty;
            }
            catch
            {
                return node.ToString().Trim().Trim('"');
            }
        }

        return node.ToString().Trim().Trim('"');
    }

    private static string StripJsonNoise(string body)
    {
        var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            return string.Empty;
        }

        return text;
    }

    private static string TruncateHead(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= max)
        {
            return text;
        }

        return text.Substring(0, max - 3) + "...";
    }
}
