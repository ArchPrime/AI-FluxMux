using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class LocalReasoningPayloadPolicy
{
    public static bool ApplyToPayload(JsonObject payload, string? reasoningMode)
    {
        var mode = LocalReasoningLaunchPolicy.NormalizeMode(reasoningMode);
        var changed = false;

        if (payload["messages"] is JsonArray messages && LocalReasoningLaunchPolicy.IsOff(mode))
        {
            var (stripped, strippedChanged) = LocalThinkingContentFilter.StripThinkingFromMessages(messages);
            if (strippedChanged)
            {
                payload["messages"] = stripped;
                changed = true;
            }
        }

        if (LocalReasoningLaunchPolicy.IsOn(mode))
        {
            var onKwargs = payload["chat_template_kwargs"] as JsonObject ?? new JsonObject();
            onKwargs["enable_thinking"] = true;
            payload["chat_template_kwargs"] = onKwargs;
            return changed;
        }

        if (LocalReasoningLaunchPolicy.IsAuto(mode))
        {
            payload.Remove("enable_thinking");
            return changed;
        }

        var offKwargs = payload["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        offKwargs["enable_thinking"] = false;
        payload["chat_template_kwargs"] = offKwargs;
        payload.Remove("reasoning");
        payload.Remove("reasoning_effort");
        payload.Remove("enable_thinking");
        payload.Remove("think");
        return changed;
    }
}
