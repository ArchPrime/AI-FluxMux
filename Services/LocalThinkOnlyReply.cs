using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Reasoning Off strips thinking before it reaches the Client app. If that was
/// the whole reply, Cline would otherwise see an empty 200 and treat the task
/// as finished.
/// </summary>
public static class LocalThinkOnlyReply
{
    public const string Type = "local_think_only";
    public const string Message =
        PortRulesPostMortem.ChatTurnCannotContinue
        + "llama-server only thought; no reply or tool call reached the Client app.";

    public static string FormatClientMessage(JsonObject? state = null)
        => FluxMuxGatewayRouting.FormatThinkOnlyMessage(state);

    public static bool IsOffMode(string? reasoningMode)
        => LocalReasoningLaunchPolicy.IsOff(reasoningMode);

    public static bool HasNoClientWork(JsonObject? message)
    {
        if (message is null)
        {
            return true;
        }

        if (message["tool_calls"] is JsonArray { Count: > 0 })
        {
            return false;
        }

        if (message["function_call"] is JsonObject)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(VisibleText(message["content"]));
    }

    public static bool CompletionIsThinkOnly(JsonObject? root, string? reasoningMode, bool strippedThinking)
    {
        if (!strippedThinking || !IsOffMode(reasoningMode) || root?["choices"] is not JsonArray choices)
        {
            return false;
        }

        var anyChoice = false;
        foreach (var node in choices.OfType<JsonObject>())
        {
            anyChoice = true;
            if (!HasNoClientWork(node["message"] as JsonObject)
                || !HasNoClientWork(node["delta"] as JsonObject))
            {
                return false;
            }
        }

        return anyChoice;
    }

    public static bool ApplyNoticeToFinishedChoice(JsonObject choice, string notice)
    {
        var reason = choice["finish_reason"]?.ToString();
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (choice["delta"] is JsonObject delta)
        {
            delta["content"] = ControlLabelMarkup.ForClientApp(notice);
            return true;
        }

        if (choice["message"] is JsonObject message)
        {
            message["content"] = ControlLabelMarkup.ForClientApp(notice);
            return true;
        }

        choice["delta"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = ControlLabelMarkup.ForClientApp(notice)
        };
        return true;
    }

    private static string VisibleText(JsonNode? content)
    {
        if (content is JsonValue value)
        {
            return value.ToString() ?? string.Empty;
        }

        if (content is JsonArray parts)
        {
            return string.Join(
                "\n",
                parts.OfType<JsonObject>()
                    .Select(part => part["text"]?.ToString())
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return string.Empty;
    }
}
