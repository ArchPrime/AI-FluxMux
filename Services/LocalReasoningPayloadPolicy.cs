using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class LocalReasoningPayloadPolicy
{
    public static bool ApplyToPayload(JsonObject payload, string? reasoningMode, Func<bool>? userClientReinflatesContext = null)
    {
        var mode = LocalReasoningLaunchPolicy.NormalizeMode(reasoningMode);
        var changed = false;

        // If the user's client reinflates the context (forwards the full chat each turn, e.g. Cline),
        // skip thinking stripping from the messages. Stripping would modify the prompt and prevent
        // KV cache reuse. However, we still apply the reasoning mode settings (enable_thinking,
        // thinking_budget, etc.) so the model knows whether to generate thinking.
        var skipThinkingStrip = userClientReinflatesContext?.Invoke() == true;

        if (!skipThinkingStrip && payload["messages"] is JsonArray messages && LocalReasoningLaunchPolicy.IsOff(mode))
        {
            var (stripped, strippedChanged) = LocalThinkingContentFilter.StripThinkingFromMessages(messages);
            if (strippedChanged)
            {
                payload["messages"] = stripped;
                changed = true;
            }
        }

        if (LocalReasoningLaunchPolicy.IsThinkingEnabled(mode))
        {
            var onKwargs = payload["chat_template_kwargs"] as JsonObject ?? new JsonObject();
            onKwargs["enable_thinking"] = true;
            // Qwen 3.8 reads effort from the template kwargs. Top-level
            // reasoning_effort is ignored, so Low/Medium were falling through
            // to the card default (xhigh).
            var effort = LocalReasoningRequestPolicy.Effort(mode)
                ?? (LocalReasoningLaunchPolicy.IsOn(mode) ? "medium" : null);
            if (string.IsNullOrWhiteSpace(effort))
            {
                onKwargs.Remove("reasoning_effort");
                payload.Remove("reasoning_effort");
            }
            else
            {
                onKwargs["reasoning_effort"] = effort;
                payload["reasoning_effort"] = effort;
            }

            payload["chat_template_kwargs"] = onKwargs;
            return changed;
        }

        if (LocalReasoningLaunchPolicy.IsAuto(mode))
        {
            payload.Remove("enable_thinking");
            payload.Remove("thinking_budget");
            return changed;
        }

        var offKwargs = payload["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        offKwargs["enable_thinking"] = false;
        offKwargs["thinking_budget"] = 0;
        payload["chat_template_kwargs"] = offKwargs;
        payload["thinking_budget"] = 0;
        payload.Remove("reasoning");
        payload.Remove("reasoning_effort");
        payload.Remove("enable_thinking");
        payload.Remove("think");
        return changed;
    }

    public static void ApplyThinkingBudget(JsonObject payload, string? reasoningMode)
    {
        var maxTokens = 0;
        if (payload["max_tokens"] is not null)
        {
            int.TryParse(
                payload["max_tokens"]?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out maxTokens);
        }

        var budget = LocalReasoningRequestPolicy.ThinkingBudget(reasoningMode, maxTokens);
        var kwargs = payload["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        if (budget is null)
        {
            kwargs.Remove("thinking_budget");
            payload.Remove("thinking_budget");
        }
        else
        {
            kwargs["thinking_budget"] = budget.Value;
            payload["thinking_budget"] = budget.Value;
        }

        if (kwargs.Count > 0)
        {
            payload["chat_template_kwargs"] = kwargs;
        }
        else
        {
            payload.Remove("chat_template_kwargs");
        }
    }
}
