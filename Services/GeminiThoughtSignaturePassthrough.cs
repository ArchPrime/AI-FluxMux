using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class GeminiThoughtSignaturePassthrough
{
    public const string SkipValidator = "skip_thought_signature_validator";

    public static bool HasToolContinuation(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return false;
        }

        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            var role = (message["role"]?.ToString() ?? string.Empty).Trim();
            if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)
                || role.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message["tool_calls"] is JsonArray { Count: > 0 }
                || message["function_call"] is JsonObject)
            {
                return true;
            }

            if (message["parts"] is JsonArray parts)
            {
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (part["functionCall"] is JsonObject || part["function_call"] is JsonObject)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static int EnsureSkipSignaturesForMissingThoughts(JsonObject payload)
    {
        var patched = 0;
        if (payload["messages"] is not JsonArray messages)
        {
            return patched;
        }

        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            if (message["tool_calls"] is JsonArray calls)
            {
                foreach (var callNode in calls)
                {
                    if (callNode is JsonObject call)
                    {
                        patched += EnsureOnOpenAiToolCall(call);
                    }
                }
            }

            if (message["parts"] is JsonArray parts)
            {
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (part["functionCall"] is JsonObject || part["function_call"] is JsonObject)
                    {
                        patched += EnsureOnNativePart(part);
                    }
                }
            }
        }

        return patched;
    }

    private static int EnsureOnOpenAiToolCall(JsonObject call)
    {
        if (ReadSignature(call) is { Length: > 0 })
        {
            return 0;
        }

        var extra = call["extra_content"] as JsonObject ?? new JsonObject();
        var google = extra["google"] as JsonObject ?? new JsonObject();
        google["thought_signature"] = SkipValidator;
        extra["google"] = google;
        call["extra_content"] = extra;
        call["thought_signature"] = SkipValidator;
        return 1;
    }

    private static int EnsureOnNativePart(JsonObject part)
    {
        if (ReadSignature(part) is { Length: > 0 })
        {
            return 0;
        }

        part["thought_signature"] = SkipValidator;
        part["thoughtSignature"] = SkipValidator;
        return 1;
    }

    private static string? ReadSignature(JsonObject obj)
    {
        foreach (var key in new[] { "thought_signature", "thoughtSignature" })
        {
            var value = obj[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (obj["extra_content"] is JsonObject extra
            && extra["google"] is JsonObject google)
        {
            var nested = google["thought_signature"]?.ToString()
                         ?? google["thoughtSignature"]?.ToString();
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }
}
