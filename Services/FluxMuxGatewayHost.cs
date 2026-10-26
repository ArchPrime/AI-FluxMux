using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public sealed class FluxMuxGatewayHost : IDisposable
{
    private const int MinUpstreamTimeoutSec = 90;
    private const int MaxUpstreamTimeoutSec = 120;
    private const int SwitchWaitSec = 180;

    private readonly string _statePath;
    private readonly string _lastServedPath;
    private readonly string _recommendPath;
    private readonly string _cloudBreakerPath;
    private readonly string _localReloadPath;
    private readonly string _logPath;
    private readonly string _contextPath;
    private readonly string _lastRouteExplanationPath;
    private readonly HttpClient _http;
    private readonly object _logLock = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _loopCts;
    private Task? _acceptLoop;
    private int _port;
    private readonly IdleSessionCoordinator _idleSession;
    private GenerationSpeedTracker? _generationSpeed;
    private readonly object _churnLock = new();
    private DateTime _lastLocalToolTurnUtc;
    private int _rapidLocalToolTurns;
    private readonly object _portRulesLock = new();
    private PortForwardingRules _portRules = PortForwardingRules.Defaults;
    private PortRulesTelemetry _lastPortRulesTelemetry = PortRulesTelemetry.Empty;
    private string _lastPortRulesDiagnostic = string.Empty;
    private DateTime _lastHangTallyUtc;
    private int _portRuleSendAnywayGrace;
    private bool _currentTurnIsLongLoad;

    public FluxMuxGatewayHost(
        string statePath,
        string lastServedPath,
        string recommendPath,
        string cloudBreakerPath,
        string localReloadPath,
        string logPath,
        string contextPath,
        string lastRouteExplanationPath,
        IdleSessionCoordinator? idleSession = null)
    {
        _statePath = statePath;
        _lastServedPath = lastServedPath;
        _recommendPath = recommendPath;
        _cloudBreakerPath = cloudBreakerPath;
        _localReloadPath = localReloadPath;
        _logPath = logPath;
        _contextPath = contextPath;
        _lastRouteExplanationPath = lastRouteExplanationPath;
        _idleSession = idleSession ?? new IdleSessionCoordinator();
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Func<CancellationToken, Task>? PrepareForChatAsync { get; set; }

    public Func<Task>? ParkLocalAfterHangAsync { get; set; }

    public Func<bool>? HonorCloudRecommendThisProcess { get; set; }

    public Func<JsonObject, JsonObject>? EnrichState { get; set; }

    public Func<string, string, (string Mode, double TriggerPercent, int KeepTurns, int KeepToolResults, int PinUserChars)? >? GetLocalAutoCompactSettings { get; set; }
    public Func<string, string, int?>? GetLocalMaxForwardedImages { get; set; }
    public Func<string, string, (int Context, string Offload)>? GetLocalProfileContextAndOffload { get; set; }

    /// <summary>
    /// Per-profile forward-prune toggle ("LocalForwardPruneEnabled", default on). Read live each
    /// turn so it can be switched mid-chat without a reload. Returns null when the profile cannot
    /// be resolved (treated as on, the default).
    /// </summary>
    public Func<string, string, bool?>? GetForwardPruneEnabled { get; set; }

    /// <summary>
    /// Raised when the armed Hail Mary rear-prune is actually consumed on a turn (success or
    /// no-op), so the UI can clear the "queued" pulse and refresh the context gauge with the new
    /// (smaller) prompt-token count. Fires exactly once per armed prune.
    /// </summary>
    public Action<int>? ReportHailMaryApplied { get; set; }

    public Action<string>? ReportPortRulesPostMortem { get; set; }

    public Action<PortRulesTelemetry>? ReportPortRulesTelemetry { get; set; }

    /// <summary>
    /// Reports the measured prompt-token count + context limit for the live "Context" gauge.
    /// Called by the gateway each turn after the real token count is measured.
    /// </summary>
    public Action<int, int, string>? ReportContextUsage { get; set; }

    /// <summary>
    /// Raised when a turn 400s on context overflow and the non-destructive Hail Mary prune
    /// pipeline (stages 1-4) already ran but the turn still can't fit. The UI shows the
    /// Hail Mary banner with a button to run the destructive stage-5 middle-cut.
    /// </summary>
    public Action<string>? ReportHailMaryOffer { get; set; }

    /// <summary>
    /// Returns and clears the armed Hail Mary REAR-context prune choice (stop + image-strip).
    /// When armed, the next local forward applies the rear-context prune to the payload before
    /// sending, using the chosen one-off aggression stop. The <c>WasArmed</c> flag is true only
    /// when the user actually clicked the button; when false the gateway skips the prune entirely.
    /// </summary>
    public Func<(HailMaryCompaction.HailMaryAggression Stop, bool StripImages, bool WasArmed)>? ConsumeHailMaryMiddleCut { get; set; }

    /// <summary>
    /// Raised for every gateway log line (Hail Mary results, forward-prune results, context
    /// measurements, route decisions, etc.) so the UI diagnostic terminal can show them live.
    /// This is the "report results to terminal" path: previously Log() only wrote to a file.
    /// </summary>
    public Action<string>? ReportLog { get; set; }

    public GenerationSpeedTracker? GenerationSpeed
    {
        get => _generationSpeed;
        set => _generationSpeed = value;
    }

    public IdleSessionCoordinator IdleSession => _idleSession;

    public PortForwardingRules PortRules
    {
        get
        {
            lock (_portRulesLock)
            {
                return _portRules;
            }
        }
        set
        {
            lock (_portRulesLock)
            {
                _portRules = (value ?? PortForwardingRules.Defaults).Clamp().ForForwarding();
            }
        }
    }

    private PortForwardingRules CurrentRules()
    {
        lock (_portRulesLock)
        {
            return _portRules;
        }
    }

    public bool IsListening => _listener is { IsListening: true } && _port > 0;

    public int Port => _port;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (IsListening && _port == port)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        HttpListenerException? lastListenError = null;
        HttpListener? listener = null;
        foreach (var prefix in new[] { $"http://127.0.0.1:{port}/", $"http://localhost:{port}/" })
        {
            listener = new HttpListener { IgnoreWriteExceptions = true };
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                lastListenError = null;
                break;
            }
            catch (HttpListenerException ex)
            {
                lastListenError = ex;
                try
                {
                    listener.Close();
                }
                catch
                {
                }

                listener = null;
            }
        }

        if (listener is null || !listener.IsListening)
        {
            var detail = lastListenError is null
                ? "The in-process gateway could not bind the public port."
                : lastListenError.ErrorCode == 5
                    ? "Windows refused HttpListener on this port (access denied). URL reservations are not required for 127.0.0.1 in most setups; another service may be using HTTP.sys on that port, or a policy is blocking it."
                    : lastListenError.Message;
            throw new InvalidOperationException(detail, lastListenError);
        }

        _listener = listener;
        _port = port;
        _loopCts = new CancellationTokenSource();
        var loopToken = _loopCts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, loopToken), loopToken);
        Log($"managed bridge boot on 127.0.0.1:{port}");
        EmitContextEvent("bridge_boot", $"port={port}");
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task StopAsync()
    {
        var loop = _acceptLoop;
        var cts = _loopCts;
        var listener = _listener;
        _acceptLoop = null;
        _loopCts = null;
        _listener = null;
        _port = 0;

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch
        {
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _http.Dispose();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            var path = NormalizeListenerPath(context.Request.Url?.AbsolutePath);
            var method = context.Request.HttpMethod ?? "GET";
            if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                ApplyCorsHeaders(context.Response);
                context.Response.KeepAlive = true;
                context.Response.StatusCode = 204;
                context.Response.ContentLength64 = 0;
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                && (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/v1/health", StringComparison.OrdinalIgnoreCase)))
            {
                var state = LoadState();
                await SendJsonAsync(context.Response, 200, new JsonObject
                {
                    ["ok"] = true,
                    ["gateway"] = "FluxMux",
                    ["mode"] = Str(state, "mode", "unknown"),
                    ["phase"] = Str(state, "phase", "idle"),
                    ["routing"] = RoutingOn(state),
                    ["local_phase"] = Str(state, "local_phase"),
                    ["cloud_phase"] = Str(state, "cloud_phase"),
                    ["provider"] = Str(state, "provider", "unknown"),
                    ["model"] = Str(state, "preferred_model")
                }).ConfigureAwait(false);
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                && IsModelsListPath(path))
            {
                await SendJsonAsync(context.Response, 200, ListModels(LoadState())).ConfigureAwait(false);
                return;
            }

            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                || (path != "/v1/chat/completions" && path != "/chat/completions"))
            {
                await SendOpenAiErrorAsync(context.Response, "Not Found", 404, "not_found_error").ConfigureAwait(false);
                return;
            }

            using (_idleSession.BeginChat())
            {
                if (PrepareForChatAsync is not null)
                {
                    await PrepareForChatAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await HandleChatCompletionsAsync(context).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log("gateway handler failed: " + ex.Message);
            try
            {
                await SendOpenAiErrorAsync(context.Response, "AI-FluxMux could not finish this request. Retry in a fresh chat.", 502, "proxy_error").ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await OpenAiStreamTelemetryProxy.WriteErrorAndDoneAsync(
                        context.Response.OutputStream,
                        "This turn ended before a reply finished. Start a fresh chat.",
                        "proxy_error").ConfigureAwait(false);
                    Log("stream aborted after headers: proxy_error");
                }
                catch
                {
                }
            }
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
            }
            catch
            {
            }
        }
    }

    private async Task HandleChatCompletionsAsync(HttpListenerContext context)
    {
        string bodyText;
        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
        {
            bodyText = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(string.IsNullOrWhiteSpace(bodyText) ? "{}" : bodyText) as JsonObject
                      ?? throw new InvalidOperationException("Payload must be a JSON object.");
        }
        catch
        {
            await SendOpenAiErrorAsync(context.Response, "Invalid JSON payload.", 400, "invalid_request_error").ConfigureAwait(false);
            return;
        }

        var state = await WaitForRoutableStateAsync().ConfigureAwait(false);
        if (!HasReadyTarget(state))
        {
            var phase = Str(state, "phase", "idle");
            var mode = Str(state, "mode", "idle");
            string message;
            if (phase is "idle" or "" || mode is "idle" or "")
            {
                message = "No model is launched. Launch a local or cloud model in AI-FluxMux, then retry.";
            }
            else if (phase == "warming" || Str(state, "local_phase") == "warming")
            {
                message = "The local model is still loading. Wait, then retry.";
            }
            else if (phase is "launching" or "transitioning")
            {
                message = "A model is starting. Wait, then retry.";
            }
            else if (phase == "failed")
            {
                message = "The selected model failed to start. Check AI-FluxMux, then Launch again.";
            }
            else
            {
                message = "No model is ready yet. Wait or Launch again, then retry.";
            }

            await SendOpenAiErrorAsync(context.Response, message, 503, "route_unavailable", headers: new Dictionary<string, string> { ["Retry-After"] = "5" }).ConfigureAwait(false);
            return;
        }

        var forceRoute = context.Request.Headers[CloudEndpointValidationProbe.ForceRouteHeader];
        var isProbeRequest = context.Request.Headers[CloudEndpointValidationProbe.ProbeRequestHeader]?.Any() ?? false;
        var requestId = Guid.NewGuid().ToString("N")[..8];
        string? overlayVariant = null;
        var artifacts = LocalSessionArtifactPolicy.Apply(payload);
        var sessionArtifacts = LocalSessionArtifactPolicy.Inspect(payload["messages"] as JsonArray);
        if (artifacts.Omitted > 0)
        {
            Log("local chat: omitted "
                + artifacts.Omitted.ToString(CultureInfo.InvariantCulture)
                + " session-created diagnostic file(s); llama-server still sees the calls. The Client app still has the full output.");
        }

        if (artifacts.HottestEdits >= LocalSessionArtifactPolicy.EditLoopThreshold)
        {
            Log("local chat: same project file rewritten "
                + artifacts.HottestEdits.ToString(CultureInfo.InvariantCulture)
                + " times; llama-server was asked to change one thing. The Client app still has the full chat.");
        }

        var portRules = CurrentRules();
        var clearedResults = LocalToolResultClearing.ClearOlderResults(payload, portRules);
        if (clearedResults > 0)
        {
            Log("local chat: omitted "
                + clearedResults.ToString(CultureInfo.InvariantCulture)
                + " older tool result(s); llama-server still sees the calls. The Client app still has the full output.");
        }

        var preferredModel = Str(state, "local_preferred_model");
        var variant = Str(state, "local_variant");
        var perProfileMaxImages = GetLocalMaxForwardedImages?.Invoke(preferredModel, variant);
        var maxImages = perProfileMaxImages ?? portRules.MaxForwardedImages;
        var droppedPictures = portRules.MaxPicturesEnabled
            ? LocalChatPayloadSignals.KeepMostRecentImages(payload, maxImages)
            : 0;
        if (droppedPictures > 0)
        {
            Log("local chat: dropped "
                + droppedPictures.ToString(CultureInfo.InvariantCulture)
                + " older picture(s); llama-server only gets the "
                + maxImages.ToString(CultureInfo.InvariantCulture)
                + " most recent. The Client app still has the full album.");
        }

        var haltAsToolMill = LocalToolResultClearing.ShouldHaltAsToolMill(payload, clearedResults, portRules);
        var rapidChurn = false;
        var rapidStreak = 0;
        lock (_churnLock)
        {
            var now = DateTime.UtcNow;
            var since = _lastLocalToolTurnUtc == default
                ? TimeSpan.MaxValue
                : now - _lastLocalToolTurnUtc;
            _rapidLocalToolTurns = LocalToolResultClearing.NextRapidChurnStreak(
                clearedResults,
                since,
                _rapidLocalToolTurns,
                portRules);
            _lastLocalToolTurnUtc = now;
            rapidStreak = _rapidLocalToolTurns;
            rapidChurn = LocalToolResultClearing.ShouldHaltAsRapidChurn(_rapidLocalToolTurns, portRules);
            if (rapidChurn)
            {
                _rapidLocalToolTurns = 0;
            }
        }

        haltAsToolMill = haltAsToolMill || rapidChurn;
        if (haltAsToolMill && ConsumePortRuleSendAnywayGrace())
        {
            Log("port rule pause: sending this mill turn anyway (grace after Send this turn anyway)");
            haltAsToolMill = false;
            rapidChurn = false;
        }
        var repeatedThisTurn = portRules.RepeatedCommandEnabled
            && LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _);
        PublishPortRulesTelemetry(new PortRulesTelemetry
        {
            HasLocalTurn = true,
            Omitted = clearedResults,
            ObserveOnly = LocalChatPayloadSignals.CountRecentObserveOnlyTools(
                payload,
                portRules.ObserveOnlyMillCount),
            RapidStreak = rapidStreak,
            PicturesKept = LocalChatPayloadSignals.CountForwardedImages(payload),
            DumpCount = sessionArtifacts.ArtifactCount,
            RepeatedCommandThisTurn = repeatedThisTurn
        });
        var compactOn = ParseBool(Str(LoadLocalReload(), "forward_compact"));
        // Probe/stress-test requests must reach the model at their INTENDED fill level. If the
        // gateway auto-compacted them, the model would see a shrunken prompt and the test's
        // TTFT/tok/s readings would be meaningless (and the context gauge would disagree with
        // the test's fill %). So skip forward-compaction for probes; the ShouldBlockLocalForward
        // safety check below still runs so the test can observe the real "context too small"
        // behavior at high fill.
        var compactApplied = isProbeRequest ? false : TryApplyForwardCompact(payload, state, compactOn);

        OfferDocumentContextDialogIfNeeded(state, payload, compactApplied, isProbeRequest);
        var reloadOffered = false;
        var decision = FluxMuxGatewayRouting.DecideRoute(state, payload, forceRoute);
        var promptChars = 0;
        try
        {
            promptChars = (payload["messages"] ?? new JsonArray()).ToJsonString().Length;
        }
        catch
        {
        }

        Log("route decide: kind=" + decision.Kind
            + " filling=" + FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload).ToString()
            + " large=" + FluxMuxGatewayRouting.PromptLooksTooLargeForLocal(state, payload, promptChars).ToString()
            + " ram_spill=" + FluxMuxGatewayRouting.ShouldOfferRamSpillCloudConsent(state, payload, promptChars).ToString()
            + " tools=" + GeminiThoughtSignaturePassthrough.HasToolContinuation(payload).ToString()
            + " n_chars=" + promptChars.ToString(CultureInfo.InvariantCulture)
            + " n_prompt=" + LocalRequestOverlayRouting.EstimatePromptTokens(payload).ToString(CultureInfo.InvariantCulture)
            + " n_fill=" + LocalRequestOverlayRouting.EstimatePromptTokens(
                payload,
                LocalRequestOverlayRouting.FillingCharsPerToken).ToString(CultureInfo.InvariantCulture)
            + (string.IsNullOrWhiteSpace(decision.ConsentReason) ? string.Empty : " reason=" + decision.ConsentReason));
        if (decision.OfferReload && FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute))
        {
            OfferLocalReloadIfNeeded(state, payload);
        }

        var routeKind = decision.Kind;
        var rec = LoadRecommend();
        if (FluxMuxGatewayRouting.ShouldHonorApprovedCloudWindow(
                CloudTargetReady(state),
                forceRoute,
                Str(rec, "status"),
                ParseLong(Str(rec, "allow_until"), 0),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                HonorCloudRecommendThisProcess?.Invoke() == true,
                Str(rec, "source")))
        {
            routeKind = FluxMuxGatewayRouting.Cloud;
            Log("cloud recommend window: forwarding to the ready cloud after Use cloud");
            if (FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute))
            {
                routeKind = await MaybeOfferReturnToLocalAsync(state, payload, rec, forceRoute).ConfigureAwait(false);
            }
        }
        else if (routeKind == FluxMuxGatewayRouting.CloudConsent
            && FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute))
        {
            var consentReason = FluxMuxGatewayRouting.AnnotateConsentReason(
                decision.ConsentReason,
                state,
                payload,
                promptChars);
            if (RouteRecoveryPolicy.PreferLocalReloadOverCloudConsent(
                    IsLocalReloadOfferPending(),
                    consentReason))
            {
                routeKind = FluxMuxGatewayRouting.Local;
                Log("cloud consent skipped: Load suggested is already pending for this filling turn");
            }
            else
            {
                var fillingTurn = FluxMuxGatewayRouting.ConsentTimesOutToError(consentReason);
                routeKind = await ApplyCloudConsentAsync(
                    FirstNonEmpty(consentReason, "The latest prompt asks to use a cloud model."),
                    failOnTimeout: fillingTurn,
                    waitForAnswer: RouteRecoveryPolicy.HoldInFlightRequestForCloudConsent(),
                    source: RouteRecoveryPolicy.CapacitySource).ConfigureAwait(false);
                if (routeKind == FluxMuxGatewayRouting.Cloud)
                {
                    state = LoadState();
                }
            }
        }
        else if (routeKind == FluxMuxGatewayRouting.CloudConsent)
        {
            routeKind = FluxMuxGatewayRouting.Local;
            Log("cloud consent skipped: internal Validate / ready probe");
        }

        var cloudRecommendStatus = Str(LoadRecommend(), "status");
        // The local "context filling" block is checked after the local payload is finalized
        // (PrepareLocalChatPayloadAsync + overlay + compaction) so it can measure the real
        // prompt token count from llama-server. See the block below in the routeKind == "local"
        // branch.

        var (boundKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, routeKind);
        routeKind = boundKind;
        if (routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase)
            && LocalChatPayloadSignals.PayloadHasImage(payload)
            && !FluxMuxGatewayRouting.CloudTakesImages(state))
        {
            await SendOpenAiErrorAsync(
                context.Response,
                FluxMuxGatewayRouting.FormatCloudVisionUnavailableMessage(state, provider, preferred),
                400,
                FluxMuxGatewayRouting.CloudVisionUnavailableType).ConfigureAwait(false);
            Log("cloud_vision_unavailable: picture turn blocked because "
                + provider + " / " + preferred + " cannot take pictures");
            return;
        }

        reloadOffered = IsLocalReloadOfferPending();
        var cloudConsentOffered = IsCloudRecommendPending();
        var requested = Str(payload, "model");
        var model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
        var localReasoningForResponse = routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? Str(state, "local_reasoning", "Off")
            : null;
        var turnStart = DateTime.Now;
        int? measuredPromptTokens = null;
        string? forwardPruneSummary = null;
        if (routeKind == "local")
        {
            await PrepareLocalChatPayloadAsync(payload, state).ConfigureAwait(false);
            var overlay = LocalRequestOverlayRouting.Pick(state, payload, requested);
            overlayVariant = overlay?["variant"]?.ToString();
            var requestedMax = ParseInt(payload["max_tokens"]?.ToString() ?? string.Empty, 0);
            LocalRequestOverlayRouting.Apply(payload, overlay, portRules, requestedMax);
            if (overlay is not null)
            {
                Log("local overlay: variant=" + (overlay["variant"]?.ToString() ?? "")
                    + " temperature=" + (overlay["temperature"]?.ToString() ?? "")
                    + " max_tokens=" + (overlay["max_tokens"]?.ToString() ?? "")
                    + " reasoning=" + (localReasoningForResponse ?? ""));
            }

            var hotContext = ParseInt(Str(state, "local_context"), 0);
            // Diagnostic: log the live local_context + variant the gateway is using for blocking,
            // so we can see if it's stale (a previous profile's value) at the moment of a block.
            Log("[ContextDiag] blocking-check local_context=" + hotContext
                + " variant=" + Str(state, "local_variant")
                + " model=" + Str(state, "local_preferred_model")
                + " phase=" + Str(state, "local_phase"));
            // so the blocking/compaction decisions use the true count instead of the chars/4
            // estimate, which over-counts for dense tokenizers and causes false "context full"
            // blocks. Falls back to the estimate when the count is unavailable.
            //
            // The count is NOT hard-wired: it is read live from the currently-loaded model's own
            // tokenizer. `local_port` is the port of the launched model and `local_preferred_model`
            // is the active model/variant, both of which change per profile. Switching profiles
            // therefore yields a different, correct count automatically.
            var localPort = ParseInt(Str(state, "local_port"), 0);
            if (localPort > 0 && hotContext > 0)
            {
                measuredPromptTokens = await LocalPromptTokenCounter.CountAsync(
                    localPort,
                    payload,
                    Str(state, "local_preferred_model")).ConfigureAwait(false);
                if (measuredPromptTokens is not null)
                {
                    Log("local prompt tokens (measured): "
                        + measuredPromptTokens.Value.ToString(CultureInfo.InvariantCulture)
                        + " (estimate "
                        + LocalRequestOverlayRouting.EstimatePromptTokens(payload).ToString(CultureInfo.InvariantCulture)
                        + ")");
                    // Report the live context usage for the UI's "Context" gauge.
                    ReportContextUsage?.Invoke(
                        measuredPromptTokens.Value,
                        hotContext,
                        Str(state, "local_preferred_model"));
                }
            }

            // Forward pruning: a per-turn fill throttle. On EVERY local turn, as it happens, trim
            // the turn's own waste (dedupe, collapse repeated tool loops, truncate oversized older
            // tool results) so the context fills more slowly. This is NOT conventional compaction
            // (which summarizes older turns at a threshold) and NOT Hail Mary (one-off emergency
            // rear-prune) — it is a distinct, conservative, always-on mechanism. It is gated on the
            // per-profile "LocalForwardPruneEnabled" toggle (default on) so it can be turned off
            // mid-chat without a reload. Uses the model's measured token count when available.
            //
            // NOTE: this does NOT re-measure the prompt after pruning. Re-measuring is a network
            // round-trip to llama-server (LocalPromptTokenCounter.CountAsync → POST
            // /input_tokens), and doing it on every pruned turn would add a full extra call to the
            // model per turn — a significant speed hit. The prune itself is a cheap in-memory JSON
            // edit; the gauge refresh is not worth an extra llama-server call each turn. The next
            // turn's own measurement will reflect the pruned history anyway.
            if (!isProbeRequest && hotContext > 0 && GetForwardPruneEnabled?.Invoke(model, Str(state, "local_variant")) == true)
            {
                var forwardPruned = ForwardPrune.PruneTurn(payload, hotContext, measuredPromptTokens);
                forwardPruneSummary = forwardPruned.Pruned
                    ? "pruned " + forwardPruned.TokensSaved + " tokens (" + string.Join(", ", forwardPruned.Stages.Select(s => s.Split(':')[0].Trim())) + ")"
                    : "nothing to prune";
            }

            if (!compactApplied && !isProbeRequest)
            {
                compactApplied = TryApplyForwardCompact(payload, state, compactOn, measuredPromptTokens);
            }

            // Offer the YaRN spill-compact dialog using the measured token count (not the
            // chars/4 estimate, which over-counts for dense tokenizers like Qwen and would
            // falsely trigger "approaching VRAM limit" at low context fill).
            OfferSpillCompactIfNeeded(state, payload, compactApplied, isProbeRequest, measuredPromptTokens);

            // Hail Mary stage-5 middle-cut: if the user clicked the Hail Mary button after a
            // context-overflow 400, apply the destructive middle-cut to this payload before
            // sending. This is the one-shot retry that gives the turn a chance to fit.
            //
            // The middle-cut is now token-driven: it drops the oldest (least useful) turns until
            // the prompt fits under the target, keeping at least MiddleCutKeepTurns recent turns
            // for conversational context. This scales with chat length — in a 500-turn chat it
            // drops hundreds of turns; in a 12-turn chat it drops only what is needed.
            if (!isProbeRequest && ConsumeHailMaryMiddleCut is not null)
            {
                var (hailStop, hailStripImages, hailWasArmed) = ConsumeHailMaryMiddleCut.Invoke();
                // Only run the rear-prune when the user actually armed it (clicked the button).
                // Previously this ran on every turn and logged a no-op each time, cluttering the
                // terminal. Now it's a true one-off.
                if (hailWasArmed)
                {
                    var hotContextForHailMary = ParseInt(Str(state, "local_context"), 0);
                    if (hotContextForHailMary > 0)
                    {
                    // Manual, one-off REAR-context prune: drop older conversational turns so the
                    // full forward turn can complete with the headroom the chosen stop requests.
                    // The system prompt, last real user task, and active tool-pair are preserved.
                    // Pass the model's measured token count (when available) so the prune's
                    // before/target check uses the real count, not the over-counting estimate
                    // (which made the prune a no-op on dense tokenizers like Qwen).
                    var hailMary = HailMaryCompaction.RearContextPrune(payload, hotContextForHailMary, hailStop, hailStripImages, measuredPromptTokens);
                    if (hailMary.MiddleCutDropped > 0 || hailMary.ImagesStripped > 0)
                    {
                        Log("hail-mary (" + hailStop + (hailStripImages ? ", strip-img" : "") + "): " + hailMary.DiagnosticsLine);
                        var hailInserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
                        if (hailInserted > 0)
                        {
                            Log("local chat: inserted a user turn so the Qwen template has a query");
                        }
                    }
                    else
                    {
                        Log("hail-mary (" + hailStop + "): no-op (too few turns or tool-pair-locked, " + (measuredPromptTokens?.ToString() ?? "n/a") + " tokens)");
                    }

                    // The armed rear-prune has now actually occurred (whether it dropped turns or
                    // was a no-op). Report back so the UI clears the "queued" pulse and refreshes
                    // the context gauge with the new (smaller) prompt-token count. Re-measure when
                    // a daemon port is available so the gauge reflects the pruned prompt.
                    int? gaugeTokens = measuredPromptTokens;
                    var hailLocalPort = ParseInt(Str(state, "local_port"), 0);
                    if (hailLocalPort > 0)
                    {
                        var reMeasured = await LocalPromptTokenCounter.CountAsync(
                            hailLocalPort,
                            payload,
                            Str(state, "local_preferred_model")).ConfigureAwait(false);
                        if (reMeasured is not null)
                        {
                            gaugeTokens = reMeasured;
                            measuredPromptTokens = reMeasured;
                        }
                    }
                    if (gaugeTokens is not null)
                    {
                        ReportContextUsage?.Invoke(gaugeTokens.Value, hotContextForHailMary, Str(state, "local_preferred_model"));
                        ReportHailMaryApplied?.Invoke(gaugeTokens.Value);
                    }
                }
                }
            }

            // Local "context filling" block, now that the payload is finalized and we have the
            // model's real prompt token count. This is the safety check that stops a turn whose
            // prompt already fills the loaded window before it is sent to llama-server.
            //
            // Probe/stress-test requests are EXEMPT from this block: the test deliberately probes
            // near and past the context limit to measure TTFT/tok/s and observe the real
            // "context too small" behavior. Blocking the test's own requests would (a) prevent the
            // test from reaching its intended fill levels, and (b) surface false "Context is too
            // small" warnings in the UI when the gauge shows plenty of room (the test's "Clear"
            // prompt or a concurrent Cline request hitting a stale context state). The genuine
            // overflow is still caught: llama-server reports a real exceed_context_size_error,
            // which the test observes as the expected 400 at the limit.
            if (!isProbeRequest &&
                FluxMuxGatewayRouting.ShouldBlockLocalForward(
                    routeKind,
                    state,
                    payload,
                    cloudRecommendStatus,
                    forceRoute,
                    measuredPromptTokens))
            {
                // Update the context gauge with the CURRENT prompt's measured token count
                // before showing the block dialog. The gauge may be stale (from a previous
                // turn), so the user would see a misleading value. This ensures the gauge
                // matches the current context when the dialog appears.
                if (measuredPromptTokens is not null && hotContext > 0)
                {
                    ReportContextUsage?.Invoke(
                        measuredPromptTokens.Value,
                        hotContext,
                        Str(state, "local_preferred_model"));
                }

                // The turn is blocked for context reasons. Pass blockedForContext: true so the
                // reload offer always includes a "Compact and retry" escape, even if no reload
                // candidate is available. This is the graceful-escape fix: a blocked turn must
                // never strand the user with no recovery path.
                OfferLocalReloadIfNeeded(state, payload, measuredPromptTokens, blockedForContext: true);
                reloadOffered = IsLocalReloadOfferPending();
                var lastServed = ReadLastServedIdentity();
                // The overflow message must only reference Port rules when the profile's
                // compaction mode actually uses them. The profile's LocalAutoCompactMode is the
                // source of truth: "port-rules" → Port rules apply; "per-profile" → the
                // profile's own settings apply; "none" → no FluxMux compaction. Passing the
                // GLOBAL portRules.CompactEnabled here would misattribute the overflow to a
                // Port rule when the profile uses per-profile or none mode (even if global
                // Port rules are enabled). Resolve the profile's mode and gate on it.
                var overflowModel = Str(state, "local_preferred_model");
                var overflowVariant = Str(state, "local_variant");
                var overflowProfile = GetLocalAutoCompactSettings?.Invoke(overflowModel, overflowVariant);
                var overflowMode = overflowProfile.HasValue ? overflowProfile.Value.Mode : "port-rules";
                var portRulesActiveForProfile = portRules.CompactEnabled
                    && !overflowMode.Equals("per-profile", StringComparison.OrdinalIgnoreCase)
                    && !overflowMode.Equals("none", StringComparison.OrdinalIgnoreCase);
                var fillingDetails = FluxMuxGatewayRouting.LoadedLocalCannotTakeTurn(state, payload)
                    ? null
                    : PortRulesPostMortem.FormatContextOverflow(compactApplied, portRules, portRulesActiveForProfile);
                await SendOpenAiErrorAsync(
                    context.Response,
                    FluxMuxGatewayRouting.FormatBlockedLocalTurnMessage(
                        state,
                        payload,
                        promptChars,
                        lastServed.Kind,
                        lastServed.Label,
                        fillingDetails),
                    400,
                    FluxMuxGatewayRouting.LocalFillingBlockedType).ConfigureAwait(false);
                Log("local_context_filling: blocked llama-server on a filling turn n_ctx="
                    + hotContext.ToString(CultureInfo.InvariantCulture)
                    + " n_prompt=" + (measuredPromptTokens ?? EstimateNeededContext(payload)).ToString(CultureInfo.InvariantCulture)
                    + " route=" + routeKind);
                // Suppress the user-visible context event for probe requests (the context-fill
                // stress test deliberately drives prompts past the limit to observe the real
                // failure). The 400 response above is still returned so the test can observe
                // the genuine "context too small" behavior; we only skip the context *event*
                // that would otherwise surface as a spurious warning in the telemetry/UI.
                if (!isProbeRequest)
                {
                    EmitContextEvent(
                        "local_context_filling",
                        "provider=Local model=" + FirstNonEmpty(Str(state, "local_preferred_model"), Str(payload, "model"))
                        + " n_ctx=" + hotContext.ToString(CultureInfo.InvariantCulture)
                        + " n_prompt=" + (measuredPromptTokens ?? EstimateNeededContext(payload)).ToString(CultureInfo.InvariantCulture)
                        + " status=400");
                }
                // Suppress the user-visible post-mortem for probe requests (the context-fill
                // stress test deliberately drives prompts past the limit to observe the real
                // failure). The 400 response above is still returned so the test can observe
                // the genuine "context too small" behavior; we only skip the diagnostic log
                // entry that would otherwise surface as a spurious "PORT" warning in the UI.
                if (!isProbeRequest)
                {
                    ReportPortRulesFinding(fillingDetails);
                }
                return;
            }

            var beforeClamp = ParseInt(payload["max_tokens"]?.ToString() ?? string.Empty, requestedMax);
            var clamped = LocalRequestOverlayRouting.ClampMaxTokensToContext(payload, hotContext, portRules.CompactHeadroom);
            if (hotContext > 0 && clamped != beforeClamp)
            {
                Log("local max_tokens clamped from " + beforeClamp.ToString(CultureInfo.InvariantCulture)
                    + " to " + clamped.ToString(CultureInfo.InvariantCulture)
                    + " so prompt plus reply fit context " + hotContext.ToString(CultureInfo.InvariantCulture));
            }

            LocalReasoningPayloadPolicy.ApplyThinkingBudget(payload, localReasoningForResponse);
            if (payload["thinking_budget"] is not null)
            {
                Log("local chat: thinking_budget=" + payload["thinking_budget"]!.ToString());
            }

            if (FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute))
            {
                OfferLocalReloadIfNeeded(state, payload, measuredPromptTokens);
            }

            reloadOffered = IsLocalReloadOfferPending();
            if (LocalChatPayloadSignals.PayloadHasImage(payload)
                && !Str(state, "local_vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                && !LocalChatPayloadSignals.LatestUserTurnHasImage(payload))
            {
                if (LocalChatPayloadSignals.StripImagesFromPayload(payload))
                {
                    Log("local chat: dropped leftover pictures so the Images-off local can take this text turn");
                }
            }

            if (LocalChatPayloadSignals.PayloadHasImage(payload)
                && !Str(state, "local_vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute)
                    && FluxMuxGatewayRouting.RoutingOn(state)
                    && FluxMuxGatewayRouting.CapacityAllowsCloudPromotion(state))
                {
                    var hopKind = await ApplyCloudConsentAsync(
                        FirstNonEmpty(
                            FluxMuxGatewayRouting.LocalCannotCloudConsentReason,
                            "The loaded local cannot take this turn, so llama-server was not asked to start it."),
                        failOnTimeout: true,
                        waitForAnswer: RouteRecoveryPolicy.HoldInFlightRequestForCloudConsent(),
                        source: RouteRecoveryPolicy.CapacitySource).ConfigureAwait(false);
                    if (hopKind == FluxMuxGatewayRouting.Cloud)
                    {
                        state = LoadState();
                        (routeKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, FluxMuxGatewayRouting.Cloud);
                        model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                        payload["model"] = model;
                        localReasoningForResponse = null;
                        Log("local vision miss: sending this turn to the cloud after Switch to cloud");
                        if (!FluxMuxGatewayRouting.CloudTakesImages(state))
                        {
                            await SendOpenAiErrorAsync(
                                context.Response,
                                FluxMuxGatewayRouting.FormatCloudVisionUnavailableMessage(state, provider, preferred),
                                400,
                                FluxMuxGatewayRouting.CloudVisionUnavailableType).ConfigureAwait(false);
                            Log("cloud_vision_unavailable: picture turn blocked because "
                                + provider + " / " + preferred + " cannot take pictures");
                            return;
                        }
                    }
                }

                if (routeKind == "local")
                {
                    await SendOpenAiErrorAsync(
                        context.Response,
                        FluxMuxGatewayRouting.FormatLocalVisionUnavailableMessage(state),
                        400,
                        FluxMuxGatewayRouting.LocalVisionUnavailableType).ConfigureAwait(false);
                    Log("local_vision_unavailable: picture turn blocked because the loaded local profile has Images off");
                    return;
                }
            }

            if (routeKind == "local"
                && LocalRequestOverlayRouting.PromptExceedsContext(payload, hotContext, measuredPromptTokens: measuredPromptTokens))
            {
                var overflow = FluxMuxGatewayRouting.DecideOverflowFallback(state, payload);
                if (overflow.Kind == FluxMuxGatewayRouting.CloudConsent
                    && string.IsNullOrWhiteSpace(forceRoute))
                {
                    var overflowKind = await ApplyCloudConsentAsync(
                        FluxMuxGatewayRouting.AnnotateConsentReason(
                            FirstNonEmpty(overflow.ConsentReason ?? string.Empty, FluxMuxGatewayRouting.FillingCloudConsentReason),
                            state,
                            payload,
                            promptChars),
                        failOnTimeout: true,
                        waitForAnswer: RouteRecoveryPolicy.HoldInFlightRequestForCloudConsent(),
                        source: RouteRecoveryPolicy.CapacitySource).ConfigureAwait(false);
                    if (overflowKind == FluxMuxGatewayRouting.Cloud)
                    {
                        (routeKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, FluxMuxGatewayRouting.Cloud);
                        model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                        payload["model"] = model;
                        localReasoningForResponse = null;
                        Log("local overflow: sending this turn to the ready cloud after Use cloud");
                    }
                    else
                    {
                        overflow = new GatewayRouteResult(FluxMuxGatewayRouting.Local, OfferReload: true);
                    }
                }

                if (overflow.Kind == FluxMuxGatewayRouting.Cloud && string.IsNullOrWhiteSpace(forceRoute))
                {
                    (routeKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, FluxMuxGatewayRouting.Cloud);
                    model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                    payload["model"] = model;
                    localReasoningForResponse = null;
                    Log("local overflow: sending this turn to the ready cloud instead of llama-server");
                }
                else if (routeKind == "local")
                {
                    var overflowTokens = EstimateNeededContext(payload);
                    var lastServed = ReadLastServedIdentity();
                    // Update the context gauge with the CURRENT prompt's measured token count
                    // before showing the overflow dialog. The gauge may be stale (from a previous
                    // turn), so the user would see a misleading value.
                    if (measuredPromptTokens is not null && hotContext > 0)
                    {
                        ReportContextUsage?.Invoke(
                            measuredPromptTokens.Value,
                            hotContext,
                            Str(state, "local_preferred_model"));
                    }
                    // Use the profile-aware compaction mode (same pattern as the filling-block
                    // path above) so the overflow message only references Port rules when the
                    // profile's compaction mode actually uses them.
                    var overflowModel2 = Str(state, "local_preferred_model");
                    var overflowVariant2 = Str(state, "local_variant");
                    var overflowProfile2 = GetLocalAutoCompactSettings?.Invoke(overflowModel2, overflowVariant2);
                    var overflowMode2 = overflowProfile2.HasValue ? overflowProfile2.Value.Mode : "port-rules";
                    var portRulesActiveForProfile2 = portRules.CompactEnabled
                        && !overflowMode2.Equals("per-profile", StringComparison.OrdinalIgnoreCase)
                        && !overflowMode2.Equals("none", StringComparison.OrdinalIgnoreCase);
                    var overflowDetails = PortRulesPostMortem.FormatContextOverflow(compactApplied, portRules, portRulesActiveForProfile2);
                    await SendOpenAiErrorAsync(
                        context.Response,
                        FluxMuxGatewayRouting.FormatLocalContextOverflowMessage(
                            state,
                            lastServed.Kind,
                            lastServed.Label,
                            overflowDetails),
                        400,
                        "exceed_context_size_error").ConfigureAwait(false);
                    Log("exceed_context_size_error: reconnect/prompt already fills local_context=" + hotContext.ToString(CultureInfo.InvariantCulture));
                    EmitContextEvent(
                        "local_context_overflow",
                        "provider=" + provider
                        + " model=" + model
                        + " n_ctx=" + hotContext.ToString(CultureInfo.InvariantCulture)
                        + " n_prompt=" + overflowTokens.ToString(CultureInfo.InvariantCulture)
                        + " status=400");
                    if (!isProbeRequest)
                    {
                        ReportPortRulesFinding(overflowDetails);
                        // Offer the Hail Mary banner: the non-destructive prune pipeline
                        // (stages 1-4) already ran in TryApplyForwardCompact. If the turn
                        // still can't fit, the user can click the button to run the
                        // destructive stage-5 middle-cut and re-send.
                        ReportHailMaryOffer?.Invoke(
                            "hail-mary: this turn is too large for the loaded Context even after pruning. "
                            + "Click 'Hail Mary: shrink this turn' to cut the middle of the history and re-send.");
                    }
                    return;
                }
            }

            if (routeKind == "local"
                && portRules.RepeatedCommandEnabled
                && FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute)
                && LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out var repeatedCommand))
            {
                Log("local chat: Client app repeated the same command: " + Truncate(repeatedCommand, 80));
                var finding = PortRulesPostMortem.FormatRepeatedCommand();
                var pause = await AskPortRulePauseAsync(finding, portRules).ConfigureAwait(false);
                if (pause == FluxMuxGatewayRouting.Cloud)
                {
                    state = LoadState();
                    (routeKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, FluxMuxGatewayRouting.Cloud);
                    model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                    payload["model"] = model;
                    localReasoningForResponse = null;
                    Log("local tool loop: sending this turn to the ready cloud after Switch to cloud");
                }
                else if (pause == RouteRecoveryPolicy.WaitStatus)
                {
                    NotePortRuleSendAnyway();
                    Log("port rule pause: sending this repeated-command turn anyway");
                }
                else if (pause == RouteRecoveryPolicy.SteerStatus)
                {
                    await SendAssistantNoticeAsync(
                        context.Response,
                        PortRulesPostMortem.FormatSteerNotice(finding)).ConfigureAwait(false);
                    ReportPortRulesFinding(finding);
                    return;
                }
                else if (routeKind == "local")
                {
                    await SendOpenAiErrorAsync(
                        context.Response,
                        FluxMuxGatewayRouting.FormatRepeatedToolMessage(state, repeatedCommand),
                        400,
                        FluxMuxGatewayRouting.RepeatedToolType).ConfigureAwait(false);
                    Log("cline_repeated_command: llama-server was not asked");
                    EmitContextEvent(
                        "cline_repeated_command",
                        "provider=" + provider
                        + " model=" + model
                        + " status=400");
                    ReportPortRulesFinding(finding);
                    return;
                }
            }

            if (routeKind == "local"
                && haltAsToolMill)
            {
                var finding = PortRulesPostMortem.FormatMill(
                    clearedResults,
                    rapidChurn,
                    rapidStreak,
                    portRules);
                var pause = await AskPortRulePauseAsync(finding ?? string.Empty, portRules).ConfigureAwait(false);
                if (pause == FluxMuxGatewayRouting.Cloud)
                {
                    state = LoadState();
                    (routeKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, FluxMuxGatewayRouting.Cloud);
                    model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                    payload["model"] = model;
                    localReasoningForResponse = null;
                    Log("port rule pause: sending this mill turn to the ready cloud");
                }
                else if (pause == RouteRecoveryPolicy.WaitStatus)
                {
                    NotePortRuleSendAnyway();
                    Log("port rule pause: sending this mill turn anyway");
                }
                else if (pause == RouteRecoveryPolicy.SteerStatus)
                {
                    await SendAssistantNoticeAsync(
                        context.Response,
                        PortRulesPostMortem.FormatSteerNotice(finding)).ConfigureAwait(false);
                    ReportPortRulesFinding(finding);
                    return;
                }
                else
                {
                    await SendOpenAiErrorAsync(
                        context.Response,
                        FluxMuxGatewayRouting.FormatToolMillMessage(
                            state,
                            clearedResults,
                            rapidChurn,
                            rapidStreak,
                            portRules),
                        400,
                        FluxMuxGatewayRouting.ToolMillType).ConfigureAwait(false);
                    Log("cline_tool_mill: llama-server was not asked after omitting "
                        + clearedResults.ToString(CultureInfo.InvariantCulture)
                        + " older tool result(s)"
                        + (rapidChurn ? " (rapid churn)" : string.Empty));
                    EmitContextEvent(
                        FluxMuxGatewayRouting.ToolMillType,
                        "provider=" + provider
                        + " model=" + model
                        + " n_omitted=" + clearedResults.ToString(CultureInfo.InvariantCulture)
                        + " status=400");
                    ReportPortRulesFinding(finding);
                    return;
                }
            }

            if (routeKind == "local" && portRules.DiagnosticDumpEnabled && sessionArtifacts.ShouldHalt)
            {
                var finding = PortRulesPostMortem.FormatDiagnosticDump(sessionArtifacts.ArtifactCount);
                var pause = await AskPortRulePauseAsync(finding, portRules).ConfigureAwait(false);
                if (pause == FluxMuxGatewayRouting.Cloud)
                {
                    state = LoadState();
                    (routeKind, upstreamUrl, upstreamMode, provider, preferred) = BindRoute(state, FluxMuxGatewayRouting.Cloud);
                    model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                    payload["model"] = model;
                    localReasoningForResponse = null;
                    Log("port rule pause: sending this dump turn to the ready cloud");
                }
                else if (pause == RouteRecoveryPolicy.WaitStatus)
                {
                    NotePortRuleSendAnyway();
                    Log("port rule pause: sending this dump turn anyway");
                }
                else if (pause == RouteRecoveryPolicy.SteerStatus)
                {
                    await SendAssistantNoticeAsync(
                        context.Response,
                        PortRulesPostMortem.FormatSteerNotice(finding)).ConfigureAwait(false);
                    ReportPortRulesFinding(finding);
                    return;
                }
                else
                {
                    await SendOpenAiErrorAsync(
                        context.Response,
                        LocalSessionArtifactPolicy.FormatHaltMessage(state),
                        400,
                        LocalSessionArtifactPolicy.HaltType).ConfigureAwait(false);
                    Log("cline_diagnostic_dump: llama-server was not asked after "
                        + sessionArtifacts.ArtifactCount.ToString(CultureInfo.InvariantCulture)
                        + " session diagnostic file(s)");
                    EmitContextEvent(
                        LocalSessionArtifactPolicy.HaltType,
                        "provider=" + provider
                        + " model=" + model
                        + " n_dump=" + sessionArtifacts.ArtifactCount.ToString(CultureInfo.InvariantCulture)
                        + " status=400");
                    ReportPortRulesFinding(finding);
                    return;
                }
            }
        }

        payload["model"] = model;

        if (routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(forceRoute)
            && CloudCircuitBreaker.TryGetBlock(_cloudBreakerPath, provider, model, out var breakerBlock))
        {
            if (LocalTargetReady(state))
            {
                Log("cloud breaker open; failing over to ready local provider=" + provider + " model=" + model + " reason=" + breakerBlock.Reason);
                EmitContextEvent("cloud_breaker_failover_local", "provider=" + provider + " model=" + model + " reason=" + breakerBlock.Reason);
                (routeKind, upstreamUrl, upstreamMode, provider, preferred) = ResolveTarget(state, payload, "local");
                model = string.IsNullOrWhiteSpace(preferred) ? requested : preferred;
                payload["model"] = model;
                localReasoningForResponse = Str(state, "local_reasoning", "Off");
                await PrepareLocalChatPayloadAsync(payload, state).ConfigureAwait(false);
                var failoverOverlay = LocalRequestOverlayRouting.Pick(state, payload, requested);
                overlayVariant = failoverOverlay?["variant"]?.ToString();
                LocalRequestOverlayRouting.Apply(payload, failoverOverlay);
                var failoverContext = ParseInt(Str(state, "local_context"), 0);
                LocalRequestOverlayRouting.ClampMaxTokensToContext(payload, failoverContext);
            }
            else
            {
                OfferCloudFailureRecovery(state, provider, model, breakerBlock.Reason);
                await SendOpenAiErrorAsync(
                    context.Response,
                    breakerBlock.Message,
                    breakerBlock.Reason.Equals("rate_limited", StringComparison.OrdinalIgnoreCase) ? 429 : 503,
                    "cloud_breaker_open",
                    headers: new Dictionary<string, string>
                    {
                        ["Retry-After"] = breakerBlock.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture)
                    }).ConfigureAwait(false);
                Log("cloud breaker open reason=" + breakerBlock.Reason + " provider=" + provider + " model=" + model);
                EmitContextEvent("cloud_breaker_open", "provider=" + provider + " model=" + model + " reason=" + breakerBlock.Reason);
                return;
            }
        }

        if (routeKind == "cloud" && provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var patched = GeminiThoughtSignaturePassthrough.EnsureSkipSignaturesForMissingThoughts(payload);
            if (patched > 0)
            {
                Log("gemini thought_signature: filled " + patched.ToString(CultureInfo.InvariantCulture)
                    + " missing function-call signature(s) so a Cline tool turn can continue");
            }
        }

        JsonNode requestPayload = payload;
        IReadOnlyList<string>? localDeclaredTools = null;
        var localAllowParallel = true;
        if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            var names = LocalToolCallHealing.CollectDeclaredToolNames(payload);
            if (names.Count > 0)
            {
                localDeclaredTools = names;
                localAllowParallel = LocalToolCallHealing.AllowsParallelToolCalls(payload);
            }
        }

        if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            LocalStopHygiene.Apply(payload, portRules.StopHygieneMode);
            LocalPrefixCachePolicy.ApplyAfterCompact(payload, compactApplied, portRules);
        }

        if (upstreamMode == "anthropic")
        {
            requestPayload = MapOpenAiToAnthropic(payload, state);
        }

        HttpRequestMessage CreateUpstreamRequest()
        {
            var created = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
            {
                Content = new StringContent(requestPayload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            ApplyUpstreamHeaders(created, state, provider);
            return created;
        }

        _generationSpeed?.Begin(routeKind);
        var sseStarted = false;
        LocalStreamHangClock? hangClock = null;
        try
        {
            // Detect long-load profile (YaRN / extended-context / GPU+CPU) BEFORE computing
            // the copy timeout, so RequestTimeoutForPayload can apply the ThinkingFirstByteSeconds
            // extension. Previously this detection ran AFTER the timeout was computed, so the
            // extension was never applied and the copy timeout was too short for cold YaRN loads.
            _currentTurnIsLongLoad = false;
            if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var localCtxForTimeout = ParseInt(Str(state, "local_context"), 0);
                var offloadForTimeout = Str(state, "local_gpu_offload");
                if (LocalLoadingRetryPolicy.IsLongLoadProfile(localCtxForTimeout, offloadForTimeout))
                {
                    _currentTurnIsLongLoad = true;
                }
                Log("long-load detect: ctx=" + localCtxForTimeout
                    + " offload=" + (string.IsNullOrWhiteSpace(offloadForTimeout) ? "(empty)" : offloadForTimeout)
                    + " isLongLoad=" + _currentTurnIsLongLoad
                    + " local_phase=" + Str(state, "local_phase")
                    + " local_preferred_model=" + Str(state, "local_preferred_model"));
            }
            var timeoutSec = RequestTimeoutForPayload(
                requestPayload as JsonObject ?? payload,
                localStream: routeKind.Equals("local", StringComparison.OrdinalIgnoreCase),
                portRules,
                longLoadProfile: _currentTurnIsLongLoad);
            // For long-load profiles (YaRN / extended-context / GPU+CPU), the 503-retry
            // loop can run for minutes while llama-server loads from cold. The copy
            // timeout above is sized for the "loaded but slow first byte" phase and is
            // shorter than that, so it would cancel the retry loop early. Extend the
            // timeout to cover the long-load retry window so the loop can ride out the
            // cold load instead of being cut off and misreported as a failure.
            if (_currentTurnIsLongLoad)
            {
                var localCtxForTimeout = ParseInt(Str(state, "local_context"), 0);
                var offloadForTimeout = Str(state, "local_gpu_offload");
                var longLoadWindowSec = LocalLoadingRetryPolicy.EffectiveMaxTotalWaitSeconds(localCtxForTimeout, offloadForTimeout)
                    + portRules.LoadingRetryDelaySeconds
                    + 30; // margin for the final probe + overhead
                timeoutSec = Math.Max(timeoutSec, longLoadWindowSec);
            }
            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            // Only log the [turn] line when we have a real token measurement. During launch
            // (503 retries) the measurement is null, and logging "n/a tokens" is just noise.
            if (measuredPromptTokens is not null)
            {
                var pruneBit = forwardPruneSummary is not null ? " · " + forwardPruneSummary : string.Empty;
                Log("[turn] " + routeKind + " · " + (DateTime.Now - turnStart).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms" + pruneBit);
            }
            HttpResponseMessage? response = null;
            var loadingRetries = 0;
            string? loadingDetail = null;
            int status;
            while (true)
            {
                response?.Dispose();
                using var request = CreateUpstreamRequest();
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                Log("upstream request: status=" + status
                    + " route=" + routeKind
                    + " loadingRetries=" + loadingRetries
                    + " timeoutSec=" + timeoutSec);
                if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
                    && !response.IsSuccessStatusCode)
                {
                    loadingDetail = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                    // Pass the active profile's context + offload mode so the retry
                    // budget adapts: YaRN / extended-context / GPU+CPU profiles load
                    // slowly from cold and need a much longer 503-retry window than
                    // the default, otherwise the gateway gives up and converts the
                    // "still loading" into a non-transient error that fails the launch.
                    var localCtxForRetry = ParseInt(Str(state, "local_context"), 0);
                    var offloadForRetry = Str(state, "local_gpu_offload");
                    if (LocalLoadingRetryPolicy.ShouldRetry(status, loadingDetail, loadingRetries, portRules, localCtxForRetry, offloadForRetry))
                    {
                        loadingRetries++;
                        PatchPortRulesTelemetry(prev => prev with
                        {
                            HasLocalTurn = true,
                            LoadingRetries = loadingRetries
                        });
                        Log("local loading retry "
                            + loadingRetries.ToString(CultureInfo.InvariantCulture)
                            + "/"
                            + portRules.LoadingRetryCount.ToString(CultureInfo.InvariantCulture)
                            + " after llama-server 503");
                        await Task.Delay(TimeSpan.FromSeconds(portRules.LoadingRetryDelaySeconds), timeoutCts.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                break;
            }

            using (response)
            {
            var contentType = response!.Content.Headers.ContentType?.ToString() ?? "application/json";

            if (!response.IsSuccessStatusCode)
            {
                var detail = loadingDetail
                    ?? await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var (message, errorType, mappedStatus, _) = ClassifyUpstreamError(status, detail, provider, model);
                if (routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase))
                {
                    var retryAfter = status == 429 ? TryParseRetryAfterSeconds(response) : null;
                    CloudCircuitBreaker.RecordFailure(_cloudBreakerPath, provider, model, errorType, retryAfter);
                    if (FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute)
                        && FluxMuxUpstreamErrorClassifier.ShouldOfferRouteSwitch(errorType))
                    {
                        OfferCloudFailureRecovery(state, provider, model, errorType);
                    }
                }

                if (errorType == "exceed_context_size_error")
                {
                    if (routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase))
                    {
                        message = FluxMuxGatewayRouting.FormatNewSlotEndpointMessage(
                            LocalReloadRouting.FormatCloudContextLead(
                                ProxyRuntimeStateSecrets.FormatCloudLabel(provider, model)),
                            Str(state, "endpoint_app"));
                    }
                    else
                    {
                        var lastServed = ReadLastServedIdentity();
                        // Use the profile-aware compaction mode so the overflow message only
                        // references Port rules when the profile's compaction mode actually uses them.
                        var overflowModel3 = Str(state, "local_preferred_model");
                        var overflowVariant3 = Str(state, "local_variant");
                        var overflowProfile3 = GetLocalAutoCompactSettings?.Invoke(overflowModel3, overflowVariant3);
                        var overflowMode3 = overflowProfile3.HasValue ? overflowProfile3.Value.Mode : "port-rules";
                        var portRulesActiveForProfile3 = portRules.CompactEnabled
                            && !overflowMode3.Equals("per-profile", StringComparison.OrdinalIgnoreCase)
                            && !overflowMode3.Equals("none", StringComparison.OrdinalIgnoreCase);
                        message = FluxMuxGatewayRouting.FormatLocalContextOverflowMessage(
                            state,
                            lastServed.Kind,
                            lastServed.Label,
                            PortRulesPostMortem.FormatContextOverflow(compactApplied, portRules, portRulesActiveForProfile3));
                    }
                }
                else if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
                    && portRules.Loading503Enabled
                    && LocalLoadingRetryPolicy.LooksLikeDaemonLoading(status, detail))
                {
                    message = PortRulesPostMortem.WithStopAdvice(
                        PortRulesPostMortem.FormatLoading503(loadingRetries, portRules));
                }

                await SendOpenAiErrorAsync(context.Response, message, mappedStatus, errorType).ConfigureAwait(false);
                var eventType = errorType == "exceed_context_size_error" ? "local_context_overflow" : errorType;
                Log($"{errorType} provider={provider} model={model} status={mappedStatus} detail={Truncate(detail, 400)}");
                if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
                    && errorType == "exceed_context_size_error")
                {
                    if (!isProbeRequest)
                    {
                        var overflowModel4 = Str(state, "local_preferred_model");
                        var overflowVariant4 = Str(state, "local_variant");
                        var overflowProfile4 = GetLocalAutoCompactSettings?.Invoke(overflowModel4, overflowVariant4);
                        var overflowMode4 = overflowProfile4.HasValue ? overflowProfile4.Value.Mode : "port-rules";
                        var portRulesActiveForProfile4 = portRules.CompactEnabled
                            && !overflowMode4.Equals("per-profile", StringComparison.OrdinalIgnoreCase)
                            && !overflowMode4.Equals("none", StringComparison.OrdinalIgnoreCase);
                        ReportPortRulesFinding(PortRulesPostMortem.FormatContextOverflow(compactApplied, portRules, portRulesActiveForProfile4));
                        // Offer the Hail Mary banner (upstream 400 path).
                        ReportHailMaryOffer?.Invoke(
                            "hail-mary: this turn is too large for the loaded Context even after pruning. "
                            + "Click 'Hail Mary: shrink this turn' to cut the middle of the history and re-send.");
                    }
                }
                else if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
                    && portRules.Loading503Enabled
                    && LocalLoadingRetryPolicy.LooksLikeDaemonLoading(status, detail))
                {
                    ReportPortRulesFinding(PortRulesPostMortem.FormatLoading503(loadingRetries, portRules));
                }
                var overflowDetail = errorType == "exceed_context_size_error"
                    ? "provider=" + provider
                      + " model=" + model
                      + " n_ctx=" + ParseInt(Str(state, "local_context"), 0).ToString(CultureInfo.InvariantCulture)
                      + " n_prompt=" + EstimateNeededContext(payload).ToString(CultureInfo.InvariantCulture)
                      + " status=" + mappedStatus.ToString(CultureInfo.InvariantCulture)
                    : "provider=" + provider + " model=" + model + " status=" + mappedStatus.ToString(CultureInfo.InvariantCulture);
                EmitContextEvent(eventType, overflowDetail);
                return;
            }

            if (upstreamMode == "anthropic")
            {
                var upstreamBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                JsonObject parsed;
                try
                {
                    parsed = JsonNode.Parse(upstreamBody) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    parsed = new JsonObject();
                }

                await SendJsonAsync(context.Response, status, MapAnthropicToOpenAi(parsed, model)).ConfigureAwait(false);
                _generationSpeed?.ProcessJsonBody(Encoding.UTF8.GetBytes(upstreamBody));
                Log($"upstream ok provider={provider} model={model} status={status}");
                EmitContextEvent("upstream_ok", $"provider={provider} model={model} status={status}");
                NoteSuccessfulUpstream(routeKind, provider, model, overlayVariant, compactApplied, reloadOffered, cloudConsentOffered, requestId);
                return;
            }

            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = status;
                context.Response.ContentType = contentType;
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.KeepAlive = true;
                context.Response.SendChunked = true;
                var openBytes = Encoding.UTF8.GetBytes(OpenAiStreamTelemetryProxy.StreamOpenComment);
                await context.Response.OutputStream.WriteAsync(openBytes, timeoutCts.Token).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                sseStarted = true;
                await using var upstream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                using var hangCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                var gotUpstreamBytes = false;
                hangClock = new LocalStreamHangClock(portRules);
                if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
                {
                    var baseDeadline = LocalStreamHangPolicy.FirstByteDeadlineForThinkBudget(
                        ReadThinkingBudget(payload),
                        portRules);
                    // For long-load profiles (YaRN / extended-context / GPU+CPU), the
                    // first-byte deadline must be much longer than the normal 25s/120s
                    // because the model can take minutes to finish loading from cold.
                    // Use the ThinkingFirstByteSeconds port rule (default 600s) so the
                    // hang policy doesn't kill the turn while the model is still loading.
                    if (_currentTurnIsLongLoad)
                    {
                        var thinkingDeadline = TimeSpan.FromSeconds(portRules.ThinkingFirstByteSeconds);
                        hangClock.FirstByteDeadline = thinkingDeadline > baseDeadline ? thinkingDeadline : baseDeadline;
                        Log("hang clock: long-load profile, FirstByteDeadline="
                            + (int)hangClock.FirstByteDeadline.TotalSeconds
                            + "s (ThinkingFirstByteSeconds=" + portRules.ThinkingFirstByteSeconds
                            + ", baseDeadline=" + (int)baseDeadline.TotalSeconds + "s)");
                    }
                    else
                    {
                        hangClock.FirstByteDeadline = baseDeadline;
                        Log("hang clock: normal profile, FirstByteDeadline="
                            + (int)hangClock.FirstByteDeadline.TotalSeconds + "s");
                    }
                }

                var hangWatch = routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
                    && portRules.HangEnabled
                    ? WatchLocalStreamHangAsync(
                        () => gotUpstreamBytes,
                        hangClock,
                        hangCts,
                        timeoutCts)
                    : Task.CompletedTask;
                var responseSanitized = false;
                var replyBudget = ParseInt(payload["max_tokens"]?.ToString() ?? string.Empty, 0);
                // Time-to-first-byte telemetry: record when the request started copying and
                // when the first upstream byte arrived. This is the number that's >600s on a
                // cold YaRN launch — it tells us whether the slowness is in the launch (model
                // load + KV cache allocation) or in the first request (lazy KV allocation).
                var ttfbStopwatch = Stopwatch.StartNew();
                var firstByteLogged = false;
                try
                {
                    var copy = await OpenAiStreamTelemetryProxy.CopyAsync(
                        upstream,
                        context.Response.OutputStream,
                        _generationSpeed,
                        localReasoningForResponse,
                        hangCts.Token,
                        _ =>
                        {
                            if (!gotUpstreamBytes)
                            {
                                gotUpstreamBytes = true;
                                if (!firstByteLogged)
                                {
                                    firstByteLogged = true;
                                    Log($"ttfb: first byte after {ttfbStopwatch.ElapsedMilliseconds} ms (model={model})");
                                }
                            }
                            hangClock.NoteByte();
                        },
                        localDeclaredTools,
                        localAllowParallel,
                        sessionArtifacts,
                        replyBudget).ConfigureAwait(false);
                    if (firstByteLogged)
                    {
                        Log($"ttfb: stream complete after {ttfbStopwatch.ElapsedMilliseconds} ms total (model={model})");
                    }
                    responseSanitized = copy.Sanitized;
                    if (copy.ThinkCutOff)
                    {
                        Log("local_think_cutoff: llama-server was still thinking when this turn ended; Client app was told why");
                        EmitContextEvent(
                            LocalThinkBudgetNotice.Type,
                            "provider=" + provider + " model=" + model + " stream=true");
                        ReportPortRulesFinding(LocalThinkBudgetNotice.FormatDiagnostics(
                            localReasoningForResponse,
                            replyBudget,
                            compactApplied));
                    }
                    else if (copy.ThinkOnly)
                    {
                        Log("local_think_only: llama-server only produced thinking; Cline was not given an empty complete");
                    EmitContextEvent(
                        "local_think_only",
                        "provider=" + provider + " model=" + model + " stream=true");
                    ReportPortRulesFinding(PortRulesPostMortem.FormatThinkOnly(portRules));
                    }

                    if (copy.Healed)
                    {
                        Log("local chat: healed malformed llama-server tool call(s) so the Client app received structured tool_calls");
                        EmitContextEvent(
                            "local_tool_heal",
                            "provider=" + provider + " model=" + model + " stream=true");
                    }

                    if (copy.BlockedWrites)
                    {
                        Log("local chat: blocked diagnostic write_to_file from reaching the Client app (stream)");
                    }
                }
                finally
                {
                    hangCts.Cancel();
                    try
                    {
                        await hangWatch.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                await context.Response.OutputStream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                Log($"upstream ok (stream) provider={provider} model={model} status={status}");
                if (responseSanitized)
                {
                    Log("local chat: stripped thinking markup from endpoint response (stream)");
                }
                EmitContextEvent("upstream_ok", $"provider={provider} model={model} status={status} stream=true");
                NoteSuccessfulUpstream(routeKind, provider, model, overlayVariant, compactApplied, reloadOffered, cloudConsentOffered, requestId);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            _generationSpeed?.ProcessJsonBody(bytes);
            if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var (sanitized, strippedThinking, healed) = LocalEndpointResponseSanitizer.SanitizeJsonBody(
                    bytes,
                    localReasoningForResponse,
                    localDeclaredTools,
                    localAllowParallel,
                    sessionArtifacts);
                bytes = sanitized;
                if (strippedThinking)
                {
                    Log("local chat: stripped thinking markup from endpoint response");
                }

                if (healed)
                {
                    Log("local chat: healed malformed llama-server tool call(s) so the Client app received structured tool_calls");
                    EmitContextEvent(
                        "local_tool_heal",
                        "provider=" + provider + " model=" + model + " status=" + status.ToString(CultureInfo.InvariantCulture));
                }

                if (strippedThinking
                    && bytes.Length > 0
                    && TryParseCompletion(bytes, out var completion)
                    && LocalThinkOnlyReply.CompletionIsThinkOnly(
                        completion,
                        localReasoningForResponse,
                        strippedThinking))
                {
                    await SendOpenAiErrorAsync(
                        context.Response,
                        FluxMuxGatewayRouting.FormatThinkOnlyMessage(state, portRules),
                        400,
                        FluxMuxGatewayRouting.ThinkOnlyType).ConfigureAwait(false);
                    Log("local_think_only: llama-server only produced thinking; Cline was not given an empty complete");
                    EmitContextEvent(
                        "local_think_only",
                        "provider=" + provider + " model=" + model + " status=400");
                    ReportPortRulesFinding(PortRulesPostMortem.FormatThinkOnly(portRules));
                    return;
                }
            }

            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, timeoutCts.Token).ConfigureAwait(false);
            Log($"upstream ok provider={provider} model={model} status={status}");
            EmitContextEvent("upstream_ok", $"provider={provider} model={model} status={status}");
            NoteSuccessfulUpstream(routeKind, provider, model, overlayVariant, compactApplied, reloadOffered, cloudConsentOffered, requestId);
            }
        }
        catch (Exception ex)
        {
            var exStr = ex.Message;
            var (message, errorType, status, hint) = ClassifyUpstreamError(0, string.Empty, provider, model, exStr);
            if (routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase))
            {
                CloudCircuitBreaker.RecordFailure(_cloudBreakerPath, provider, model, errorType);
                if (FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute)
                    && FluxMuxUpstreamErrorClassifier.ShouldOfferRouteSwitch(errorType))
                {
                    OfferCloudFailureRecovery(state, provider, model, errorType);
                }
            }

            if (sseStarted && routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                var endpointApp = Str(LoadState(), "endpoint_app");
                string? hangDetails = null;
                if (hangClock is not null)
                {
                    var quietSeconds = (int)(DateTime.UtcNow - hangClock.CopyStartUtc).TotalSeconds;
                    hangDetails = PortRulesPostMortem.FormatHang(
                        hangClock.LastAbortReason,
                        quietSeconds,
                        hangClock.WaitLongerCount,
                        (int)hangClock.FirstByteDeadline.TotalSeconds,
                        portRules,
                        localModelReady: LocalTargetReady(LoadState()),
                        portRulesActive: portRules.Enabled);
                    ReportPortRulesFinding(hangDetails);
                }

                message = LocalStreamHangPolicy.FormatHangAbortMessage(endpointApp, hangDetails);
                hint = string.Empty;
                errorType = "request_timeout";
                var park = ParkLocalAfterHangAsync;
                if (park is not null)
                {
                    _ = park();
                }

                var recStatus = Str(LoadRecommend(), "status");
                if (FluxMuxGatewayRouting.AllowsOperatorRouting(forceRoute)
                    && recStatus is not ("pending" or "yes" or "no" or "timeout"))
                {
                    await ApplyCloudConsentAsync(
                        message,
                        failOnTimeout: true,
                        waitForAnswer: false,
                        source: RouteRecoveryPolicy.LocalHangAbortSource).ConfigureAwait(false);
                }
            }

            await FinishClientChatErrorAsync(
                context.Response,
                message,
                status,
                errorType,
                hint,
                sseStarted).ConfigureAwait(false);
            Log($"{errorType} provider={provider} model={model} detail={exStr}"
                + (sseStarted ? " (ended SSE so the Client app can stop waiting)" : string.Empty));
            EmitContextEvent(errorType, $"provider={provider} model={model}");
        }
        finally
        {
            _generationSpeed?.Complete();
        }
    }

    private static void ApplyUpstreamHeaders(HttpRequestMessage request, JsonObject state, string provider)
    {
        switch (provider)
        {
            case "Gemini":
                AddBearer(request, Str(state, "gemini_key"));
                break;
            case "Anthropic":
                var anthropicKey = Str(state, "anthropic_key");
                if (!string.IsNullOrWhiteSpace(anthropicKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", anthropicKey);
                }

                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            case "OpenAI":
                AddBearer(request, Str(state, "openai_key"));
                break;
            case "Copilot GitHub":
                AddBearer(request, Str(state, "copilot_key"));
                CopilotIntegratorHeaders.Apply(request);
                break;
            case "Custom OpenAI-Compatible":
                ApplyCustomCompatAuth(request, state);
                break;
            default:
                if (ParseBool(Str(state, "is_custom_compat")))
                {
                    ApplyCustomCompatAuth(request, state);
                }

                break;
        }
    }

    private static void ApplyCustomCompatAuth(HttpRequestMessage request, JsonObject state)
    {
        var key = Str(state, "custom_key");
        var authMode = Str(state, "custom_auth_mode", "Bearer");
        if (authMode.Equals("API Key Header", StringComparison.OrdinalIgnoreCase))
        {
            var header = Str(state, "custom_api_key_header", "X-API-Key");
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(header))
            {
                request.Headers.TryAddWithoutValidation(header, key);
            }
        }
        else if (authMode.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            AddBearer(request, key);
        }
    }

    private static void AddBearer(HttpRequestMessage request, string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    // Catalog comes from proxy state only. Do not read ~/.cline here: Cline
    // reopens those files when the panel loads, and a lock on GET /models
    // can stop the webview on the second VS Code launch.
    private JsonObject ListModels(JsonObject state) => FluxMuxGatewayModels.List(state);

    internal static bool IsModelsListPath(string path)
    {
        var normalized = NormalizeListenerPath(path);
        return normalized.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("/models", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeListenerPath(string? path)
    {
        var text = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (text.Length > 1)
        {
            text = text.TrimEnd('/');
        }

        return text;
    }

    private async Task<JsonObject> WaitForRoutableStateAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(SwitchWaitSec);
        var state = LoadState();
        while (DateTime.UtcNow < deadline)
        {
            var phase = Str(state, "phase", "idle");
            if (HasReadyTarget(state) || phase is not ("launching" or "transitioning" or "warming"))
            {
                return state;
            }

            await Task.Delay(250).ConfigureAwait(false);
            state = LoadState();
        }

        return state;
    }

    private (string Kind, string Url, string UpstreamMode, string Provider, string Model) ResolveTarget(
        JsonObject state,
        JsonObject payload,
        string? forceRoute = null)
    {
        var kind = ClassifyRoute(state, payload, forceRoute);
        return BindRoute(state, kind);
    }

    private (string Kind, string Url, string UpstreamMode, string Provider, string Model) BindRoute(
        JsonObject state,
        string kind)
    {
        if (kind == "local")
        {
            var localPort = ParseInt(Str(state, "local_port"), _port > 0 ? _port + 1 : 8081);
            var model = FirstNonEmpty(Str(state, "local_preferred_model"), Str(state, "preferred_model"));
            return ("local", $"http://127.0.0.1:{localPort}/v1/chat/completions", "openai", "Local", model);
        }

        var provider = Str(state, "provider", "OpenAI");
        if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            provider = "OpenAI";
        }

        var cloudState = CloneObject(state);
        cloudState["mode"] = "cloud";
        cloudState["provider"] = provider;
        var (url, upstreamMode) = UpstreamForState(cloudState);
        var cloudModel = FirstNonEmpty(Str(state, "cloud_preferred_model"), Str(state, "preferred_model"));
        return ("cloud", url, upstreamMode, provider, cloudModel);
    }

    private string ClassifyRoute(JsonObject state, JsonObject payload, string? forceRoute = null)
    {
        var route = FluxMuxGatewayRouting.DecideRoute(state, payload, forceRoute);
        if (!string.IsNullOrWhiteSpace(forceRoute))
        {
            Log("forced route=" + route.Kind + " header=" + forceRoute.Trim());
            return route.Kind;
        }

        if (route.OfferReload)
        {
            OfferLocalReloadIfNeeded(state, payload);
        }

        return route.Kind;
    }

    private void OfferLocalReloadIfNeeded(JsonObject state, JsonObject payload, int? measuredPromptTokens = null, bool blockedForContext = false)
    {
        if (!LocalTargetReady(state))
        {
            return;
        }

        var hotModel = Str(state, "local_preferred_model");
        var hotVariant = Str(state, "local_variant");
        var hotVision = Str(state, "local_vision").Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        var hotReasoning = Str(state, "local_reasoning", "Off");
        var hotContext = ParseInt(Str(state, "local_context"), 0);
        var (needVision, needThinking) = LocalReloadRouting.EvaluateCapabilityNeeds(payload, hotVision, hotReasoning);
        var promptTokens = measuredPromptTokens ?? LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var turnTokens = promptTokens;
        var promptExceeds = LocalRequestOverlayRouting.PromptExceedsContext(payload, hotContext, measuredPromptTokens: measuredPromptTokens);
        var thisTurnHasImage = LocalChatPayloadSignals.LatestUserTurnHasImage(payload);

        var existing = LoadLocalReload();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ParseLong(Str(existing, "suppress_until"), 0) > now)
        {
            return;
        }

        var compactAlreadyOn = ParseBool(Str(existing, "forward_compact"));
        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision,
            needThinking,
            hotContext,
            turnTokens,
            promptExceeds,
            compactAlreadyOn,
            hotModel,
            hotVariant,
            thisTurnHasImage,
            blockedForContext);
        if (offer is null)
        {
            if (LocalReloadRouting.LocalCannotCoverTurn(null, needVision, promptExceeds))
            {
                OfferCloudWhenLocalCannot(state, needVision);
            }
            else if (LocalReloadRouting.ShouldClearLeftoverVisionMiss(
                Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase),
                ParseBool(Str(existing, "needVision")),
                needVision,
                needThinking,
                promptExceeds))
            {
                existing["status"] = "idle";
                existing["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                SaveLocalReload(existing);
                Log("local reload recommend cleared: leftover pictures are not a vision miss on this text turn");
            }

            return;
        }

        var pending = Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase);
        var candidate = offer.Candidate;
        var candModel = candidate is null ? string.Empty : Str(candidate, "model");
        var candVariant = candidate is null ? string.Empty : Str(candidate, "variant");
        if (pending)
        {
            var existingModel = Str(existing, "model");
            var samePending = existingModel.Equals(candModel, StringComparison.OrdinalIgnoreCase)
                && Str(existing, "variant").Equals(candVariant, StringComparison.OrdinalIgnoreCase);
            if (!samePending && !string.IsNullOrWhiteSpace(existingModel))
            {
                return;
            }
        }

        var display = candidate is null
            ? string.Empty
            : LocalReloadRouting.FormatPackLabel(candModel, candVariant);
        var lastServed = ReadLastServedIdentity();
        var prompt = LocalReloadRouting.BuildOfferReason(
            hotModel,
            hotVariant,
            hotVision,
            hotReasoning,
            hotContext,
            needVision,
            needThinking,
            turnTokens,
            candidate,
            offer.NearLimit,
            offer.DestinationTight,
            compactAlreadyOn,
            offer.BetterChance,
            true,
            lastServed.Kind,
            lastServed.Label);

        SaveLocalReload(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = prompt.Trim(),
            ["model"] = candModel,
            ["variant"] = candVariant,
            ["displayName"] = display,
            ["hotModel"] = hotModel,
            ["hotVariant"] = hotVariant,
            ["hotVision"] = hotVision ? "Enabled" : "Disabled",
            ["hotReasoning"] = hotReasoning,
            ["hotContext"] = hotContext,
            ["needVision"] = needVision,
            ["needThinking"] = needThinking,
            ["neededContext"] = turnTokens,
            ["nearLimit"] = offer.NearLimit,
            ["destinationTight"] = offer.DestinationTight,
            ["compactRecommended"] = offer.CompactRecommended,
            ["betterChance"] = offer.BetterChance,
            ["forward_compact"] = compactAlreadyOn,
            ["candVision"] = candidate is null ? string.Empty : Str(candidate, "vision"),
            ["candReasoning"] = candidate is null ? string.Empty : Str(candidate, "reasoning"),
            ["candContext"] = candidate is null ? 0 : ParseInt(Str(candidate, "context"), 0),
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log(candidate is null
            ? "local reload recommend pending: compact in place"
            : "local reload recommend pending: " + display);
        if (LocalReloadRouting.LocalCannotCoverTurn(offer, needVision, promptExceeds))
        {
            OfferCloudWhenLocalCannot(state, needVision);
        }
    }

    private void OfferCloudWhenLocalCannot(JsonObject state, bool needVision)
    {
        var existing = LoadRecommend();
        var status = Str(existing, "status");
        if (status is "pending" or "yes")
        {
            return;
        }

        var provider = FirstNonEmpty(Str(state, "last_cloud_provider"), Str(state, "provider"));
        var model = FirstNonEmpty(Str(state, "last_cloud_model"), Str(state, "cloud_preferred_model"));
        if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            provider = Str(state, "last_cloud_provider");
        }

        var label = string.IsNullOrWhiteSpace(model)
            ? "your last successful cloud model"
            : string.IsNullOrWhiteSpace(provider)
                ? model
                : provider + " / " + model;
        var why = needVision
            ? "No local model profile with Images on can take this picture turn."
            : "No local model profile can take this turn.";
        SaveRecommend(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = why
                + " Switch to "
                + label
                + "? That launches it if it is not already running. This Client-app chat can continue.",
            ["source"] = RouteRecoveryPolicy.LocalCannotSource,
            ["fail_on_timeout"] = true,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("local cannot cover: offering cloud " + label);
    }

    private void OfferCloudFailureRecovery(JsonObject state, string cloudProvider, string cloudModel, string errorType)
    {
        var reason = FluxMuxUpstreamErrorClassifier.FormatCloudFailureRecoveryReason(
            cloudProvider,
            cloudModel,
            errorType);
        SaveRecommend(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = reason,
            ["source"] = RouteRecoveryPolicy.CloudFailSource,
            ["fail_on_timeout"] = true,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("cloud fail recovery pending: " + errorType + " provider=" + cloudProvider + " model=" + cloudModel);
        if (!LocalTargetReady(state))
        {
            OfferLocalAfterCloudExhaustion(state, cloudProvider, cloudModel, errorType);
        }
    }

    private void OfferLocalAfterCloudExhaustion(JsonObject state, string cloudProvider, string cloudModel, string reason)
    {
        var existing = LoadLocalReload();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ParseLong(Str(existing, "suppress_until"), 0) > now)
        {
            return;
        }

        if (Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(Str(existing, "model")))
        {
            return;
        }

        var candidate = PickAnyLocalPoolCandidate(state);
        if (candidate is null)
        {
            Log("cloud exhaustion: no local Quick Select / validated local to recommend");
            return;
        }

        var candModel = Str(candidate, "model");
        var candVariant = Str(candidate, "variant");
        var display = LocalReloadRouting.FormatPackLabel(candModel, candVariant);
        var providerName = string.IsNullOrWhiteSpace(cloudProvider) ? "Cloud" : cloudProvider;
        var modelName = string.IsNullOrWhiteSpace(cloudModel) ? "cloud model" : cloudModel;
        var why = reason.Equals("quota_exhausted", StringComparison.OrdinalIgnoreCase)
            ? providerName + " / " + modelName + " looks out of quota or free-plan tokens."
            : reason.Equals("rate_limited", StringComparison.OrdinalIgnoreCase)
                ? providerName + " / " + modelName + " is rate-limited."
                : providerName + " / " + modelName + " is paused (" + reason + ").";
        var prompt = why
            + " Load " + display
            + " from Quick Select? "
            + ClineSwitchSurvivalPolicy.HarnessIfAlsoOnPortAdvice;

        SaveLocalReload(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = prompt,
            ["model"] = candModel,
            ["variant"] = candVariant,
            ["displayName"] = display,
            ["hotModel"] = string.Empty,
            ["hotVariant"] = string.Empty,
            ["hotVision"] = "Disabled",
            ["hotReasoning"] = "Off",
            ["hotContext"] = 0,
            ["needVision"] = false,
            ["needThinking"] = false,
            ["neededContext"] = 0,
            ["nearLimit"] = false,
            ["destinationTight"] = false,
            ["compactRecommended"] = false,
            ["forward_compact"] = false,
            ["candVision"] = Str(candidate, "vision"),
            ["candReasoning"] = Str(candidate, "reasoning"),
            ["candContext"] = ParseInt(Str(candidate, "context"), 0),
            ["cloud_failover"] = true,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("cloud exhaustion local recommend pending: " + display);
    }

    private static JsonObject? PickAnyLocalPoolCandidate(JsonObject state)
    {
        if (state["local_reload_pool"] is not JsonArray pool)
        {
            return null;
        }

        foreach (var node in pool)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(Str(item, "model")))
            {
                return item;
            }
        }

        return null;
    }

    private bool TryApplyForwardCompact(JsonObject payload, JsonObject state, bool compactOn, int? measuredPromptTokens = null)
    {
        var rules = CurrentRules();

        // Check if the user has chosen "full fidelity" for a document. In this case, we skip
        // auto-compaction to let the document run in RAM at full precision.
        var reloadState = LoadLocalReload();
        var docMode = Str(reloadState, "documentContextMode");
        bool fullFidelityActive = docMode.Equals("full_this_turn", StringComparison.OrdinalIgnoreCase)
            || docMode.Equals("full_n_turns", StringComparison.OrdinalIgnoreCase);

        // Look up per-profile auto-compact settings (independent of local_context).
        var model = Str(state, "local_preferred_model");
        var variant = Str(state, "local_variant");
        var perProfile = GetLocalAutoCompactSettings?.Invoke(model, variant);
        string? perProfileMode = null;
        if (perProfile.HasValue)
        {
            perProfileMode = perProfile.Value.Mode;
        }

        // "None / endpoint managed": the user explicitly opted out of gateway
        // compaction. Skip both the wizard path and the Port-rules fallback so the
        // endpoint (llama.cpp / the model itself) manages context. This check is
        // outside the autoContext > 0 gate so it works even when local_context is 0.
        //
        // A null result means the per-profile settings could not be resolved (no
        // config, no LocalProfiles, or the model::variant key is missing). In that
        // case we must NOT fall back to the Port-rules compaction below — doing so
        // would defeat the user's explicit "None / endpoint managed" opt-out. Treat
        // an unresolvable profile as "none" so the opt-out is honoured.
        if (!perProfile.HasValue || perProfileMode == "none")
        {
            return false;
        }

        // Wizard-controlled auto-compaction: independent of Port rules.
        // Fires when the prompt exceeds the trigger threshold (default 80% of native context).
        // Per-profile settings override Port rules defaults.
        if (!fullFidelityActive)
        {
            var autoContext = ParseInt(Str(state, "local_context"), 0);
            if (autoContext > 0)
            {
                // Determine which settings to use.
                bool autoCompactEnabled;
                double triggerPercent;
                int keepTurns;
                int keepToolResults;
                int pinUserChars;

                if (perProfile.HasValue)
                {
                    var (mode, trigger, keepT, keepTool, pinUser) = perProfile.Value;
                    autoCompactEnabled = mode == "per-profile";
                    triggerPercent = trigger;
                    keepTurns = keepT;
                    keepToolResults = keepTool;
                    pinUserChars = pinUser;
                }
                else
                {
                    // Fall back to Port rules defaults.
                    autoCompactEnabled = rules.AutoCompactEnabled;
                    triggerPercent = rules.AutoCompactTriggerPercent;
                    keepTurns = rules.AutoCompactKeepTurns;
                    keepToolResults = rules.AutoCompactKeepToolResults;
                    pinUserChars = rules.AutoCompactPinUserChars;
                }

                if (autoCompactEnabled)
                {
                    // Prefer the model's real token count over the chars/4 estimate for the
                    // trigger, so dense-tokenizer prompts are not compacted on an over-count.
                    var autoPromptTokens = measuredPromptTokens ?? LocalRequestOverlayRouting.EstimatePromptTokens(payload);
                    var triggerThreshold = (int)(autoContext * triggerPercent);
                    if (autoPromptTokens >= triggerThreshold)
                    {
                        var autoRules = rules with
                        {
                            CompactKeepTurns = keepTurns,
                            CompactToolKeepTurns = keepToolResults,
                            CompactPreservedUserChars = pinUserChars
                        };
                        if (LocalHistoryCompaction.TryCompactPayload(payload, autoRules, force: true))
                        {
                            Log("auto-compact: prompt " + autoPromptTokens.ToString(CultureInfo.InvariantCulture)
                                + " tokens exceeded trigger " + triggerThreshold.ToString(CultureInfo.InvariantCulture)
                                + " — shortened older turns before routing");
                            var autoInserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
                            if (autoInserted > 0)
                            {
                                Log("local chat: inserted a user turn so the Qwen template has a query");
                            }
                            return true;
                        }

                        Log("auto-compact: prompt " + autoPromptTokens.ToString(CultureInfo.InvariantCulture)
                            + " tokens exceeded trigger " + triggerThreshold.ToString(CultureInfo.InvariantCulture)
                            + " but older turns could not be shortened (too few turns, or one over-full turn)");
                    }
                }
            }
        }

        // Port-rules compaction: the original path, gated by CompactEnabled.
        // Skipped when full fidelity is active for a document, OR when the per-profile
        // autocompact mode is "per-profile" (the user opted into profile-specific compaction,
        // so port rules should not fire).
        if (!rules.CompactEnabled || fullFidelityActive || perProfileMode == "per-profile")
        {
            return false;
        }

        var hotContext = ParseInt(Str(state, "local_context"), 0);
        var promptTokens = measuredPromptTokens ?? LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        var promptExceeds = LocalRequestOverlayRouting.PromptExceedsContext(payload, hotContext, rules.CompactHeadroom, measuredPromptTokens);
        var filling = FluxMuxGatewayRouting.PromptFillsLocalContextForCompact(state, payload, measuredPromptTokens);
        if (!LocalHistoryCompaction.ShouldForwardCompact(
                compactOn,
                promptTokens,
                hotContext,
                promptExceeds,
                filling,
                rules))
        {
            return false;
        }

        if (!LocalHistoryCompaction.TryCompactPayload(payload, rules, force: true))
        {
            // Forward-compaction bailed out: "too few turns, or one over-full turn".
            // The over-full-turn case is exactly what Hail Mary's non-destructive prune
            // pipeline (stages 1-4: dedupe, loop-collapse, result-truncate, image-strip)
            // fixes without a model round-trip. Run it automatically as a last resort.
            // Stage 5 (the blunt middle-cut) stays behind the operator's button.
            var hailMary = HailMaryCompaction.Salvage(payload, hotContext, rules.CompactHeadroom, allowMiddleCut: false, stripImages: false);
            if (hailMary.TokensSaved > 0)
            {
                Log("hail-mary: " + hailMary.DiagnosticsLine);
                var hailInserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
                if (hailInserted > 0)
                {
                    Log("local chat: inserted a user turn so the Qwen template has a query");
                }

                return true;
            }

            Log("local history compact: Context is filling but older turns could not be shortened (too few turns, or one over-full turn)");
            return false;
        }

        Log("local history compact: shortened older turns before routing");
        var inserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
        if (inserted > 0)
        {
            Log("local chat: inserted a user turn so the Qwen template has a query");
        }

        return true;
    }

    /// <summary>
    /// When a YaRN-extended local session's live prompt exceeds the VRAM-resident context
    /// (i.e. it is spilling into ordinary memory), surface the existing reload banner in
    /// "compact in place" mode so the user can pull the working set back onto the graphics
    /// card. Manual only: it never auto-compacts; it just offers.
    /// </summary>
    private void OfferSpillCompactIfNeeded(JsonObject state, JsonObject payload, bool compactAlreadyApplied, bool isProbeRequest = false, int? measuredPromptTokens = null)
    {
        if (isProbeRequest)
        {
            return; // internal probe (e.g. context-fill stress test): never steer it
        }

        if (compactAlreadyApplied)
        {
            return; // already compacted this turn
        }

        if (!LocalTargetReady(state))
        {
            return;
        }

        // Only relevant for profiles that opted into YaRN context extension (stamped at launch).
        if (!ParseBool(Str(state, "local_extend_context_into_ram")))
        {
            return;
        }

        var hotContext = ParseInt(Str(state, "local_context"), 0);
        if (hotContext <= 0)
        {
            return;
        }

        // The VRAM-resident context is stamped at launch. When the live prompt exceeds it,
        // the YaRN overflow is running in ordinary memory.
        var vramContext = ParseInt(Str(state, "local_vram_context"), 0);
        if (vramContext <= 0 || vramContext >= hotContext)
        {
            return; // not actually extended, or no VRAM boundary to talk about
        }

        // Use the model's real token count when available. The chars/4 estimate over-counts
        // for dense tokenizers (Qwen, ~3-4 chars/token), so a prompt well under the VRAM
        // window can read as "approaching the limit" and falsely trigger the spill-compact
        // dialog (which sets nearLimit=true and produces a bogus "Context is too small"
        // warning). When the measured count is unavailable, fall back to the estimate —
        // the spill-compact offer is non-blocking (it just suggests compacting), so a
        // slightly over-counted estimate is acceptable here.
        var promptTokens = measuredPromptTokens ?? LocalRequestOverlayRouting.EstimatePromptTokens(payload);

        // Compute the overflow (how much of the prompt is in slow RAM) and the urgency level.
        //   Urgency 0: prompt is at 85%+ of the VRAM window but not yet spilling — compact to
        //               keep the working set on the fast card.
        //   Urgency 1: prompt has spilled into RAM, but the overflow is small (<= 25% of the VRAM
        //               window) — compacting a few turns would pull it back onto the card.
        //   Urgency 2: the overflow is large (> 25% of the VRAM window) — most of the context is
        //               running in slow RAM; compacting is strongly recommended.
        int overflow = Math.Max(0, promptTokens - vramContext);
        int headroomThreshold = (int)(vramContext * 0.85);
        int recoverableThreshold = (int)(vramContext * 0.25);

        int urgency;
        if (promptTokens >= headroomThreshold && overflow <= 0)
        {
            urgency = 0; // at the edge of the VRAM window, not yet spilling
        }
        else if (overflow > 0 && overflow <= recoverableThreshold)
        {
            urgency = 1; // spilling, but recoverable with a modest compaction
        }
        else if (overflow > recoverableThreshold)
        {
            urgency = 2; // large overflow, most context in slow RAM
        }
        else
        {
            return; // still comfortably within the VRAM window
        }

        // Dynamic SSD guardrail: sample live available RAM. If it's below a safety floor, the
        // machine is at risk of paging to SSD (very slow). Escalate urgency to the maximum and
        // warn the user. This reacts to competing usage by other apps and Windows in real time.
        double availableRamGiB = HostTelemetrySampler.GetAvailablePhysicalRamGiB();
        const double ramSafetyFloorGiB = 4.0; // minimum free RAM before SSD paging becomes likely
        bool nearSsdEdge = availableRamGiB > 0 && availableRamGiB < ramSafetyFloorGiB;
        if (nearSsdEdge)
        {
            urgency = 2;
        }

        var existing = LoadLocalReload();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ParseLong(Str(existing, "suppress_until"), 0) > now)
        {
            return;
        }

        var compactAlreadyOn = ParseBool(Str(existing, "forward_compact"));
        if (Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase) && compactAlreadyOn)
        {
            return; // already asking
        }

        // Build the reason text based on urgency and the SSD guardrail.
        string reason;
        if (nearSsdEdge)
        {
            reason = "Context warning: RAM low — compact before context and/or system spill to SSD? (catastrophic system slowdown &/or crashing likely)";
        }
        else if (urgency == 2)
        {
            reason = "Context warning: most of this chat's context is running in slow RAM (very slow) — compact now to pull it back onto the graphics card?";
        }
        else if (urgency == 1)
        {
            reason = "Context warning: part of this chat's context has spilled into RAM (slower) — compact now to pull it back onto the graphics card?";
        }
        else
        {
            reason = "Context warning: the graphics-card portion of this chat's context is nearly full — compact to keep the working set on the fast card?";
        }

        SaveLocalReload(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = reason,
            ["model"] = string.Empty,
            ["variant"] = string.Empty,
            ["displayName"] = string.Empty,
            ["hotModel"] = Str(state, "local_preferred_model"),
            ["hotVariant"] = Str(state, "local_variant"),
            ["hotContext"] = hotContext,
            ["neededContext"] = promptTokens,
            ["nearLimit"] = true,
            ["destinationTight"] = nearSsdEdge,
            ["compactRecommended"] = true,
            ["betterChance"] = false,
            ["forward_compact"] = compactAlreadyOn,
            ["spillCompact"] = true,
            ["spillUrgency"] = urgency,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("local spill compact recommend pending: prompt=" + promptTokens + " vram=" + vramContext + " window=" + hotContext + " urgency=" + urgency + " nearSsd=" + nearSsdEdge);
    }

    /// <summary>
    /// Offers a "Large document detected" dialog when the prompt contains a dominant document
    /// message that exceeds the VRAM window. The user chooses how to handle context:
    /// full fidelity for this turn, full fidelity for N turns, summarize, or cancel.
    /// </summary>
    private void OfferDocumentContextDialogIfNeeded(JsonObject state, JsonObject payload, bool compactAlreadyApplied, bool isProbeRequest = false)
    {
        if (isProbeRequest)
        {
            return; // internal probe (e.g. context-fill stress test): never steer it
        }

        if (compactAlreadyApplied)
        {
            return; // already compacted this turn
        }

        if (!LocalTargetReady(state))
        {
            return;
        }

        // Only relevant for profiles that opted into YaRN context extension (stamped at launch).
        if (!ParseBool(Str(state, "local_extend_context_into_ram")))
        {
            return;
        }

        var hotContext = ParseInt(Str(state, "local_context"), 0);
        if (hotContext <= 0)
        {
            return;
        }

        var vramContext = ParseInt(Str(state, "local_vram_context"), 0);
        if (vramContext <= 0 || vramContext >= hotContext)
        {
            return; // not actually extended, or no VRAM boundary to talk about
        }

        // Detect a dominant document message.
        var (hasDocument, docTokens, totalTokens) = LocalRequestOverlayRouting.DetectDominantDocument(payload);
        if (!hasDocument)
        {
            return; // no large document in this turn
        }

        // Only offer the dialog if the document exceeds the VRAM window.
        if (totalTokens <= vramContext)
        {
            return; // still fits on the graphics card
        }

        // Check if we're already in a "full fidelity for N turns" mode from a previous turn.
        var existing = LoadLocalReload();
        var docMode = Str(existing, "documentContextMode");
        if (docMode.Equals("full_n_turns", StringComparison.OrdinalIgnoreCase))
        {
            var remainingTurns = ParseInt(Str(existing, "documentContextTurns"), 0);
            if (remainingTurns > 1)
            {
                // Decrement the counter and keep the full-fidelity mode.
                SaveLocalReload(new JsonObject
                {
                    ["status"] = "pending",
                    ["documentContextMode"] = "full_n_turns",
                    ["documentContextTurns"] = (remainingTurns - 1).ToString(),
                    ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                });
                return; // no dialog needed, already in full-fidelity mode
            }
        }

        // Check if we're already asking for a document context decision.
        if (Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase)
            && Str(existing, "documentContextMode").Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            return; // already asking
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ParseLong(Str(existing, "suppress_until"), 0) > now)
        {
            return;
        }

        var reason = "This turn includes a large document (~" + (docTokens / 1024) + "K tokens). "
            + "It won't fit in graphics-card memory, so it will run in ordinary memory (slower). "
            + "How should context be handled?";

        SaveLocalReload(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = reason,
            ["documentContextMode"] = "pending",
            ["documentContextTokens"] = docTokens.ToString(),
            ["documentContextTotal"] = totalTokens.ToString(),
            ["model"] = string.Empty,
            ["variant"] = string.Empty,
            ["displayName"] = string.Empty,
            ["hotModel"] = Str(state, "local_preferred_model"),
            ["hotVariant"] = Str(state, "local_variant"),
            ["hotContext"] = hotContext,
            ["neededContext"] = totalTokens,
            ["nearLimit"] = true,
            ["destinationTight"] = false,
            ["compactRecommended"] = false,
            ["betterChance"] = false,
            ["forward_compact"] = ParseBool(Str(existing, "forward_compact")),
            ["spillCompact"] = false,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("document context dialog pending: doc=" + docTokens + " total=" + totalTokens + " vram=" + vramContext);
    }

    private static int EstimateNeededContext(JsonObject payload)
    {
        var maxTokens = ParseInt(payload["max_tokens"]?.ToString() ?? string.Empty, 0);
        var total = LocalRequestOverlayRouting.EstimatePromptTokens(payload) + Math.Max(maxTokens, 0);
        return total >= 2048 ? total : 0;
    }

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("1", StringComparison.OrdinalIgnoreCase)
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private async Task<string> MaybeOfferReturnToLocalAsync(
        JsonObject state,
        JsonObject payload,
        JsonObject rec,
        string? forceRoute)
    {
        var natural = FluxMuxGatewayRouting.DecideRoute(state, payload, forceRoute);
        var suffice = CloudReturnToLocalPolicy.LocalWouldHaveSufficed(natural.Kind)
            || FluxMuxGatewayRouting.LoadedLocalCouldTakeLatestUserTurn(state, payload);
        var streak = CloudReturnToLocalPolicy.NextStreak(
            ParseInt(Str(rec, "local_sufficient_count"), 0),
            suffice);
        var armedRaw = Str(rec, "return_armed");
        var armed = CloudReturnToLocalPolicy.RearmAfterThisTurn(
            suffice,
            string.IsNullOrWhiteSpace(armedRaw) || ParseBool(armedRaw));
        var harness = FluxMuxGatewayRouting.IsHarnessEndpoint(state);
        var alreadyPending = Str(rec, "status").Equals("pending", StringComparison.OrdinalIgnoreCase)
            && CloudReturnToLocalPolicy.IsReturnToLocalSource(Str(rec, "source"));
        var localLabel = LocalReloadRouting.FormatPackLabel(
            Str(state, "local_preferred_model"),
            Str(state, "local_variant"));
        rec["local_sufficient_count"] = streak;
        rec["return_armed"] = armed;
        if (!CloudReturnToLocalPolicy.ShouldOffer(
                inApprovedCloudWindow: true,
                localReady: FluxMuxGatewayRouting.LocalTargetReady(state),
                localWouldHaveSufficed: suffice,
                returnArmed: armed,
                streakAfterThisTurn: streak,
                harnessEndpoint: harness,
                alreadyPendingReturn: alreadyPending))
        {
            SaveRecommend(rec);
            return FluxMuxGatewayRouting.Cloud;
        }

        var reason = CloudReturnToLocalPolicy.FormatReason(localLabel, harness);
        if (!CloudReturnToLocalPolicy.WaitForThisTurn(harness))
        {
            OfferReturnToLocal(rec, reason, streak, armed);
            Log("return to local offered; this turn stays on cloud until accepted");
            return FluxMuxGatewayRouting.Cloud;
        }

        var choice = await ApplyReturnToLocalConsentAsync(rec, reason, streak, armed).ConfigureAwait(false);
        return choice;
    }

    private void OfferReturnToLocal(JsonObject rec, string reason, int streak, bool armed)
    {
        rec["status"] = "pending";
        rec["reason"] = reason;
        rec["source"] = CloudReturnToLocalPolicy.Source;
        rec["fail_on_timeout"] = false;
        rec["local_sufficient_count"] = streak;
        rec["return_armed"] = armed;
        rec["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        SaveRecommend(rec);
    }

    private async Task<string> ApplyReturnToLocalConsentAsync(JsonObject rec, string reason, int streak, bool armed)
    {
        OfferReturnToLocal(rec, reason, streak, armed);
        Log("return to local pending: " + reason);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            rec = LoadRecommend();
            var status = Str(rec, "status");
            if (status == "no")
            {
                return FluxMuxGatewayRouting.Local;
            }

            if (status == "yes")
            {
                return FluxMuxGatewayRouting.Cloud;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        rec = LoadRecommend();
        rec["status"] = "yes";
        rec["source"] = CloudReturnToLocalPolicy.Source;
        rec["fail_on_timeout"] = false;
        rec["local_sufficient_count"] = 0;
        rec["return_armed"] = false;
        rec["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        SaveRecommend(rec);
        Log("return to local timed out; staying on the ready cloud");
        return FluxMuxGatewayRouting.Cloud;
    }

    private async Task<string> ApplyCloudConsentAsync(
        string reason,
        bool failOnTimeout,
        bool waitForAnswer,
        string source = "",
        int waitSeconds = 45)
    {
        var rec = LoadRecommend();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var allowUntil = ParseLong(Str(rec, "allow_until"), 0);
        var suppressUntil = ParseLong(Str(rec, "suppress_until"), 0);
        var status = Str(rec, "status");
        if (status == "yes" && allowUntil > now)
        {
            return "cloud";
        }

        if (status == "no" && suppressUntil > now)
        {
            return "local";
        }

        if (status != "pending")
        {
            SaveRecommend(new JsonObject
            {
                ["status"] = "pending",
                ["reason"] = string.IsNullOrWhiteSpace(reason) ? "Cloud looks notably better for this task." : reason,
                ["source"] = string.IsNullOrWhiteSpace(source) ? RouteRecoveryPolicy.CapacitySource : source,
                ["fail_on_timeout"] = failOnTimeout,
                ["allow_until"] = 0,
                ["suppress_until"] = 0,
                ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            });
            Log("cloud recommend pending: " + reason);
        }

        if (!waitForAnswer)
        {
            return failOnTimeout ? FluxMuxGatewayRouting.ConsentTimeout : "local";
        }

        var wait = Math.Clamp(waitSeconds, 10, 120);
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        while (DateTime.UtcNow < deadline)
        {
            rec = LoadRecommend();
            status = Str(rec, "status");
            if (status == "yes")
            {
                return "cloud";
            }

            if (status == "no")
            {
                return "local";
            }

            if (RouteRecoveryPolicy.IsWaitStatus(status))
            {
                return RouteRecoveryPolicy.WaitStatus;
            }

            if (RouteRecoveryPolicy.IsSteerStatus(status))
            {
                return RouteRecoveryPolicy.SteerStatus;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        rec["status"] = "timeout";
        rec["reason"] = FirstNonEmpty(Str(rec, "reason"), reason);
        rec["fail_on_timeout"] = failOnTimeout;
        rec["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        SaveRecommend(rec);
        if (failOnTimeout)
        {
            Log("cloud recommend timed out; failing this turn before llama-server");
            return FluxMuxGatewayRouting.ConsentTimeout;
        }

        Log("cloud recommend timed out; staying local");
        return "local";
    }

    private (string Url, string Mode) UpstreamForState(JsonObject state)
    {
        if (Str(state, "mode") == "local")
        {
            var localPort = ParseInt(Str(state, "local_port"), _port > 0 ? _port + 1 : 8081);
            return ($"http://127.0.0.1:{localPort}/v1/chat/completions", "openai");
        }

        var provider = Str(state, "provider", "OpenAI");
        if (provider == "Gemini")
        {
            return (Rstrip(FirstNonEmpty(Str(state, "endpoint"), "https://generativelanguage.googleapis.com/v1beta/openai")) + "/chat/completions", "openai");
        }

        if (provider == "OpenAI")
        {
            return (Rstrip(FirstNonEmpty(Str(state, "endpoint"), "https://api.openai.com/v1")) + "/chat/completions", "openai");
        }

        if (provider == "Copilot GitHub")
        {
            return (Rstrip(FirstNonEmpty(Str(state, "copilot_endpoint"), "https://api.githubcopilot.com")) + "/chat/completions", "openai");
        }

        if (provider == "Custom OpenAI-Compatible" || ParseBool(Str(state, "is_custom_compat")))
        {
            var baseUrl = Rstrip(FirstNonEmpty(Str(state, "custom_endpoint"), Str(state, "endpoint"), "https://api.openai.com/v1"));
            var path = NormalizeCustomPath(Str(state, "custom_chat_path"), "/chat/completions");
            return (baseUrl + path, "openai");
        }

        if (provider == "Anthropic")
        {
            return ("https://api.anthropic.com/v1/messages", "anthropic");
        }

        return (Rstrip(FirstNonEmpty(Str(state, "endpoint"), "https://api.openai.com/v1")) + "/chat/completions", "openai");
    }

    private async Task PrepareLocalChatPayloadAsync(JsonObject payload, JsonObject state)
    {
        var reasoningMode = LocalReasoningLaunchPolicy.NormalizeMode(Str(state, "local_reasoning", "Off"));

        if (LocalReasoningPayloadPolicy.ApplyToPayload(payload, reasoningMode))
        {
            Log("local chat: stripped thinking from history forwarded to llama-server (Client app still shows it)");
        }

        if (payload["messages"] is JsonArray messages)
        {
            payload["messages"] = NormalizeLocalChatMessages(payload["messages"] as JsonArray ?? messages);
            var inserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
            if (inserted > 0)
            {
                Log("local chat: inserted a user turn so the Qwen template has a query");
            }
        }

        var preparedImages = await LocalChatImageNormalizer.NormalizeAsync(payload, log: Log).ConfigureAwait(false);
        if (preparedImages > 0)
        {
            Log("local chat: embedded " + preparedImages + " image(s) for llama-server");
        }
    }

    private JsonArray NormalizeLocalChatMessages(JsonArray messages)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        var systems = new List<JsonNode?>();
        var rest = new JsonArray();
        foreach (var msg in messages)
        {
            var role = (msg as JsonObject)?["role"]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
            if (role is "system" or "developer")
            {
                systems.Add(msg);
            }
            else
            {
                rest.Add(msg?.DeepClone());
            }
        }

        if (systems.Count == 0)
        {
            return messages;
        }

        var merged = string.Join("\n\n", systems.Select(MessageText)).Trim();
        if (string.IsNullOrWhiteSpace(merged))
        {
            return rest;
        }

        Log($"local chat: moved {systems.Count} system message(s) to the front for the chat template");
        var result = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = merged }
        };
        foreach (var item in rest)
        {
            result.Add(item?.DeepClone());
        }

        return result;
    }

    private static string CloudFitReason(JsonObject payload)
    {
        var user = LastUserText(payload);
        if (string.IsNullOrWhiteSpace(user))
        {
            return string.Empty;
        }

        var lowered = user.ToLowerInvariant();
        if (LooksLikeToolPayload(user))
        {
            return string.Empty;
        }

        if (FluxMuxCloudCallLimits.PromptAsksForCloud(user))
        {
            return "The latest prompt asks to use a cloud model.";
        }

        if (user.Length >= 6000)
        {
            return "The latest prompt is very large for a de-emphasised cloud budget.";
        }

        foreach (var phrase in new[] { "architecture review", "system design", "threat model", "formal proof", "refactor the entire", "migrate the whole" })
        {
            if (lowered.Contains(phrase))
            {
                return "This looks like a heavy design or reasoning task that usually benefits from the cloud profile.";
            }
        }

        return string.Empty;
    }

    private static bool LooksLikeToolPayload(string text)
    {
        var lowered = text.ToLowerInvariant();
        return lowered.Contains("tool_result") || lowered.Contains("<tool") || lowered.Contains("[tool") || lowered.Contains("tool_call");
    }

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
                return MessageText(msg);
            }
        }

        return string.Empty;
    }

    private static string MessageText(JsonNode? msg)
    {
        if (msg is not JsonObject obj)
        {
            return string.Empty;
        }

        var content = obj["content"];
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue)
        {
            return content.ToString() ?? string.Empty;
        }

        if (content is JsonArray parts)
        {
            var texts = new List<string>();
            foreach (var part in parts)
            {
                if (part is JsonValue)
                {
                    texts.Add(part.ToString() ?? string.Empty);
                }
                else if (part is JsonObject block)
                {
                    var text = FirstNonEmpty(Str(block, "text"), Str(block, "content"));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        texts.Add(text);
                    }
                }
            }

            return string.Join("\n", texts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        return content.ToString() ?? string.Empty;
    }

    private static JsonObject MapOpenAiToAnthropic(JsonObject payload, JsonObject state)
    {
        var messages = new JsonArray();
        if (payload["messages"] is JsonArray source)
        {
            foreach (var item in source.OfType<JsonObject>())
            {
                var role = Str(item, "role", "user");
                if (role == "system")
                {
                    role = "user";
                }

                JsonNode contentBlocks = item["content"] is JsonArray list
                    ? list.DeepClone()
                    : new JsonArray { new JsonObject { ["type"] = "text", ["text"] = item["content"]?.ToString() ?? string.Empty } };
                messages.Add(new JsonObject { ["role"] = role, ["content"] = contentBlocks });
            }
        }

        return new JsonObject
        {
            ["model"] = FirstNonEmpty(Str(payload, "model"), Str(state, "preferred_model"), "claude-3-5-sonnet-latest"),
            ["messages"] = messages,
            ["max_tokens"] = ParseInt(FirstNonEmpty(Str(payload, "max_tokens"), Str(state, "max_tokens")), 2048),
            ["temperature"] = ParseDouble(FirstNonEmpty(Str(payload, "temperature"), Str(state, "temperature")), 0.7)
        };
    }

    private static JsonObject MapAnthropicToOpenAi(JsonObject payload, string model)
    {
        var textParts = new List<string>();
        if (payload["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                if (Str(block, "type") == "text")
                {
                    textParts.Add(Str(block, "text"));
                }
            }
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new JsonObject
        {
            ["id"] = FirstNonEmpty(Str(payload, "id"), "chatcmpl-anthropic"),
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = string.Concat(textParts) },
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = payload["usage"]?.DeepClone() ?? new JsonObject()
        };
    }

    private static int? ReadThinkingBudget(JsonObject payload)
    {
        var node = payload["thinking_budget"] ?? (payload["chat_template_kwargs"] as JsonObject)?["thinking_budget"];
        if (node is null)
        {
            var thinkingOn = (payload["chat_template_kwargs"] as JsonObject)?["enable_thinking"]?.ToString();
            if (string.Equals(thinkingOn, "true", StringComparison.OrdinalIgnoreCase)
                || thinkingOn == "True")
            {
                return null;
            }

            return 0;
        }

        return int.TryParse(
            node.ToString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var budget)
            ? budget
            : 0;
    }

    private static int RequestTimeoutForPayload(
        JsonObject payload,
        bool localStream,
        PortForwardingRules rules,
        bool longLoadProfile = false)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        try
        {
            var promptChars = (payload["messages"] ?? new JsonArray()).ToJsonString().Length;
            var promptTokens = Math.Max(1, promptChars / 4);
            var completionTokens = Math.Max(1, ParseInt(Str(payload, "max_tokens"), 256));
            var estimated = 45 + (promptTokens / 500.0) + (completionTokens / 15.0);
            var seconds = (int)Math.Ceiling(estimated);
            if (localStream)
            {
                var clamped = LocalStreamHangPolicy.ClampCopyTimeoutSeconds(seconds, ReadThinkingBudget(payload), live);
                // For long-load profiles, the copy timeout must cover the thinking
                // first-byte deadline so the request isn't cancelled while the model
                // is still loading from cold.
                if (longLoadProfile)
                {
                    var thinkingSec = live.ThinkingFirstByteSeconds + live.DecisionSeconds + live.WaitLongerSeconds + live.DecisionSeconds;
                    return Math.Max(clamped, Math.Min(LocalStreamHangPolicy.MaxCopyTimeoutSeconds, thinkingSec));
                }
                return clamped;
            }

            return Math.Max(MinUpstreamTimeoutSec, Math.Min(MaxUpstreamTimeoutSec, seconds));
        }
        catch
        {
            return localStream ? live.MinCopyTimeoutSeconds : MinUpstreamTimeoutSec;
        }
    }

    private static (string Message, string ErrorType, int Status, string Hint) ClassifyUpstreamError(
        int status,
        string detail,
        string provider,
        string model,
        string exStr = "")
        => FluxMuxUpstreamErrorClassifier.Classify(status, detail, provider, model, exStr);

    private static string ExtractUpstreamErrorMessage(string detail, string fallback)
        => FluxMuxUpstreamErrorClassifier.ExtractUpstreamErrorMessage(detail, fallback);

    private static bool RoutingOn(JsonObject state) => FluxMuxGatewayRouting.RoutingOn(state);

    private static bool LocalTargetReady(JsonObject state) => FluxMuxGatewayRouting.LocalTargetReady(state);

    private static bool CloudTargetReady(JsonObject state) => FluxMuxGatewayRouting.CloudTargetReady(state);

    private static bool HasReadyTarget(JsonObject state) => LocalTargetReady(state) || CloudTargetReady(state);

    private JsonObject LoadState()
    {
        JsonObject state;
        if (!File.Exists(_statePath))
        {
            state = new JsonObject();
        }
        else
        {
            try
            {
                state = JsonNode.Parse(File.ReadAllText(_statePath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                state = new JsonObject();
            }
        }

        return EnrichState?.Invoke(state) ?? state;
    }

    private (string Kind, string Label) ReadLastServedIdentity()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastServedPath) || !File.Exists(_lastServedPath))
            {
                return (string.Empty, string.Empty);
            }

            var root = JsonNode.Parse(File.ReadAllText(_lastServedPath)) as JsonObject ?? new JsonObject();
            return (
                Str(root, "kind"),
                ProxyRuntimeStateSecrets.FormatCloudLabel(Str(root, "provider"), Str(root, "model")));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private JsonObject LoadRecommend()
    {
        if (!File.Exists(_recommendPath))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(_recommendPath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private void SaveRecommend(JsonObject payload)
    {
        try
        {
            File.WriteAllText(_recommendPath, payload.ToJsonString());
        }
        catch
        {
        }
    }

    private JsonObject LoadLocalReload()
    {
        if (string.IsNullOrWhiteSpace(_localReloadPath) || !File.Exists(_localReloadPath))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(_localReloadPath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private void SaveLocalReload(JsonObject payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_localReloadPath))
            {
                return;
            }

            File.WriteAllText(_localReloadPath, payload.ToJsonString());
        }
        catch
        {
        }
    }

    private void NoteCloudBreakerSuccess(string routeKind, string provider, string model)
    {
        if (!routeKind.Equals("cloud", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CloudCircuitBreaker.RecordSuccess(_cloudBreakerPath, provider, model);
    }

    private static int? TryParseRetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return Math.Clamp((int)Math.Ceiling(delta.TotalSeconds), 15, 300);
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return Math.Clamp((int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds), 15, 300);
        }

        return null;
    }

    private void SaveLastServed(string kind, string provider, string model)
    {
        try
        {
            var payload = new JsonObject
            {
                ["kind"] = kind ?? string.Empty,
                ["provider"] = provider ?? string.Empty,
                ["model"] = model ?? string.Empty,
                ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            };
            File.WriteAllText(_lastServedPath, payload.ToJsonString());
        }
        catch
        {
        }
    }

    private void NoteSuccessfulUpstream(
        string routeKind,
        string provider,
        string model,
        string? overlayVariant,
        bool compactApplied,
        bool reloadOffered,
        bool cloudConsentOffered,
        string requestId)
    {
        SaveLastServed(routeKind, provider, model);
        SaveLastRouteExplanation(routeKind, overlayVariant, compactApplied, reloadOffered, cloudConsentOffered, requestId, provider, model);
        NoteCloudBreakerSuccess(routeKind, provider, model);
        // A successful local turn means the "Context is too small" / reload offer is stale —
        // the model answered fine, so dismiss the banner. Without this, a bogus offer
        // (e.g. from a stale local_context or an over-counting estimate) stays up and
        // misleads the user into thinking the turn was blocked when it actually succeeded.
        if (routeKind.Equals(FluxMuxGatewayRouting.Local, StringComparison.OrdinalIgnoreCase))
        {
            ClearStaleLocalReloadOffer();
        }
    }

    /// <summary>
    /// Clears a pending local-reload offer after a successful local turn. The offer was
    /// raised before the turn was sent (based on the prompt token count), but the model
    /// answered fine — so the "Context is too small" banner is stale and should go away.
    /// Only clears when the offer is a context/near-limit one (not a vision or thinking
    /// miss, which may still be relevant on the next turn).
    /// </summary>
    private void ClearStaleLocalReloadOffer()
    {
        try
        {
            var existing = LoadLocalReload();
            if (!Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Do not clear a vision or thinking miss — those are capability gaps that
            // persist across turns until the profile is changed.
            if (ParseBool(Str(existing, "needVision")) || ParseBool(Str(existing, "needThinking")))
            {
                return;
            }

            existing["status"] = "idle";
            existing["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            SaveLocalReload(existing);
            Log("local reload offer cleared: turn succeeded, context warning was stale");
        }
        catch
        {
        }
    }

    private void SaveLastRouteExplanation(
        string routeKind,
        string? overlayVariant,
        bool compactApplied,
        bool reloadOffered,
        bool cloudConsentOffered,
        string requestId,
        string provider,
        string model)
    {
        try
        {
            var payload = GatewayRouteExplanation.ToJson(
                routeKind,
                overlayVariant,
                compactApplied,
                reloadOffered,
                cloudConsentOffered,
                requestId,
                provider,
                model);
            File.WriteAllText(_lastRouteExplanationPath, payload.ToJsonString());
        }
        catch
        {
        }
    }

    private bool IsLocalReloadOfferPending()
    {
        var existing = LoadLocalReload();
        return Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCloudRecommendPending()
    {
        var existing = LoadRecommend();
        return Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase);
    }

    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }

        // Surface the line to the UI diagnostic terminal so Hail Mary / forward-prune / context
        // results are visible live (previously Log() only wrote to a file).
        try
        {
            ReportLog?.Invoke(message);
        }
        catch
        {
        }
    }

    private void ReportPortRulesFinding(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Log("port rules post-mortem: " + text);
        EmitContextEvent("port_rules_postmortem", text);
        try
        {
            ReportPortRulesPostMortem?.Invoke(text);
        }
        catch
        {
        }
    }

    private void NotePortRuleSendAnyway()
        => _portRuleSendAnywayGrace = 1;

    private bool ConsumePortRuleSendAnywayGrace()
    {
        if (_portRuleSendAnywayGrace <= 0)
        {
            return false;
        }

        _portRuleSendAnywayGrace--;
        return true;
    }

    private void PublishPortRulesTelemetry(PortRulesTelemetry snap)
    {
        snap ??= PortRulesTelemetry.Empty;
        lock (_portRulesLock)
        {
            _lastPortRulesTelemetry = snap;
        }

        try
        {
            ReportPortRulesTelemetry?.Invoke(snap);
        }
        catch
        {
        }

        var line = snap.CompactDiagnosticLine(PortRules);
        if (string.IsNullOrWhiteSpace(line)
            || string.Equals(line, _lastPortRulesDiagnostic, StringComparison.Ordinal))
        {
            return;
        }

        _lastPortRulesDiagnostic = line;
        Log("PORT " + line);
    }

    private void PatchPortRulesTelemetry(Func<PortRulesTelemetry, PortRulesTelemetry> update)
    {
        PortRulesTelemetry next;
        lock (_portRulesLock)
        {
            next = update(_lastPortRulesTelemetry);
        }

        PublishPortRulesTelemetry(next);
    }

    private async Task<string> AskPortRulePauseAsync(string finding, PortForwardingRules rules)
    {
        var prompt = PortRulesPostMortem.FormatPausePrompt(finding);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = finding;
        }

        // fail_on_timeout is false: the turn HALTS and waits for the user's steering choice.
        // The banner is already showing the options (Send this turn anyway / End this turn /
        // Let me steer / Switch to ...). There is no benefit to bypassing the user's steer,
        // so the turn must not fail on a timer — it waits until the user explicitly chooses.
        SaveRecommend(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = prompt,
            ["source"] = RouteRecoveryPolicy.PortRulePauseSource,
            ["fail_on_timeout"] = false,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("port rule pause pending: " + prompt);

        // Wait indefinitely (in DecisionSeconds increments) until the user explicitly chooses.
        // The banner stays up the whole time; no re-prompt is needed because the first prompt
        // is already displaying. The only way out is a user choice (yes/no/wait/steer).
        var wait = Math.Clamp(rules.DecisionSeconds, 10, 120);
        while (true)
        {
            var deadline = DateTime.UtcNow.AddSeconds(wait);
            while (DateTime.UtcNow < deadline)
            {
                var rec = LoadRecommend();
                var status = Str(rec, "status");
                if (status == "yes")
                {
                    return FluxMuxGatewayRouting.Cloud;
                }

                if (status == "no")
                {
                    return "local";
                }

                if (RouteRecoveryPolicy.IsWaitStatus(status))
                {
                    return RouteRecoveryPolicy.WaitStatus;
                }

                if (RouteRecoveryPolicy.IsSteerStatus(status))
                {
                    return RouteRecoveryPolicy.SteerStatus;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            // No choice yet after this window. The turn stays halted; the banner remains
            // visible. Log and keep waiting — do NOT fail the turn.
            Log("port rule pause: still waiting for user steering choice (turn halted, banner showing)");
        }
    }

    private static async Task SendAssistantNoticeAsync(HttpListenerResponse response, string message)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        ApplyCorsHeaders(response);
        using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await OpenAiStreamTelemetryProxy.WriteErrorAndDoneAsync(
                response.OutputStream,
                message,
                "notice",
                string.Empty,
                writeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EmitContextEvent(string eventType, string details)
    {
        try
        {
            var payload = new JsonObject
            {
                ["event_id"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["event_type"] = eventType,
                ["details"] = details,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            File.AppendAllText(_contextPath, payload.ToJsonString() + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int status, JsonObject payload, IDictionary<string, string>? headers = null)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        response.KeepAlive = true;
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        ApplyCorsHeaders(response);
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                response.Headers[pair.Key] = pair.Value;
            }
        }

        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
    }

    private static void ApplyCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "*";
    }

    private static Task SendOpenAiErrorAsync(
        HttpListenerResponse response,
        string message,
        int status,
        string errorType,
        string hint = "",
        IDictionary<string, string>? headers = null)
    {
        _ = errorType;
        _ = hint;
        return SendJsonAsync(response, status, FluxMuxGatewayRouting.EndpointErrorBody(message), headers);
    }

    private static async Task FinishClientChatErrorAsync(
        HttpListenerResponse response,
        string message,
        int status,
        string errorType,
        string hint,
        bool sseStarted)
    {
        if (sseStarted)
        {
            using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await OpenAiStreamTelemetryProxy.WriteErrorAndDoneAsync(
                    response.OutputStream,
                    message,
                    errorType,
                    hint,
                    writeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        await SendOpenAiErrorAsync(response, message, status, errorType, hint).ConfigureAwait(false);
    }

    private async Task WatchLocalStreamHangAsync(
        Func<bool> gotUpstreamBytes,
        LocalStreamHangClock clock,
        CancellationTokenSource hangCts,
        CancellationTokenSource timeoutCts)
    {
        try
        {
            while (!hangCts.IsCancellationRequested)
            {
                await Task.Delay(250, hangCts.Token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (clock.Rules.HangEnabled
                    && (now - _lastHangTallyUtc).TotalSeconds >= 1)
                {
                    _lastHangTallyUtc = now;
                    var quiet = (int)(now - (gotUpstreamBytes() ? clock.LastByteUtc : clock.CopyStartUtc)).TotalSeconds;
                    PatchPortRulesTelemetry(prev => prev with
                    {
                        HasLocalTurn = true,
                        QuietSeconds = Math.Max(0, quiet),
                        HangQuietSeconds = clock.FirstByteDeadlineSeconds
                    });
                }

                if (!LocalStreamHangPolicy.ShouldAbort(
                    gotUpstreamBytes(),
                    now - clock.CopyStartUtc,
                    now - clock.LastByteUtc,
                    clock.FirstByteDeadline,
                    clock.StallDeadline,
                    out var reason))
                {
                    continue;
                }

                Log("local stream hang: " + reason
                    + " after "
                    + ((int)(now - clock.CopyStartUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture)
                    + "s — asking whether to wait longer on this turn");
                var decision = await AskHangWaitAsync(
                    () => gotUpstreamBytes(),
                    clock,
                    hangCts.Token).ConfigureAwait(false);
                if (decision == LocalStreamHangPolicy.RecoveredStatus)
                {
                    Log("local stream hang: llama-server started sending; Cline is still on this turn");
                    continue;
                }

                if (RouteRecoveryPolicy.IsWaitStatus(decision))
                {
                    clock.ResetForWaitLonger();
                    try
                    {
                        LocalStreamHangPolicy.ExtendCopyTimeout(timeoutCts, clock.Rules);
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    Log("local stream hang: waiting longer ("
                        + clock.Rules.WaitLongerSeconds.ToString(CultureInfo.InvariantCulture)
                        + "s) on this turn");
                    continue;
                }

                clock.LastAbortReason = reason;
                Log("local stream hang: ending SSE so the Client app can stop waiting");
                hangCts.Cancel();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DismissHangWaitIfPending();
        }
    }

    private async Task<string> AskHangWaitAsync(
        Func<bool> gotUpstreamBytes,
        LocalStreamHangClock clock,
        CancellationToken cancellationToken)
    {
        var waitMessage = LocalStreamHangPolicy.FormatHangWaitMessage(Str(LoadState(), "endpoint_app"));
        // fail_on_timeout is false: a hang is a situation the chat can self-manage (llama-server
        // may still be working). On timeout, default to the safe "wait longer" option rather
        // than failing the turn. The user can still explicitly choose "End this turn" or
        // "Switch to cloud" if they want to stop waiting.
        SaveRecommend(new JsonObject
        {
            ["status"] = "pending",
            ["reason"] = waitMessage,
            ["source"] = RouteRecoveryPolicy.LocalHangSource,
            ["fail_on_timeout"] = false,
            ["allow_until"] = 0,
            ["suppress_until"] = 0,
            ["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
        Log("cloud recommend pending: " + waitMessage);

        var deadline = DateTime.UtcNow.AddSeconds(LocalStreamHangPolicy.DecisionSeconds);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LocalStreamHangPolicy.StreamRecovered(
                    gotUpstreamBytes(),
                    DateTime.UtcNow - clock.LastByteUtc,
                    clock.StallDeadline))
                {
                    DismissHangWaitIfPending();
                    return LocalStreamHangPolicy.RecoveredStatus;
                }

                var rec = LoadRecommend();
                var status = Str(rec, "status");
                if (RouteRecoveryPolicy.IsWaitStatus(status))
                {
                    return RouteRecoveryPolicy.WaitStatus;
                }

                if (status == "yes")
                {
                    return "cloud";
                }

                if (status == "no")
                {
                    return "end";
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            DismissHangWaitIfPending();
            throw;
        }

        // No explicit choice within the decision window. Default to the safe "wait longer"
        // option: the chat can self-manage (llama-server may still be producing), so keep the
        // turn alive rather than failing it. The banner is dismissed; the next hang will
        // re-prompt if the stream is still quiet.
        DismissHangWaitIfPending();
        Log("local stream hang: no choice within decision window; defaulting to wait longer (safe default)");
        return RouteRecoveryPolicy.WaitStatus;
    }

    private void DismissHangWaitIfPending()
    {
        var rec = LoadRecommend();
        if (!Str(rec, "status").Equals("pending", StringComparison.OrdinalIgnoreCase)
            || !RouteRecoveryPolicy.IsLocalHangSource(Str(rec, "source")))
        {
            return;
        }

        rec["status"] = "idle";
        rec["reason"] = string.Empty;
        rec["fail_on_timeout"] = false;
        rec["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        SaveRecommend(rec);
        Log("local stream hang: llama-server is working again; closed the wait prompt");
    }

    private static string Str(JsonObject? obj, string key, string fallback = "")
    {
        if (obj is null)
        {
            return fallback;
        }

        var node = obj[key];
        if (node is null)
        {
            return fallback;
        }

        var text = node.ToString()?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static bool TryParseCompletion(byte[] bytes, out JsonObject? completion)
    {
        completion = null;
        try
        {
            completion = JsonNode.Parse(bytes) as JsonObject;
            return completion is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static long ParseLong(string value, long fallback)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string Rstrip(string value) => (value ?? string.Empty).TrimEnd('/');

    private static string NormalizeCustomPath(string value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return text.StartsWith('/') ? text : "/" + text;
    }

    private static JsonObject CloneObject(JsonObject source)
        => source.DeepClone() as JsonObject ?? new JsonObject();

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];
}
