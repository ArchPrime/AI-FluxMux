using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace FluxMux.Avalonia.Services;

public sealed record ModelScenario(
    bool IsClineLike, bool IsRamSpill, bool IsLongLoadProfile, bool IsColdStart, bool IsPortRulesOn, bool IsRoutingOn, bool IsLocalReady, bool IsCloudReady, string? CloudCapacity, int HotContext, int VramContext, int PromptTokens, int PredictedPrefillSeconds, double RamUsagePercent, bool CloudBreakerOpen);

public enum SupervisorVerdict { Healthy, Watch, ReloadRecommended, CompactRecommended, CloudConsentRecommended, RamThrashRisk, LaunchStalled, ContextOverflow }

public sealed class FluxMuxSupervisor : IDisposable
{
    private const int TickIntervalMs = 5000;
    private const double RamThrashWarnPercent = 70.0;
    private const double RamThrashKillPercent = 85.0;
    private const int ContextFillWatchPercent = 80;
    private readonly string _logDirectory;
    private readonly string _proxyRuntimeStatePath;
    private readonly string _localRuntimeStatePath;
    private readonly string _localContextUsagePath;
    private readonly string _proxyCloudBreakerPath;
    private readonly string _proxyLocalReloadRecommendPath;
    private readonly string _supervisorHealthPath;
    private Timer? _timer;
    private string _lastVerdict = string.Empty;
    private readonly object _writeLock = new();

    public FluxMuxSupervisor(string logDirectory)
    {
        _logDirectory = logDirectory;
        _proxyRuntimeStatePath = Path.Combine(logDirectory, "Proxy_Runtime_State.json");
        _localRuntimeStatePath = Path.Combine(logDirectory, "Local_Runtime_State.json");
        _localContextUsagePath = Path.Combine(logDirectory, "Local_Context_Usage.json");
        _proxyCloudBreakerPath = Path.Combine(logDirectory, "Proxy_Cloud_Breaker.json");
        _proxyLocalReloadRecommendPath = Path.Combine(logDirectory, "Proxy_Local_Reload_Recommend.json");
        _supervisorHealthPath = Path.Combine(logDirectory, "Supervisor_Health.json");
    }

    public void Start()
    {
        Stop();
        _timer = new Timer(OnTick, null, TickIntervalMs, TickIntervalMs);
    }
    public void Stop() { _timer?.Dispose(); _timer = null; }

    public void Tick()
    {
        try
        {
            var scenario = ComputeScenario();
            var verdict = EvaluateVerdict(scenario);
            var health = new JsonObject
            {
                ["scenario"] = FluxMuxSupervisor.ToJson(scenario),
                ["fillPercent"] = ComputeFillPercent(scenario),
                ["verdict"] = verdict.ToString().ToLowerInvariant(),
                ["recommendations"] = BuildRecommendations(scenario, verdict),
                ["risks"] = BuildRisks(scenario),
                ["portRules"] = BuildPortRules(scenario),
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            var verdictStr = verdict.ToString().ToLowerInvariant();
            lock (_writeLock)
            {
                if (verdictStr != _lastVerdict)
                {
                    _lastVerdict = verdictStr;
                    // Read Proxy_Last_Served.json only when the health file is actually written
                    // (verdict changed). Reading and parsing it on every tick was needless I/O on
                    // the hot path; the value is consumed only by this write.
                    health["lastServed"] = ReadLastServed();
                    AtomicWriteAllText(_supervisorHealthPath, health.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch (Exception ex)
        {
            // Log the error so we can diagnose why the health file isn't being written.
            try
            {
                string diagPath = Path.Combine(_logDirectory, "Supervisor_Tick_Error.log");
                File.AppendAllText(diagPath, $"[{DateTime.UtcNow:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }
        }
    }

    private void OnTick(object? state) { Tick(); }
    public ModelScenario ComputeScenario()
    {
        var proxyState = ReadJsonFile(_proxyRuntimeStatePath);
        var localState = ReadJsonFile(_localRuntimeStatePath);
        var contextUsage = ReadJsonFile(_localContextUsagePath);
        var cloudBreaker = ReadJsonFile(_proxyCloudBreakerPath);
        var localReload = ReadJsonFile(_proxyLocalReloadRecommendPath);
        int hotContext = ParseInt(GetStr(proxyState, "local_context"));
        int vramContext = ParseInt(GetStr(proxyState, "local_vram_context"));
        int promptTokens = ParseInt(GetStr(contextUsage, "prompt_tokens"));
        bool isLocalReady = FluxMuxGatewayRouting.LocalTargetReady(proxyState);
        bool isCloudReady = FluxMuxGatewayRouting.CloudTargetReady(proxyState);
        bool isRoutingOn = FluxMuxGatewayRouting.RoutingOn(proxyState);
        string? cloudCapacity = GetStr(proxyState, "cloud_routing_capacity");
        if (string.IsNullOrWhiteSpace(cloudCapacity)) cloudCapacity = "Normal";
        bool cloudBreakerOpen = IsCloudBreakerOpen(cloudBreaker);
        double ramUsagePercent = GetRamUsagePercent();
        string endpointApp = GetStr(proxyState, "endpoint_app");
        bool isClineLike = endpointApp.Equals("Cline", StringComparison.OrdinalIgnoreCase);
        bool isRamSpill = vramContext > 0 && promptTokens > vramContext && hotContext > vramContext;
        string gpuOffloadMode = GetStr(proxyState, "local_gpu_offload");
        bool isLongLoadProfile = LocalLoadingRetryPolicy.IsLongLoadProfile(hotContext, gpuOffloadMode);
        bool isColdStart = IsLocalModelRecentlyReady(localState);
        bool isPortRulesOn = localReload is not null && (GetBool(localReload, "forward_compact") || GetBool(localReload, "spill_compact"));
        int predictedPrefillSeconds = promptTokens > 0 ? (int)Math.Ceiling(promptTokens / 500.0) : 0;
        return new ModelScenario(isClineLike, isRamSpill, isLongLoadProfile, isColdStart, isPortRulesOn, isRoutingOn, isLocalReady, isCloudReady, cloudCapacity, hotContext, vramContext, promptTokens, predictedPrefillSeconds, ramUsagePercent, cloudBreakerOpen);
    }

    public SupervisorVerdict EvaluateVerdict(ModelScenario scenario)
    {
        if (scenario.HotContext > 0 && scenario.PromptTokens > 0)
        {
            if (scenario.PromptTokens + 256 > scenario.HotContext) return SupervisorVerdict.ContextOverflow;
        }
        if (scenario.RamUsagePercent >= RamThrashKillPercent) return SupervisorVerdict.RamThrashRisk;
        if (!scenario.IsLocalReady)
        {
            var localState = ReadJsonFile(_localRuntimeStatePath);
            string phase = GetStr(localState, "phase");
            if (!string.IsNullOrEmpty(phase) && phase != "ready" && phase != "stopped") return SupervisorVerdict.LaunchStalled;
        }
        int fillPercent = ComputeFillPercent(scenario);
        if (fillPercent >= ContextFillWatchPercent && scenario.IsLocalReady)
        {
            var proxyState = ReadJsonFile(_proxyRuntimeStatePath);
            if (proxyState?["local_reload_pool"] is JsonArray pool && pool.Count > 0)
            {
                foreach (var node in pool)
                {
                    if (node is JsonObject candidate && ParseInt(GetStr(candidate, "context")) > scenario.HotContext)
                        return SupervisorVerdict.ReloadRecommended;
                }
            }
            if (!scenario.IsClineLike) return SupervisorVerdict.CompactRecommended;
        }
        if (scenario.IsRoutingOn && scenario.IsCloudReady && !scenario.CloudBreakerOpen && fillPercent >= 90)
            return SupervisorVerdict.CloudConsentRecommended;
        if (scenario.RamUsagePercent >= RamThrashWarnPercent) return SupervisorVerdict.Watch;
        return SupervisorVerdict.Healthy;
    }
    private JsonArray BuildRecommendations(ModelScenario scenario, SupervisorVerdict verdict)
    {
        var recs = new JsonArray();
        switch (verdict)
        {
            case SupervisorVerdict.ContextOverflow: recs.Add(CreateRec("context-overflow", "The prompt exceeds the model context window.")); break;
            case SupervisorVerdict.RamThrashRisk: recs.Add(CreateRec("ram-thrash", "RAM usage exceeds 85%. Model will be killed automatically.")); break;
            case SupervisorVerdict.LaunchStalled: recs.Add(CreateRec("launch-stalled", "Local model is still loading.")); break;
            case SupervisorVerdict.ReloadRecommended: recs.Add(CreateRec("reload-to-bigger-context", "Context filling up. Larger context variant available.")); break;
            case SupervisorVerdict.CompactRecommended: recs.Add(CreateRec("compact", "Context filling up. Consider compacting.")); break;
            case SupervisorVerdict.CloudConsentRecommended: recs.Add(CreateRec("cloud-consent", "Local cannot take turn. Cloud fallback available.")); break;
            case SupervisorVerdict.Watch: recs.Add(CreateRec("ram-watch", "RAM usage elevated.")); break;
        }
        if (scenario.IsRamSpill && scenario.PredictedPrefillSeconds > 60) recs.Add(CreateRec("ram-spill-prefill", $"Prompt spilled into RAM. Prefill ~{scenario.PredictedPrefillSeconds}s."));
        if (scenario.IsColdStart && scenario.PredictedPrefillSeconds > 0) recs.Add(CreateRec("cold-start", $"First prefill after launch. ~{scenario.PredictedPrefillSeconds}s."));
        return recs;
    }

    private JsonObject BuildRisks(ModelScenario scenario)
    {
        return new JsonObject
        {
            ["staleEstimateRisk"] = scenario.IsColdStart && scenario.PredictedPrefillSeconds > 0,
            ["watchdogOscillationRisk"] = scenario.IsRamSpill && scenario.PredictedPrefillSeconds > 120,
            ["ramThrashRisk"] = scenario.RamUsagePercent >= RamThrashWarnPercent,
            ["contextOverflowRisk"] = scenario.HotContext > 0 && scenario.PromptTokens > 0 && (scenario.PromptTokens + 256) > scenario.HotContext
        };
    }

    private JsonObject BuildPortRules(ModelScenario scenario)
    {
        bool compactEnabled = !scenario.IsClineLike && scenario.IsPortRulesOn;
        string reason = scenario.IsClineLike ? "client reinflates context" : (scenario.IsPortRulesOn ? "port rules active" : "port rules off");
        return new JsonObject { ["enabled"] = scenario.IsPortRulesOn, ["compactEnabled"] = compactEnabled, ["reason"] = reason };
    }

    public int ComputeFillPercent(ModelScenario scenario)
    {
        if (scenario.HotContext <= 0 || scenario.PromptTokens <= 0) return 0;
        return Math.Clamp((int)(scenario.PromptTokens * 100.0 / scenario.HotContext), 0, 100);
    }
    private string ReadLastServed()
    {
        try
        {
            var p = Path.Combine(_logDirectory, "Proxy_Last_Served.json");
            if (!File.Exists(p)) return string.Empty;
            var root = ReadJsonFile(p);
            if (root is null) return string.Empty;
            string kind = GetStr(root, "kind");
            string model = GetStr(root, "model");
            string provider = GetStr(root, "provider");
            return kind.Equals("local", StringComparison.OrdinalIgnoreCase) ? "Local/" + model : $"{provider}/{model}";
        }
        catch { return string.Empty; }
    }

    private static bool IsCloudBreakerOpen(JsonObject? breaker)
    {
        if (breaker is null || breaker["entries"] is not JsonObject entries) return false;
        var now = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            if (entry.Value is not JsonObject be) continue;
            string openUntil = GetStr(be, "openUntilUtc");
            if (string.IsNullOrWhiteSpace(openUntil)) continue;
            if (DateTime.TryParse(openUntil, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var until) && until > now) return true;
        }
        return false;
    }

    private static double GetRamUsagePercent()
    {
        try
        {
            double total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (total <= 0) return 0.0;
            double procRam = 0;
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.ProcessName.Contains("llama", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Contains("FluxMux", StringComparison.OrdinalIgnoreCase)) procRam = Math.Max(procRam, p.WorkingSet64); }
                catch { }
                finally { p.Dispose(); }
            }
            return procRam * 100.0 / total;
        }
        catch { return 0.0; }
    }

    private static bool IsLocalModelRecentlyReady(JsonObject? localState)
    {
        if (localState is null) return false;
        string updatedUtc = GetStr(localState, "updated_utc");
        if (string.IsNullOrWhiteSpace(updatedUtc)) return false;
        if (!DateTime.TryParse(updatedUtc, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var updated)) return false;
        return (DateTime.UtcNow - updated).TotalSeconds < 30;
    }
    private static JsonObject? ReadJsonFile(string path)
    {
        try { if (!File.Exists(path)) return null; return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch { return null; }
    }

    private static string GetStr(JsonObject? obj, string key)
    {
        if (obj is null) return string.Empty;
        var v = obj[key];
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return v?.ToString() ?? string.Empty;
    }

    private static bool GetBool(JsonObject? obj, string key)
    {
        if (obj is null) return false;
        var v = obj[key];
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var f)) return f;
        var t = v?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        return t is "1" or "true" or "yes" or "on";
    }

    private static int ParseInt(string value) => int.TryParse(value, out var r) ? r : 0;

    private static JsonObject CreateRec(string type, string message) => new() { ["type"] = type, ["message"] = message };

    private static void AtomicWriteAllText(string path, string contents)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }


    public static JsonObject ToJson(ModelScenario s)
    {
        return new JsonObject
        {
            ["isClineLike"] = s.IsClineLike,
            ["isRamSpill"] = s.IsRamSpill,
            ["isLongLoadProfile"] = s.IsLongLoadProfile,
            ["isColdStart"] = s.IsColdStart,
            ["isPortRulesOn"] = s.IsPortRulesOn,
            ["isRoutingOn"] = s.IsRoutingOn,
            ["isLocalReady"] = s.IsLocalReady,
            ["isCloudReady"] = s.IsCloudReady,
            ["cloudCapacity"] = s.CloudCapacity ?? "Normal",
            ["hotContext"] = s.HotContext,
            ["vramContext"] = s.VramContext,
            ["promptTokens"] = s.PromptTokens,
            ["predictedPrefillSeconds"] = s.PredictedPrefillSeconds,
            ["ramUsagePercent"] = Math.Round(s.RamUsagePercent, 1),
            ["cloudBreakerOpen"] = s.CloudBreakerOpen
        };
    }


    // --- Phase 2: Prefill Prediction ---

    private int _currentPrefillEstimateSeconds;
    private readonly object _prefillEstimateLock = new();

    // Live telemetry: the last actual prefill speed (tokens/sec) and when it was measured.
    // Used to calibrate the next estimate when the learned model has no data for the
    // current model/variant. Reset when the model changes.
    private double _lastActualPrefillSpeedTokensPerSec;
    private DateTimeOffset _lastActualPrefillUtc = DateTimeOffset.MinValue;

    public Action<int>? SetGatewayPrefillEstimate { get; set; }
    public Action? ClearGatewayPrefillEstimate { get; set; }

    /// <summary>
    /// Records the actual prefill speed after a prefill completes. Called from the UI
    /// when the gateway reports the first byte. Used to calibrate the next estimate
    /// when the learned model has no data for the current model/variant.
    /// </summary>
    public void RecordActualPrefillSpeed(int promptTokens, long elapsedMs)
    {
        if (promptTokens <= 0 || elapsedMs <= 0) return;
        double speed = promptTokens / (elapsedMs / 1000.0);
        if (speed > 0)
        {
            _lastActualPrefillSpeedTokensPerSec = speed;
            _lastActualPrefillUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Resets the last-actual-prefill-speed when the model changes. Called from the UI
    /// when a new model is launched or the profile changes.
    /// </summary>
    public void ResetPrefillSpeedCalibration()
    {
        _lastActualPrefillSpeedTokensPerSec = 0;
        _lastActualPrefillUtc = DateTimeOffset.MinValue;
    }

    public int PredictPrefillSeconds(ModelScenario scenario, int promptTokens, PrefillPerformanceModel? prefillModel)
    {
        if (promptTokens <= 0) return 0;
        bool useRamSpillEstimate = scenario.IsRamSpill || scenario.IsLongLoadProfile;
        if (useRamSpillEstimate)
        {
            int ramEstimate = prefillModel?.RamSpillEstimateSeconds(promptTokens) ?? 0;
            if (ramEstimate <= 0) ramEstimate = 60;
            // Live telemetry: adjust for RAM pressure. When the system is under memory
            // pressure (>70% RAM used), RAM-spill prefills are slower due to page pressure.
            // +1% per point above 70%, capped at +30%.
            if (scenario.RamUsagePercent > 70.0)
            {
                double ramPressureFactor = 1.0 + Math.Min(0.30, (scenario.RamUsagePercent - 70.0) * 0.01);
                ramEstimate = (int)Math.Ceiling(ramEstimate * ramPressureFactor);
            }
            return Math.Clamp(ramEstimate, 60, 1800);
        }

        // Hybrid VRAM+RAM estimate: when the prompt exceeds the VRAM context, the first
        // VramContext tokens run at VRAM speed and the remainder runs at RAM speed.
        // This is more accurate than treating the whole prompt as either all-VRAM or
        // all-RAM.
        if (scenario.VramContext > 0 && promptTokens > scenario.VramContext)
        {
            int vramTokens = scenario.VramContext;
            int ramTokens = promptTokens - vramTokens;

            double vramSpeed = prefillModel?.CurrentSpeedTokensPerSec ?? PrefillPerformanceModel.DefaultTokensPerSec;
            double ramSpeed = vramSpeed * 0.25; // RAM is ~4x slower than VRAM for prefill

            double vramTime = vramTokens / Math.Max(1.0, vramSpeed);
            double ramTime = ramTokens / Math.Max(1.0, ramSpeed);
            int hybridEstimate = (int)Math.Ceiling(vramTime + ramTime);

            // Live telemetry: adjust for RAM pressure.
            if (scenario.RamUsagePercent > 70.0)
            {
                double ramPressureFactor = 1.0 + Math.Min(0.30, (scenario.RamUsagePercent - 70.0) * 0.01);
                hybridEstimate = (int)Math.Ceiling(hybridEstimate * ramPressureFactor);
            }

            return Math.Clamp(hybridEstimate, 5, 1800);
        }

        int baseEstimate = prefillModel?.EstimateSeconds(promptTokens, scenario.IsColdStart) ?? 0;
        bool hasLearnedData = baseEstimate > 0;
        if (baseEstimate <= 0)
        {
            // Live telemetry: use the last actual prefill speed to calibrate the estimate
            // when the learned model has no data. This is more accurate than a static
            // 60-second default because it reflects the actual speed of the current model
            // on this hardware.
            if (_lastActualPrefillSpeedTokensPerSec > 0
                && (DateTimeOffset.UtcNow - _lastActualPrefillUtc).TotalSeconds < 300)
            {
                baseEstimate = (int)Math.Ceiling(promptTokens / _lastActualPrefillSpeedTokensPerSec);
                hasLearnedData = true; // Treat as learned data to avoid the multiplier
            }
            else
            {
                baseEstimate = 60;
            }
        }
        double multiplier = 1.0;
        if (!hasLearnedData)
        {
            if (promptTokens > 65536) multiplier = 2.0;
            else if (promptTokens > 32768) multiplier = 1.5;
        }
        int estimate = (int)Math.Ceiling(baseEstimate * multiplier);

        // Context-length nonlinearity: prefill speed isn't perfectly linear in token count.
        // For very long contexts (>32K), attention is O(n²) in the worst case, so the
        // per-token cost increases as the context grows. Apply a gentle correction that
        // reduces the effective speed as context grows: 0% at 4K tokens, ~10% at 128K.
        if (promptTokens > 4096)
        {
            double logRatio = Math.Log2((double)promptTokens / 4096.0) / Math.Log2(131072.0 / 4096.0);
            double speedPenalty = 1.0 - 0.10 * Math.Clamp(logRatio, 0.0, 1.0);
            estimate = (int)Math.Ceiling(estimate / speedPenalty);
        }

        int floor = hasLearnedData ? 5 : (promptTokens > 32768 ? 30 : 0);
        estimate = Math.Max(estimate, floor);
        return Math.Clamp(estimate, 0, 1800);
    }

    public void SetPrefillEstimate(int seconds)
    {
        lock (_prefillEstimateLock) { _currentPrefillEstimateSeconds = seconds; }
        SetGatewayPrefillEstimate?.Invoke(seconds);
    }

    public void ClearPrefillEstimate()
    {
        lock (_prefillEstimateLock) { _currentPrefillEstimateSeconds = 0; }
        ClearGatewayPrefillEstimate?.Invoke();
    }

    public int CurrentPrefillEstimateSeconds
    {
        get { lock (_prefillEstimateLock) { return _currentPrefillEstimateSeconds; } }
    }


    // --- Phase 3: Reload-Recommend + Hang Watchdog ---

    private readonly object _reloadLock = new();

    public LocalReloadRecommendSnapshot? ReadLocalReloadRecommend()
    {
        try
        {
            if (!File.Exists(_proxyLocalReloadRecommendPath)) return null;
            var root = ReadJsonFile(_proxyLocalReloadRecommendPath);
            if (root is null) return null;
            string status = GetStr(root, "status");
            if (string.IsNullOrWhiteSpace(status) || status.Equals("idle", StringComparison.OrdinalIgnoreCase)
                || status.Equals("yes", StringComparison.OrdinalIgnoreCase) || status.Equals("no", StringComparison.OrdinalIgnoreCase))
                return null;
            string reason = GetStr(root, "reason");
            if (string.IsNullOrWhiteSpace(reason) && !status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                return null;
            return new LocalReloadRecommendSnapshot(
                status, reason, GetStr(root, "model"), GetStr(root, "variant"), GetStr(root, "displayName"), GetStr(root, "updatedUtc"),
                GetBool(root, "compactRecommended"), GetBool(root, "destinationTight"), GetBool(root, "forward_compact"),
                GetBool(root, "cloud_failover"), GetBool(root, "spillCompact"),
                GetStr(root, "documentContextMode").Equals("pending", StringComparison.OrdinalIgnoreCase),
                ParseInt(GetStr(root, "documentContextTokens")), ParseInt(GetStr(root, "documentContextTotal")), ParseInt(GetStr(root, "spillUrgency")));
        }
        catch { return null; }
    }

    public void AnswerLocalReloadRecommend(bool loadSuggested, bool enableCompact = false)
    {
        lock (_reloadLock)
        {
            var root = File.Exists(_proxyLocalReloadRecommendPath) ? ReadJsonFile(_proxyLocalReloadRecommendPath) : new JsonObject();
            long until = DateTimeOffset.UtcNow.AddMinutes(15.0).ToUnixTimeSeconds();
            root["status"] = (loadSuggested || enableCompact ? "yes" : "no");
            root["allow_until"] = (loadSuggested ? until : 0);
            root["suppress_until"] = (loadSuggested || enableCompact ? 0 : until);
            if (enableCompact)
            {
                root["forward_compact"] = true;
                // The operator clicked Compact: compact the next turn whatever its size. Kept apart
                // from forward_compact, which also means "Compact is on for this profile" and is
                // set on every Launch — read as "compact now", it force-compacted the first turn
                // after each launch and was then cleared, which switched automatic compaction off
                // for the rest of the session.
                root["compact_now"] = true;
                root["compactRecommended"] = false;
            }
            else if (!loadSuggested)
            {
                root["forward_compact"] = false;
            }
            root["updatedUtc"] = DateTime.UtcNow.ToString("o");
            AtomicWriteAllText(_proxyLocalReloadRecommendPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void ClearLocalReloadRecommend()
    {
        lock (_reloadLock)
        {
            if (!File.Exists(_proxyLocalReloadRecommendPath)) return;
            var root = ReadJsonFile(_proxyLocalReloadRecommendPath);
            if (root is null) return;
            string status = GetStr(root, "status");
            if (string.IsNullOrWhiteSpace(status) || status.Equals("idle", StringComparison.OrdinalIgnoreCase)
                || status.Equals("yes", StringComparison.OrdinalIgnoreCase) || status.Equals("no", StringComparison.OrdinalIgnoreCase))
                return;
            root["status"] = "idle";
            root["updatedUtc"] = DateTime.UtcNow.ToString("o");
            AtomicWriteAllText(_proxyLocalReloadRecommendPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static bool ShouldSuppressHangDialog(int waitLongerCount, string reason)
    {
        return waitLongerCount > 0 && reason == LocalStreamHangPolicy.FirstByteReason;
    }


    // --- Phase 4: RAM Watchdog + Launch Monitor ---

    private Timer? _ramWatchdogTimer;
    private DateTime _ramWatchdogExceededUtc = DateTime.MinValue;
    private readonly object _ramWatchdogLock = new();

    private const int RamWatchdogIntervalMs = 5000;
    private const double RamWatchdogThresholdPercent = 0.85;
    private const int RamWatchdogSustainSeconds = 30;

    /// <summary>Delegate to get the current llama-server process (null when not running).</summary>
    public Func<Process?>? GetLocalServerProcess { get; set; }

    /// <summary>Delegate to kill the llama-server process tree.</summary>
    public Action? KillLocalServerProcess { get; set; }

    /// <summary>Delegate to raise the RAM overflow notice to the UI.</summary>
    public Action<string>? RamOverflowNotice { get; set; }

    /// <summary>
    /// Starts the RAM watchdog. Checks the llama-server process working set every 5 seconds.
    /// If it exceeds 85% of total RAM for 30 consecutive seconds, kills the process tree.
    /// </summary>
    public void StartRamWatchdog()
    {
        lock (_ramWatchdogLock)
        {
            StopRamWatchdog();
            _ramWatchdogExceededUtc = DateTime.MinValue;
            _ramWatchdogTimer = new Timer(OnRamWatchdogTick, null, RamWatchdogIntervalMs, RamWatchdogIntervalMs);
        }
    }

    /// <summary>Stops the RAM watchdog.</summary>
    public void StopRamWatchdog()
    {
        lock (_ramWatchdogLock)
        {
            _ramWatchdogTimer?.Dispose();
            _ramWatchdogTimer = null;
            _ramWatchdogExceededUtc = DateTime.MinValue;
        }
    }

    private void OnRamWatchdogTick(object? state)
    {
        try
        {
            var process = GetLocalServerProcess?.Invoke();
            if (process is null || process.HasExited) return;

            double totalRamBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalRamBytes <= 0) return;

            double processRamBytes = process.WorkingSet64;
            double thresholdBytes = totalRamBytes * RamWatchdogThresholdPercent;

            if (processRamBytes > thresholdBytes)
            {
                var nowUtc = DateTime.UtcNow;
                lock (_ramWatchdogLock)
                {
                    if (_ramWatchdogExceededUtc == DateTime.MinValue)
                    {
                        _ramWatchdogExceededUtc = nowUtc;
                    }
                    else if ((nowUtc - _ramWatchdogExceededUtc).TotalSeconds >= RamWatchdogSustainSeconds)
                    {
                        _ramWatchdogExceededUtc = DateTime.MinValue;
                        StopRamWatchdog();
                        try
                        {
                            KillLocalServerProcess?.Invoke();
                        }
                        catch { }
                        RamOverflowNotice?.Invoke(
                            "RAM overflow watchdog: the local model was using more than 85% of system RAM for 30 seconds. "
                            + "It was killed automatically to prevent a system-wide freeze. The next local chat will reload it.");
                        return;
                    }
                }
            }
            else
            {
                lock (_ramWatchdogLock) { _ramWatchdogExceededUtc = DateTime.MinValue; }
            }
        }
        catch { }
    }

    /// <summary>
    /// Logs a launch-monitor message to launch_monitor.log. Thread-safe.
    /// </summary>
    public void LogLaunchMonitor(string message)
    {
        try
        {
            // Background, dated, size-rotated (see RollingLogFile).
            RollingLogFile.For(Path.Combine(_logDirectory, "launch_monitor.log")).WriteLine(message);
        }
        catch { }
    }

    public void Dispose() { Stop(); StopRamWatchdog(); }
}
















