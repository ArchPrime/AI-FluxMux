using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public readonly record struct GatewayRouteResult(string Kind, bool OfferReload = false, string? ConsentReason = null);

public static class FluxMuxGatewayRouting
{
    public const string Local = "local";
    public const string Cloud = "cloud";
    public const string CloudConsent = "cloud-consent";
    public const string ConsentTimeout = "consent-timeout";

    public const string HarnessEndpointApp = "DeepSeek Harness";
    public const int LargePromptChars = 48000;
    public const string CapacityCloudConsentReason =
        "This chat turn has become large enough that it will likely spill into RAM and slow down significantly. Your ready-loaded cloud model may be a lot faster at this point.";
    public const string FillingCloudConsentReason =
        "This turn looks too large for the current local model's Context, so llama-server was not asked to start it.";
    public const string LocalCannotCloudConsentReason =
        "The loaded local cannot take this turn, so llama-server was not asked to start it.";
    public const string ExplicitCloudConsentReason =
        "The latest prompt asks to use a cloud model.";
    public const string LocalFillingBlockedType = "local_context_filling";
    public const string LocalFillingBlockedMessage =
        "This turn cannot continue: it is too large for the current local model's Context.";
    public const string LocalContextOverflowMessage =
        "This turn cannot continue: it is too large for the local model's Context.";
    public const string LocalVisionUnavailableType = "local_vision_unavailable";
    public const string LocalVisionUnavailableMessage =
        "This turn cannot continue: the request included a picture, and Images is off on the loaded model profile.";
    public const string CloudVisionUnavailableType = "cloud_vision_unavailable";
    public const string EndpointBarDisclaimer =
        "That is not the Client app Context bar, which can read lower. The Client app may also end a stalled turn on its own.";

    public static JsonObject EndpointErrorBody(string message)
    {
        var text = string.IsNullOrWhiteSpace(message)
            ? "This turn cannot continue."
            : message.Trim();
        return new JsonObject
        {
            ["message"] = text,
            ["error"] = text
        };
    }

    public static GatewayRouteResult DecideRoute(JsonObject state, JsonObject payload, string? forceRoute = null)
    {
        var localOk = LocalTargetReady(state);
        var cloudOk = CloudTargetReady(state);
        if (IsForce(forceRoute, Cloud))
        {
            return new GatewayRouteResult(Cloud);
        }

        if (IsForce(forceRoute, Local))
        {
            return new GatewayRouteResult(Local);
        }

        if (!RoutingOn(state))
        {
            return new GatewayRouteResult(Str(state, "mode") == "local" ? Local : Cloud);
        }

        return DecidePromptedRoute(localOk, cloudOk, state, payload);
    }

    public static string ClassifyRoute(JsonObject state, JsonObject payload, string? forceRoute = null)
        => DecideRoute(state, payload, forceRoute).Kind;

    public static bool AllowsOperatorRouting(string? forceRoute)
        => string.IsNullOrWhiteSpace(forceRoute);

    public static bool ShouldHonorApprovedCloudWindow(
        bool cloudOk,
        string? forceRoute,
        string? recommendStatus,
        long allowUntilUnix,
        long nowUnix,
        bool approvedThisProcess = false,
        string? recommendSource = null)
    {
        if (!approvedThisProcess || !cloudOk || IsForce(forceRoute, Local))
        {
            return false;
        }

        return CloudReturnToLocalPolicy.StillHonorCloudWindow(
            recommendStatus,
            recommendSource,
            allowUntilUnix,
            nowUnix);
    }

    public static bool RoutingOn(JsonObject state)
    {
        var value = state["routing_enabled"];
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        var text = value?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        return text is "1" or "true" or "yes" or "on";
    }

    public static bool LocalTargetReady(JsonObject state)
        => Str(state, "local_phase") == "ready"
           || (Str(state, "mode") == "local" && Str(state, "phase") == "ready");

    public static bool CloudTargetReady(JsonObject state)
        => Str(state, "cloud_phase") == "ready"
           || (Str(state, "mode") == "cloud" && Str(state, "phase") == "ready");

    public static bool CapacityAllowsCloudPromotion(JsonObject state)
    {
        var capacity = Str(state, "cloud_routing_capacity", "Normal").ToLowerInvariant();
        return !capacity.StartsWith("hold", StringComparison.Ordinal)
            && !capacity.StartsWith("low", StringComparison.Ordinal);
    }

    public static bool CapacityAllowsExplicitCloudAsk(JsonObject state)
    {
        var capacity = Str(state, "cloud_routing_capacity", "Normal").ToLowerInvariant();
        return !capacity.StartsWith("hold", StringComparison.Ordinal);
    }

    public static GatewayRouteResult DecideOverflowFallback(JsonObject state, JsonObject payload)
    {
        if (!RoutingOn(state) || !CloudTargetReady(state))
        {
            return new GatewayRouteResult(Local, OfferReload: LocalTargetReady(state));
        }

        if (GeminiThoughtSignaturePassthrough.HasToolContinuation(payload))
        {
            return new GatewayRouteResult(Local, OfferReload: true);
        }

        var capacity = Str(state, "cloud_routing_capacity", "Normal").ToLowerInvariant();
        if (capacity.StartsWith("hold", StringComparison.Ordinal))
        {
            return new GatewayRouteResult(Local, OfferReload: true);
        }

        return new GatewayRouteResult(
            CloudConsent,
            OfferReload: true,
            ConsentReason: FillingCloudConsentReason);
    }

    public static bool PromptFillsLocalContext(JsonObject state, JsonObject payload)
    {
        if (LocalChatPayloadSignals.PayloadHasImage(payload)
            && !Str(state, "local_vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            && LocalChatPayloadSignals.LatestUserTurnHasImage(payload))
        {
            return false;
        }

        return EvaluatePromptFillsLocalContext(state, payload);
    }

    private static bool EvaluatePromptFillsLocalContext(JsonObject state, JsonObject payload)
    {
        var hotContext = ParseInt(Str(state, "local_context"));
        if (hotContext <= 0)
        {
            return false;
        }

        var promptStd = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var maxTokens = Math.Max(MaxTokensForTurn(state, payload), 0);
        if (LocalChatPayloadSignals.PayloadHasImage(payload)
            && LocalChatPayloadSignals.LatestUserTurnHasImage(payload))
        {
            return promptStd + maxTokens > hotContext
                || LocalRequestOverlayRouting.PromptExceedsContext(payload, hotContext);
        }

        var promptFill = LocalRequestOverlayRouting.EstimatePromptTokens(
            payload,
            LocalRequestOverlayRouting.FillingCharsPerToken);

        // Fail-early when the forwarded prompt itself is over (2 chars/token),
        // or when a normal estimate plus the reply budget cannot fit.
        // Do not add max tokens onto the pessimistic prompt and then apply
        // Compact's 85% watermark — that 400s Harness while it still shows
        // about half the loaded Context used.
        // Picture bytes are a capability cost, not text; they use the standard
        // estimate plus a per-picture allowance, not this 2-character path.
        return promptFill > hotContext
            || promptStd + maxTokens > hotContext
            || LocalRequestOverlayRouting.PromptExceedsContext(payload, hotContext);
    }

    private static int MaxTokensForTurn(JsonObject state, JsonObject payload)
    {
        var requested = ParseInt(Str(payload, "max_tokens"));
        if (requested > 0)
        {
            return requested;
        }

        if (state["local_overlays"] is not JsonArray overlays)
        {
            return 0;
        }

        foreach (var node in overlays)
        {
            if (node is not JsonObject overlay)
            {
                continue;
            }

            var overlayMax = ParseInt(Str(overlay, "max_tokens"));
            if (overlayMax > 0)
            {
                return overlayMax;
            }
        }

        return 0;
    }

    public static bool PromptLooksTooLargeForLocal(JsonObject state, JsonObject payload, int? messageBlobLength = null)
    {
        var blobLength = messageBlobLength ?? MessageBlobLength(payload);
        if (blobLength >= LargePromptChars)
        {
            return true;
        }

        return PromptFillsLocalContext(state, payload);
    }

    public static bool LocalOffloadUsesSystemRam(JsonObject state)
    {
        var mode = Str(state, "local_gpu_offload").Trim();
        return mode.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("CPU only", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldOfferRamSpillCloudConsent(
        JsonObject state,
        JsonObject payload,
        int? messageBlobLength = null)
    {
        if (PromptFillsLocalContext(state, payload) || !LocalOffloadUsesSystemRam(state))
        {
            return false;
        }

        var blobLength = messageBlobLength ?? MessageBlobLength(payload);
        return blobLength >= LargePromptChars;
    }

    public static bool ConsentTimesOutToError(string? reason)
        => !string.IsNullOrWhiteSpace(reason)
           && (reason.StartsWith(FillingCloudConsentReason, StringComparison.Ordinal)
               || reason.StartsWith(LocalCannotCloudConsentReason, StringComparison.Ordinal));

    public static bool LoadedLocalCannotTakeTurn(JsonObject state, JsonObject payload)
        => LocalChatPayloadSignals.LatestUserTurnHasImage(payload)
           && !Str(state, "local_vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase);

    public static bool LoadedLocalCouldTakeLatestUserTurn(JsonObject state, JsonObject payload)
    {
        if (!LocalTargetReady(state))
        {
            return false;
        }

        if (LocalChatPayloadSignals.LatestUserTurnHasImage(payload)
            && !Str(state, "local_vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (EvaluatePromptFillsLocalContext(state, payload))
        {
            return false;
        }

        if (LocalOffloadUsesSystemRam(state)
            && MessageBlobLength(payload) >= LargePromptChars)
        {
            return false;
        }

        return true;
    }

    public static bool ShouldOfferCloudHopBecauseLocalCannotTakeTurn(JsonObject state, JsonObject payload)
        => RoutingOn(state)
           && CapacityAllowsCloudPromotion(state)
           && LoadedLocalCannotTakeTurn(state, payload);

    public static bool CloudTakesImages(JsonObject state)
        => CloudCatalogVision.FlagAllowsImages(Str(state, "cloud_vision"));

    public static string FormatCloudVisionUnavailableMessage(
        JsonObject? state,
        string? provider = null,
        string? model = null)
    {
        var label = ProxyRuntimeStateSecrets.FormatCloudLabel(
            FirstNonEmpty(provider ?? string.Empty, state is null ? string.Empty : Str(state, "provider")),
            FirstNonEmpty(
                model ?? string.Empty,
                state is null ? string.Empty : Str(state, "cloud_preferred_model"),
                state is null ? string.Empty : Str(state, "preferred_model")));
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "the ready cloud";
        }

        return FormatNewSlotEndpointMessage(
            "This turn cannot continue: " + label + " cannot take pictures.",
            EndpointApp(state));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    public static string FormatBlockedLocalTurnMessage(
        JsonObject state,
        JsonObject payload,
        int promptChars = 0,
        string? lastServedKind = null,
        string? lastServedCloudLabel = null)
        => LoadedLocalCannotTakeTurn(state, payload)
            ? FormatLocalVisionUnavailableMessage(state)
            : FormatFillingBlockedMessage(state, payload, promptChars, lastServedKind, lastServedCloudLabel);

    public static string FormatForwardedBasis(JsonObject state, JsonObject payload, int promptChars = 0)
    {
        var chars = promptChars > 0 ? promptChars : MessageBlobLength(payload);
        var promptStd = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var promptFill = LocalRequestOverlayRouting.EstimatePromptTokens(
            payload,
            LocalRequestOverlayRouting.FillingCharsPerToken);
        var ctx = ParseInt(Str(state, "local_context"));
        var maxTok = MaxTokensForTurn(state, payload);
        var ctxText = ctx > 0 ? ctx.ToString(CultureInfo.InvariantCulture) : "unknown";
        var maxText = maxTok > 0
            ? " Reply budget " + maxTok.ToString(CultureInfo.InvariantCulture) + " tokens."
            : string.Empty;
        return "AI-FluxMux measured the messages it would forward to llama-server: "
            + chars.ToString(CultureInfo.InvariantCulture) + " characters, about "
            + promptStd.ToString(CultureInfo.InvariantCulture) + " tokens (4 characters each), or about "
            + promptFill.ToString(CultureInfo.InvariantCulture) + " tokens on a tighter 2-character estimate. Loaded Context is "
            + ctxText + "."
            + maxText
            + " " + EndpointBarDisclaimer;
    }

    public static string AnnotateConsentReason(string? reason, JsonObject state, JsonObject payload, int promptChars = 0)
    {
        _ = state;
        _ = payload;
        _ = promptChars;
        return string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
    }

    public static string FormatFillingBlockedMessage(
        JsonObject state,
        JsonObject payload,
        int promptChars = 0,
        string? lastServedKind = null,
        string? lastServedCloudLabel = null)
    {
        _ = payload;
        _ = promptChars;
        var body = ProxyRuntimeStateSecrets.LastServedWasCloud(lastServedKind)
            ? LocalReloadRouting.FormatCloudContextLead(lastServedCloudLabel)
            : LocalFillingBlockedMessage;
        return FormatNewSlotEndpointMessage(body, EndpointApp(state));
    }

    public static string FormatLocalContextOverflowMessage(
        JsonObject? state,
        string? lastServedKind = null,
        string? lastServedCloudLabel = null)
    {
        var body = ProxyRuntimeStateSecrets.LastServedWasCloud(lastServedKind)
            ? LocalReloadRouting.FormatCloudContextLead(lastServedCloudLabel)
            : LocalContextOverflowMessage;
        return FormatNewSlotEndpointMessage(body, EndpointApp(state));
    }

    public static string FormatLocalVisionUnavailableMessage(JsonObject? state)
        => FormatNewSlotEndpointMessage(LocalVisionUnavailableMessage, EndpointApp(state));

    public static string FormatNewSlotEndpointMessage(string message, string? endpointApp)
    {
        var body = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        _ = endpointApp;
        return body + " " + ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice();
    }

    public static bool IsHarnessApp(string? endpointApp)
        => (endpointApp ?? string.Empty).Equals(HarnessEndpointApp, StringComparison.OrdinalIgnoreCase);

    public static bool IsClineApp(string? endpointApp)
        => (endpointApp ?? string.Empty).Equals("Cline", StringComparison.OrdinalIgnoreCase);

    private static string EndpointApp(JsonObject? state)
        => state is null ? string.Empty : Str(state, "endpoint_app");

    public static bool ShouldBlockLocalForward(
        string routeKind,
        JsonObject state,
        JsonObject payload,
        string? cloudRecommendStatus = null,
        string? forceRoute = null)
    {
        if (IsForce(forceRoute, Local))
        {
            return false;
        }

        if (routeKind.Equals(ConsentTimeout, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!routeKind.Equals(Local, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(cloudRecommendStatus, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return PromptFillsLocalContext(state, payload);
    }

    private static int MessageBlobLength(JsonObject payload)
    {
        try
        {
            var blob = (payload["messages"] ?? new JsonArray()).ToJsonString();
            return LocalRequestOverlayRouting.RedactImageBytesForEstimate(
                blob,
                out _,
                LocalChatPayloadSignals.PayloadHasImage(payload)).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static int ParseInt(string value)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    public static bool IsHarnessEndpoint(JsonObject state)
    {
        var app = Str(state, "endpoint_app");
        return app.Equals(HarnessEndpointApp, StringComparison.OrdinalIgnoreCase);
    }

    private static GatewayRouteResult DecidePromptedRoute(bool localOk, bool cloudOk, JsonObject state, JsonObject payload)
    {
        if (localOk)
        {
            if (RoutingOn(state))
            {
                var toolContinuation = GeminiThoughtSignaturePassthrough.HasToolContinuation(payload);
                if (ShouldOfferCloudHopBecauseLocalCannotTakeTurn(state, payload))
                {
                    return new GatewayRouteResult(
                        CloudConsent,
                        OfferReload: true,
                        ConsentReason: LocalCannotCloudConsentReason);
                }

                if (cloudOk)
                {
                    // Tool history must not skip filling/large: Harness Deep Dive
                    // packs prior tool turns and would otherwise grind llama-server
                    // with no Use cloud prompt. Explicit "use cloud" stays off
                    // during tool continuation so Gemini thought signatures are
                    // not dropped mid-loop.
                    if (CapacityAllowsCloudPromotion(state) && PromptFillsLocalContext(state, payload))
                    {
                        return new GatewayRouteResult(
                            CloudConsent,
                            OfferReload: true,
                            ConsentReason: FillingCloudConsentReason);
                    }

                    if (CapacityAllowsCloudPromotion(state)
                        && ShouldOfferRamSpillCloudConsent(state, payload))
                    {
                        return new GatewayRouteResult(
                            CloudConsent,
                            OfferReload: true,
                            ConsentReason: CapacityCloudConsentReason);
                    }

                    if (!toolContinuation
                        && CapacityAllowsExplicitCloudAsk(state)
                        && FluxMuxCloudCallLimits.PromptAsksForCloud(LastUserText(payload)))
                    {
                        return new GatewayRouteResult(
                            CloudConsent,
                            OfferReload: true,
                            ConsentReason: ExplicitCloudConsentReason);
                    }
                }

                return new GatewayRouteResult(Local, OfferReload: true);
            }

            return new GatewayRouteResult(Local);
        }

        if (cloudOk)
        {
            return new GatewayRouteResult(Cloud);
        }

        return new GatewayRouteResult(Str(state, "mode") == "local" ? Local : Cloud);
    }

    private static bool IsForce(string? forceRoute, string kind)
        => !string.IsNullOrWhiteSpace(forceRoute)
           && forceRoute.Trim().Equals(kind, StringComparison.OrdinalIgnoreCase);

    private static string LastUserText(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
        {
            return string.Empty;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject msg)
            {
                continue;
            }

            if (Str(msg, "role").Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return msg["content"]?.ToString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool ModelMatches(string requested, string preferred)
    {
        var req = requested.Trim().ToLowerInvariant();
        var pref = preferred.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(req) || string.IsNullOrWhiteSpace(pref))
        {
            return false;
        }

        return req == pref || req.Contains(pref) || pref.Contains(req);
    }

    private static string Str(JsonObject obj, string key, string fallback = "")
        => obj[key]?.ToString() ?? fallback;
}
