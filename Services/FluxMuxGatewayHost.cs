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
using FluxMux.Avalonia.Services.ContextEngine;

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
    public static int SharedTurnCounter = 0;

    // KV-cache reuse diagnostic: tracks the previous turn's measured prompt-token count so we
    // can compute the DELTA in prompt size between consecutive turns. If the llama-server KV
    // cache is being reused across turns, the prefill time for turn N+1 should scale with the
    // DELTA (only the new tokens need prefilling), not with the TOTAL prompt size. If prefill
    // time scales with the total every turn, the cache is NOT being reused and the model is
    // re-reading the entire context from scratch each turn — the root cause of the "slow
    // responses" complaint. The delta is logged alongside the prefill duration in
    // onFirstContentToken so the operator can eyeball the relationship.
    private int _prevTurnPromptTokens = 0;
    private readonly object _prevTurnPromptTokensLock = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _loopCts;
    // Generation-based, one-shot supersede cancellation. The old design used a single shared
    // _activeHandlerCts that was cancelled on every launch supersede and NEVER reset, so every
    // subsequent local request inherited a dead token and 502'd immediately — the "keeps
    // generating errors" storm when an impatient user mashes Launch/retry. Instead we now keep
    // a monotonically increasing generation counter. Each handler captures the generation at
    // request-start; it only honors a supersede if the generation advanced AFTER it started.
    // A request that starts after the last supersede (talking to the new model) never sees a
    // generation change and proceeds normally — so "next message just works" after a swap.
    private int _launchSupersedeGeneration;
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

    // Shared "model is loading" state for coalescing 503 retries. When the local model is
    // still loading (llama-server returns 503 "Loading model"), multiple concurrent requests
    // would each spawn their own 503 retry loop, hammering the same llama-server. Instead,
    // the first request to hit a 503 sets _localLoadingSignal; subsequent requests await the
    // same signal instead of spawning a new retry loop. When the model becomes ready (a probe
    // succeeds), the signal is set and all waiting requests proceed. This prevents the
    // concurrent 503 retry storm that can interfere with the launch itself.
    private readonly object _localLoadingLock = new();
    private ManualResetEventSlim? _localLoadingSignal;
    private DateTime _localLoadingSinceUtc = DateTime.MinValue;

    // Semaphore to limit concurrent requests to the local model to 1. When the client sends
    // multiple requests concurrently (e.g., a probe + the real request, or retries), the model
    // processes them in parallel, which slows down each one. By limiting concurrent requests to
    // 1, the model can focus on the real request and generate it faster.
    private readonly SemaphoreSlim _localRequestSemaphore = new(1, 1);

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
    /// Returns true when the user's client reinflates the context (forwards the full chat each
    /// turn, e.g. Cline). When true, the gateway skips FluxMux compaction entirely, because the
    /// client will resend the full history on the next turn anyway. This is a global flag, not
    /// per-profile.
    /// </summary>
    public Func<bool>? GetUserClientReinflatesContext { get; set; }

    /// <summary>
    /// Per-profile forward-prune toggle ("LocalForwardPruneEnabled", default on). Read live each
    /// turn so it can be switched mid-chat without a reload. Returns null when the profile cannot
    /// be resolved (treated as on, the default).
    /// </summary>
    public Func<string, string, bool?>? GetForwardPruneEnabled { get; set; }

    /// <summary>
    /// Per-profile VIBE_STATE toggle ("LocalVibeStateEnabled", default on). Read live each turn.
    /// When on, the context engine re-injects the session's task/state/next at the top of the
    /// system prompt so the model always knows "where we are" even after the working context has
    /// been paged away. Returns null when the profile cannot be resolved (treated as on).
    /// </summary>
    public Func<string, string, bool?>? GetVibeStateEnabled { get; set; }

    /// <summary>
    /// The context engine (VIBE_STATE + paged context). Injected by the runtime service; null when
    /// the capability is not wired up (treated as disabled). The engine reads/writes
    /// <c>VIBE_STATE.md</c> through its <c>IContextIo</c> seam, so a locked or missing file never
    /// breaks a turn.
    /// </summary>
    public FluxMux.Avalonia.Services.ContextEngine.IContextEngine? ContextEngine { get; set; }

    /// <summary>
    /// Path to the session's <c>VIBE_STATE.md</c>. Supplied by the runtime service (next to the
    /// config file). The engine uses this to load/save the persistent task/state/next record.
    /// </summary>
    public string VibeStatePath { get; set; } = string.Empty;

    /// <summary>
    /// The paged-context swap store. Injected by the runtime service; null when paging is not
    /// wired up. When non-null and paging is enabled, the forward-prune truncate stage spills
    /// oversized older tool-result bodies here and leaves a <c>swap_id</c> pointer (lossless).
    /// </summary>
    public FluxMux.Avalonia.Services.ContextEngine.ContextSwapStore? SwapStore { get; set; }

    /// <summary>
    /// The durable essence log (the session's "meaning": current task/state/next/files + goal
    /// evolution + decisions + dead ends). Injected by the runtime service; null when not wired
    /// up. This is the source of truth re-asserted after compaction / cold resume.
    /// </summary>
    public FluxMux.Avalonia.Services.ContextEngine.EssenceLog? EssenceLog { get; set; }

    /// <summary>
    /// Re-asserts the durable essence after a compaction edge / cold resume (rewrites the Cline
    /// compaction file and/or builds the local re-injection block). Injected by the runtime
    /// service; null when not wired up.
    /// </summary>
    public FluxMux.Avalonia.Services.ContextEngine.EssenceReasserter? EssenceReasserter { get; set; }

    /// <summary>
    /// The MCP control plane (update_vibe_state, read_swap_log, git_checkpoint). Injected by the
    /// runtime service; null when not wired up. Handles <c>POST /mcp</c> requests.
    /// </summary>
    public FluxMux.Avalonia.Services.ContextEngine.McpControlPlane? McpControlPlane { get; set; }

    /// <summary>
    /// Per-process compaction-edge detector for essence preservation. Fires on a sharp token drop
    /// (compaction), a new session key (resumed chat), the first turn since start (cold resume),
    /// or a context-limit jump (profile/model reset).
    /// </summary>
    private readonly FluxMux.Avalonia.Services.ContextEngine.CompactionEdgeDetector _essenceEdgeDetector
        = new();

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

    /// <summary>
    /// Raised when a local turn is sent to llama-server and prefill begins (Fix 3 — prefill
    /// progress indicator). The argument is the measured prompt-token count (0 when unknown),
    /// which the UI uses to estimate how long the prefill will take. The UI shows
    /// "Prefilling context… elapsed Xs" from this point until the first byte arrives.
    /// </summary>
    public Action<int>? ReportPrefillStarted { get; set; }

    /// <summary>
    /// Raised when the first byte of a local turn's reply arrives (prefill complete, Fix 3).
    /// The argument is the elapsed milliseconds since prefill started. The UI stops the
    /// "Prefilling context…" indicator when this fires.
    /// </summary>
    public Action<long>? ReportPrefillFirstByte { get; set; }

    /// <summary>
    /// Raised when the first REASONING (thinking) token of a local turn's reply arrives.
    /// This is the signal that the model has finished prefilling and started thinking.
    /// The UI switches from "Prefilling context…" to "Thinking…" when this fires.
    /// The argument is the elapsed milliseconds since prefill started.
    /// </summary>
    public Action<long>? ReportThinkingStarted { get; set; }

    /// <summary>
    /// The estimated prefill duration in seconds for the current turn, set by the UI
    /// when prefill starts. The hang detection uses this estimate to decide when to
    /// show the "wait longer" dialog: the dialog only appears if the elapsed time
    /// exceeds BOTH the FirstByteDeadline AND this estimate. This prevents the dialog
    /// from appearing before the model has had a reasonable chance to finish prefilling.
    /// </summary>
    public int CurrentPrefillEstimateSeconds { get; set; }

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

    /// <summary>
    /// Signals that a new local launch has superseded the in-progress one, so that stale
    /// handlers parked in the 503-retry loop (talking to the OLD llama-server) stop retrying
    /// and instead return a clean 502. This is a ONE-SHOT, generation-based signal: it bumps
    /// the supersede generation, and each handler only honors it if the generation advanced
    /// AFTER that handler started. A request that starts after this call (talking to the new
    /// model) captures the new generation and is NOT cancelled — so "next message just works"
    /// after a swap, instead of the old behavior where a single shared CTS stayed cancelled
    /// forever and every subsequent request 502'd immediately.
    /// </summary>
    public void CancelInFlightHandlers()
    {
        Interlocked.Increment(ref _launchSupersedeGeneration);
        Log("gateway: launch supersede signalled (generation=" + _launchSupersedeGeneration + "); in-flight handlers from the previous model will 502, new requests proceed normally");
    }

    /// <summary>
    /// Returns the current supersede generation. Call this at request-start and capture the
    /// value; later, <see cref="WasSupersededAfterStart"/> tells you whether a supersede
    /// happened after this request began.
    /// </summary>
    public int CurrentSupersedeGeneration => Volatile.Read(ref _launchSupersedeGeneration);

    /// <summary>
    /// True if a launch supersede was signalled AFTER the given start-generation was captured.
    /// A handler uses this to decide whether to abort (superseded) or keep waiting (not
    /// superseded — it is talking to the current model).
    /// </summary>
    public bool WasSupersededAfterStart(int startGeneration)
        => Volatile.Read(ref _launchSupersedeGeneration) != startGeneration;

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

            _ = Task.Run(() => HandleContextAsync(context, CancellationToken.None), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken handlerToken)
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

            // MCP control plane: the model's tools for managing the paged-context capability
            // (update_vibe_state, read_swap_log, git_checkpoint). POST /mcp with a JSON body of
            // { "tool": "name", "args": { ... } }. Returns { "ok": true, ... } or
            // { "ok": false, "error": "..." }. 404 when the control plane is not wired up.
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/mcp")
            {
                await HandleMcpRequestAsync(context).ConfigureAwait(false);
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

                await HandleChatCompletionsAsync(context, handlerToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log("gateway handler failed: " + ex.Message);
            try
            {
                // If the local model was stopped (memory guard, idle timeout, or user-initiated),
                // return a 503 with Retry-After so the client (e.g. Cline) can automatically retry.
                // A 502 is treated as a terminal error by most clients, so the chat gets stuck.
                var state = await WaitForRoutableStateAsync().ConfigureAwait(false);
                var phase = Str(state, "phase", "idle");
                var localPhase = Str(state, "local_phase", "");
                bool modelWasStopped = !HasReadyTarget(state)
                    && (phase is "idle" or "" || localPhase is "idle" or "" || localPhase == "stopped");

                if (modelWasStopped)
                {
                    // The model was stopped (memory guard, idle timeout, or user-initiated).
                    // Return a 503 with Retry-After so the client can automatically retry.
                    // The model will be reloaded on the next request.
                    await SendOpenAiErrorAsync(
                        context.Response,
                        "The local model was stopped to free memory. It will be reloaded automatically. Please retry your request.",
                        503,
                        "route_unavailable",
                        headers: new Dictionary<string, string> { ["Retry-After"] = "5" }).ConfigureAwait(false);
                }
                else
                {
                    await SendOpenAiErrorAsync(context.Response, "AI-FluxMux could not finish this request. Retry in a fresh chat.", 502, "proxy_error").ConfigureAwait(false);
                }
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

    private async Task HandleChatCompletionsAsync(HttpListenerContext context, CancellationToken handlerToken)
    {
        // Detect probe requests BEFORE the semaphore. Probes are tiny "Reply with the single
        // word ready." checks fired by the launch monitor to confirm a (re)loaded model is
        // responding. They must NEVER queue behind a multi-minute YaRN/RAM-spill prefill, and a
        // prefill must never block a probe. Before the semaphore was added (pre-Nov-2), probes
        // and the real request ran in parallel (llama-server's single slot time-slices them), so
        // a VRAM→YaRN mid-chat switch "just worked." Serializing them turned a slow-but-fine
        // prefill into a hard Long-load first-byte timeout.
        var isProbeRequest =
            context.Request.Headers[CloudEndpointValidationProbe.ProbeRequestHeader]?.Any() ?? false;

        // Limit concurrent REAL requests to the local model to 1. When the client sends multiple
        // real requests concurrently (e.g., retries, or two overlapping turns), the model
        // processes them in parallel, which slows down each one. By limiting concurrent real
        // requests to 1, the model can focus on the real request and generate it faster. Probes
        // are excluded (see above) so they never contend with a long prefill.
        if (!isProbeRequest)
        {
            await _localRequestSemaphore.WaitAsync(handlerToken).ConfigureAwait(false);
        }
        try
        {
            await HandleChatCompletionsCoreAsync(context, handlerToken).ConfigureAwait(false);
        }
        finally
        {
            if (!isProbeRequest)
            {
                _localRequestSemaphore.Release();
            }
        }
    }

    private async Task HandleChatCompletionsCoreAsync(HttpListenerContext context, CancellationToken handlerToken)
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
        // If the user's client reinflates the context (forwards the full chat each turn, e.g. Cline),
        // skip session artifact stripping entirely. The client will resend the full history on the next
        // turn anyway, so stripping tool results here would modify the prompt and prevent KV cache reuse.
        var artifacts = GetUserClientReinflatesContext?.Invoke() == true
            ? default
            : LocalSessionArtifactPolicy.Apply(payload);
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
        int clearedResults = 0;
        // If the client reinflates the context (e.g., Cline), skip tool-result-clearing too.
        // The client resends the full history each turn, so clearing results here would modify
        // the prompt and prevent KV cache reuse — the same reasoning as the session-artifact skip above.
        if (portRules.ToolResultClearingEnabled && GetUserClientReinflatesContext?.Invoke() != true)
        {
            clearedResults = LocalToolResultClearing.ClearOlderResults(payload, portRules);
        }
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
        var compactApplied = isProbeRequest ? false : TryApplyForwardCompact(payload, state, compactOn, forceCompact: compactOn);

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

                    // Essence preservation: first, update the durable essence log with the current
                    // task/state/next (extracted from the full conversation history in this request).
                    // This runs on every local turn so the essence log is always current. Then,
                    // detect a compaction/restart edge and re-assert the durable essence so it
                    // survives the context reset. Never throws; no-op when the reasserter is not
                    // wired up or there is no essence.
                    UpdateVibeStateAfterTurn(payload, model, Str(state, "local_variant"));
                    ObserveEssenceEdge(payload, state, measuredPromptTokens.Value, hotContext);
                }
            }

            // VIBE_STATE re-injection (stage 0): when enabled, prepend the session's persistent
            // task/state/next to the system prompt so the model always knows "where we are" even
            // after the working context has been paged away. This is a cheap, prefix-safe system
            // suffix (a few lines) and runs before forward-prune so the pruned turn still carries
            // the current vibe. No-op when disabled or when no VIBE_STATE.md exists yet.
            if (!isProbeRequest && hotContext > 0)
            {
                InjectVibeState(payload, model, Str(state, "local_variant"));
            }

            // Forward pruning: a per-turn fill throttle. On EVERY local turn, as it happens, trim
            // the turn's own waste (dedupe, collapse repeated tool loops, truncate oversized older
            // tool results) so the context fills more slowly. This is NOT conventional compaction
            // (which summarizes older turns at a threshold) and NOT Hail Mary (one-off emergency
            // rear-prune) — it is a distinct, conservative, always-on mechanism. It is gated on the
            // per-profile "LocalForwardPruneEnabled" toggle (default on) so it can be turned off
            // mid-chat without a reload. Uses the model's measured token count when available.
            //
            // IMPORTANT: forward pruning modifies the prompt (dedupe, loop-collapse, tool-result
            // truncation), so it breaks the exact-prefix KV cache. If the client reinflates the
            // context (e.g., Cline, which resends the full history each turn), skip it entirely —
            // the same reasoning as the session-artifact and tool-result-clearing skips above.
            // Otherwise every pruned turn is a cache miss.
            //
            // NOTE: this does NOT re-measure the prompt after pruning. Re-measuring is a network
            // round-trip to llama-server (LocalPromptTokenCounter.CountAsync → POST
            // /input_tokens), and doing it on every pruned turn would add a full extra call to the
            // model per turn — a significant speed hit. The prune itself is a cheap in-memory JSON
            // edit; the gauge refresh is not worth an extra llama-server call each turn. The next
            // turn's own measurement will reflect the pruned history anyway.
            if (!isProbeRequest && hotContext > 0
                && GetUserClientReinflatesContext?.Invoke() != true
                && GetForwardPruneEnabled?.Invoke(model, Str(state, "local_variant")) == true)
            {
                // Paged context: when the context engine is wired up and paging is enabled, use
                // its PageContext (which passes the swap store so the truncate stage spills
                // oversized bodies and leaves a swap_id pointer — lossless). When paging is off or
                // the engine is not wired up, fall back to the direct ForwardPrune.PruneTurn call
                // (the lossy head+tail stub, identical to pre-paging behavior).
                var forwardPruned = (ContextEngine is not null && ContextEngine.IsContextPagingEnabled())
                    ? ContextEngine.PageContext(payload, hotContext, measuredPromptTokens, swapStore: SwapStore)
                    : ForwardPrune.PruneTurn(payload, hotContext, measuredPromptTokens);
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
            //
            // hailMaryStillOver: true when the middle-cut dropped turns down to the protected
            // floor (system + initial brief + last task + recent turns) but the prompt STILL
            // exceeds the target — i.e. the turn itself is too large to fit even after trimming
            // history. This feeds the honest "turn too large" block message (Fix 2).
            var hailMaryStillOver = false;
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
                    hailMaryStillOver = hailMary.MiddleCutStillOver;
                    if (hailMary.MiddleCutDropped > 0 || hailMary.ImagesStripped > 0)
                    {
                        Log("hail-mary (" + hailStop + (hailStripImages ? ", strip-img" : "") + "): " + hailMary.DiagnosticsLine);
                        if (hailMary.MiddleCutStillOver)
                        {
                            Log("hail-mary: still over after trimming to the protected floor — the turn itself is too large to fit");
                        }
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
                        fillingDetails,
                        hailMaryStillOver),
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
            // Capture the supersede generation at request-start. A launch supersede
            // (CancelInFlightHandlers) bumps this counter; we only honor it if it advanced
            // AFTER this request began. A request that starts after the last supersede is
            // talking to the current model and must keep waiting for it to load — it is NOT
            // cancelled. This is what makes "next message just works" after a swap, instead
            // of the old shared-CTS design where one supersede killed every later request.
            int startSupersedeGeneration = CurrentSupersedeGeneration;
            // The per-turn timeout still governs the upstream send + 503-retry waits.
            var handlerToken2 = timeoutCts.Token;
            // Only log the [turn] line when we have a real token measurement. During launch
            // (503 retries) the measurement is null, and logging "n/a tokens" is just noise.
            if (measuredPromptTokens is not null)
            {
                SharedTurnCounter++;
                // Only include the forward-prune summary if the client doesn't reinflate the context.
                // When the client reinflates (e.g. Cline), forward pruning is disabled, so the
                // summary would be misleading.
                var pruneBit = (forwardPruneSummary is not null && GetUserClientReinflatesContext?.Invoke() != true) 
                    ? " · " + forwardPruneSummary 
                    : string.Empty;
                Log("[turn " + SharedTurnCounter + "] " + routeKind + " · " + (DateTime.Now - turnStart).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms" + pruneBit);

                // DIAG (temporary): one line that ties together the measured prompt size, which
                // context transforms fired this turn, and whether the client reinflates the context.
                // This lets us see from the log whether the prompt is large (context-size problem)
                // or small-but-reprefilled (cache-miss problem), and which transform is active.
                // Remove once the prefill behavior is understood.
                if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
                {
                    var reinflate = GetUserClientReinflatesContext?.Invoke() == true;
                    var fpOn = GetForwardPruneEnabled?.Invoke(model, Str(state, "local_variant")) == true;
                    Log("[prefill-diag] turn=" + SharedTurnCounter
                        + " promptTokens=" + measuredPromptTokens
                        + " reinflate=" + reinflate
                        + " forwardPruneGate=" + fpOn
                        + " pruneFired=" + (forwardPruneSummary is not null));
                }
            }
            HttpResponseMessage? response = null;
            var loadingRetries = 0;
            string? loadingDetail = null;
            int status = 0;
            try
            {
                while (true)
                {
                    // If a launch supersede was signalled AFTER this request started, this
                    // request was talking to the OLD model — stop immediately and let the
                    // outer code send a clean 502 instead of sending a new upstream request
                    // that will be ignored. A request that started after the last supersede
                    // (talking to the current model) does NOT abort here; it keeps waiting.
                    if (WasSupersededAfterStart(startSupersedeGeneration))
                    {
                        Log("upstream request aborted: superseded after request start (gen " + startSupersedeGeneration + " -> " + CurrentSupersedeGeneration + ")");
                        break;
                    }
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
                            // Coalesce concurrent 503 retries onto a shared wait. The first request
                            // to hit a 503 creates the shared signal; subsequent requests await the
                            // same signal instead of spawning their own retry loop. This prevents
                            // the concurrent 503 retry storm that can interfere with the launch.
                            var loadingSignal = BeginLocalLoadingWait();
                            // Wait for either the shared signal (model became ready) or the retry
                            // delay to elapse. If the signal is set, the model is ready and we can
                            // retry immediately. If the delay elapses first, we retry after the
                            // normal backoff.
                            var delayMs = portRules.LoadingRetryDelaySeconds * 1000;
                            var completed = await Task.WhenAny(
                                Task.Delay(delayMs, handlerToken2),
                                Task.Run(() => loadingSignal.Wait(handlerToken2)));
                            // If a launch supersede was signalled AFTER this request started,
                            // stop retrying immediately and let the outer code send a clean 502.
                            // A request that started after the last supersede keeps waiting for
                            // the current model to load.
                            if (WasSupersededAfterStart(startSupersedeGeneration))
                            {
                                Log("local loading retry aborted: superseded after request start (gen " + startSupersedeGeneration + " -> " + CurrentSupersedeGeneration + ")");
                                break;
                            }
                            if (loadingSignal.IsSet)
                            {
                                Log("local loading: shared signal set, retrying immediately");
                            }
                            continue;
                        }
                    }

                    break;
                }
            }
            finally
            {
                // Always clear the shared loading signal when the retry loop exits (success,
                // failure, OR cancellation). This prevents the race condition where a new 503
                // creates a new signal that no one will ever set (because the model is already
                // ready or the launch has failed/cancelled).
                if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase))
                {
                    EndLocalLoadingWait();
                }
            }

            // If a launch supersede was signalled AFTER this request started and no response
            // was received, send a clean 502 and return. This request was talking to the OLD
            // model; the user's next message (a fresh request, new generation) will target the
            // new model and proceed normally.
            if (response is null)
            {
                if (WasSupersededAfterStart(startSupersedeGeneration))
                {
                    Log("superseded before response: sending 502 (gen " + startSupersedeGeneration + " -> " + CurrentSupersedeGeneration + ")");
                    await SendOpenAiErrorAsync(context.Response, "This turn was cancelled because a new model launch superseded it. Your conversation is intact — send your next message and it will run on the new model.", 502, "proxy_error").ConfigureAwait(false);
                    return;
                }
                // Should not happen in practice, but guard against it.
                await SendOpenAiErrorAsync(context.Response, "AI-FluxMux could not finish this request. Retry in a fresh chat.", 502, "proxy_error").ConfigureAwait(false);
                return;
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
                // Do NOT create a hang clock for probe requests. A probe is a tiny "Reply with the
                // single word ready." check, not the user's real message. If the model is still
                // loading (503 retries), the probe would hit the hang clock's first-byte deadline
                // and abort, which is misleading (the probe is not a real turn). The 503-retry
                // logic already handles the "model is still loading" case for probes.
                hangClock = isProbeRequest ? null : new LocalStreamHangClock(portRules);
                if (routeKind.Equals("local", StringComparison.OrdinalIgnoreCase) && hangClock is not null)
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
                        // For long-load profiles, a quiet stream AFTER the first byte is NORMAL
                        // (the model is working in RAM-spill, which can take many minutes). The
                        // default stall deadline (45s) is far too short and causes false-positive
                        // aborts of turns that would have completed. Use a much longer window
                        // (1800s / 30 min) so the stall path only fires if the model is genuinely
                        // stuck (no progress for 30 min+), not just working slowly in RAM-spill.
                        // The first-byte deadline (600s) covers the "model is still loading" case;
                        // the stall deadline covers the "model produced a byte then went quiet" case.
                        hangClock.StallDeadline = TimeSpan.FromSeconds(1800);
                        Log("hang clock: long-load profile, FirstByteDeadline="
                            + (int)hangClock.FirstByteDeadline.TotalSeconds
                            + "s, StallDeadline="
                            + (int)hangClock.StallDeadline.TotalSeconds
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

                // The hang watchdog runs for ALL local routes, regardless of HangEnabled.
                // HangEnabled only controls the aggressive port-rule behaviors (telemetry
                // tallying, auto-abort) — it must NOT disable the basic "llama-server is
                // stuck, let me out" safety. When port rules are off, a stuck prefill
                // (llama-server accepted the request but never streams a content token, e.g.
                // RAM-spill thrash or a stalled stream) would otherwise sit forever with the
                // progress bar looping at ~80% and no way to intervene. The watchdog's
                // telemetry tallying is already gated on HangEnabled inside
                // WatchLocalStreamHangAsync; the first-byte/stall detection and the
                // "wait longer / end / switch" dialog are the safety net that must always run.
                var hangWatch = routeKind.Equals("local", StringComparison.OrdinalIgnoreCase)
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
                // Prefill/generation progress indicator: signal the UI that a turn has begun,
                // with the measured prompt-token count so it can estimate how long the wait will
                // take. Fires for all routes (local and cloud) so the bar never sits idle while
                // the user is awaiting a response. For cloud routes the "prefill" phase is really
                // "waiting for first byte from the cloud provider."
                //
                // Do NOT fire for probe requests (the launch "Reply with the single word ready."
                // check). A probe is a tiny ~59-token request that is NOT the user's real message;
                // firing PrefillStartedNotice for it would show a misleading "Reading a large
                // conversation (~59 tokens)" line at launch time, before the model is even ready
                // to prefill the user's actual (much larger) conversation.
                if (!isProbeRequest)
                {
                    ReportPrefillStarted?.Invoke(measuredPromptTokens ?? 0);
                }
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
                                    // NOTE: Do NOT fire ReportPrefillFirstByte here. The first SSE
                                    // byte arrives immediately (headers / an empty data line) and
                                    // does NOT mark the end of prefill. For large / RAM-spilled
                                    // prompts, the model can take many minutes to actually process
                                    // the prompt before it produces the first real token. Firing
                                    // the "switch to generation" event at the first byte would make
                                    // the UI show "generating…" for the entire prefill duration,
                                    // which is misleading. Instead, fire it at the first CONTENT
                                    // token (see onFirstContentToken below), which is when the
                                    // model has truly finished prefilling and started generating.
                                }
                            }
                            hangClock?.NoteByte();
                        },
                        localDeclaredTools,
                        localAllowParallel,
                        sessionArtifacts,
                        replyBudget,
                        onFirstContentToken: () =>
                        {
                            // This fires when the gateway first sends a VISIBLE content token
                            // (content / text / tool_calls, NOT reasoning) to the client. This is
                            // the true end of the prefill/thinking window: the model has finished
                            // thinking and started generating the actual reply. Tell the UI to
                            // switch to the generation phase NOW.
                            var prefillMs = ttfbStopwatch.ElapsedMilliseconds;
                            Log("ttfb: first content token after " + prefillMs + " ms (model=" + model + ")");
                            // KV-cache reuse diagnostic: log the prompt-token delta vs. the previous
                            // turn alongside the prefill duration. If the llama-server KV cache is
                            // reused, prefill time should track the DELTA (new tokens only). If it
                            // tracks the TOTAL, the cache is not reused and the model re-reads the
                            // whole context each turn.
                            if (measuredPromptTokens is not null)
                            {
                                int prevTokens;
                                int deltaTokens;
                                lock (_prevTurnPromptTokensLock)
                                {
                                    prevTokens = _prevTurnPromptTokens;
                                    deltaTokens = measuredPromptTokens.Value - prevTokens;
                                    _prevTurnPromptTokens = measuredPromptTokens.Value;
                                }
                                var prefillThroughput = prefillMs > 0
                                    ? (deltaTokens / (prefillMs / 1000.0)).ToString("0.#", CultureInfo.InvariantCulture)
                                    : "n/a";
                                Log("kv-cache: prompt=" + measuredPromptTokens.Value
                                    + " delta=" + deltaTokens
                                    + " prefillMs=" + prefillMs
                                    + " prefillThroughput=" + prefillThroughput + " tok/s (model=" + model + ")");
                            }
                            // Signal the hang watchdog that the first content token has arrived.
                            // Until this point the stream is in the "prefill / first token" window
                            // (even if header bytes already arrived), so the watchdog keeps the
                            // capped prefill-estimate deadline in effect. After this, the normal
                            // post-first-byte stall deadline governs.
                            hangClock?.NoteContentToken();
                            ReportPrefillFirstByte?.Invoke(prefillMs);
                        },
                        onFirstReasoningToken: () =>
                        {
                            // This fires when the gateway first sees a REASONING (thinking) token.
                            // The model has finished prefilling and started thinking. Tell the UI
                            // to switch from "Prefilling context…" to "Thinking…".
                            Log("ttfb: first reasoning token after " + ttfbStopwatch.ElapsedMilliseconds + " ms (model=" + model + ")");
                            ReportThinkingStarted?.Invoke(ttfbStopwatch.ElapsedMilliseconds);
                        }).ConfigureAwait(false);
                    if (firstByteLogged)
                    {
                        Log($"ttfb: stream complete after {ttfbStopwatch.ElapsedMilliseconds} ms total (model={model})");
                        // Diagnostic: log the actual generation stats so we can distinguish a
                        // "long reply" (many tokens at normal speed) from a "slow generation"
                        // (few tokens at a low rate). The tracker is disabled by default, so
                        // this only logs when the operator has turned the generation-speed
                        // graph on.
                        var genSnap = _generationSpeed?.ReadSnapshot();
                        if (genSnap is not null && genSnap.CompletionTokens > 0)
                        {
                            Log("gen stats: " + genSnap.CompletionTokens + " tokens at "
                                + genSnap.TokensPerSecond.ToString("0.#", CultureInfo.InvariantCulture)
                                + " tok/s (model=" + model + ")");
                        }
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
                // VIBE_STATE is NOT auto-updated after every turn. Auto-updating would change the
                // reinjection block (Task/State/Next) on every turn, which changes the system prompt
                // and breaks the KV cache prefix — forcing a full cold prefill every single turn.
                // Instead, VIBE_STATE is only updated when the model explicitly calls the
                // update_vibe_state MCP tool, or when the user edits the file directly. This keeps
                // the injected block stable across turns so the KV cache prefix is preserved.
                return;
            }

            // Non-streaming (application/json) path: the whole response is read before it is
            // sent to the client, so the user waits during ReadAsByteArrayAsync. Fire the
            // prefill/first-byte notices here too so the bottom-bar indicator shows activity
            // instead of sitting idle. (The streaming path fires these earlier.)
            // Do NOT fire for probe requests (same reason as the streaming path above).
            if (!isProbeRequest)
            {
                ReportPrefillStarted?.Invoke(measuredPromptTokens ?? 0);
            }
            var nonStreamStopwatch = Stopwatch.StartNew();
            var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            ReportPrefillFirstByte?.Invoke(nonStreamStopwatch.ElapsedMilliseconds);
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
            // VIBE_STATE is NOT auto-updated after every turn (see the streaming path above for
            // the full rationale). Auto-updating would change the system prompt every turn and
            // break the KV cache prefix, forcing a full cold prefill every time.
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
                // Don't park llama-server on a first-byte hang for long-load/YaRN profiles.
                // A first-byte timeout on a spill prefill is "slow", not "dead". Parking
                // forces a cold reload on the next turn, which is even slower. Keep the
                // server warm; only park on a confirmed mid-stream stall.
                if (!_currentTurnIsLongLoad)
                {
                    var park = ParkLocalAfterHangAsync;
                    if (park is not null)
                    {
                        _ = park();
                    }
                }
                else
                {
                    Log("local stream hang: skipping park for long-load/YaRN profile (first-byte timeout is slow, not dead)");
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

    /// <summary>
    /// Marks the local model as "loading" and returns the shared signal that all concurrent
    /// requests should await. The first request to hit a 503 "Loading model" calls this;
    /// subsequent requests get the same signal and await it instead of spawning their own
    /// 503 retry loop. This coalesces N concurrent retry loops into one, preventing the
    /// storm that can interfere with the launch.
    ///
    /// IMPORTANT: The signal is only created if it doesn't already exist (regardless of its
    /// set/unset state). This prevents the race condition where a request that just finished
    /// (and set the signal) is followed by another request that creates a NEW signal, which
    /// no one will ever set (because the model is already ready). The signal is cleared by
    /// <see cref="EndLocalLoadingWait"/> when the retry loop exits (success OR failure).
    /// </summary>
    private ManualResetEventSlim BeginLocalLoadingWait()
    {
        lock (_localLoadingLock)
        {
            if (_localLoadingSignal is null)
            {
                _localLoadingSignal = new ManualResetEventSlim(false);
                _localLoadingSinceUtc = DateTime.UtcNow;
                Log("local loading: first 503 detected, coalescing concurrent requests onto a shared wait");
            }
            return _localLoadingSignal;
        }
    }

    /// <summary>
    /// Signals that the local model is ready (or the retry loop has exited for any reason)
    /// and clears the shared signal so it can be reused for the next loading episode. All
    /// requests awaiting the signal are released. Called from the 503 retry loop when it
    /// exits (success OR failure), so that no request is left waiting on a stale signal.
    /// </summary>
    private void EndLocalLoadingWait()
    {
        lock (_localLoadingLock)
        {
            if (_localLoadingSignal is { IsSet: false })
            {
                var waitedMs = (long)(DateTime.UtcNow - _localLoadingSinceUtc).TotalMilliseconds;
                _localLoadingSignal.Set();
                Log("local loading: model ready after " + waitedMs + " ms, releasing coalesced requests");
            }
            // Clear the signal so it can be reused for the next loading episode. This prevents
            // the race condition where a new 503 creates a new signal that no one will set.
            _localLoadingSignal = null;
            _localLoadingSinceUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// True when a local model load is in progress (the shared loading signal is active and
    /// not yet set). Used to decide whether to coalesce a 503 retry onto the shared wait.
    /// </summary>
    private bool IsLocalLoadingWaitActive()
    {
        lock (_localLoadingLock)
        {
            return _localLoadingSignal is { IsSet: false };
        }
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

    /// <summary>
    /// Clears the forward_compact flag so the force-compact is a one-off action.
    /// </summary>
    private void ClearForwardCompactFlag()
    {
        var existing = LoadLocalReload();
        if (ParseBool(Str(existing, "forward_compact")))
        {
            existing["forward_compact"] = false;
            existing["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            SaveLocalReload(existing);
            Log("local reload: forward_compact flag cleared after force-compact");
        }
    }

    /// <summary>
    /// Suppresses the forward-compact offer for a period of time after a failed compact.
    /// This prevents the compact button from reappearing on every turn when the compact
    /// cannot shrink the prompt (e.g., one big over-full turn).
    /// </summary>
    private void SuppressForwardCompactFor(int minutes)
    {
        var existing = LoadLocalReload();
        var until = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
        existing["suppress_until"] = until;
        existing["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        SaveLocalReload(existing);
        Log("local reload: forward-compact suppressed for " + minutes + " minutes after failed compact");
    }

    private bool TryApplyForwardCompact(JsonObject payload, JsonObject state, bool compactOn, int? measuredPromptTokens = null, bool forceCompact = false)
    {
        var rules = CurrentRules();

        // If the user's client reinflates the context (forwards the full chat each turn, e.g. Cline),
        // skip FluxMux compaction entirely. The client will resend the full history on the next turn
        // anyway, so compacting here is wasted work. Let the client manage its own context.
        if (GetUserClientReinflatesContext?.Invoke() == true)
        {
            return false;
        }

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

        // Force-compact: the user explicitly clicked the Compact button. Bypass all
        // threshold checks and compact immediately. This is a one-off action, so we
        // clear the forward_compact flag after a successful compact.
        if (forceCompact)
        {
            Log("[compact] Compacting context now...");
            var forceHotContext = ParseInt(Str(state, "local_context"), 0);
            var forceRules = rules;
            if (perProfile.HasValue)
            {
                var (mode, trigger, keepT, keepTool, pinUser) = perProfile.Value;
                forceRules = rules with
                {
                    CompactKeepTurns = keepT,
                    CompactToolKeepTurns = keepTool,
                    CompactPreservedUserChars = pinUser
                };
            }
            if (LocalHistoryCompaction.TryCompactPayload(payload, forceRules, force: true))
            {
                Log("local history compact: force-compact applied (user clicked Compact)");
                var forceInserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
                if (forceInserted > 0)
                {
                    Log("local chat: inserted a user turn so the Qwen template has a query");
                }
                ClearForwardCompactFlag();
                return true;
            }
            var hailMary = HailMaryCompaction.Salvage(payload, forceHotContext, rules.CompactHeadroom, allowMiddleCut: false, stripImages: false);
            if (hailMary.TokensSaved > 0)
            {
                Log("hail-mary: " + hailMary.DiagnosticsLine);
                var hailInserted = LocalChatTemplateGuard.EnsureUserQuery(payload);
                if (hailInserted > 0)
                {
                    Log("local chat: inserted a user turn so the Qwen template has a query");
                }
                ClearForwardCompactFlag();
                return true;
            }
            Log("[compact] FAILED: The prompt is one big over-full turn that cannot be compacted. To continue, switch to a bigger-context model profile, or start a new chat.");
            SuppressForwardCompactFor(15);
            ClearForwardCompactFlag();
            return false;
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
        //
        // HYSTERESIS: To prevent the dialog from flickering when the prompt hovers around the
        // trigger threshold, use two separate thresholds:
        //   - SHOW threshold: the prompt must exceed this to SHOW the dialog (90% VRAM, or
        //     overflow > 30% of VRAM).
        //   - HIDE threshold: the prompt must drop below this to HIDE the dialog (80% VRAM, or
        //     overflow < 20% of VRAM).
        // The show threshold is higher than the hide threshold, so the dialog doesn't flicker
        // when the prompt hovers around a single value.
        int overflow = Math.Max(0, promptTokens - vramContext);
        // The warning should fire when the prompt ACTUALLY exceeds the VRAM-resident context
        // (i.e. it is spilling into RAM), not when it is merely approaching it. The old 90%
        // threshold fired too early — at 91% context fill the prompt was still entirely in
        // VRAM, so the "about to spill" warning was premature and misleading. Use 100% as the
        // show threshold: the dialog appears only when the prompt has actually spilled into
        // RAM (overflow > 0). The hide threshold stays at 80% so the dialog doesn't flicker
        // when the prompt hovers around the VRAM boundary.
        int showHeadroomThreshold = vramContext; // show at 100% VRAM (prompt actually exceeds VRAM)
        int hideHeadroomThreshold = (int)(vramContext * 0.80); // hide below 80% VRAM
        int showOverflowThreshold = (int)(vramContext * 0.30); // show at overflow > 30% VRAM
        int hideOverflowThreshold = (int)(vramContext * 0.20); // hide below overflow < 20% VRAM

        // Check if the dialog is currently showing (i.e., the snapshot is "pending").
        var existing = LoadLocalReload();
        bool dialogCurrentlyShowing = Str(existing, "status").Equals("pending", StringComparison.OrdinalIgnoreCase)
            && ParseBool(Str(existing, "spillCompact"));

        int urgency;
        if (dialogCurrentlyShowing)
        {
            // Dialog is showing: use the HIDE threshold to decide whether to hide it.
            if (promptTokens < hideHeadroomThreshold && overflow < hideOverflowThreshold)
            {
                // Prompt has dropped below the hide threshold: hide the dialog by setting the
                // status to "idle" (which makes the dialog invisible).
                existing["status"] = "idle";
                existing["updatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                SaveLocalReload(existing);
                Log("local spill compact recommend dismissed: prompt=" + promptTokens + " vram=" + vramContext + " (below hide threshold)");
                return;
            }
            // Prompt is still above the hide threshold: keep the dialog showing. Use the
            // existing urgency level (or recompute it) to update the reason text.
            urgency = ParseInt(Str(existing, "spillUrgency"), 1);
        }
        else
        {
            // Dialog is not showing: use the SHOW threshold to decide whether to show it.
            if (promptTokens >= showHeadroomThreshold && overflow <= 0)
            {
                urgency = 0; // at the edge of the VRAM window, not yet spilling
            }
            else if (overflow > 0 && overflow <= showOverflowThreshold)
            {
                urgency = 1; // spilling, but recoverable with a modest compaction
            }
            else if (overflow > showOverflowThreshold)
            {
                urgency = 2; // large overflow, most context in slow RAM
            }
            else
            {
                return; // still comfortably within the VRAM window (below show threshold)
            }
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

        if (LocalReasoningPayloadPolicy.ApplyToPayload(payload, reasoningMode, GetUserClientReinflatesContext))
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

    /// <summary>
    /// Handles a <c>POST /mcp</c> request: reads the JSON body, dispatches to the
    /// <see cref="McpControlPlane"/>, and writes the JSON response. 404 when the control plane is
    /// not wired up; 400 on a malformed body; 200 with the tool's result otherwise. Never throws.
    /// </summary>
    private async Task HandleMcpRequestAsync(HttpListenerContext context)
    {
        var plane = McpControlPlane;
        if (plane is null)
        {
            await SendOpenAiErrorAsync(context.Response, "MCP not available", 404, "mcp_not_available").ConfigureAwait(false);
            return;
        }

        JsonObject? body;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var raw = await reader.ReadToEndAsync().ConfigureAwait(false);
            body = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw) as JsonObject;
        }
        catch
        {
            await SendOpenAiErrorAsync(context.Response, "Malformed JSON body", 400, "invalid_request").ConfigureAwait(false);
            return;
        }

        var result = plane.Handle(body ?? new JsonObject());
        await SendJsonAsync(context.Response, 200, result).ConfigureAwait(false);
    }

    /// <summary>
    /// Essence preservation: observes the turn's measured prompt-token count and, when a
    /// compaction/restart edge is detected, re-asserts the durable essence so it survives the
    /// context reset. For Cline-like clients it rewrites the session's .compaction.json (rich
    /// format: essence summary + recent tail) AND injects the essence block for the immediate
    /// turn (belt-and-suspenders). For local clients it injects the essence block for this turn
    /// (the next turn carries it forward; no per-turn re-add). Never throws; no-op when the
    /// reasserter is not wired up.
    /// </summary>
    private void ObserveEssenceEdge(JsonObject payload, JsonObject state, int promptTokens, int contextLimit)
    {
        var reasserter = EssenceReasserter;
        if (reasserter is null)
        {
            return;
        }

        try
        {
            // Resolve a stable session key: the Cline session id when this is a Cline-like client,
            // else the local model identity (so different local sessions don't cross-trigger).
            var isClineLike = GetUserClientReinflatesContext?.Invoke() == true;
            var sessionKey = isClineLike
                ? (ClineCompactionWriter.GetMostRecentSessionId() ?? "_cline")
                : ("local:" + Str(state, "local_preferred_model"));

            var edge = _essenceEdgeDetector.Observe(
                promptTokens, contextLimit, sessionKey, Str(state, "local_preferred_model"));
            if (!edge)
            {
                return;
            }

            Log("essence: compaction/restart edge detected (session=" + sessionKey
                + ", tokens=" + promptTokens + ", limit=" + contextLimit + ")");

            if (isClineLike)
            {
                // Cline: rewrite the compaction file with the essence (rich format) so Cline
                // assimilates it on its next turn.
                var sessionId = ClineCompactionWriter.GetMostRecentSessionId();
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var messagesPath = Path.Combine(
                        ClineCompactionWriter.GetSessionDir(sessionId),
                        sessionId + ".messages.json");
                    var written = reasserter.ReassertClineCompaction(sessionId, messagesPath);
                    if (written is not null)
                    {
                        Log("essence: re-asserted Cline compaction file at " + written);
                    }
                }

                // Also inject for the immediate turn (in case Cline reads its own context before
                // the file is re-read).
                var block = reasserter.BuildLocalInjectionBlock();
                if (!string.IsNullOrWhiteSpace(block))
                {
                    InjectEssenceBlock(payload, block);
                }
            }
            else
            {
                // Local endpoint: inject the essence block for this turn (the next turn carries it).
                var block = reasserter.BuildLocalInjectionBlock();
                if (!string.IsNullOrWhiteSpace(block))
                {
                    InjectEssenceBlock(payload, block);
                    Log("essence: re-injected " + block.Length + " chars into the system prompt (edge)");
                }
            }
        }
        catch
        {
            // An essence re-assert failure must never break a turn.
        }
    }

    /// <summary>
    /// Injects an essence block into the payload's system message (appended to the existing system
    /// message, or inserted at the front if none exists — prefix-safe). Used by the essence
    /// re-asserter on a compaction edge.
    /// </summary>
    private void InjectEssenceBlock(JsonObject payload, string block)
    {
        var messages = payload["messages"] as JsonArray;
        if (messages is null || messages.Count == 0)
        {
            return;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msg)
            {
                continue;
            }

            var role = Str(msg, "role").Trim().ToLowerInvariant();
            if (role is "system" or "developer")
            {
                var existing = MessageText(msg);
                msg["content"] = existing.Length > 0 ? existing + "\n\n" + block : block;
                return;
            }
        }

        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = block });
    }

    /// <summary>
    /// Stage-0 VIBE_STATE re-injection: when the per-profile toggle is on and a
    /// <c>VIBE_STATE.md</c> exists, prepends the session's persistent task/state/next to the
    /// system prompt so the model always knows "where we are" even after the working context has
    /// been paged away.
    ///
    /// This is a prefix-safe system suffix: it is appended to the existing system message (not
    /// prepended, so the model's original system instructions keep their position and the KV-cache
    /// prefix stays stable). The block is deliberately terse (a few lines) so it costs almost
    /// nothing in tokens. No-op when the engine is not wired up, the toggle is off, or no
    /// VIBE_STATE.md exists yet. All I/O goes through the engine's <c>IContextIo</c> seam, so a
    /// locked or corrupt file never breaks a turn.
    /// </summary>
    private void InjectVibeState(JsonObject payload, string model, string variant)
    {
        var engine = ContextEngine;
        if (engine is null)
        {
            return;
        }

        // Read the live per-profile toggle. Null (profile unresolvable) is treated as on, the
        // default — matching the forward-prune gate.
        var enabled = GetVibeStateEnabled?.Invoke(model, variant);
        if (enabled == false)
        {
            return;
        }

        var block = engine.BuildReinjectionBlock(VibeStatePath);
        if (string.IsNullOrWhiteSpace(block))
        {
            // No VIBE_STATE.md yet, or empty state — nothing to inject.
            return;
        }

        var messages = payload["messages"] as JsonArray;
        if (messages is null || messages.Count == 0)
        {
            return;
        }

        // Find the first system/developer message and append the block to it. If none exists,
        // insert a new system message at the front (prefix-safe: it becomes the first message).
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msg)
            {
                continue;
            }

            var role = Str(msg, "role").Trim().ToLowerInvariant();
            if (role is "system" or "developer")
            {
                var existing = MessageText(msg);
                msg["content"] = existing.Length > 0 ? existing + "\n\n" + block : block;
                // DIAG (temporary): hash the injected block so we can see from the log whether the
                // VIBE_STATE block is changing between turns. A changing block changes the system
                // prompt, which breaks the KV cache prefix and forces a large re-prefill every turn.
                // If this hash is stable across turns, the VIBE_STATE block is NOT the cause of the
                // repeated prefilling. Remove this once the prefill behavior is understood.
                Log("vibe-state: re-injected " + block.Length + " chars into the system prompt (blockHash=" + StableHash(block) + ")");
                return;
            }
        }

        // No system message present: insert one at the front so the block is prefix-stable.
        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = block });
        Log("vibe-state: inserted a system message with " + block.Length + " chars at the front (blockHash=" + StableHash(block) + ")");
    }

    /// <summary>
    /// DIAG (temporary): a cheap, stable 8-hex-char hash of a string, used only to detect whether
    /// the VIBE_STATE reinjection block is changing between turns. Not a security hash — just a
    /// quick "did this string change?" fingerprint for the log. Remove once the prefill behavior
    /// is understood.
    /// </summary>
    private static string StableHash(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "empty";
        }

        unchecked
        {
            var hash = 23;
            foreach (var c in value)
            {
                hash = hash * 31 + c;
            }

            return ((uint)hash).ToString("x8");
        }
    }

    /// <summary>
    /// After each local turn, refresh VIBE_STATE.md with the current task/state/next. This is the
    /// automatic counterpart to the <c>update_vibe_state</c> MCP tool: instead of the model calling
    /// the tool explicitly, FluxMux updates the file on every turn so the session's "where we are"
    /// note stays current without any model involvement.
    ///
    /// The update is conservative: it only writes if the state actually changed, and it never
    /// throws. A failure here must never break a turn.
    /// </summary>
    private void UpdateVibeStateAfterTurn(JsonObject payload, string model, string variant)
    {
        var engine = ContextEngine;
        if (engine is null)
        {
            return;
        }

        // Read the live per-profile toggle. Null (profile unresolvable) is treated as on, the
        // default — matching the forward-prune gate.
        var enabled = GetVibeStateEnabled?.Invoke(model, variant);
        if (enabled == false)
        {
            return;
        }

        try
        {
            var messages = payload["messages"] as JsonArray;
            if (messages is null || messages.Count < 2)
            {
                return;
            }

            // Find the first user message (the original ask → stable task), the last user message
            // (the current intent → pivot detection), and the last assistant message (the state).
            string firstUser = string.Empty;
            string lastUser = string.Empty;
            string lastAssistant = string.Empty;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i] is not JsonObject msg)
                {
                    continue;
                }

                var role = Str(msg, "role").Trim().ToLowerInvariant();
                if (role == "user")
                {
                    var text = MessageText(msg);
                    if (string.IsNullOrEmpty(firstUser) && !string.IsNullOrWhiteSpace(text))
                    {
                        firstUser = text;
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lastUser = text;
                    }
                }
                else if (role == "assistant")
                {
                    var text = MessageText(msg);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lastAssistant = text;
                    }
                }
            }

            if (string.IsNullOrEmpty(lastUser))
            {
                return;
            }

            var existing = engine.LoadVibeState(VibeStatePath);
            var essence = EssenceLog;

            // Goal-pivot detection: if the last user message diverges from the stored task, record
            // an evolution entry (the "changing goals" capture) so the essence survives compaction.
            // The stable task is the original ask (existing.Task, or firstUser on the very first
            // turn) unless a pivot is detected.
            var pivoted = !string.IsNullOrWhiteSpace(existing.Task) && LooksLikePivot(existing.Task, lastUser);
            string task;
            if (!string.IsNullOrWhiteSpace(existing.Task) && !pivoted)
            {
                task = existing.Task; // keep the stable task
            }
            else if (string.IsNullOrWhiteSpace(existing.Task))
            {
                // First turn: seed the stable task from the original ask (first user message),
                // distilled to a concise label (the essence carries meaning, not a transcript).
                var seed = !string.IsNullOrWhiteSpace(firstUser) ? firstUser : lastUser;
                task = DistillTask(seed);
            }
            else
            {
                // Pivot detected: the new intent becomes the task (distilled to a concise label).
                task = DistillTask(lastUser);
            }

            if (essence is not null && pivoted)
            {
                essence.RecordEvolution(existing.Task, task, "user pivoted");
                Log("vibe-state: goal pivot detected (task updated via essence log)");
            }

            // Mechanical capture of decisions and dead ends from the last assistant message.
            // These are the highest-value "meaning" that a lossy compaction destroys, and they
            // can't be reconstructed from the conversation tail. We look for explicit markers
            // (decision: / decided / going with / chose / opted for, and dead end / doesn't work /
            // failed / abandoned / not viable) and record the surrounding text. Conservative:
            // only records when a marker is found, and dedupes against already-recorded entries.
            if (essence is not null && !string.IsNullOrWhiteSpace(lastAssistant))
            {
                foreach (var decision in ExtractDecisions(lastAssistant))
                {
                    essence.RecordDecision(decision);
                    Log("vibe-state: decision captured (" + decision.Length + " chars)");
                }

                foreach (var deadEnd in ExtractDeadEnds(lastAssistant))
                {
                    essence.RecordDeadEnd(deadEnd);
                    Log("vibe-state: dead end captured (" + deadEnd.Length + " chars)");
                }
            }

            // Build a proper digest (not raw truncations): prefer explicit markers, else a bounded
            // tail of the last assistant message.
            var state = ExtractStateDigest(lastAssistant);
            var next = ExtractNextMarker(lastAssistant, existing.Next);

            var newState = new VibeState
            {
                Task = task,
                State = state,
                Next = next,
                Files = existing.Files,
            };

            // Only write if something actually changed (avoid unnecessary disk I/O).
            if (newState.Task != existing.Task || newState.State != existing.State || newState.Next != existing.Next)
            {
                engine.SaveVibeState(VibeStatePath, newState);
                // Mirror into the essence log (the durable source of truth for re-assertion).
                essence?.UpdateCurrent(task, state, next, existing.Files);
                Log("vibe-state: auto-updated after turn (task=" + newState.Task.Length
                    + " chars, state=" + newState.State.Length + " chars, next=" + newState.Next.Length + " chars)");
            }
        }
        catch
        {
            // A VIBE_STATE update failure must never break a turn.
        }
    }

    /// <summary>
    /// Cheap goal-pivot heuristic: true when <paramref name="newUserMessage"/> shares little
    /// overlap with <paramref name="storedTask"/> (token-set Jaccard similarity below 0.3). Used to
    /// decide whether the user has pivoted to a new goal (record an evolution entry) or is still on
    /// the same task (keep the stable task).
    /// </summary>
    internal static bool LooksLikePivot(string storedTask, string newUserMessage)
    {
        // A real goal pivot is a substantial new instruction, not a short confirmation like
        // "ok", "yes", "ok rebuilt", or "go ahead". Short messages share few tokens with the
        // stored task and would otherwise be mis-flagged as pivots, clobbering the stable task.
        // Require the new message to carry enough distinct content tokens to be a genuine pivot.
        var b = TokenSet(newUserMessage);
        if (b.Count < 3)
        {
            return false;
        }

        var a = TokenSet(storedTask);
        if (a.Count == 0)
        {
            return false;
        }

        var intersect = 0;
        foreach (var t in a)
        {
            if (b.Contains(t))
            {
                intersect++;
            }
        }

        var union = a.Count + b.Count - intersect;
        return union > 0 && ((double)intersect / union) < 0.3;
    }

    /// <summary>Builds a lower-cased token set (words of length >= 4, to ignore short stopwords)
    /// for cheap overlap comparison.</summary>
    private static HashSet<string> TokenSet(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seps = new[] { ' ', ',', '.', '!', '?', ':', ';', '\n', '\r', '(', ')', '"', '\'', '[', ']' };
        foreach (var raw in text.ToLowerInvariant().Split(seps, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length >= 4)
            {
                set.Add(t);
            }
        }

        return set;
    }

    /// <summary>
    /// Extracts a state digest from the last assistant message: prefers explicit Done/In
    /// progress/Blocked/Next markers; otherwise a bounded tail of the text. Never returns more
    /// than ~400 chars.
    /// </summary>
    private static string ExtractStateDigest(string assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var line in assistantText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Done:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("In progress:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Blocked:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Next:", StringComparison.OrdinalIgnoreCase))
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(trimmed);
            }
        }

        var digest = sb.ToString();
        if (string.IsNullOrWhiteSpace(digest))
        {
            // No explicit markers: take a bounded tail of the text, but collapse whitespace and
            // strip leading conversational filler so the digest is concise (not a raw transcript
            // with newlines). The essence should carry the *meaning* of where we are, not the
            // verbatim last message.
            var tail = assistantText.Length > 240 ? assistantText[^240..] : assistantText;
            digest = DistillTask(tail, maxChars: 400);
        }

        return digest.Length > 400 ? digest[^400..] : digest;
    }

    /// <summary>
    /// Extracts an explicit Next/TODO marker from the last assistant message; falls back to
    /// <paramref name="fallback"/> when none is found.
    /// </summary>
    private static string ExtractNextMarker(string assistantText, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            foreach (var line in assistantText.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Next:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("TODO:", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
                }
            }
        }

        return fallback;
    }

    /// <summary>
    /// Extracts decision markers from the last assistant message. Looks for lines that start with
    /// or contain explicit decision phrases ("Decision:", "I decided", "We'll use", "Going with",
    /// "Chose to", "Opted for", "Decided to"). Returns a bounded list of unique decision strings.
    /// Conservative: only records when a marker is found; returns an empty list otherwise.
    /// </summary>
    internal static List<string> ExtractDecisions(string assistantText)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return results;
        }

        // Decision markers: lines that explicitly state a decision. We match on the *start* of the
        // line (after trimming leading markdown/bullets) so we capture "Decision: X" or "- I decided
        // to use Y" but NOT a line that merely lists the markers in quotes (e.g. my own
        // documentation of this feature). The marker must be followed by substantive content.
        var decisionMarkers = new[]
        {
            "decision:",
            "i decided",
            "we'll use", "we will use",
            "going with",
            "chose to", "chose",
            "opted for", "opted to",
            "decided to", "decided",
        };

        foreach (var rawLine in assistantText.Split('\n'))
        {
            // Strip leading markdown bullets/numbering so "- Decision: X" matches "decision:".
            var trimmed = rawLine.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var lower = trimmed.ToLowerInvariant();

            // Find a decision marker at the start of the line.
            string? matchedMarker = null;
            foreach (var marker in decisionMarkers)
            {
                if (lower.StartsWith(marker, StringComparison.Ordinal))
                {
                    matchedMarker = marker;
                    break;
                }
            }

            if (matchedMarker is null)
            {
                continue;
            }

            // The marker must be followed by substantive content (not just the marker alone, and
            // not a line that's clearly listing markers in quotes). Require at least 15 chars of
            // content after the marker.
            var contentAfterMarker = trimmed[matchedMarker.Length..].Trim().TrimStart(':', ' ', '—', '-');
            if (contentAfterMarker.Length < 15)
            {
                continue;
            }

            // Skip lines that look like they're listing the markers themselves (contain multiple
            // quoted markers) — that's documentation, not a real decision.
            if (CountQuotedOccurrences(trimmed) >= 3)
            {
                continue;
            }

            // Extract the decision text (the full line, bounded to 200 chars).
            var decision = trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;

            // Dedupe: only add if not already in the list.
            if (!results.Any(existing => existing == decision))
            {
                results.Add(decision);
            }

            // Bound the list to avoid excessive entries.
            if (results.Count >= 5)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Counts how many distinct quoted phrases (text inside double quotes) appear in a line. Used
    /// to detect lines that are *listing* markers (documentation) rather than stating a real
    /// decision/dead-end. A line with 3+ quoted phrases is almost certainly a list, not a decision.
    /// </summary>
    private static int CountQuotedOccurrences(string line)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                if (!inQuotes)
                {
                    count++;
                }

                inQuotes = !inQuotes;
            }
        }

        return count;
    }

    /// <summary>
    /// Extracts dead-end markers from the last assistant message. Looks for lines that explicitly
    /// state a failed approach or dead end ("dead end", "doesn't work", "failed", "abandoned",
    /// "gave up on", "not viable", "no good", "rejected", "doesn't compile", "crashes"). Returns a
    /// bounded list of unique dead-end strings. Conservative: only records when a marker is found;
    /// returns an empty list otherwise.
    /// </summary>
    internal static List<string> ExtractDeadEnds(string assistantText)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return results;
        }

        // Dead-end markers: lines that explicitly state a failed approach. Match on the *start* of
        // the line (after trimming leading markdown/bullets) so we capture "Dead end: X" or
        // "- This approach doesn't work because Y" but NOT a line that merely lists the markers in
        // quotes (e.g. my own documentation of this feature). The marker must be followed by
        // substantive content.
        var deadEndMarkers = new[]
        {
            "dead end",
            "doesn't work", "does not work",
            "failed",
            "abandoned",
            "gave up on",
            "not viable",
            "no good",
            "rejected",
            "doesn't compile", "does not compile",
            "crashes",
            "doesn't build", "does not build",
        };

        foreach (var rawLine in assistantText.Split('\n'))
        {
            // Strip leading markdown bullets/numbering so "- Dead end: X" matches "dead end".
            var trimmed = rawLine.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var lower = trimmed.ToLowerInvariant();

            // Find a dead-end marker at the start of the line.
            string? matchedMarker = null;
            foreach (var marker in deadEndMarkers)
            {
                if (lower.StartsWith(marker, StringComparison.Ordinal))
                {
                    matchedMarker = marker;
                    break;
                }
            }

            if (matchedMarker is null)
            {
                continue;
            }

            // The marker must be followed by substantive content (not just the marker alone, and
            // not a line that's clearly listing markers in quotes). Require at least 15 chars of
            // content after the marker.
            var contentAfterMarker = trimmed[matchedMarker.Length..].Trim().TrimStart(':', ' ', '—', '-');
            if (contentAfterMarker.Length < 15)
            {
                continue;
            }

            // Skip lines that look like they're listing the markers themselves (contain multiple
            // quoted markers) — that's documentation, not a real dead end.
            if (CountQuotedOccurrences(trimmed) >= 3)
            {
                continue;
            }

            // Extract the dead-end text (the full line, bounded to 200 chars).
            var deadEnd = trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;

            // Dedupe: only add if not already in the list.
            if (!results.Any(existing => existing == deadEnd))
            {
                results.Add(deadEnd);
            }

            // Bound the list to avoid excessive entries.
            if (results.Count >= 5)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Distills a raw user instruction into a concise task label. The essence log should carry the
    /// *meaning* of the ask, not a verbatim transcript. This strips conversational filler ("ok",
    /// "please", "can you", "also", "hey", leading politeness), collapses whitespace, and trims to a
    /// tighter bound. It is a cheap heuristic (no model call) — good enough to turn "ok, can you
    /// please also read examples of the essence that was used at last compaction" into "read
    /// examples of the essence used at last compaction". Never throws; returns the trimmed input
    /// when nothing can be distilled.
    /// </summary>
    internal static string DistillTask(string raw, int maxChars = 140)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();

        // Collapse all whitespace (newlines, multiple spaces) into single spaces.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        // Strip leading conversational filler. These are common openers that carry no task meaning.
        // Applied repeatedly so "ok, please also can you..." collapses fully.
        var leadingFiller = new[]
        {
            "ok,", "ok", "okay", "okay,", "yes", "yes,", "yeah", "yep",
            "please", "pls", "please,", "can you", "could you", "would you",
            "can you please", "could you please", "please also", "also", "and",
            "hey", "hi", "hello", "so", "well", "right", "alright", "alright,",
        };

        bool changed;
        do
        {
            changed = false;
            var lower = text.ToLowerInvariant();
            foreach (var filler in leadingFiller)
            {
                if (lower.StartsWith(filler, StringComparison.Ordinal))
                {
                    // Only strip if followed by a boundary (space, comma, or end) so we don't
                    // eat into a real word (e.g. "ok" should not strip from "okay" already handled,
                    // but "also" must not strip from "also" embedded in "also" — the boundary check
                    // ensures we strip whole filler tokens).
                    var rest = text[filler.Length..];
                    if (rest.Length == 0 || rest[0] is ' ' or ',' or '.' or '!' or '?')
                    {
                        text = rest.TrimStart(' ', ',', '.', '!', '?', ' ');
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        // Strip trailing conversational filler ("thanks", "thx", "please", "let me know").
        var trailingFiller = new[]
        {
            " thanks", " thanks.", " thx", " please", " please.", " let me know",
            " let me know.", " sound good?", " sounds good?",
        };

        changed = true;
        while (changed)
        {
            changed = false;
            var lower = text.ToLowerInvariant();
            foreach (var filler in trailingFiller)
            {
                if (lower.EndsWith(filler, StringComparison.Ordinal))
                {
                    text = text[..^filler.Length].TrimEnd(' ', ',', '.', '!', '?');
                    changed = true;
                    break;
                }
            }
        }

        // If stripping left nothing, fall back to the original trimmed text.
        if (string.IsNullOrWhiteSpace(text))
        {
            text = raw.Trim();
        }

        // Trim to the bound.
        return text.Length > maxChars ? text[..maxChars].TrimEnd() + "…" : text;
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

    /// <summary>
    /// Atomically writes text to a file by writing to a temp file in the same directory and
    /// then renaming it over the target. On NTFS, a same-volume rename is atomic, so a crash
    /// mid-write can never leave the target file truncated or corrupt. The temp file is
    /// created in the same directory as the target (required for the rename to be atomic).
    /// If the write or rename fails, the temp file is cleaned up and the original target is
    /// left untouched.
    /// </summary>
    private static void AtomicWriteAllText(string path, string contents)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, contents);
            // Same-directory rename is atomic on NTFS. Overwrite any existing target.
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup of the temp file; never let a state-write failure propagate.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
            throw;
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
            AtomicWriteAllText(_recommendPath, payload.ToJsonString());
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

            AtomicWriteAllText(_localReloadPath, payload.ToJsonString());
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
            AtomicWriteAllText(_lastServedPath, payload.ToJsonString());
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
        // Note: spill-compact offers are NOT cleared here — the hysteresis logic in
        // OfferSpillCompactIfNeeded handles the show/hide decision for those.
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

            // Do not clear a spill-compact offer — the hysteresis logic in
            // OfferSpillCompactIfNeeded handles the show/hide decision. Clearing it here
            // would cause the dialog to flicker when the prompt hovers around the trigger
            // threshold. The spill-compact offer will be hidden by OfferSpillCompactIfNeeded
            // when the prompt drops below the hide threshold.
            if (ParseBool(Str(existing, "spillCompact")))
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
            AtomicWriteAllText(_lastRouteExplanationPath, payload.ToJsonString());
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
            // Capture the original stall deadline so we can restore it after ResetForWaitLonger.
            // For long-load profiles, the stall deadline is set to the first-byte deadline (600s+)
            // because a quiet stream after the first byte is normal (RAM-spill prefill). Without
            // this, ResetForWaitLonger would reset it to 90s and the false-positive abort would
            // recur on the next wait-longer cycle.
            var originalStallDeadline = clock.StallDeadline;
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

                // Prefill / first-token window: until the first REAL content token arrives, the
                // stream is still "prefilling" even if header bytes have already arrived. For a
                // thinking model the first byte arrives quickly but the first content token can be
                // minutes away. During this window we use the (capped) prefill estimate to decide
                // when to show the "wait longer" dialog, instead of the long post-first-byte stall
                // deadline (which would never fire for a slow-but-healthy prefill). This check runs
                // BEFORE the ShouldAbort gate, because once header bytes have arrived ShouldAbort
                // only fires on the (very long) stall deadline — it would not surface a slow
                // prefill that is still within the capped estimate.
                //
                // SAFETY CAP: Even if the estimate is large (e.g. inflated by a corrupted learned
                // model), the dialog must appear after 120s. This prevents the user from being
                // stuck with no options for an unbounded time.
                //
                // The effective prefill window is max(estimate, FirstByteDeadline): the estimate
                // may EXTEND the wait for a genuinely slow prefill, but it must never SHORTEN the
                // long-load first-byte deadline. A stale estimate from a previous turn (the
                // estimate is only cleared on launch/abort, not on normal completion) can be far
                // smaller than the 600s long-load deadline; using min() let a 60s stale estimate
                // surface the dialog at 60s and then oscillate (StreamRecovered kept returning
                // true because header bytes were present and the stall deadline was 1800s), spamming
                // the log for the rest of the prefill. max() keeps the long-load deadline intact.
                const int PrefillEstimateHardCapSeconds = 120;
                int cappedEstimate = Math.Min(CurrentPrefillEstimateSeconds, PrefillEstimateHardCapSeconds);
                int effectiveEstimate = Math.Max(cappedEstimate, clock.FirstByteDeadlineSeconds);
                string reason;
                if (!clock.GotContentToken && effectiveEstimate > 0)
                {
                    var elapsedSinceCopyStart = (now - clock.CopyStartUtc).TotalSeconds;
                    if (elapsedSinceCopyStart < effectiveEstimate)
                    {
                        // Still within the prefill / first-token window. Log once and keep waiting.
                        if (clock.WaitLongerCount == 0)
                        {
                            Log("local stream hang: still within prefill/first-token window ("
                                + (int)elapsedSinceCopyStart + "s / "
                                + effectiveEstimate + "s, raw estimate " + CurrentPrefillEstimateSeconds + "s, bytes="
                                + (gotUpstreamBytes() ? "yes" : "no") + ") — waiting without dialog");
                        }
                        continue;
                    }
                    // Past the prefill window with no content token: fall through to the dialog
                    // below (reason = first-byte, since no content token has arrived).
                    reason = LocalStreamHangPolicy.FirstByteReason;
                }
                else if (!LocalStreamHangPolicy.ShouldAbort(
                    gotUpstreamBytes(),
                    now - clock.CopyStartUtc,
                    now - clock.LastByteUtc,
                    clock.FirstByteDeadline,
                    clock.StallDeadline,
                    out reason))
                {
                    continue;
                }

                // Oscillation guard: once we have surfaced the "wait longer" dialog for this turn
                // and the user chose to keep waiting (WaitLongerCount > 0), do NOT re-surface it
                // every 250ms. The previous cycle already extended the copy timeout; re-asking the
                // same question on every tick (which is what happened for 897s when the estimate
                // was stale and StreamRecovered kept returning true) spams the log and the UI.
                // After a wait-longer choice, rely on ShouldAbort's extended deadlines to detect a
                // genuinely stuck stream; only re-surface if a NEW stall condition arises.
                if (clock.WaitLongerCount > 0 && reason == LocalStreamHangPolicy.FirstByteReason)
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
                    // Restore the long stall deadline for long-load profiles. ResetForWaitLonger
                    // resets it to WaitLongerSeconds (90s), which is too short for a RAM-spill
                    // prefill that can take minutes. The original deadline (600s+ for long-load)
                    // is the correct window for "genuinely stuck" detection.
                    if (clock.StallDeadline < originalStallDeadline)
                    {
                        clock.StallDeadline = originalStallDeadline;
                    }
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