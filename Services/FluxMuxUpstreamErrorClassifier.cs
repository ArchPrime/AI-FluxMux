using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Maps upstream HTTP failures to FluxMux error types (including cloud breaker reasons).
/// </summary>
public static class FluxMuxUpstreamErrorClassifier
{
    public static (string Message, string ErrorType, int Status, string Hint) Classify(
        int status,
        string detail,
        string provider,
        string model,
        string exStr = "")
    {
        var blob = $"{detail} {exStr}".ToLowerInvariant();
        var providerName = string.IsNullOrWhiteSpace(provider) ? "the selected route" : provider;
        var modelName = string.IsNullOrWhiteSpace(model) ? "the selected model" : model;

        if (blob.Contains("exceed_context_size_error") || blob.Contains("exceeds the available context size"))
        {
            return (
                FluxMuxGatewayRouting.LocalContextOverflowMessage,
                "exceed_context_size_error",
                status == 0 ? 400 : status,
                string.Empty);
        }

        if ((blob.Contains("template") && blob.Contains("parser"))
            || blob.Contains("system message must be at the beginning")
            || blob.Contains("no user query found in messages"))
        {
            return (
                "The local model rejected this chat's message order. Start a fresh chat, or try a different model.",
                "template_parser_error",
                status == 0 ? 400 : status,
                string.Empty);
        }

        if (blob.Contains("loading model") || blob.Contains("model is loading"))
        {
            return (
                "The local model is still loading. Wait, then retry.",
                "unavailable_error",
                503,
                string.Empty);
        }

        if (blob.Contains("out of memory") || blob.Contains("cuda_error_out_of_memory") || blob.Contains("failed to allocate") || blob.Contains("ggml_gallocr"))
        {
            return (
                "The local model ran out of memory. Choose a smaller Context or a smaller model, then retry in a fresh chat.",
                "insufficient_memory",
                status is 0 or 500 or 502 ? 507 : status,
                string.Empty);
        }

        if (blob.Contains("model_not_supported") || blob.Contains("requested model is not supported") || blob.Contains("not available for integrator"))
        {
            return (
                $"{providerName} did not accept this model. Choose a different model, then retry.",
                "model_not_supported",
                status == 0 ? 400 : status,
                string.Empty);
        }

        if (blob.Contains("invalid_api_key") || blob.Contains("incorrect api key") || blob.Contains("unauthenticated") || status == 401)
        {
            return (
                $"Authentication failed for {providerName}. Check the API key in AI-FluxMux, then retry.",
                "auth_failed",
                401,
                string.Empty);
        }

        // Monthly / plan token exhaustion often arrives as HTTP 429 with code rate_limited
        // (GitHub Copilot: "exceeded your Copilot token usage"). Treat as quota, not a short pause.
        if (LooksLikeQuotaExhaustion(blob, status))
        {
            return (
                $"{providerName} quota looks exhausted. Choose a different model, then retry.",
                "quota_exhausted",
                status is 402 or 403 or 429 ? status : 403,
                string.Empty);
        }

        if (status == 429
            || blob.Contains("rate limit")
            || blob.Contains("rate_limited")
            || blob.Contains("too many requests"))
        {
            return (
                $"{providerName} is rate-limited. Wait a moment, then retry.",
                "rate_limited",
                429,
                string.Empty);
        }

        if (status == 403 || blob.Contains("quota") || blob.Contains("billing") || blob.Contains("insufficient_quota"))
        {
            return (
                $"{providerName} quota looks exhausted. Choose a different model, then retry.",
                "quota_exhausted",
                403,
                string.Empty);
        }

        if (status == 404 || blob.Contains("model_not_found") || blob.Contains("not found"))
        {
            return (
                $"The model '{modelName}' was not found on {providerName}. Choose a different model, then retry.",
                "model_not_found",
                404,
                string.Empty);
        }

        if (status == 503 || blob.Contains("overloaded") || blob.Contains("unavailable"))
        {
            return (
                $"{providerName} is busy or unavailable. Wait a moment, then retry.",
                "upstream_overloaded",
                503,
                string.Empty);
        }

        if (blob.Contains("connection refused") || blob.Contains("econnrefused") || blob.Contains("actively refused"))
        {
            return (
                "The local model is not running. Launch it in AI-FluxMux, then retry.",
                "server_not_running",
                502,
                string.Empty);
        }

        if (blob.Contains("10053") || blob.Contains("10054") || blob.Contains("forcibly closed") || blob.Contains("connection aborted"))
        {
            return (
                $"The connection to {providerName} closed before a reply finished. Retry in a fresh chat.",
                "connection_reset",
                502,
                string.Empty);
        }

        if (blob.Contains("timed out") || blob.Contains("timeout") || blob.Contains("taskcanceled") || blob.Contains("cancel"))
        {
            var local = providerName.Equals("Local", StringComparison.OrdinalIgnoreCase)
                || providerName.Contains("llama-server", StringComparison.OrdinalIgnoreCase);
            return (
                local
                    ? "The local model took too long to reply. Start a fresh chat."
                    : $"{providerName} took too long to reply. Retry, or start a fresh chat.",
                "request_timeout",
                504,
                string.Empty);
        }

        if (blob.Contains("failed to load image or audio")
            || blob.Contains("failed to load image")
            || blob.Contains("cannot take pictures")
            || (blob.Contains("image") && (blob.Contains("not support") || blob.Contains("unsupported"))))
        {
            var local = providerName.Equals("Local", StringComparison.OrdinalIgnoreCase)
                || providerName.Contains("llama-server", StringComparison.OrdinalIgnoreCase);
            return (
                local
                    ? "The local model could not read this picture. Try another image in a fresh chat."
                    : FluxMuxGatewayRouting.FormatCloudVisionUnavailableMessage(null, provider, model),
                local ? "invalid_request_error" : FluxMuxGatewayRouting.CloudVisionUnavailableType,
                status == 0 ? 400 : status,
                string.Empty);
        }

        if (blob.Contains("invalid_request_error"))
        {
            return (
                $"{providerName} rejected the request. Start a fresh chat, or choose a different model.",
                "invalid_request_error",
                status == 0 ? 400 : status,
                string.Empty);
        }

        return (
            "This request failed. Retry in a fresh chat, or choose a different model.",
            "upstream_http_error",
            status == 0 ? 502 : status,
            string.Empty);
    }

    /// <summary>
    /// True when the upstream body looks like monthly / plan token exhaustion rather than a brief RPM limit.
    /// </summary>
    public static bool LooksLikeQuotaExhaustion(string loweredBlob, int status)
    {
        if (status == 402)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(loweredBlob))
        {
            return false;
        }

        if (loweredBlob.Contains("insufficient_quota", StringComparison.Ordinal)
            || loweredBlob.Contains("exceeded your copilot token usage", StringComparison.Ordinal)
            || loweredBlob.Contains("exceeded your premium request", StringComparison.Ordinal)
            || loweredBlob.Contains("exceeded your monthly", StringComparison.Ordinal)
            || loweredBlob.Contains("monthly chat message limit", StringComparison.Ordinal))
        {
            return true;
        }

        if (loweredBlob.Contains("token usage", StringComparison.Ordinal)
            && (loweredBlob.Contains("exceed", StringComparison.Ordinal)
                || loweredBlob.Contains("limit", StringComparison.Ordinal)
                || loweredBlob.Contains("exhaust", StringComparison.Ordinal)))
        {
            return true;
        }

        if (loweredBlob.Contains("ai credits", StringComparison.Ordinal)
            && (loweredBlob.Contains("exhaust", StringComparison.Ordinal)
                || loweredBlob.Contains("exceed", StringComparison.Ordinal)
                || loweredBlob.Contains("limit", StringComparison.Ordinal)))
        {
            return true;
        }

        if (loweredBlob.Contains("usage limit", StringComparison.Ordinal)
            && (loweredBlob.Contains("exceed", StringComparison.Ordinal)
                || loweredBlob.Contains("reach", StringComparison.Ordinal)
                || loweredBlob.Contains("quota", StringComparison.Ordinal)))
        {
            return true;
        }

        if (loweredBlob.Contains("free plan", StringComparison.Ordinal)
            && (loweredBlob.Contains("limit", StringComparison.Ordinal)
                || loweredBlob.Contains("quota", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldOpenCloudBreaker(string errorType)
        => errorType is "quota_exhausted" or "rate_limited" or "request_timeout" or "upstream_overloaded";

    /// <summary>
    /// Cloud failures where switching to another ready model is a reasonable next step.
    /// Broader than <see cref="ShouldOpenCloudBreaker"/>: auth and missing-model
    /// should prompt a switch even though they do not open a timed pause.
    /// </summary>
    public static bool ShouldOfferRouteSwitch(string errorType)
        => errorType is "quota_exhausted"
            or "rate_limited"
            or "request_timeout"
            or "upstream_overloaded"
            or "auth_failed"
            or "model_not_found"
            or "model_not_supported"
            or "connection_reset"
            or "cloud_vision_unavailable";

    public static string FormatCloudFailureRecoveryReason(string provider, string model, string errorType)
    {
        var providerName = string.IsNullOrWhiteSpace(provider) ? "Cloud" : provider.Trim();
        var modelName = string.IsNullOrWhiteSpace(model) ? "the cloud model" : model.Trim();
        var why = errorType switch
        {
            "quota_exhausted" => "ran out of token budget or quota",
            "rate_limited" => "hit a rate limit",
            "request_timeout" => "took too long",
            "upstream_overloaded" => "is overloaded or unavailable",
            "auth_failed" => "rejected the API key",
            "model_not_found" => "could not find this model",
            "model_not_supported" => "does not accept this model on the chat route",
            "connection_reset" => "closed the connection before a complete reply",
            _ => "did not finish this turn"
        };
        return providerName + " / " + modelName + " " + why
            + ". AI-FluxMux ended this turn so the Client app can stop waiting. This chat turn is over. "
            + ControlLabelMarkup.Mark(RouteRecoveryPolicy.ResumeWaitingLabel)
            + ", or "
            + ControlLabelMarkup.Mark(RouteRecoveryPolicy.SwitchLabelPrefix)
            + " the other ready model.";
    }

    public static string ExtractUpstreamErrorMessage(string detail, string fallback)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return fallback;
        }

        try
        {
            if (JsonNode.Parse(detail) is JsonObject parsed)
            {
                if (parsed["error"] is JsonObject err && err["message"] is not null)
                {
                    return err["message"]!.ToString() ?? fallback;
                }

                if (parsed["error"] is JsonValue errText)
                {
                    return errText.ToString() ?? fallback;
                }

                if (parsed["message"] is not null)
                {
                    return parsed["message"]!.ToString() ?? fallback;
                }
            }
        }
        catch
        {
        }

        var text = detail.Trim();
        if (text.Length > 500)
        {
            text = text[..500] + "…";
        }

        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }
}
