#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8619, CS8620, CS8622, CS8625
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public sealed record SwitchBenchmarkRouteSelection(
    string RouteType,
    string Provider,
    string Model,
    string Variant,
    string DisplayName);

public sealed record LastServedRouteSnapshot(
    string Kind,
    string Provider,
    string Model,
    string UpdatedUtc);

public sealed record CloudRecommendSnapshot(
    string Status,
    string Reason,
    string UpdatedUtc,
    bool FailOnTimeout = false,
    string Source = "");

public sealed record LocalReloadRecommendSnapshot(
    string Status,
    string Reason,
    string Model,
    string Variant,
    string DisplayName,
    string UpdatedUtc,
    bool CompactRecommended = false,
    bool DestinationTight = false,
    bool ForwardCompactActive = false,
    bool CloudFailover = false);

public sealed class FluxMuxRuntimeService
{
	private sealed class ChatProbeResult
	{
		public bool IsSuccess { get; init; }

		public long LatencyMs { get; init; }

		public string Content { get; init; } = string.Empty;

		public string Details { get; init; } = string.Empty;

		public int? PromptTokens { get; init; }

		public int? CompletionTokens { get; init; }

		public int AttemptsUsed { get; init; }

		public long ProbeWindowMs { get; init; }
	}

	private readonly record struct RouteSlotDescriptor(string SlotId, string Label, string RouteType, string CloudProvider, string CloudModel, string LocalModel, string LocalVariant, string DisplayLabel, string ProbeModelLabel, bool IsValid);

	private readonly record struct ContextFidelityScore(int EssentialHits, int EssentialTotal, int NoiseHits, int NoiseTotal);

	private readonly record struct ContextFidelityTierDefinition(int Level, string Name, int MinEssentialHits, int MaxNoiseHits);

	private sealed record ContextFidelityTierResult(int Level, string TierName, string Status, bool Passed, bool IsSuccess, int EssentialHits, int EssentialTotal, int NoiseHits, int NoiseTotal, string LocalLaunchDetails, string CloudLaunchDetails, string LocalReturnDetails, string LocalFirstContent, string LocalReturnContent, long AverageLatencyMs, int TotalPromptTokens, string Details)
	{
		public JsonObject ToJsonNode()
		{
			return new JsonObject
			{
				["level"] = Level,
				["tierName"] = TierName,
				["status"] = Status,
				["passed"] = Passed,
				["isSuccess"] = IsSuccess,
				["essentialHits"] = EssentialHits,
				["essentialTotal"] = EssentialTotal,
				["noiseHits"] = NoiseHits,
				["noiseTotal"] = NoiseTotal,
				["averageLatencyMs"] = AverageLatencyMs,
				["totalPromptTokens"] = TotalPromptTokens,
				["details"] = Details,
				["localLaunchDetails"] = LocalLaunchDetails,
				["cloudLaunchDetails"] = CloudLaunchDetails,
				["localReturnDetails"] = LocalReturnDetails,
				["localFirstContent"] = LocalFirstContent,
				["localReturnContent"] = LocalReturnContent
			};
		}
	}

	public readonly record struct LocalVisionRecommendation(bool Enabled, string ProjectorPath, string Hint);

	public sealed class LocalHardwareLaunchAdvice
	{
		public double GpuTotalGb { get; init; }

		public double ModelFileGb { get; init; }

		public bool NvidiaAvailable { get; init; }

		public int ContextPriorityFirst { get; init; }

		public int ContextPrioritySecond { get; init; }

		public int ContextSafeDefault { get; init; }

		public int ContextPriorityLower { get; init; }

		public string OffloadMode { get; init; } = "CPU only";

		public string OffloadModeAtMaxContext { get; init; } = "CPU only";

		public string GpuLayers { get; init; } = "Auto";

		public string BatchSize { get; init; } = "512";

		public string UbatchSize { get; init; } = "128";

		public string FlashAttention { get; init; } = "Auto";

		public bool PreferFitEnabled { get; init; } = true;

		public string SpecType { get; init; } = "ngram-simple";

		public bool EnableSwaFull { get; init; }

		public string MaxTokens { get; init; } = "4096";

		public string CacheRam { get; init; } = "8192";
	}

	private sealed class GgufModelShape
	{
		public string Architecture { get; init; } = string.Empty;

		public int BlockCount { get; init; }

		public int EmbeddingLength { get; init; }

		public int HeadCount { get; init; }

		public int HeadCountKv { get; init; }

		public int ContextLength { get; init; }

		public int SlidingWindow { get; init; }

		public long FileLength { get; init; }

		public DateTime LastWriteUtc { get; init; }
	}

	private sealed class GpuInventoryRow
	{
		public int Index { get; init; }

		public string Name { get; init; } = string.Empty;

		public double TotalGb { get; init; }

		public double UsedGb { get; init; }

		public double FreeGb { get; init; }

		public string Driver { get; init; } = string.Empty;
	}

	private sealed class GpuGuardrailState
	{
		public bool UseConservativeLocalLaunch { get; init; }

		public string ShortLabel { get; init; } = "n/a";

		public string Message { get; init; } = string.Empty;
	}

	private sealed class DirectoryIndex
	{
		public IReadOnlyList<string> GgufFiles { get; }

		public IReadOnlyList<string> MmprojFiles { get; }

		public DirectoryIndex(IReadOnlyList<string> ggufFiles, IReadOnlyList<string> mmprojFiles)
		{
			GgufFiles = ggufFiles;
			MmprojFiles = mmprojFiles;
		}
	}

	private static readonly HttpClient HttpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(4L)
	};

	private static readonly HttpClient ProbeHttpClient = CreateProbeHttpClient();

	private static readonly HttpClient LocalEndpointReadinessClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(2L)
	};

	public const int PublicEndpointReadyProbeTimeoutMs = 5000;

	private const int PublicEndpointReadyProbeAttempts = 3;

	private const int LocalProbeMinimumTimeoutMs = 60000;

	private const int CloudProbeAttemptTimeoutMs = 45000;

	private const int LocalDiscoveryProbeTimeoutMs = 1200;

	private const int LocalDiscoveryMaxDurationMs = 8000;

	private const int LocalDiscoveryMaxCandidatePorts = 24;

	private readonly string _configPath;

	private readonly string _secretsPath;

	private readonly string _scriptRoot;

	private readonly string _workspaceRoot;

	private readonly string _workbenchRoot;

	private readonly string _logDirectory;

	private readonly string _proxyRuntimeStatePath;

	private readonly string _proxyLastServedPath;
	private readonly string _proxyLastRouteExplanationPath;

	private readonly string _proxyCloudRecommendPath;
	private readonly string _proxyCloudBreakerPath;

	private readonly string _proxyLocalReloadRecommendPath;

	private readonly string _localRuntimeStatePath;

	private readonly object _proxyStateLock = new object();

	private readonly JsonObject _proxyRuntimeSecrets = new();

	private bool _requestRoutingEnabled;
	private bool _honorCloudRecommendThisProcess;

	private string _requestRoutingTopology = RequestRoutingTopology.DualHot;

	private string _cloudRoutingCapacity = "Normal";

	private string _endpointApp = string.Empty;

	private readonly List<(string Variant, double Temperature, int MaxTokens, string Reasoning)> _localRequestOverlays = new List<(string, double, int, string)>();

	private readonly string _proxyCloudHandshakeLogPath;

	private readonly object _directoryIndexLock = new object();

	private readonly object _jsonCacheLock = new object();

	private readonly object _ggufShapeLock = new object();

	private readonly Dictionary<string, GgufModelShape> _ggufShapeCache = new Dictionary<string, GgufModelShape>(StringComparer.OrdinalIgnoreCase);

	private readonly object _nvidiaGpuInventoryLock = new object();

	private List<GpuInventoryRow>? _nvidiaGpuInventoryCache;

	private DateTime _nvidiaGpuInventoryCacheUtc;

	private Process? _localServerProcess;

	private FluxMuxGatewayHost? _gatewayHost;

	private PortForwardingRules _portForwardingRules = PortForwardingRules.Defaults;

	private readonly IdleSessionCoordinator _idleSession = new IdleSessionCoordinator();

	private readonly GenerationSpeedTracker _generationSpeedTracker = new();

	private readonly SemaphoreSlim _idleLifecycle = new SemaphoreSlim(1, 1);

	private CancellationTokenSource? _idleLoopCts;

	private Task? _idleLoopTask;

	private bool _idleTimeoutEnabled;

	private int _idleTimeoutMinutes = IdleTimeoutPolicy.DefaultMinutes;

	private bool _localSleepingFromIdle;

	private ParkedLocalLaunch? _parkedLocalLaunch;

	public event Action<string>? IdleTimeoutNotice;

	public event Action<string>? PortRulesPostMortemNotice;

	public event Action<PortRulesTelemetry>? PortRulesTelemetryNotice;

	private sealed record ParkedLocalLaunch(
		string Model,
		string Variant,
		string ModelDirectory,
		int Port,
		bool VisionEnabled,
		string ProjectorPath,
		int MaxImageEdge);

	private string _lastLocalStderrTail = string.Empty;

	private string _lastCloudProxyTail = string.Empty;

	private bool _hasUnmanagedLocalEndpoint;

	private int _unmanagedLocalEndpointPort = -1;

	private string? _llamaHelpText;

	private string _llamaHelpTextPath = string.Empty;

	private int _managedLocalPort = -1;

	private string _managedLocalModel = string.Empty;

	private string _managedLocalVariant = string.Empty;

	private string _managedLocalReasoning = "Off";
	private bool _managedLocalVisionEnabled;
	private int _managedLocalContext;
	private int _managedLocalMaxTokens;
	private string _managedLocalLaunchFingerprint = string.Empty;

	private int _managedProxyPort = -1;

	private int _exitShutdownCompleted;

	private readonly Dictionary<string, CloudCatalogVision.Traits> _cloudCatalogTraits = new(StringComparer.OrdinalIgnoreCase);

	private string _activeCloudProvider = string.Empty;

	private string _activeCloudModel = string.Empty;

	private string _lastSuccessfulCloudProvider = string.Empty;

	private string _lastSuccessfulCloudModel = string.Empty;

	private DateTime _activeCloudSinceUtc = DateTime.MinValue;

	private string _cachedDirectoryIndexPath = string.Empty;

	private DateTime _cachedDirectoryIndexStampUtc = DateTime.MinValue;

	private List<string> _cachedGgufFiles = new List<string>();

	private List<string> _cachedMmprojFiles = new List<string>();

	private readonly Dictionary<string, (DateTime StampUtc, JsonObject Root)> _jsonCache = new Dictionary<string, (DateTime, JsonObject)>(StringComparer.OrdinalIgnoreCase);

	private readonly string _proxyContextEventsPath;

	private readonly string _proxyStdOutPath;

	private readonly string _proxyStdErrPath;

	private readonly string _proxyBridgeScriptPath;

	private readonly string _switchBenchmarkLogPath;


	private string _startupRuntimeNotice = string.Empty;

	public int ManagedLocalServerPid
	{
		get
		{
			Process localServerProcess = _localServerProcess;
			return (localServerProcess != null && !localServerProcess.HasExited) ? _localServerProcess.Id : 0;
		}
	}

	public bool HasCompletedExitShutdown => Volatile.Read(in _exitShutdownCompleted) == 1;

	public bool RequestRoutingEnabled => _requestRoutingEnabled;

	public string RequestRoutingTopologyValue => _requestRoutingTopology;

	private static HttpClient CreateProbeHttpClient()
	{
		SocketsHttpHandler handler = new SocketsHttpHandler
		{
			PooledConnectionLifetime = TimeSpan.FromSeconds(1L),
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(1L),
			ConnectTimeout = TimeSpan.FromSeconds(3L)
		};
		return new HttpClient(handler)
		{
			Timeout = TimeSpan.FromMinutes(3L)
		};
	}

	public FluxMuxRuntimeService(string configPath)
	{
		_configPath = configPath;
		_scriptRoot = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
		_workspaceRoot = Directory.GetParent(_scriptRoot)?.FullName ?? _scriptRoot;
		_workbenchRoot = Directory.GetParent(_workspaceRoot)?.FullName ?? _workspaceRoot;
		_secretsPath = Path.Combine(_scriptRoot, "fluxmux_secrets.json");
		_logDirectory = Path.Combine(_workbenchRoot, "Logs");
		_proxyRuntimeStatePath = Path.Combine(_logDirectory, "Proxy_Runtime_State.json");
		_proxyLastServedPath = Path.Combine(_logDirectory, "Proxy_Last_Served.json");
		_proxyLastRouteExplanationPath = Path.Combine(_logDirectory, "Proxy_Last_Route_Explanation.json");
		_proxyCloudRecommendPath = Path.Combine(_logDirectory, "Proxy_Cloud_Recommend.json");
		_proxyCloudBreakerPath = Path.Combine(_logDirectory, "Proxy_Cloud_Breaker.json");
		_proxyLocalReloadRecommendPath = Path.Combine(_logDirectory, "Proxy_Local_Reload_Recommend.json");
		_localRuntimeStatePath = Path.Combine(_logDirectory, "Local_Runtime_State.json");
		_proxyCloudHandshakeLogPath = Path.Combine(_logDirectory, "Proxy_Cloud_Handshake.log");
		_proxyContextEventsPath = Path.Combine(_logDirectory, "Proxy_Context_Events.jsonl");
		_proxyStdOutPath = Path.Combine(_logDirectory, "proxy_python_stdout.log");
		_proxyStdErrPath = Path.Combine(_logDirectory, "proxy_python_stderr.log");
		_proxyBridgeScriptPath = Path.Combine(Path.GetTempPath(), "fluxmux_server_bridge.py");
		_switchBenchmarkLogPath = Path.Combine(_logDirectory, "Switch_Context_Fidelity_Benchmarks.jsonl");
		Directory.CreateDirectory(_logDirectory);
		_gatewayHost = CreateGatewayHost();
		EnsureIdleTimeoutLoop();
		int stoppedCloudBridges = StopStaleManagedProxyFromRuntimeState(clearStateIfStopped: true);
		int stoppedLocalRuntimes = StopStaleManagedLocalFromRuntimeState(clearStateIfStopped: true);
		int leftoverLocalPort = ReadLocalRuntimeStatePort();
		if (leftoverLocalPort < 1)
		{
			leftoverLocalPort = ResolveOrchestratorPort(LoadJsonCached(_configPath)) + 1;
		}
		if (leftoverLocalPort > 0 && IsTcpPortOpen("127.0.0.1", leftoverLocalPort) && TryStopLocalEndpointOwnerByPort(leftoverLocalPort, out int _, out string _))
		{
			stoppedLocalRuntimes++;
			ClearLocalRuntimeState("startup-orphan-local-cleanup");
		}
		if (stoppedCloudBridges > 0)
		{
			_startupRuntimeNotice = $"Startup cleanup: stopped {stoppedCloudBridges} stale managed cloud bridge process(es).";
		}
		if (stoppedLocalRuntimes > 0)
		{
			_startupRuntimeNotice = (string.IsNullOrWhiteSpace(_startupRuntimeNotice) ? $"Startup cleanup: stopped {stoppedLocalRuntimes} stale managed local runtime process(es)." : (_startupRuntimeNotice + $" Also stopped {stoppedLocalRuntimes} stale managed local runtime process(es)."));
		}
	}

	public string ConsumeStartupRuntimeNotice()
	{
		string startupRuntimeNotice = _startupRuntimeNotice;
		_startupRuntimeNotice = string.Empty;
		return startupRuntimeNotice;
	}

	public void SetPortForwardingRules(PortForwardingRules rules)
	{
		_portForwardingRules = (rules ?? PortForwardingRules.Defaults).Clamp();
		if (_gatewayHost is not null)
		{
			_gatewayHost.PortRules = _portForwardingRules.ForForwarding();
		}
	}

	private FluxMuxGatewayHost CreateGatewayHost()
	{
		var host = new FluxMuxGatewayHost(
			_proxyRuntimeStatePath,
			_proxyLastServedPath,
			_proxyCloudRecommendPath,
			_proxyCloudBreakerPath,
			_proxyLocalReloadRecommendPath,
			_proxyCloudHandshakeLogPath,
			_proxyContextEventsPath,
			_proxyLastRouteExplanationPath,
			_idleSession);
		host.PrepareForChatAsync = PrepareLocalAfterIdleAsync;
		host.ParkLocalAfterHangAsync = ParkLocalAfterStreamHangAsync;
		host.HonorCloudRecommendThisProcess = () => _honorCloudRecommendThisProcess;
		host.GenerationSpeed = _generationSpeedTracker;
		host.EnrichState = EnrichProxyRuntimeState;
		host.ReportPortRulesPostMortem = note => PortRulesPostMortemNotice?.Invoke(note);
		host.ReportPortRulesTelemetry = snap => PortRulesTelemetryNotice?.Invoke(snap);
		host.PortRules = _portForwardingRules;
		TryCaptureAndRedactExistingRuntimeState();
		return host;
	}

	private JsonObject EnrichProxyRuntimeState(JsonObject state)
	{
		ProxyRuntimeStateSecrets.Apply(state, _proxyRuntimeSecrets);
		try
		{
			ProxyRuntimeStateSecrets.FillMissingFromStores(
				state,
				LoadJsonCached(_configPath),
				LoadJsonCached(_secretsPath));
		}
		catch
		{
		}

		return state;
	}

	private void TryCaptureAndRedactExistingRuntimeState()
	{
		try
		{
			lock (_proxyStateLock)
			{
				if (!File.Exists(_proxyRuntimeStatePath))
				{
					return;
				}

				JsonObject state = LoadJson(_proxyRuntimeStatePath);
				if (!ProxyRuntimeStateSecrets.LooksLikeSecretDump(state))
				{
					return;
				}

				ProxyRuntimeStateSecrets.Capture(state, _proxyRuntimeSecrets);
				SaveProxyRuntimeState(state);
			}
		}
		catch
		{
		}
	}

	public void SetIdleTimeout(bool enabled, int minutes)
	{
		_idleTimeoutEnabled = enabled;
		_idleTimeoutMinutes = IdleTimeoutPolicy.ClampMinutes(minutes);
		EnsureIdleTimeoutLoop();
	}

	public void NoteUserInterfaceActivity()
	{
		_idleSession.NoteActivity();
	}

	public int InFlightChatCount => _idleSession.InFlightChatCount;

	public GenerationSpeedSnapshot? ReadGenerationSpeedSnapshot()
		=> _generationSpeedTracker.ReadSnapshot();

	public void SetGenerationSpeedTelemetryEnabled(bool enabled)
		=> _generationSpeedTracker.SetEnabled(enabled);

	private void EnsureIdleTimeoutLoop()
	{
		if (_idleLoopTask != null)
		{
			return;
		}

		_idleLoopCts = new CancellationTokenSource();
		_idleLoopTask = Task.Run(() => IdleTimeoutLoopAsync(_idleLoopCts.Token));
	}

	private async Task IdleTimeoutLoopAsync(CancellationToken cancellationToken)
	{
		using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(30.0));
		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				await ConsiderIdleUnloadAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task ConsiderIdleUnloadAsync(CancellationToken cancellationToken)
	{
		if (!IdleTimeoutPolicy.ShouldUnloadLocal(
			    _idleTimeoutEnabled,
			    IsManagedLocalAlive(),
			    _idleSession.InFlightChatCount,
			    _idleSession.LastActivityUtc,
			    DateTime.UtcNow,
			    _idleTimeoutMinutes))
		{
			return;
		}

		if (!_idleSession.TryStartUnload(TimeSpan.FromMinutes(_idleTimeoutMinutes)))
		{
			return;
		}

		try
		{
			await _idleLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				if (!IsManagedLocalAlive())
				{
					return;
				}

				await StopLocalCoreAsync(preserveParkedForIdleWake: true).ConfigureAwait(false);
				_localSleepingFromIdle = true;
				IdleTimeoutNotice?.Invoke(
					"Idle timeout: stopped the local model after " + _idleTimeoutMinutes.ToString(CultureInfo.InvariantCulture)
					+ " minutes with no chat. The next chat will reload it.");
			}
			finally
			{
				_idleLifecycle.Release();
			}
		}
		finally
		{
			_idleSession.EndUnload();
		}
	}

	private async Task ParkLocalAfterStreamHangAsync()
	{
		if (!IsManagedLocalAlive())
		{
			return;
		}

		await _idleLifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
		try
		{
			if (!IsManagedLocalAlive())
			{
				return;
			}

			await StopLocalCoreAsync(preserveParkedForIdleWake: true).ConfigureAwait(false);
			_localSleepingFromIdle = true;
			IdleTimeoutNotice?.Invoke(
				"Hang abort: parked llama-server so the AI-FluxMux window stays usable. The next local chat reloads it.");
		}
		finally
		{
			_idleLifecycle.Release();
		}
	}

	private async Task PrepareLocalAfterIdleAsync(CancellationToken cancellationToken)
	{
		if (!_localSleepingFromIdle)
		{
			return;
		}

		await _idleLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_localSleepingFromIdle || _parkedLocalLaunch is null)
			{
				return;
			}

			ParkedLocalLaunch parked = _parkedLocalLaunch;
			_localSleepingFromIdle = false;
			await LaunchLocalAsync(
				parked.Model,
				parked.Variant,
				parked.ModelDirectory,
				parked.Port,
				parked.VisionEnabled,
				parked.ProjectorPath,
				parked.MaxImageEdge,
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_idleLifecycle.Release();
		}
	}

	private void RememberParkedLocalLaunch(string model, string variant, string modelDirectory, int port, bool visionEnabled, string projectorPath, int maxImageEdge)
	{
		_parkedLocalLaunch = new ParkedLocalLaunch(model, variant, modelDirectory, port, visionEnabled, projectorPath, maxImageEdge);
		_localSleepingFromIdle = false;
		_idleSession.NoteActivity();
	}

	public bool IsManagedLocalProfileLive(string model, string variant)
	{
		Process localServerProcess = _localServerProcess;
		return localServerProcess != null && !localServerProcess.HasExited && _managedLocalPort > 0 && _managedLocalModel.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase) && FirstNonEmpty(_managedLocalVariant, "(defaults)").Equals(FirstNonEmpty(variant, "(defaults)"), StringComparison.OrdinalIgnoreCase);
	}

	public bool IsManagedLocalAlive()
	{
		Process localServerProcess = _localServerProcess;
		return localServerProcess != null && !localServerProcess.HasExited && _managedLocalPort > 0;
	}

	public int? GetLocalIdleStopMinutesRemaining()
	{
		if (!_idleTimeoutEnabled || _idleTimeoutMinutes <= 0 || !IsManagedLocalAlive())
		{
			return null;
		}

		if (_idleSession.InFlightChatCount > 0)
		{
			return null;
		}

		var lastActivityUtc = _idleSession.LastActivityUtc;
		return IdleTimeoutPolicy.GetMinutesRemaining(lastActivityUtc, DateTime.UtcNow, _idleTimeoutMinutes);
	}

	public (string Model, string Variant) GetManagedLocalIdentity()
	{
		return (Model: _managedLocalModel, Variant: _managedLocalVariant);
	}

	public string GetManagedLocalLaunchFingerprint()
		=> _managedLocalLaunchFingerprint ?? string.Empty;

	public void RetargetManagedLocalVariant(string model, string oldVariant, string newVariant)
	{
		if (!_managedLocalModel.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		if (!FirstNonEmpty(_managedLocalVariant, "(defaults)").Equals(FirstNonEmpty(oldVariant, "(defaults)"), StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		_managedLocalVariant = newVariant ?? string.Empty;
		if (_parkedLocalLaunch is { } parked
			&& parked.Model.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			&& FirstNonEmpty(parked.Variant, "(defaults)").Equals(FirstNonEmpty(oldVariant, "(defaults)"), StringComparison.OrdinalIgnoreCase))
		{
			_parkedLocalLaunch = parked with { Variant = _managedLocalVariant };
		}
	}

	public bool IsCloudRouteActive()
	{
		return !string.IsNullOrWhiteSpace(_activeCloudProvider) && !string.IsNullOrWhiteSpace(_activeCloudModel);
	}

	public (string Provider, string Model) GetActiveCloudIdentity()
		=> (Provider: _activeCloudProvider, Model: _activeCloudModel);

	public (string Provider, string Model) GetLastSuccessfulCloudIdentity()
		=> (Provider: _lastSuccessfulCloudProvider, Model: _lastSuccessfulCloudModel);

	public void SetRequestRoutingEnabled(bool enabled)
	{
		_requestRoutingEnabled = enabled;
		PatchProxyRoutingFlag();
	}

	public void SetRequestRoutingTopology(string topology)
	{
		_requestRoutingTopology = RequestRoutingTopology.Normalize(topology);
		PatchProxyRoutingFlag();
	}

	private bool IsDualHotRouting()
		=> RequestRoutingTopology.ShouldKeepBothHot(_requestRoutingEnabled, _requestRoutingTopology);

	public void SetCloudRoutingCapacity(string capacity)
	{
		_cloudRoutingCapacity = NormalizeCloudRoutingCapacity(capacity);
		PatchProxyRoutingFlag();
	}

	public void SetEndpointApp(string endpointApp)
	{
		_endpointApp = (endpointApp ?? string.Empty).Trim();
		PatchProxyRoutingFlag();
	}

	public void ResetLocalRequestOverlays()
	{
		_localRequestOverlays.Clear();
		PatchProxyRoutingFlag();
	}

	public void UpsertLocalRequestOverlay(string variant, double temperature, int maxTokens, string? reasoning = null)
	{
		string name = (string.IsNullOrWhiteSpace(variant) ? "(defaults)" : variant.Trim());
		var mode = LocalReasoningLaunchPolicy.NormalizeMode(reasoning);
		_localRequestOverlays.RemoveAll(item => item.Variant.Equals(name, StringComparison.OrdinalIgnoreCase));
		_localRequestOverlays.Add((name, temperature, Math.Max(1, maxTokens), mode));
		PatchProxyRoutingFlag();
	}

	public void ApplyLiveLocalRequestSettings(string variant, double temperature, int maxTokens, string? reasoning)
	{
		_managedLocalReasoning = LocalReasoningLaunchPolicy.NormalizeMode(reasoning);
		if (maxTokens > 0)
		{
			_managedLocalMaxTokens = maxTokens;
		}

		UpsertLocalRequestOverlay(variant, temperature, maxTokens, _managedLocalReasoning);
	}

	public IReadOnlyList<(string Variant, double Temperature, int MaxTokens, string Reasoning)> GetLocalRequestOverlays()
	{
		return _localRequestOverlays.ToList();
	}

	public LastServedRouteSnapshot? ReadLastServedRoute()
	{
		try
		{
			if (!File.Exists(_proxyLastServedPath))
			{
				return null;
			}
			JsonObject root = LoadJson(_proxyLastServedPath);
			string kind = GetString(root, "kind");
			string modelId = GetString(root, "model");
			if (string.IsNullOrWhiteSpace(kind) && string.IsNullOrWhiteSpace(modelId))
			{
				return null;
			}
			return new LastServedRouteSnapshot(kind, GetString(root, "provider"), modelId, GetString(root, "updatedUtc"));
		}
		catch
		{
			return null;
		}
	}

	public GatewayRouteExplanationSnapshot? ReadLastRouteExplanation()
	{
		try
		{
			if (!File.Exists(_proxyLastRouteExplanationPath))
			{
				return null;
			}

			return GatewayRouteExplanation.Parse(LoadJson(_proxyLastRouteExplanationPath) as JsonObject);
		}
		catch
		{
			return null;
		}
	}

	public async Task<LocalValidateSpeedMetrics> ProbeValidateSpeedThroughPublicEndpointAsync(
		int publicGatewayPort,
		string model,
		CancellationToken cancellationToken = default)
		=> await LocalValidateSpeedProbe.ProbePublicEndpointAsync(ProbeHttpClient, publicGatewayPort, model, cancellationToken);

	public void RecordLocalValidateSpeedMetrics(string model, string variant, LocalValidateSpeedMetrics metrics)
	{
		if (!metrics.IsSuccess)
		{
			return;
		}

		TryUpdateLocalProfileSettings(model, variant, settings =>
		{
			settings["ValidateTtftMs"] = metrics.TtftMs;
			settings["ValidateTokensPerSecond"] = metrics.TokensPerSecond.ToString(CultureInfo.InvariantCulture);
			settings["ValidateCompletionTokens"] = metrics.CompletionTokens;
			settings["ValidateSpeedUpdatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
		});
	}

	public LocalVramVariantSuggestion? FindValidatedVramAlternative(
		string model,
		string variant,
		double freeGiB,
		double footprintGiB,
		double modelFileGiB)
	{
		JsonObject root = LoadJsonCached(_configPath);
		if (root["LocalProfiles"] is not JsonObject profiles)
		{
			return null;
		}

		return LocalVramVariantAdvisor.PickValidatedAlternative(
			profiles,
			model,
			variant,
			freeGiB,
			footprintGiB,
			modelFileGiB);
	}

	public CloudBreakerBlock? ReadCloudBreakerBlock(string provider, string model)
		=> CloudCircuitBreaker.ReadOpenBlock(_proxyCloudBreakerPath, provider, model);

	public CloudRecommendSnapshot? ReadCloudRecommend()
	{
		try
		{
			if (!File.Exists(_proxyCloudRecommendPath))
			{
				return null;
			}
			JsonObject root = LoadJson(_proxyCloudRecommendPath);
			string status = GetString(root, "status");
			if (string.IsNullOrWhiteSpace(status) || status.Equals("idle", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			if (status.Equals("yes", StringComparison.OrdinalIgnoreCase)
			    || status.Equals("no", StringComparison.OrdinalIgnoreCase)
			    || status.Equals(RouteRecoveryPolicy.WaitStatus, StringComparison.OrdinalIgnoreCase)
			    || RouteRecoveryPolicy.IsSteerStatus(status))
			{
				return null;
			}
			var failOnTimeout = false;
			if (!(root["fail_on_timeout"] is JsonValue failValue && failValue.TryGetValue<bool>(out failOnTimeout)))
			{
				failOnTimeout = GetString(root, "fail_on_timeout").Equals("true", StringComparison.OrdinalIgnoreCase);
			}
			if (RouteRecoveryPolicy.IsTimeoutStatus(status)
			    && !RouteRecoveryPolicy.ShouldKeepTimedOutCloudRecommend(failOnTimeout))
			{
				return null;
			}
			string reason = GetString(root, "reason");
			if (string.IsNullOrWhiteSpace(reason) && !status.Equals("pending", StringComparison.OrdinalIgnoreCase) && !status.Equals("timeout", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return new CloudRecommendSnapshot(
				status,
				reason,
				GetString(root, "updatedUtc"),
				failOnTimeout,
				GetString(root, "source"));
		}
		catch
		{
			return null;
		}
	}

	public bool IsApprovedCloudWindowActive()
	{
		if (!_honorCloudRecommendThisProcess)
		{
			return false;
		}

		try
		{
			if (!File.Exists(_proxyCloudRecommendPath))
			{
				return false;
			}

			JsonObject root = LoadJson(_proxyCloudRecommendPath);
			if (!long.TryParse(GetString(root, "allow_until"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var until)
			    || until <= 0)
			{
				return false;
			}

			return CloudReturnToLocalPolicy.StillHonorCloudWindow(
				GetString(root, "status"),
				GetString(root, "source"),
				until,
				DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		}
		catch
		{
			return false;
		}
	}

	public void AnswerCloudRecommend(bool useCloud)
	{
		_honorCloudRecommendThisProcess = useCloud;
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = (File.Exists(_proxyCloudRecommendPath) ? LoadJson(_proxyCloudRecommendPath) : new JsonObject());
			long untilUnix = DateTimeOffset.UtcNow.AddMinutes(15.0).ToUnixTimeSeconds();
			jsonObject["status"] = (useCloud ? "yes" : "no");
			jsonObject["allow_until"] = (useCloud ? untilUnix : 0);
			jsonObject["suppress_until"] = (useCloud ? 0 : untilUnix);
			if (useCloud)
			{
				jsonObject["return_armed"] = true;
				jsonObject["local_sufficient_count"] = 0;
			}

			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyCloudRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	public void AnswerPortRuleSteer()
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = (File.Exists(_proxyCloudRecommendPath) ? LoadJson(_proxyCloudRecommendPath) : new JsonObject());
			jsonObject["status"] = RouteRecoveryPolicy.SteerStatus;
			jsonObject["allow_until"] = 0;
			jsonObject["suppress_until"] = 0;
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyCloudRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	public void AnswerHangWait()
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = (File.Exists(_proxyCloudRecommendPath) ? LoadJson(_proxyCloudRecommendPath) : new JsonObject());
			jsonObject["status"] = RouteRecoveryPolicy.WaitStatus;
			jsonObject["allow_until"] = 0;
			jsonObject["suppress_until"] = 0;
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyCloudRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	public void DeclineReturnToLocal()
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = (File.Exists(_proxyCloudRecommendPath) ? LoadJson(_proxyCloudRecommendPath) : new JsonObject());
			long untilUnix = DateTimeOffset.UtcNow.AddMinutes(15.0).ToUnixTimeSeconds();
			var allowUntil = GetString(jsonObject, "allow_until");
			if (!long.TryParse(allowUntil, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingUntil)
			    || existingUntil <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
			{
				existingUntil = untilUnix;
			}

			jsonObject["status"] = "yes";
			jsonObject["allow_until"] = existingUntil;
			jsonObject["suppress_until"] = 0;
			jsonObject["return_armed"] = false;
			jsonObject["local_sufficient_count"] = 0;
			jsonObject["source"] = CloudReturnToLocalPolicy.Source;
			jsonObject["reason"] = string.Empty;
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyCloudRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	public void DismissCloudRecommend()
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = (File.Exists(_proxyCloudRecommendPath) ? LoadJson(_proxyCloudRecommendPath) : new JsonObject());
			jsonObject["status"] = "idle";
			jsonObject["allow_until"] = 0;
			jsonObject["suppress_until"] = 0;
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyCloudRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	public void ClearStaleRecoveryPromptsOnStartup()
	{
		_honorCloudRecommendThisProcess = false;
		DismissCloudRecommend();
		ClearLocalReloadRecommend();
	}

	public LocalReloadRecommendSnapshot? ReadLocalReloadRecommend()
	{
		try
		{
			if (!File.Exists(_proxyLocalReloadRecommendPath))
			{
				return null;
			}
			JsonObject root = LoadJson(_proxyLocalReloadRecommendPath);
			string status = GetString(root, "status");
			if (string.IsNullOrWhiteSpace(status) || status.Equals("idle", StringComparison.OrdinalIgnoreCase)
				|| status.Equals("yes", StringComparison.OrdinalIgnoreCase) || status.Equals("no", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string reason = FormatStoredLocalReloadReason(root)
				?? ReplaceOtherAppWording(GetString(root, "reason"));
			if (string.IsNullOrWhiteSpace(reason) && !status.Equals("pending", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return new LocalReloadRecommendSnapshot(
				status,
				reason,
				GetString(root, "model"),
				GetString(root, "variant"),
				FirstNonEmpty(LocalReloadRouting.FormatPackLabel(GetString(root, "model"), GetString(root, "variant")), GetString(root, "displayName")),
				GetString(root, "updatedUtc"),
				ParseBool(GetString(root, "compactRecommended"), false),
				ParseBool(GetString(root, "destinationTight"), false),
				ParseBool(GetString(root, "forward_compact"), false),
				ParseBool(GetString(root, "cloud_failover"), false));
		}
		catch
		{
			return null;
		}
	}

	private string? FormatStoredLocalReloadReason(JsonObject root)
	{
		if (ParseBool(GetString(root, "cloud_failover"), false))
		{
			var storedFailover = ReplaceOtherAppWording(GetString(root, "reason"));
			return string.IsNullOrWhiteSpace(storedFailover) ? null : storedFailover;
		}

		string candModel = GetString(root, "model");
		string candVariant = GetString(root, "variant");
		bool nearLimitEarly = ParseBool(GetString(root, "nearLimit"), false);
		bool compactRecommended = ParseBool(GetString(root, "compactRecommended"), false);
		if (string.IsNullOrWhiteSpace(candModel) && !nearLimitEarly && !compactRecommended)
		{
			return null;
		}

		JsonObject state = File.Exists(_proxyRuntimeStatePath) ? LoadJson(_proxyRuntimeStatePath) : new JsonObject();
		string hotModel = FirstNonEmpty(GetString(root, "hotModel"), GetString(state, "local_preferred_model"), candModel);
		string hotVariant = FirstNonEmpty(GetString(root, "hotVariant"), GetString(state, "local_variant"), "(defaults)");
		bool hotVision = FirstNonEmpty(GetString(root, "hotVision"), GetString(state, "local_vision")).Equals("Enabled", StringComparison.OrdinalIgnoreCase);
		string hotReasoning = FirstNonEmpty(GetString(root, "hotReasoning"), GetString(state, "local_reasoning"), "Off");
		int hotContext = GetProfileInt(root, "hotContext", ParseInt(GetString(state, "local_context"), 0));

		JsonObject profiles = (LoadJsonCached(_configPath)["LocalProfiles"] as JsonObject) ?? new JsonObject();
		JsonObject? profile = FindLocalProfile(profiles, candModel, candVariant);
		string candVision = FirstNonEmpty(GetString(root, "candVision"), profile is null ? string.Empty : GetString(profile, "LocalVisionEnabled"), "Disabled");
		string candReasoning = FirstNonEmpty(GetString(root, "candReasoning"), profile is null ? string.Empty : GetString(profile, "LocalReasoning"), "Off");
		int candContext = GetProfileInt(root, "candContext", profile is null ? 0 : GetProfileInt(profile, "OverrideContext", 0));
		if (hotContext <= 0)
		{
			JsonObject? hotProfile = FindLocalProfile(profiles, hotModel, hotVariant);
			hotContext = hotProfile is null ? 0 : GetProfileInt(hotProfile, "OverrideContext", 0);
		}

		bool needVision = ParseBool(GetString(root, "needVision"), !hotVision && candVision.Equals("Enabled", StringComparison.OrdinalIgnoreCase));
		bool needThinking = ParseBool(GetString(root, "needThinking"), false);
		int neededContext = ParseInt(GetString(root, "neededContext"), 0);
		bool nearLimit = ParseBool(GetString(root, "nearLimit"), false);
		bool destinationTight = ParseBool(GetString(root, "destinationTight"), false);
		bool compactAlreadyOn = ParseBool(GetString(root, "forward_compact"), false);
		bool betterChance = ParseBool(GetString(root, "betterChance"), false);
		if (!needVision && !needThinking && neededContext <= 0 && !nearLimit && !compactAlreadyOn
			&& string.IsNullOrWhiteSpace(candModel) == false)
		{
			string stored = GetString(root, "reason");
			needVision = stored.Contains("picture", StringComparison.OrdinalIgnoreCase)
				|| stored.Contains("Images is off", StringComparison.OrdinalIgnoreCase)
				|| stored.Contains("Images is Disabled", StringComparison.OrdinalIgnoreCase);
			needThinking = stored.Contains("thinking", StringComparison.OrdinalIgnoreCase)
				|| stored.Contains("Reasoning is Off", StringComparison.OrdinalIgnoreCase);
			neededContext = 0;
		}

		if (!needVision && !needThinking && neededContext <= 0 && !nearLimit && string.IsNullOrWhiteSpace(candModel) && !compactRecommended)
		{
			return null;
		}

		JsonObject? candidate = string.IsNullOrWhiteSpace(candModel)
			? null
			: new JsonObject
			{
				["model"] = candModel,
				["variant"] = candVariant,
				["vision"] = candVision.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled",
				["reasoning"] = candReasoning,
				["context"] = candContext,
				["sameGguf"] = candModel.Equals(hotModel, StringComparison.OrdinalIgnoreCase)
			};

		var lastServed = ReadLastServedRoute();
		return LocalReloadRouting.BuildOfferReason(
			hotModel,
			hotVariant,
			hotVision,
			hotReasoning,
			hotContext,
			needVision,
			needThinking,
			neededContext,
			candidate,
			nearLimit,
			destinationTight,
			compactAlreadyOn,
			betterChance,
			true,
			lastServed?.Kind,
			ProxyRuntimeStateSecrets.FormatCloudLabel(lastServed?.Provider, lastServed?.Model));
	}

	private static string ReplaceOtherAppWording(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return message;
		}

		return message
			.Replace("your other AI app", "the Client app", StringComparison.OrdinalIgnoreCase)
			.Replace("Your other AI app", "The Client app")
			.Replace("your other app", "the Client app", StringComparison.OrdinalIgnoreCase)
			.Replace("Your other app", "The Client app");
	}

	public void AnswerLocalReloadRecommend(bool loadSuggested, bool enableCompact = false)
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = (File.Exists(_proxyLocalReloadRecommendPath) ? LoadJson(_proxyLocalReloadRecommendPath) : new JsonObject());
			long until = DateTimeOffset.UtcNow.AddMinutes(15.0).ToUnixTimeSeconds();
			jsonObject["status"] = (loadSuggested || enableCompact ? "yes" : "no");
			jsonObject["allow_until"] = (loadSuggested ? until : 0);
			jsonObject["suppress_until"] = (loadSuggested || enableCompact ? 0 : until);
			if (enableCompact)
			{
				jsonObject["forward_compact"] = true;
				jsonObject["compactRecommended"] = false;
			}
			else if (!loadSuggested)
			{
				jsonObject["forward_compact"] = false;
			}

			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyLocalReloadRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	private void SeedForwardCompactFromProfile(string model, string variant)
	{
		try
		{
			JsonObject root = LoadJsonCached(_configPath);
			JsonObject? profile = ResolveLocalProfile(root, model, variant);
			bool compactEnabled = ParseBool(GetString(profile, "AutoCompressEnabled"), fallback: true);
			lock (_proxyStateLock)
			{
				JsonObject jsonObject = File.Exists(_proxyLocalReloadRecommendPath)
					? LoadJson(_proxyLocalReloadRecommendPath)
					: new JsonObject();
				jsonObject["forward_compact"] = compactEnabled;
				if (string.IsNullOrWhiteSpace(GetString(jsonObject, "status")))
				{
					jsonObject["status"] = "idle";
				}

				jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
				File.WriteAllText(_proxyLocalReloadRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
				{
					WriteIndented = true
				}));
			}
		}
		catch
		{
		}
	}

	public void ApplyProfileForwardCompactSetting(string model, string variant)
		=> SeedForwardCompactFromProfile(model, variant);

	public void ClearLocalReloadRecommend()
	{
		lock (_proxyStateLock)
		{
			if (!File.Exists(_proxyLocalReloadRecommendPath))
			{
				return;
			}

			JsonObject jsonObject = LoadJson(_proxyLocalReloadRecommendPath);
			string status = GetString(jsonObject, "status");
			if (string.IsNullOrWhiteSpace(status) || status.Equals("idle", StringComparison.OrdinalIgnoreCase)
				|| status.Equals("yes", StringComparison.OrdinalIgnoreCase) || status.Equals("no", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			jsonObject["status"] = "idle";
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_proxyLocalReloadRecommendPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
	}

	public Task<RuntimeActionResult> ApplyRequestRoutingAsync(bool enabled, CancellationToken cancellationToken = default(CancellationToken))
	{
		_ = cancellationToken;
		_requestRoutingEnabled = enabled;
		PatchProxyRoutingFlag();
		return Task.FromResult(new RuntimeActionResult
		{
			IsSuccess = true,
			Status = (enabled ? "Request routing on" : "Request routing off"),
			Details = (enabled
				? "Launch a local profile and a cloud profile to keep both hot. The public URL stays the same; each request picks a ready target. Use Cloud Usage Mode to favour local-only routing when both are running."
				: "Launch is exclusive again: launching one side stops the other. The public URL is unchanged.")
		});
	}

	public IReadOnlyList<InstalledLocalServerCandidate> GetInstalledLocalServers()
	{
		return GetInstalledLocalServerCandidates();
	}

	public bool IsSupportedManagedLocalServer(string executablePath)
	{
		return File.Exists(executablePath) && Path.GetFileName(executablePath).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase) && IsCompatibleLocalServerExecutable(executablePath);
	}

	public async Task ShutdownManagedRuntimesAsync(bool shutdownGateway = false)
	{
		if (shutdownGateway && Interlocked.Exchange(ref _exitShutdownCompleted, 1) == 1)
		{
			return;
		}
		try
		{
			_idleLoopCts?.Cancel();
		}
		catch
		{
		}
		int leftoverLocalPort = ((_managedLocalPort > 0) ? _managedLocalPort : ReadLocalRuntimeStatePort());
		try
		{
			await StopLocalAsync();
		}
		catch
		{
		}
		if (leftoverLocalPort > 0 && IsTcpPortOpen("127.0.0.1", leftoverLocalPort))
		{
			TryStopLocalEndpointOwnerByPort(leftoverLocalPort, out int _, out string _);
			await WaitForPortToCloseAsync(leftoverLocalPort, CancellationToken.None, 3000);
		}
		try
		{
			await StopCloudAsync();
		}
		catch
		{
		}
		if (!shutdownGateway)
		{
			return;
		}
		try
		{
			if (_gatewayHost != null)
			{
				await _gatewayHost.StopAsync();
			}
		}
		catch
		{
		}
		_managedProxyPort = -1;
		ClearProxyRuntimeState("application-exit");
		ClearLocalRuntimeState("application-exit");
	}

	public async Task<RuntimeActionResult> ValidateCloudAsync(string provider, string model, CancellationToken cancellationToken = default(CancellationToken))
	{
		JsonObject root = LoadJsonCached(_configPath);
		JsonObject secrets = LoadJsonCached(_secretsPath);
		string keyName = provider switch
		{
			"Gemini" => "GeminiApiKey", 
			"Anthropic" => "AnthropicApiKey", 
			"OpenAI" => "OpenAiApiKey", 
			"Copilot GitHub" => "GitHubCopilotApiKey", 
			_ => string.Empty, 
		};
		string key = CustomCloudProviderRegistry.IsCustomCompat(provider, root)
			? CustomCloudProviderRegistry.GetApiKey(secrets, root, provider)
			: FirstNonEmpty(GetString(secrets, keyName), GetString(root, keyName));
		string endpoint = provider switch
		{
			"OpenAI" => GetString(root, "OpenAiEndpoint"), 
			"Copilot GitHub" => GetString(root, "GitHubCopilotEndpoint"), 
			_ => CustomCloudProviderRegistry.IsCustomCompat(provider, root)
				? CustomCloudProviderRegistry.GetEndpoint(root, provider)
				: string.Empty, 
		};
		if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud validation failed",
				Details = "Provider and model must both be selected."
			};
		}
		if (!string.IsNullOrWhiteSpace(keyName) && string.IsNullOrWhiteSpace(key)
			&& !CustomCloudProviderRegistry.IsCustomCompat(provider, root))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud validation failed",
				Details = $"Missing API key for {provider} ({keyName})."
			};
		}
		if (CustomCloudProviderRegistry.IsCustomCompat(provider, root)
			&& string.IsNullOrWhiteSpace(key)
			&& !string.Equals(CustomCloudProviderRegistry.GetAuthMode(root, provider), "None", StringComparison.OrdinalIgnoreCase))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud validation failed",
				Details = $"Missing API key for {provider}."
			};
		}
		if (!string.IsNullOrWhiteSpace(endpoint) && !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri _))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud validation failed",
				Details = "Invalid endpoint URI for " + provider + ": " + endpoint
			};
		}
		if (!string.IsNullOrWhiteSpace(endpoint))
		{
			try
			{
				using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, endpoint);
				using HttpResponseMessage resp = await HttpClient.SendAsync(req, cancellationToken);
				string probe = $"Endpoint probe HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
				if (IsOpenAiStyleProvider(provider))
				{
					(bool IsSuccess, string StatusDetail, RuntimeActionResult? Failure) modelCheck = await ProbeOpenAiStyleModelAsync(provider, endpoint, key, model, cancellationToken);
					if (!modelCheck.IsSuccess)
					{
						return modelCheck.Failure ?? new RuntimeActionResult
						{
							IsSuccess = false,
							Status = "Cloud validation failed",
							Details = provider + " / " + model + " model probe failed without details."
						};
					}
					probe = probe + "; model probe HTTP " + modelCheck.StatusDetail;
				}
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Cloud validation passed",
					Details = $"{provider} / {model} looks configured. {probe}"
				};
			}
			catch (Exception ex)
			{
				if (IsOpenAiStyleProvider(provider))
				{
					(bool IsSuccess, string StatusDetail, RuntimeActionResult? Failure) modelCheck2 = await ProbeOpenAiStyleModelAsync(provider, endpoint, key, model, cancellationToken);
					if (!modelCheck2.IsSuccess)
					{
						return modelCheck2.Failure ?? new RuntimeActionResult
						{
							IsSuccess = false,
							Status = "Cloud validation failed",
							Details = provider + " / " + model + " model probe failed without details."
						};
					}
					return new RuntimeActionResult
					{
						IsSuccess = true,
						Status = "Cloud validation passed",
						Details = $"{provider} / {model} key+shape valid; endpoint HEAD probe skipped ({ex.GetType().Name}). Model probe passed ({modelCheck2.StatusDetail})."
					};
				}
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Cloud validation passed (offline endpoint probe)",
					Details = $"{provider} / {model} key+shape valid; endpoint probe skipped: {ex.GetType().Name}"
				};
			}
		}
		return new RuntimeActionResult
		{
			IsSuccess = true,
			Status = "Cloud validation passed",
			Details = provider + " / " + model + " key and configuration shape look valid."
		};
	}

	public async Task<CloudModelCatalogResult> ListCloudModelsAsync(string provider, CancellationToken cancellationToken = default(CancellationToken), string? keyOverride = null)
	{
		JsonObject root = LoadJsonCached(_configPath);
		JsonObject secrets = LoadJsonCached(_secretsPath);
		string keyName = provider switch
		{
			"Gemini" => "GeminiApiKey", 
			"Anthropic" => "AnthropicApiKey", 
			"OpenAI" => "OpenAiApiKey", 
			"Copilot GitHub" => "GitHubCopilotApiKey", 
			_ => string.Empty, 
		};
		string storedKey = CustomCloudProviderRegistry.IsCustomCompat(provider, root)
			? CustomCloudProviderRegistry.GetApiKey(secrets, root, provider)
			: FirstNonEmpty(GetString(secrets, keyName), GetString(root, keyName));
		string key = (string.IsNullOrWhiteSpace(keyOverride) ? storedKey : keyOverride.Trim());
		if (string.IsNullOrWhiteSpace(provider))
		{
			return new CloudModelCatalogResult
			{
				IsSuccess = false,
				Status = "Model lookup failed",
				Details = "Choose a cloud provider first."
			};
		}
		var authMode = CustomCloudProviderRegistry.IsCustomCompat(provider, root)
			? CustomCloudProviderRegistry.GetAuthMode(root, provider)
			: "Bearer";
		if (string.IsNullOrWhiteSpace(key) && !string.Equals(authMode, "None", StringComparison.OrdinalIgnoreCase))
		{
			return new CloudModelCatalogResult
			{
				IsSuccess = false,
				Status = "Model lookup failed",
				Details = "Store an API key for " + provider + " on the Servers tab before Refresh Models can ask that provider for current ids."
			};
		}
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(20L));
			var traitsById = new Dictionary<string, CloudCatalogVision.Traits>(StringComparer.OrdinalIgnoreCase);
			List<string> list;
			if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
			{
				list = await ListGeminiModelsAsync(key, timeout.Token, traitsById);
			}
			else if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
			{
				list = await ListAnthropicModelsAsync(key, timeout.Token, traitsById);
			}
			else if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
			{
				list = await ListOpenAiStyleModelsAsync(FirstNonEmpty(GetString(root, "OpenAiEndpoint"), "https://api.openai.com/v1"), "/models", key, "Bearer", "Authorization", filterChatLike: true, copilotCatalog: false, timeout.Token, traitsById);
			}
			else if (string.Equals(provider, "Copilot GitHub", StringComparison.OrdinalIgnoreCase))
			{
				list = await ListOpenAiStyleModelsAsync(FirstNonEmpty(GetString(root, "GitHubCopilotEndpoint"), "https://api.githubcopilot.com"), "/models", key, "Bearer", "Authorization", filterChatLike: true, copilotCatalog: true, timeout.Token, traitsById);
			}
			else if (CustomCloudProviderRegistry.IsCustomCompat(provider, root))
			{
				list = await ListOpenAiStyleModelsAsync(
					FirstNonEmpty(CustomCloudProviderRegistry.GetEndpoint(root, provider), "https://api.openai.com/v1"),
					CustomCloudProviderRegistry.GetModelsPath(root, provider),
					key,
					CustomCloudProviderRegistry.GetAuthMode(root, provider),
					CustomCloudProviderRegistry.GetApiKeyHeader(root, provider),
					filterChatLike: false,
					copilotCatalog: false,
					timeout.Token,
					traitsById);
			}
			else
			{
				list = new List<string>();
			}
			List<string> models = list;
			List<string> unique = CloudModelCatalogFilter.Filter(provider, models);
			var keptTraits = new Dictionary<string, CloudCatalogVision.Traits>(StringComparer.OrdinalIgnoreCase);
			foreach (var id in unique)
			{
				if (traitsById.TryGetValue(id, out var traits))
				{
					keptTraits[id] = traits;
					_cloudCatalogTraits[CloudTraitCacheKey(provider, id)] = traits;
				}
			}

			if (unique.Count == 0)
			{
				return new CloudModelCatalogResult
				{
					IsSuccess = true,
					Status = "Provider accepted this key",
					Details = provider + " accepted this key but did not return a chat-oriented model list."
				};
			}
			string catalogNote = string.Equals(provider, "Copilot GitHub", StringComparison.OrdinalIgnoreCase)
				? " Copilot can still list ids that are not yet callable on the chat route AI-FluxMux uses — run Validate to endpoint on each profile before Quick Select."
				: " Run Validate to endpoint on Model Profiles before Quick Select offers a profile.";
			return new CloudModelCatalogResult
			{
				IsSuccess = true,
				Status = "Model lookup succeeded",
				Details = $"Found {unique.Count} chat-oriented {provider} model id(s). Open Model Profiles to create a cloud profile when you are ready.{catalogNote}",
				Models = unique,
				TraitsByModel = keptTraits
			};
		}
		catch (OperationCanceledException)
		{
			return new CloudModelCatalogResult
			{
				IsSuccess = false,
				Status = "Model lookup timed out",
				Details = provider + " did not return a model list in time. Check the connection and try again."
			};
		}
		catch (HttpRequestException ex2)
		{
			return new CloudModelCatalogResult
			{
				IsSuccess = false,
				Status = "Model lookup failed",
				Details = provider + " did not accept this key (" + ex2.Message + "). Confirm you saved the correct key for this provider on the Servers tab."
			};
		}
		catch (Exception ex3)
		{
			return new CloudModelCatalogResult
			{
				IsSuccess = false,
				Status = "Model lookup failed",
				Details = provider + " could not complete this key check (" + ex3.GetType().Name + "). Try again, or confirm the key on the Servers tab."
			};
		}
	}

	private async Task<List<string>> ListGeminiModelsAsync(string key, CancellationToken cancellationToken, IDictionary<string, CloudCatalogVision.Traits>? traitsById = null)
	{
		List<string> ids = new List<string>();
		string pageToken = null;
		for (int page = 0; page < 8; page++)
		{
			string url = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100&key=" + Uri.EscapeDataString(key);
			if (!string.IsNullOrWhiteSpace(pageToken))
			{
				url = url + "&pageToken=" + Uri.EscapeDataString(pageToken);
			}
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
			using HttpResponseMessage response = await ProbeHttpClient.SendAsync(request, cancellationToken);
			string body = await response.Content.ReadAsStringAsync(cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"Gemini models HTTP {(int)response.StatusCode}");
			}
			JsonNode jsonNode = JsonNode.Parse(body);
			if (!(jsonNode is JsonObject root))
			{
				break;
			}
			jsonNode = root["models"];
			if (jsonNode is JsonArray models)
			{
				foreach (JsonObject entry in models.OfType<JsonObject>())
				{
					jsonNode = entry["supportedGenerationMethods"];
					if (!(jsonNode is JsonArray { Count: >0 } methods) || methods.Any((JsonNode method) => string.Equals(method?.ToString(), "generateContent", StringComparison.OrdinalIgnoreCase)))
					{
						string name = entry["name"]?.ToString() ?? string.Empty;
						string id = (name.Contains('/', StringComparison.Ordinal) ? name.Substring(name.LastIndexOf('/') + 1) : name);
						if (!string.IsNullOrWhiteSpace(id))
						{
							ids.Add(id);
							if (traitsById is not null)
							{
								var traits = CloudCatalogVision.TryRead(entry);
								if (traits.Vision.HasValue || traits.ContextTokens.HasValue
									|| traits.MaxTokens.HasValue || traits.Reasoning.HasValue)
								{
									traitsById[id] = traits;
								}
							}
						}
					}
				}
			}
			pageToken = root["nextPageToken"]?.ToString();
			if (string.IsNullOrWhiteSpace(pageToken))
			{
				break;
			}
			continue;
		}
		return ids;
	}

	private async Task<List<string>> ListAnthropicModelsAsync(string key, CancellationToken cancellationToken, IDictionary<string, CloudCatalogVision.Traits>? traitsById = null)
	{
		List<string> ids = new List<string>();
		string afterId = null;
		for (int page = 0; page < 8; page++)
		{
			string url = "https://api.anthropic.com/v1/models?limit=100";
			if (!string.IsNullOrWhiteSpace(afterId))
			{
				url = url + "&after_id=" + Uri.EscapeDataString(afterId);
			}
			using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
			{
				request.Headers.TryAddWithoutValidation("x-api-key", key);
				request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
				using HttpResponseMessage response = await ProbeHttpClient.SendAsync(request, cancellationToken);
				string body = await response.Content.ReadAsStringAsync(cancellationToken);
				if (!response.IsSuccessStatusCode)
				{
					throw new HttpRequestException($"Anthropic models HTTP {(int)response.StatusCode}");
				}
				JsonNode jsonNode = JsonNode.Parse(body);
				if (jsonNode is JsonObject root)
				{
					var snapshot = CloudCatalogVision.Collect(root, filterChatLike: false);
					if (traitsById is not null)
					{
						CloudCatalogVision.Merge(traitsById, snapshot.TraitsById);
					}

					List<string> pageIds = snapshot.Ids.Count > 0 ? new List<string>(snapshot.Ids) : ExtractModelIds(root, filterChatLike: false);
					ids.AddRange(pageIds);
					bool hasMore;
					if (!string.Equals(root["has_more"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
					{
						jsonNode = root["has_more"];
						bool nextPage = false;
						hasMore = (jsonNode is JsonValue value && value.TryGetValue<bool>(out nextPage)) & nextPage;
					}
					else
					{
						hasMore = true;
					}
					object obj;
					if (pageIds.Count <= 0)
					{
						obj = root["last_id"]?.ToString();
					}
					else
					{
						obj = pageIds[pageIds.Count - 1];
					}
					afterId = (string)obj;
					if (hasMore && !string.IsNullOrWhiteSpace(afterId))
					{
						continue;
					}
				}
			}
			break;
		}
		return ids;
	}

	private async Task<List<string>> ListOpenAiStyleModelsAsync(string endpoint, string modelsPath, string key, string authMode, string apiKeyHeader, bool filterChatLike, bool copilotCatalog, CancellationToken cancellationToken, IDictionary<string, CloudCatalogVision.Traits>? traitsById = null)
	{
		using HttpRequestMessage request = new HttpRequestMessage(requestUri: CombineEndpointAndPath(endpoint, modelsPath), method: HttpMethod.Get);
		ApplyCloudCatalogAuth(request, key, authMode, apiKeyHeader);
		if (copilotCatalog)
		{
			CopilotIntegratorHeaders.Apply(request);
		}
		using HttpResponseMessage response = await ProbeHttpClient.SendAsync(request, cancellationToken);
		string body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"Models HTTP {(int)response.StatusCode}");
		}
		JsonNode payload = JsonNode.Parse(body);
		if (payload == null)
		{
			return new List<string>();
		}

		var snapshot = CloudCatalogVision.Collect(payload, filterChatLike);
		if (traitsById is not null)
		{
			CloudCatalogVision.Merge(traitsById, snapshot.TraitsById);
		}

		return snapshot.Ids.Count > 0 ? new List<string>(snapshot.Ids) : new List<string>();
	}

	private static void ApplyCloudCatalogAuth(HttpRequestMessage request, string key, string authMode, string apiKeyHeader)
	{
		if (!string.IsNullOrWhiteSpace(key) && !authMode.Equals("None", StringComparison.OrdinalIgnoreCase))
		{
			if (authMode.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || apiKeyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
			{
				request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
			}
			else
			{
				request.Headers.TryAddWithoutValidation(string.IsNullOrWhiteSpace(apiKeyHeader) ? "Authorization" : apiKeyHeader, key);
			}
		}
	}

	private static string CombineEndpointAndPath(string endpoint, string path)
	{
		string baseUrl = (endpoint ?? string.Empty).Trim().TrimEnd('/');
		string relativePath = (string.IsNullOrWhiteSpace(path) ? "/models" : path.Trim());
		if (!relativePath.StartsWith('/'))
		{
			relativePath = "/" + relativePath;
		}
		if (baseUrl.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase))
		{
			return baseUrl;
		}
		return baseUrl + relativePath;
	}

	private static List<string> ExtractModelIds(JsonNode payload, bool filterChatLike)
	{
		List<string> list = new List<string>();
		JsonArray entries;
		if (payload is JsonObject jsonObject)
		{
			if (jsonObject["data"] is JsonArray data)
			{
				entries = data;
			}
			else if (jsonObject["models"] is JsonArray models)
			{
				entries = models;
			}
			else
			{
				entries = new JsonArray();
			}
		}
		else if (payload is JsonArray array)
		{
			entries = array;
		}
		else
		{
			entries = new JsonArray();
		}
		foreach (JsonNode item in entries)
		{
			string modelId;
			if (item is JsonObject modelObject)
			{
				modelId = !string.IsNullOrWhiteSpace(modelObject["id"]?.ToString())
					? modelObject["id"].ToString()
					: (modelObject["name"]?.ToString() ?? string.Empty);
			}
			else if (item is JsonValue jsonValue)
			{
				modelId = jsonValue.ToString();
			}
			else
			{
				modelId = string.Empty;
			}
			if (modelId.Contains('/', StringComparison.Ordinal) && modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
			{
				modelId = modelId.Substring(modelId.LastIndexOf('/') + 1);
			}
			if (!string.IsNullOrWhiteSpace(modelId) && (!filterChatLike || CloudModelCatalogFilter.IsLikelyChatModelId(modelId)))
			{
				list.Add(modelId.Trim());
			}
		}
		return list;
	}

	public async Task<RuntimeActionResult> TestLocalServerReadinessAsync(string modelDirectory, int port, CancellationToken cancellationToken = default(CancellationToken))
	{
		await Task.Delay(0, cancellationToken);
		string serverPath = ResolveLlamaServerPath();
		if (!File.Exists(serverPath))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local server readiness failed",
				Details = "llama-server executable not found at resolved path: " + serverPath + ". Choose an installation on the Servers tab first."
			};
		}
		try
		{
			string helpText = GetLlamaHelpText(serverPath);
			if (string.IsNullOrWhiteSpace(helpText))
			{
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local server readiness failed",
					Details = "llama-server at '" + serverPath + "' did not respond to --help. The file may not be a working llama-server binary."
				};
			}
		}
		catch (Exception ex)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local server readiness failed",
				Details = "llama-server at '" + serverPath + "' could not be started for a version check: " + ex.Message
			};
		}
		if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local server readiness failed",
				Details = "Local model directory is missing or not accessible. Select a valid folder; local models can be added later."
			};
		}
		if (port < 1 || port > 65535)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local server readiness failed",
				Details = $"Invalid Port: {port}."
			};
		}
		int localBackendPort = port + 1;
		if (localBackendPort > 65535)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local server readiness failed",
				Details = $"Port {port} cannot reserve its daemon port (private listen port for llama-server, usually Port+1)."
			};
		}
		bool portIsOccupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any((IPEndPoint endpoint) => endpoint.Port == localBackendPort);
		bool managedServerCanBeRestarted;
		if (_managedLocalPort == localBackendPort)
		{
			Process localServerProcess = _localServerProcess;
			managedServerCanBeRestarted = localServerProcess != null && !localServerProcess.HasExited;
		}
		else
		{
			managedServerCanBeRestarted = false;
		}
		if (portIsOccupied && !managedServerCanBeRestarted)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local server readiness blocked",
				Details = $"llama-server is installed, but daemon port {localBackendPort} (private listen port for llama-server, usually Port+1) is already occupied by another listener. Choose a different Port or stop that listener before launching a model."
			};
		}
		int ggufCount = 0;
		try
		{
			ggufCount = Directory.GetFiles(modelDirectory, "*.gguf").Length;
		}
		catch
		{
		}
		string folderNote = ((ggufCount == 0) ? ("The model folder '" + modelDirectory + "' is reachable but does not yet contain local models in its top level. Copy models there before creating a local profile.") : $"The model folder '{modelDirectory}' currently has {ggufCount} top-level local model(s).");
		return new RuntimeActionResult
		{
			IsSuccess = true,
			Status = "Local server ready to mount a model",
			Details = (managedServerCanBeRestarted ? $"llama-server at '{serverPath}' is working. AI-FluxMux already owns daemon port {localBackendPort} (private listen port for llama-server) behind Port {port} and can replace that llama-server when you launch a Model Profiles entry. {folderNote}" : $"llama-server at '{serverPath}' is working. Port {port} can use daemon port {localBackendPort} (private listen port for llama-server, usually Port+1). This check does not start llama-server or load a model. {folderNote}")
		};
	}

	private bool IsOpenAiStyleProvider(string provider)
	{
		JsonObject root = LoadJsonCached(_configPath);
		return string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(provider, "Copilot GitHub", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase)
			|| CustomCloudProviderRegistry.IsCustomCompat(provider, root);
	}

	private async Task<(bool IsSuccess, string StatusDetail, RuntimeActionResult? Failure)> ProbeOpenAiStyleModelAsync(string provider, string endpoint, string key, string model, CancellationToken cancellationToken)
	{
		string baseUri = endpoint.TrimEnd('/');
		string uri = (baseUri.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUri : (baseUri + "/chat/completions"));
		JsonObject payload = CloudEndpointValidationProbe.CreatePayload(model);
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, uri)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
		};
		if (!string.IsNullOrWhiteSpace(key))
		{
			req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
		}
		try
		{
			using HttpResponseMessage response = await ProbeHttpClient.SendAsync(req, cancellationToken);
			string body = await response.Content.ReadAsStringAsync(cancellationToken);
			if (response.IsSuccessStatusCode)
			{
				return (IsSuccess: true, StatusDetail: $"{(int)response.StatusCode} {response.ReasonPhrase}", Failure: null);
			}
			string detail = HttpErrorResponseFormatter.FormatHttpErrorDetail((int)response.StatusCode, response.ReasonPhrase, body, 500);
			if ((response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound) && (detail.Contains("model_not_supported", StringComparison.OrdinalIgnoreCase) || detail.Contains("requested model is not supported", StringComparison.OrdinalIgnoreCase) || detail.Contains("is not found", StringComparison.OrdinalIgnoreCase) || detail.Contains("not supported for generateContent", StringComparison.OrdinalIgnoreCase) || detail.Contains("does not currently expose", StringComparison.OrdinalIgnoreCase)))
			{
				return (IsSuccess: false, StatusDetail: string.Empty, Failure: new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Cloud validation failed",
					Details = provider + " did not accept '" + model + "' on the chat route AI-FluxMux uses. Refresh Models can list ids before chat/completions accepts them — try another id (for Copilot GitHub, gpt-4.1 and claude-sonnet variants often validate first), then run Validate to endpoint again."
				});
			}
			if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
			{
				return (IsSuccess: false, StatusDetail: string.Empty, Failure: new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Cloud validation failed",
					Details = $"{provider} rejected the API key for model validation ({(int)response.StatusCode}). Check key scope/entitlements for this endpoint."
				});
			}
			return (IsSuccess: false, StatusDetail: string.Empty, Failure: new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud validation failed",
				Details = $"{provider} model probe failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}"
			});
		}
		catch (Exception ex)
		{
			return (IsSuccess: false, StatusDetail: string.Empty, Failure: new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud validation failed",
				Details = provider + " model probe failed: " + ex.Message
			});
		}
	}

	public async Task<RuntimeActionResult> LaunchCloudAsync(string provider, string model, CancellationToken cancellationToken = default(CancellationToken), Action<string>? progressReporter = null)
	{
		CloudCircuitBreaker.Clear(_proxyCloudBreakerPath, provider, model);
		progressReporter?.Invoke("AI-FluxMux is checking the cloud model");
		RuntimeActionResult validate = await ValidateCloudAsync(provider, model, cancellationToken);
		if (!validate.IsSuccess)
		{
			return BuildCloudFailureResult("Cloud launch failed", provider, model, validate.Details);
		}
		_activeCloudProvider = provider;
		_activeCloudModel = model;
		_lastSuccessfulCloudProvider = provider;
		_lastSuccessfulCloudModel = model;
		_activeCloudSinceUtc = DateTime.UtcNow;
		JsonObject root = LoadJsonCached(_configPath);
		JsonObject secrets = LoadJsonCached(_secretsPath);
		int proxyPort = ResolveOrchestratorPort(root);
		progressReporter?.Invoke("AI-FluxMux is preparing the public endpoint");
		RuntimeActionResult gateway = await EnsureGatewayRunningAsync(proxyPort, cancellationToken);
		if (!gateway.IsSuccess)
		{
			WriteProxyRuntimeStateForCloud(root, secrets, provider, model, proxyPort, "failed", gateway.Details, 0);
			return BuildCloudFailureResult("Cloud launch failed", provider, model, gateway.Details);
		}
		int bridgePid = GetGatewayPid();
		progressReporter?.Invoke(
			IsDualHotRouting() && IsManagedLocalAlive()
				? "AI-FluxMux is adding the cloud model beside the loaded local"
				: "AI-FluxMux is switching the cloud model");
		WriteProxyRuntimeStateForCloud(root, secrets, provider, model, proxyPort, "transitioning", validate.Details, bridgePid);
		AppendCloudHandshakeLog($"Switching cloud route provider={provider} model={model} port={proxyPort}.");
		WriteProxyRuntimeStateForCloud(root, secrets, provider, model, proxyPort, "ready", validate.Details, bridgePid);
		progressReporter?.Invoke("AI-FluxMux cloud route is ready");
		var dualHot = IsDualHotRouting() && IsManagedLocalAlive();
		return new RuntimeActionResult
		{
			IsSuccess = true,
			Status = "Cloud route ready",
			Details = dualHot
				? $"Persistent AI-FluxMux gateway on http://127.0.0.1:{proxyPort} keeps local first; {provider} / {model} is ready for hot routing when Cloud Usage Mode allows."
				: $"Persistent AI-FluxMux gateway on http://127.0.0.1:{proxyPort} is routing to {provider} / {model}."
		};
	}

	private async Task<RuntimeActionResult> EnsureGatewayRunningAsync(int proxyPort, CancellationToken cancellationToken)
	{
		if (_gatewayHost is { IsListening: true } && _managedProxyPort == proxyPort && await IsCloudBridgeReadyAsync(proxyPort, cancellationToken))
		{
			return new RuntimeActionResult
			{
				IsSuccess = true,
				Status = "Gateway ready",
				Details = "Reused the active AI-FluxMux gateway."
			};
		}

		if (IsTcpPortOpen("127.0.0.1", proxyPort) && !(_gatewayHost is { IsListening: true } && _gatewayHost.Port == proxyPort))
		{
			StopStaleManagedProxyFromRuntimeState(clearStateIfStopped: false);
			await WaitForPortToCloseAsync(proxyPort, cancellationToken);
			if (IsTcpPortOpen("127.0.0.1", proxyPort) && !(_gatewayHost is { IsListening: true } && _gatewayHost.Port == proxyPort))
			{
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Gateway launch blocked",
					Details = $"Port {proxyPort} is occupied by a service that is not an AI-FluxMux gateway."
				};
			}
		}

		try
		{
			_gatewayHost ??= CreateGatewayHost();
			_gatewayHost.GenerationSpeed = _generationSpeedTracker;
			await _gatewayHost.StartAsync(proxyPort, cancellationToken);
			_managedProxyPort = proxyPort;
		}
		catch (Exception ex)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Gateway launch failed",
				Details = "Could not start the AI-FluxMux gateway: " + ex.Message
			};
		}

		DateTime deadline = DateTime.UtcNow.AddSeconds(12.0);
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (await IsCloudBridgeReadyAsync(proxyPort, cancellationToken))
			{
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Gateway ready",
					Details = $"AI-FluxMux gateway is listening on port {proxyPort}."
				};
			}
			await Task.Delay(250, cancellationToken);
		}

		return new RuntimeActionResult
		{
			IsSuccess = false,
			Status = "Gateway launch timeout",
			Details = $"Timed out waiting for the AI-FluxMux gateway on port {proxyPort}."
		};
	}

	public async Task<RuntimeActionResult> CheckCloudHealthAsync(string provider, string model, CancellationToken cancellationToken = default(CancellationToken))
	{
		RuntimeActionResult validate = await ValidateCloudAsync(provider, model, cancellationToken);
		if (!validate.IsSuccess)
		{
			return BuildCloudFailureResult("Cloud health check failed", provider, model, validate.Details);
		}
		bool isActive = string.Equals(_activeCloudProvider, provider, StringComparison.OrdinalIgnoreCase) && string.Equals(_activeCloudModel, model, StringComparison.OrdinalIgnoreCase) && _activeCloudSinceUtc > DateTime.MinValue;
		JsonObject root = LoadJsonCached(_configPath);
		int proxyPort = ResolveOrchestratorPort(root);
		bool proxyReady = await IsCloudBridgeReadyAsync(proxyPort, cancellationToken);
		bool proxyAlive = _gatewayHost is { IsListening: true };
		string routeState = (isActive ? $"Active route since {_activeCloudSinceUtc:u}." : "Route validated but not marked active in this session.");
		if (!isActive && !proxyReady && !proxyAlive)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Cloud route not started",
				Details = validate.Details + $" No cloud route appears to be launched yet in this session. Start Launch Cloud to bring up the bridge endpoint on 127.0.0.1:{proxyPort}."
			};
		}
		routeState += (proxyReady ? $" Bridge endpoint healthy on 127.0.0.1:{proxyPort}." : $" Bridge endpoint not healthy on 127.0.0.1:{proxyPort}.");
		if (proxyAlive)
		{
			routeState += " In-process AI-FluxMux gateway is listening.";
		}
		if (File.Exists(_proxyRuntimeStatePath))
		{
			routeState += " Runtime state file present.";
		}
		string recentUpstreamError = GetRecentUpstreamProxyError(provider, model, TimeSpan.FromMinutes(30L));
		if (!string.IsNullOrWhiteSpace(recentUpstreamError))
		{
			routeState = routeState + " Recent upstream error: " + recentUpstreamError;
		}
		if (!proxyReady)
		{
			string tail = BuildProxyTailSummary();
			if (!string.IsNullOrWhiteSpace(tail))
			{
				routeState = routeState + " Latest proxy detail: " + tail;
			}
		}
		if (proxyReady)
		{
			return new RuntimeActionResult
			{
				IsSuccess = true,
				Status = "Cloud health check passed",
				Details = validate.Details + " " + routeState
			};
		}
		return BuildCloudFailureResult("Cloud health check failed", provider, model, validate.Details + " " + routeState);
	}

	public async Task<RuntimeActionResult> ValidateCloudEndpointAsync(string provider, string model, CancellationToken cancellationToken = default(CancellationToken))
	{
		RuntimeActionResult health = await CheckCloudHealthAsync(provider, model, cancellationToken);
		if (!health.IsSuccess)
		{
			return health;
		}
		int proxyPort = ResolveOrchestratorPort(LoadJsonCached(_configPath));
		ChatProbeResult probe = await SendEndpointValidationProbeAsync(proxyPort, model, cancellationToken);
		if (!probe.IsSuccess)
		{
			string failureDetail = string.IsNullOrWhiteSpace(probe.Details)
				? "The AI-FluxMux bridge is healthy, but its upstream chat request failed."
				: (probe.Details.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase)
					? probe.Details
					: "The AI-FluxMux bridge is healthy, but its upstream chat request failed. " + probe.Details);
			return BuildCloudFailureResult("Cloud endpoint validation failed", provider, model, failureDetail);
		}
		return new RuntimeActionResult
		{
			IsSuccess = true,
			Status = "Cloud endpoint validation passed",
			Details = $"AI-FluxMux reached {provider} / {model} through the live bridge endpoint in {probe.LatencyMs} ms."
		};
	}

	public async Task<RuntimeActionResult> CheckCloudEndpointLivenessAsync(string provider, string model, CancellationToken cancellationToken = default(CancellationToken))
	{
		bool isActive = string.Equals(_activeCloudProvider, provider, StringComparison.OrdinalIgnoreCase) && string.Equals(_activeCloudModel, model, StringComparison.OrdinalIgnoreCase) && _activeCloudSinceUtc > DateTime.MinValue;
		int proxyPort = ResolveOrchestratorPort(LoadJsonCached(_configPath));
		bool proxyReady = await IsCloudBridgeReadyAsync(proxyPort, cancellationToken);
		bool proxyAlive = _gatewayHost is { IsListening: true };
		bool isSuccess = isActive & proxyReady & proxyAlive & await IsCloudUpstreamReachableAsync(provider, cancellationToken);
		return new RuntimeActionResult
		{
			IsSuccess = isSuccess,
			Status = (isSuccess ? "Cloud endpoint connected" : "Cloud endpoint disconnected"),
			Details = (isSuccess ? $"Managed bridge endpoint is alive on 127.0.0.1:{proxyPort}, and the {provider} upstream is reachable." : $"The active {provider} / {model} bridge or upstream endpoint is no longer reachable.")
		};
	}

	private async Task<bool> IsCloudUpstreamReachableAsync(string provider, CancellationToken cancellationToken)
	{
		JsonObject root = LoadJsonCached(_configPath);
		string endpoint = provider switch
		{
			"Gemini" => "https://generativelanguage.googleapis.com/v1beta/openai", 
			"Anthropic" => "https://api.anthropic.com/v1/messages", 
			"OpenAI" => FirstNonEmpty(GetString(root, "OpenAiEndpoint"), "https://api.openai.com/v1"), 
			"Copilot GitHub" => FirstNonEmpty(GetString(root, "GitHubCopilotEndpoint"), "https://api.githubcopilot.com"), 
			_ => CustomCloudProviderRegistry.IsCustomCompat(provider, root)
				? CustomCloudProviderRegistry.GetEndpoint(root, provider)
				: string.Empty, 
		};
		if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
		{
			return false;
		}
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, uri);
			using (await HttpClient.SendAsync(request, cancellationToken))
			{
				return true;
			}
		}
		catch
		{
			return false;
		}
	}

	public Task<RuntimeActionResult> StopCloudAsync()
	{
		if (string.IsNullOrWhiteSpace(_activeCloudProvider) && string.IsNullOrWhiteSpace(_activeCloudModel))
		{
			return Task.FromResult(new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "No active cloud route",
				Details = "No cloud route is currently active in this session."
			});
		}
		JsonObject root = LoadJsonCached(_configPath);
		int value = ResolveOrchestratorPort(root);
		string activeCloudProvider = _activeCloudProvider;
		string activeCloudModel = _activeCloudModel;
		_activeCloudProvider = string.Empty;
		_activeCloudModel = string.Empty;
		_activeCloudSinceUtc = DateTime.MinValue;
		if (IsDualHotRouting() && IsManagedLocalAlive())
		{
			DemoteCloudFromProxyState("cloud-stop");
		}
		else
		{
			ClearProxyRuntimeState("cloud-stop");
		}
		AppendCloudHandshakeLog($"Stopped cloud route provider={activeCloudProvider} model={activeCloudModel} at {DateTime.UtcNow:u}");
		return Task.FromResult(new RuntimeActionResult
		{
			IsSuccess = true,
			Status = "Cloud route stopped",
			Details = $"Cleared active cloud route state for {activeCloudProvider} / {activeCloudModel}; the persistent gateway remains available on port {value}."
		});
	}

	public async Task<RuntimeActionResult> RestartCloudAsync(string provider, string model, CancellationToken cancellationToken = default(CancellationToken))
	{
		await StopCloudAsync();
		RuntimeActionResult launch = await LaunchCloudAsync(provider, model, cancellationToken);
		if (!launch.IsSuccess)
		{
			return launch;
		}
		return new RuntimeActionResult
		{
			IsSuccess = true,
			Status = "Cloud route restarted",
			Details = launch.Details
		};
	}

	public async Task<RuntimeActionResult> CheckLocalHealthAsync(int port, CancellationToken cancellationToken = default(CancellationToken))
	{
		Process localServerProcess = _localServerProcess;
		bool managedAlive = localServerProcess != null && !localServerProcess.HasExited;
		int probePort = LocalHealthGuidance.ResolveLlamaHealthProbePort(managedAlive, _managedLocalPort, port);
		bool ready = await IsLlamaEndpointReadyAsync(probePort, cancellationToken);
		// While the Client app is generating, /health can time out even though llama-server is fine.
		if (!ready && managedAlive && probePort > 0 && IsTcpPortOpen("127.0.0.1", probePort))
		{
			ready = true;
		}
		string recentContextOverflow = GetRecentLocalContextOverflow(TimeSpan.FromMinutes(5L), _managedLocalContext);
		if (managedAlive)
		{
			_hasUnmanagedLocalEndpoint = false;
			_unmanagedLocalEndpointPort = -1;
		}
		else if (ready)
		{
			_hasUnmanagedLocalEndpoint = false;
			_unmanagedLocalEndpointPort = -1;
		}
		if (!ready && !managedAlive && _managedLocalPort < 1 && !_hasUnmanagedLocalEndpoint)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local route not started",
				Details = "No local model is launched yet in this session. Launch a local model profile from Quick Select, or Validate to endpoint on Model Profiles, so llama-server starts and AI-FluxMux can serve http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "."
			};
		}

		bool portOpen = probePort > 0 && IsTcpPortOpen("127.0.0.1", probePort);
		int? managedPid = null;
		try
		{
			if (managedAlive)
			{
				managedPid = localServerProcess.Id;
			}
		}
		catch
		{
		}

		string detail = LocalHealthGuidance.Build(
			ready,
			managedAlive,
			portOpen,
			probePort,
			port,
			managedPid,
			recentContextOverflow,
			ready ? null : _lastLocalStderrTail,
			_managedLocalModel);
		if (ready && !managedAlive)
		{
			detail += " This llama-server is running outside this AI-FluxMux session, so "
				+ ControlLabelMarkup.Mark("Stop")
				+ " cannot control it. "
				+ ControlLabelMarkup.Mark("Launch")
				+ " from Quick Select if you want AI-FluxMux to own llama-server.";
		}
		else if (!ready && !managedAlive && _managedLocalPort == port)
		{
			detail += " llama-server has exited. Launch the local model profile again.";
		}

		bool hasRecentOverflow = !string.IsNullOrWhiteSpace(recentContextOverflow);
		bool isSuccess = ready && !hasRecentOverflow;
		string status = ((!ready) ? "Local health check failed" : (hasRecentOverflow ? "Local health check needs attention" : "Local health check passed"));
		return new RuntimeActionResult
		{
			IsSuccess = isSuccess,
			Status = status,
			Details = detail
		};
	}

	public async Task<LocalGenerationTimingResult> TimeShortLocalReplyAsync(int publicGatewayPort, string model, CancellationToken cancellationToken = default(CancellationToken))
	{
		Process localServerProcess = _localServerProcess;
		int probePort = ((localServerProcess != null && !localServerProcess.HasExited && _managedLocalPort > 0) ? _managedLocalPort : (publicGatewayPort + 1));
		return await TimeShortReadyReplyAsync(probePort, model, "Short reply completed", "The short reply test did not complete.", cancellationToken, 90000);
	}

	public async Task<LocalGenerationTimingResult> TimeShortLocalReplyThroughPublicEndpointAsync(int publicGatewayPort, string model, CancellationToken cancellationToken = default(CancellationToken), int requestTimeoutMs = 5000)
	{
		return await TimeShortReadyReplyAsync(publicGatewayPort, model, "Public endpoint reply completed", "The public endpoint test did not complete.", cancellationToken, requestTimeoutMs, FluxMuxGatewayRouting.Local);
	}

	private async Task<LocalGenerationTimingResult> TimeShortReadyReplyAsync(int port, string model, string successDetailPrefix, string failureFallback, CancellationToken cancellationToken, int requestTimeoutMs = 5000, string? forceRoute = null)
	{
		ChatProbeResult probe = await SendChatProbeAsync(port, model, CreateShortReadyProbeMessages(), Math.Clamp(requestTimeoutMs, 1000, 180000), cancellationToken, 16, forceRoute);
		return new LocalGenerationTimingResult
		{
			IsSuccess = probe.IsSuccess,
			LatencyMs = probe.LatencyMs,
			Details = (probe.IsSuccess ? $"{successDetailPrefix} in {probe.LatencyMs} ms." : FirstNonEmpty(probe.Details, failureFallback))
		};
	}

	private static JsonArray CreateShortReadyProbeMessages()
	{
		return new JsonArray
		{
			new JsonObject
			{
				["role"] = "user",
				["content"] = "Reply with the single word ready."
			}
		};
	}

	private async Task<LocalGenerationTimingResult> ProbePublicEndpointWithShortRetriesAsync(int publicPort, string model, CancellationToken cancellationToken)
	{
		LocalGenerationTimingResult last = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			last = await TimeShortLocalReplyThroughPublicEndpointAsync(publicPort, model, cancellationToken);
			if (last.IsSuccess)
			{
				return last;
			}
		}
		return last ?? new LocalGenerationTimingResult
		{
			IsSuccess = false,
			Details = "The public endpoint test did not complete."
		};
	}

	public async Task<LocalEndpointDiscoveryResult> DiscoverLocalEndpointAsync(int preferredPort, Action<string>? progressReporter = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		int[] candidates = BuildLocalEndpointDiscoveryCandidates(preferredPort).Take(24).ToArray();
		List<LocalEndpointCandidate> compatibleFindings = new List<LocalEndpointCandidate>();
		List<(int Port, string Detail)> incompatibleFindings = new List<(int, string)>();
		DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(8000.0);
		DateTime startedUtc = DateTime.UtcNow;
		DateTime lastProgressUtc = DateTime.MinValue;
		int scannedCount = 0;
		IReadOnlyList<(int Pid, string Name)> llamaProcesses = GetRunningLlamaFamilyProcesses();
		IReadOnlyList<InstalledLocalServerCandidate> installedServers = GetInstalledLocalServerCandidates();
		string installedServerPath = ResolveLlamaServerPath();
		string installationSummary = ((installedServers.Count > 0) ? $"Usable local server installation(s): {string.Join("; ", installedServers.Select((InstalledLocalServerCandidate x) => x.Name + ": " + x.ExecutablePath))}. Selected for AI-FluxMux launch: {installedServerPath}." : ("No usable llama-server installation was found. Expected location: " + installedServerPath + "."));
		TryReportProgress(ref lastProgressUtc, progressReporter, $"Scan 0/{candidates.Length}...");
		int[] array = candidates;
		foreach (int port in array)
		{
			if (cancellationToken.IsCancellationRequested || DateTime.UtcNow >= deadlineUtc)
			{
				break;
			}
			scannedCount++;
			TryReportProgress(ref lastProgressUtc, progressReporter, $"Scan {scannedCount}/{candidates.Length}: p{port}");
			if (IsTcpPortOpen("127.0.0.1", port))
			{
				TryReportProgress(ref lastProgressUtc, progressReporter, $"Probe p{port}...");
				(bool Compatible, string Detail) probe = await ProbeLocalEndpointCompatibilityAsync(port, cancellationToken, 1200);
				if (probe.Compatible)
				{
					bool hasOwner = TryGetLocalEndpointOwnerByPort(port, out int ownerPid, out string ownerName);
					compatibleFindings.Add(new LocalEndpointCandidate
					{
						Port = port,
						OwnerName = (hasOwner ? ownerName : "unknown process"),
						OwnerPid = (hasOwner ? ownerPid : 0),
						Detail = probe.Detail
					});
				}
				else
				{
					incompatibleFindings.Add((port, probe.Detail));
				}
			}
		}
		if (compatibleFindings.Count > 0)
		{
			int elapsedMs = Math.Max(1, (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds);
			LocalEndpointCandidate selected = (from x in compatibleFindings
				orderby x.Port == preferredPort descending, x.Port == _managedLocalPort descending, x.Port
				select x).First();
			string listed = string.Join(", ", from x in compatibleFindings.OrderBy((LocalEndpointCandidate x) => x.Port).Take(6)
				select (x.OwnerPid > 0) ? $"localhost:{x.Port} ({x.OwnerName}, PID {x.OwnerPid})" : $"localhost:{x.Port} ({x.OwnerName})");
			string hasMore = ((compatibleFindings.Count > 6) ? $" (+{compatibleFindings.Count - 6} more)" : string.Empty);
			return new LocalEndpointDiscoveryResult
			{
				Found = true,
				Compatible = true,
				Port = selected.Port,
				InstalledServers = installedServers,
				CompatibleServers = compatibleFindings.OrderBy((LocalEndpointCandidate x) => x.Port).ToArray(),
				Status = ((compatibleFindings.Count == 1) ? "Compatible local server discovered" : "Compatible local servers discovered"),
				Details = $"{installationSummary} Already running compatible server(s): {listed}{hasMore}. Found port {selected.Port}. Do not copy this into Port if you want AI-FluxMux to switch or route models; that other program still owns the process. If this is llama-server, built for this GPU, and nothing else will start or stop it again, point AI-FluxMux at that installation below and close the other program first. Scanned {scannedCount} candidate port(s) in {FormatDurationMs(elapsedMs)}. {selected.Detail}"
			};
		}
		if (incompatibleFindings.Count > 0)
		{
			int elapsedMs2 = Math.Max(1, (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds);
			string sample = string.Join(" | ", from x in incompatibleFindings.Take(3)
				select $"{x.Port}: {x.Detail}");
			string processSummary = ((llamaProcesses.Count == 0) ? "No running llama-server or llserver process was detected." : ("Detected llama-family process(es), but none exposed a compatible model-server port: " + string.Join(", ", llamaProcesses.Select<(int, string), string>(((int Pid, string Name) x) => $"{x.Name} (PID {x.Pid})")) + "."));
			return new LocalEndpointDiscoveryResult
			{
				Found = (llamaProcesses.Count > 0),
				Compatible = false,
				Port = 0,
				InstalledServers = installedServers,
				Status = ((llamaProcesses.Count > 0) ? "Llama server detected but not compatible" : (File.Exists(installedServerPath) ? "Local server installation ready" : "No local server installation found")),
				Details = $"{installationSummary} {processSummary} Scanned {scannedCount} candidate port(s) in {FormatDurationMs(elapsedMs2)}. Unrelated open ports were ignored: {sample}"
			};
		}
		int totalElapsedMs = Math.Max(1, (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds);
		return new LocalEndpointDiscoveryResult
		{
			Found = false,
			Compatible = false,
			Port = 0,
			InstalledServers = installedServers,
			Status = ((llamaProcesses.Count > 0) ? "Llama server detected but not listening" : (File.Exists(installedServerPath) ? "Local server installation ready" : "No local server installation found")),
			Details = ((llamaProcesses.Count > 0) ? $"{installationSummary} Detected {string.Join(", ", llamaProcesses.Select<(int, string), string>(((int Pid, string Name) x) => $"{x.Name} (PID {x.Pid})"))}, but no reachable compatible model-server port. Check its host/port arguments. Scanned {scannedCount} candidate port(s) in {FormatDurationMs(totalElapsedMs)}." : $"{installationSummary} No already-running llama-server or llserver process was detected. AI-FluxMux can start the installed server when you use Launch Local. Scanned {scannedCount} candidate port(s) in {FormatDurationMs(totalElapsedMs)}.")
		};
		static bool TryReportProgress(ref DateTime lastReportUtc, Action<string>? reporter, string message)
		{
			if (reporter == null)
			{
				return false;
			}
			DateTime utcNow = DateTime.UtcNow;
			if ((utcNow - lastReportUtc).TotalMilliseconds < 250.0)
			{
				return false;
			}
			lastReportUtc = utcNow;
			reporter(message);
			return true;
		}
	}

	private static IReadOnlyList<(int Pid, string Name)> GetRunningLlamaFamilyProcesses()
	{
		List<(int, string)> list = new List<(int, string)>();
		Process[] processes = Process.GetProcesses();
		foreach (Process process in processes)
		{
			try
			{
				string processName = process.ProcessName;
				if (processName.Contains("llama", StringComparison.OrdinalIgnoreCase) || processName.Contains("llserver", StringComparison.OrdinalIgnoreCase))
				{
					list.Add((process.Id, processName));
				}
			}
			catch
			{
			}
			finally
			{
				process.Dispose();
			}
		}
		return list;
	}

	public async Task<RuntimeActionResult> StopLocalAsync(bool preserveParkedForIdleWake = false)
	{
		await _idleLifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
		try
		{
			return await StopLocalCoreAsync(preserveParkedForIdleWake).ConfigureAwait(false);
		}
		finally
		{
			_idleLifecycle.Release();
		}
	}

	private async Task<RuntimeActionResult> StopLocalCoreAsync(bool preserveParkedForIdleWake = false)
	{
		if (!preserveParkedForIdleWake)
		{
			_parkedLocalLaunch = null;
			_localSleepingFromIdle = false;
		}

		ClearLocalReloadRecommend();

		Process localServerProcess = _localServerProcess;
		if (localServerProcess == null || localServerProcess.HasExited)
		{
			int reclaimed = StopStaleManagedLocalFromRuntimeState(clearStateIfStopped: true);
			if (reclaimed > 0)
			{
				_hasUnmanagedLocalEndpoint = false;
				_unmanagedLocalEndpointPort = -1;
				_managedLocalPort = -1;
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Local route stopped",
					Details = $"Stopped {reclaimed} stale managed local runtime process(es) from runtime state cache."
				};
			}
			int probePort = ((_unmanagedLocalEndpointPort > 0) ? _unmanagedLocalEndpointPort : ((_managedLocalPort > 0) ? _managedLocalPort : 0));
			bool portOpen = probePort > 0;
			bool endpointReady = portOpen;
			if (endpointReady)
			{
				endpointReady = await IsLlamaEndpointReadyAsync(probePort, CancellationToken.None);
			}
			if (endpointReady)
			{
				if (TryStopLocalEndpointOwnerByPort(probePort, out int ownerPid, out string ownerName))
				{
					await WaitForPortToCloseAsync(probePort, CancellationToken.None);
					_hasUnmanagedLocalEndpoint = false;
					_unmanagedLocalEndpointPort = -1;
					_managedLocalPort = -1;
					ClearLocalRuntimeState("stale-local-owner-cleanup");
					return new RuntimeActionResult
					{
						IsSuccess = true,
						Status = "Local route stopped",
						Details = $"Stopped stale local endpoint owner process {ownerName} (PID {ownerPid}) on port {probePort}."
					};
				}
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local route stop failed",
					Details = "A local endpoint is already responding on http://127.0.0.1:"
						+ probePort.ToString(CultureInfo.InvariantCulture)
						+ ", but this session could not reclaim the owning process. Stop the owning terminal/process first, then use "
						+ ControlLabelMarkup.Mark("Stop")
						+ " again."
				};
			}
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "No managed local process",
				Details = "No active local llama-server process is currently managed by Avalonia runtime service."
			};
		}
		try
		{
			int pid = _localServerProcess.Id;
			var processToStop = _localServerProcess;
			await Task.Run(() =>
			{
				try
				{
					if (!processToStop.HasExited)
					{
						processToStop.Kill(entireProcessTree: true);
					}
				}
				catch (InvalidOperationException)
				{
				}
			}).ConfigureAwait(false);
			await WaitForProcessExitOrKillAsync(processToStop, TimeSpan.FromSeconds(4L));
			int stoppedPort = ((_managedLocalPort > 0) ? _managedLocalPort : 0);
			_localServerProcess = null;
			_managedLocalPort = -1;
			_managedLocalModel = string.Empty;
			_managedLocalVariant = string.Empty;
			_managedLocalReasoning = "Off";
			_managedLocalVisionEnabled = false;
			_managedLocalContext = 0;
			_managedLocalMaxTokens = 0;
			_managedLocalLaunchFingerprint = string.Empty;
			_hasUnmanagedLocalEndpoint = false;
			_unmanagedLocalEndpointPort = -1;
			_localRequestOverlays.Clear();
			ClearLocalRuntimeState("local-stop");
			if (IsDualHotRouting() && IsCloudRouteActive())
			{
				DemoteLocalFromProxyState("local-stop");
			}
			else
			{
				ClearProxyRuntimeState("local-stop");
			}
			if (stoppedPort > 0)
			{
				await WaitForPortToCloseAsync(stoppedPort, CancellationToken.None, 4000);
			}
			return new RuntimeActionResult
			{
				IsSuccess = true,
				Status = "Local route stopped",
				Details = $"Stopped managed local llama-server process PID {pid}."
			};
		}
		catch (Exception ex)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local stop failed",
				Details = "Could not stop managed local process: " + ex.Message
			};
		}
	}

	public async Task<RuntimeActionResult> LaunchLocalAsync(string selectedLocalModel, string selectedVariant, string modelDirectory, int port, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge, CancellationToken cancellationToken = default(CancellationToken), Action<string>? progressReporter = null)
	{
		JsonObject root = LoadJsonCached(_configPath);
		int localBackendPort = port + 1;
		if (localBackendPort > 65535)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch failed",
				Details = $"Port {port} cannot reserve its daemon port (private listen port for llama-server, usually Port+1)."
			};
		}
		int launchBudgetMs = GetLocalLaunchWarmupBudgetMs(selectedLocalModel, selectedVariant);
		int estimatedLaunchMs = GetLocalLaunchEstimateMs(selectedLocalModel, selectedVariant);
		string serverPath = ResolveLlamaServerPath();
		if (!File.Exists(serverPath))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch failed",
				Details = "llama-server executable not found at resolved path: " + serverPath
			};
		}
		string modelPath = ResolveModelPath(selectedLocalModel, modelDirectory);
		if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch failed",
				Details = $"Local model not found for selection '{selectedLocalModel}' in '{modelDirectory}'."
			};
		}
		if (LocalHealthGuidance.IsVisionProjectorFile(modelPath))
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch failed",
				Details = LocalHealthGuidance.FormatVisionProjectorAsModelWarning(modelPath)
			};
		}
		RememberParkedLocalLaunch(selectedLocalModel, selectedVariant, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge);
		progressReporter?.Invoke("AI-FluxMux is preparing the public endpoint");
		RuntimeActionResult gateway = await EnsureGatewayRunningAsync(port, cancellationToken);
		if (!gateway.IsSuccess)
		{
			WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "failed", gateway.Details, GetGatewayPid());
			return gateway;
		}
		progressReporter?.Invoke("AI-FluxMux is checking the previous llama-server");
		int stoppedLeftover = StopStaleManagedLocalFromRuntimeState(clearStateIfStopped: true);
		bool stoppedPrevious = stoppedLeftover > 0;
		Process localServerProcess = null;
		bool alreadyRunningThisProfile = false;
		if (await IsLlamaEndpointReadyAsync(localBackendPort, cancellationToken))
		{
			localServerProcess = _localServerProcess;
			if (localServerProcess != null && !localServerProcess.HasExited && _managedLocalPort == localBackendPort && _managedLocalModel.Equals(selectedLocalModel, StringComparison.OrdinalIgnoreCase))
			{
				alreadyRunningThisProfile = FirstNonEmpty(_managedLocalVariant, "(defaults)").Equals(FirstNonEmpty(selectedVariant, "(defaults)"), StringComparison.OrdinalIgnoreCase);
			}
		}
		if (alreadyRunningThisProfile)
		{
			WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "ready", "Local public endpoint is already running this profile.", GetGatewayPid());
			progressReporter?.Invoke("AI-FluxMux public endpoint is already running");
			LocalGenerationTimingResult reuseProbe = await ProbePublicEndpointWithShortRetriesAsync(port, selectedLocalModel, cancellationToken);
			if (reuseProbe.IsSuccess)
			{
				progressReporter?.Invoke("AI-FluxMux and llama-server are ready");
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Local route ready",
					Details = $"llama-server is already running {selectedLocalModel} / {FirstNonEmpty(selectedVariant, "(defaults)")}. The AI-FluxMux public endpoint replied in {reuseProbe.LatencyMs} ms."
				};
			}
			return new RuntimeActionResult
			{
				IsSuccess = true,
				Status = LocalLaunchStatus.BackendReady,
				Details = "llama-server is already running, but a short check on Port (the public AI-FluxMux address) did not succeed. " + FirstNonEmpty(reuseProbe.Details, "Port check failed.")
			};
		}
		WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "transitioning", "Preparing llama-server on the daemon port.", GetGatewayPid());
		if (await IsLlamaEndpointReadyAsync(localBackendPort, cancellationToken))
		{
			localServerProcess = _localServerProcess;
			if (localServerProcess == null || localServerProcess.HasExited)
			{
				string ownerText = (TryGetLocalEndpointOwnerByPort(localBackendPort, out int ownerPid, out string ownerName) ? $"{ownerName} (PID {ownerPid})" : "another process");
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local port already in use",
					Details = $"A compatible listener owned by {ownerText} is already responding on daemon port {localBackendPort} (private listen port for llama-server, usually Port+1). Close that process before AI-FluxMux replaces the managed llama-server."
				};
			}
		}
		localServerProcess = _localServerProcess;
		double reclaimableVramGiB = 0;
		if (localServerProcess != null && !localServerProcess.HasExited)
		{
			reclaimableVramGiB = EstimateManagedLocalReclaimableVramGiB(modelDirectory);
			progressReporter?.Invoke("AI-FluxMux is stopping the previous llama-server");
			try
			{
				_localServerProcess.Kill(entireProcessTree: true);
				await _localServerProcess.WaitForExitAsync(cancellationToken);
			}
			catch
			{
			}
			stoppedPrevious = true;
			await WaitForPortToCloseAsync(localBackendPort, cancellationToken, 15000);
			if (IsTcpPortOpen("127.0.0.1", localBackendPort))
			{
				string detail = $"Previous llama-server did not release daemon port {localBackendPort} (private listen port for llama-server, usually Port+1).";
				WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "failed", detail, GetGatewayPid());
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local switch blocked",
					Details = detail
				};
			}
		}
		string helpText = GetLlamaHelpText(serverPath);
		_lastLocalStderrTail = string.Empty;
		JsonObject profile = (ResolveLocalProfile(root, selectedLocalModel, selectedVariant).DeepClone() as JsonObject) ?? new JsonObject();
		ApplyRecommendedAutoLaunchSettings(profile, modelPath, selectedVariant);
		_managedLocalReasoning = GetString(profile, "LocalReasoning");
		if (string.IsNullOrWhiteSpace(_managedLocalReasoning))
		{
			_managedLocalReasoning = "Off";
		}
		string configuredOffload = NormalizeOffloadMode(GetString(profile, "LocalGpuOffloadMode"));
		GpuGuardrailState gpuGuardrail = GetLocalGpuGuardrailState();
		string effectiveOffload = (gpuGuardrail.UseConservativeLocalLaunch ? "Auto" : configuredOffload);
		ApplyVisionLaunchOverlay(profile, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge);
		(bool Enabled, string ProjectorPath, int MaxImageEdge) vision = ResolveProfileVision(profile, modelPath);
		_managedLocalVisionEnabled = vision.Enabled;
		_managedLocalContext = GetProfileInt(profile, "OverrideContext", 0);
		List<string> args = BuildLocalServerArgs(modelPath, localBackendPort, profile, helpText, effectiveOffload, vision.Enabled, vision.ProjectorPath, vision.MaxImageEdge);
		_managedLocalMaxTokens = ReadIntArg(args, "-n");
		List<string> multiGpuArgs = BuildMultiGpuArgs(helpText, effectiveOffload, gpuGuardrail.UseConservativeLocalLaunch, profile);
		if (multiGpuArgs.Count > 0)
		{
			args.AddRange(multiGpuArgs);
		}
		string argString = string.Join(" ", args.Select(QuoteArg));
		double modelFileGiB = new FileInfo(modelPath).Length / 1073741824.0;
		double launchVramBaselineGiB = 0;
		double launchVramFreeGiB = 0;
		double launchVramTotalGiB = 0;
		bool launchVramSampled = LocalGpuVramSample.TryRead(out launchVramBaselineGiB, out launchVramTotalGiB, out launchVramFreeGiB);
		double launchVramEstimateGiB = LocalVramFootprintEstimate.EstimateGiB(profile, modelFileGiB, vision.Enabled);
		LocalVramPressureAssessment launchVramWarning = LocalVramPressureAdvisor.Assess(
			launchVramEstimateGiB,
			launchVramFreeGiB,
			launchVramTotalGiB,
			effectiveOffload.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? configuredOffload : effectiveOffload,
			vision.Enabled,
			reclaimableGiB: reclaimableVramGiB);
		if (launchVramWarning.ShowWarning)
		{
			var variantHint = string.Empty;
			if (launchVramSampled)
			{
				var suggestion = FindValidatedVramAlternative(
					selectedLocalModel,
					selectedVariant,
					launchVramFreeGiB,
					launchVramEstimateGiB,
					modelFileGiB);
				variantHint = LocalVramVariantAdvisor.FormatSuggestion(suggestion);
			}

			TryUpdateLocalProfileSettings(selectedLocalModel, selectedVariant, settings =>
			{
				settings["LastVramWarning"] = launchVramWarning.Message + variantHint;
				settings["LastVramWarningUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			});
		}
		bool vramBusyBeforeStart = LocalGpuVramSample.TryRead(out double busyUsedGiB, out double busyTotalGiB, out _)
			&& LocalLaunchStartupRetry.VramLooksBusy(busyUsedGiB, busyTotalGiB);
		if (stoppedPrevious || vramBusyBeforeStart)
		{
			progressReporter?.Invoke("AI-FluxMux is waiting for the GPU after the previous llama-server");
			await SettleAfterFailedLocalStartupAsync(localBackendPort, cancellationToken);
		}
		Process process = null;
		RuntimeActionResult? started = null;
		for (int startupAttempt = 1; startupAttempt <= LocalLaunchStartupRetry.MaxAttempts; startupAttempt++)
		{
			started = TryStartManagedLlamaProcess(
				serverPath,
				argString,
				modelPath,
				localBackendPort,
				port,
				selectedLocalModel,
				selectedVariant,
				profile,
				progressReporter,
				out process);
			if (started != null)
			{
				return started;
			}
		Stopwatch launchStopwatch = Stopwatch.StartNew();
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(launchBudgetMs);
		bool modelLoadWaitActive = false;
		bool retryStartup = false;
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (process == null || process.HasExited)
			{
				if (LocalLaunchStartupRetry.ShouldRetry(_lastLocalStderrTail, startupAttempt))
				{
					WriteProxyRuntimeStateForLocal(
						selectedLocalModel,
						port,
						localBackendPort,
						"transitioning",
						"llama-server exited during startup; AI-FluxMux is waiting for the GPU, then trying once more.",
						GetGatewayPid());
					progressReporter?.Invoke("AI-FluxMux is waiting for the GPU after llama-server stopped");
					await SettleAfterFailedLocalStartupAsync(localBackendPort, cancellationToken);
					_lastLocalStderrTail = string.Empty;
					try
					{
						process.Dispose();
					}
					catch
					{
					}
					retryStartup = true;
					break;
				}

				ClearLocalRuntimeState("local-exited-during-startup");
				WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "failed", "llama-server exited during startup.", GetGatewayPid());
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local launch failed",
					Details = "llama-server exited during startup." + (string.IsNullOrWhiteSpace(_lastLocalStderrTail) ? string.Empty : (" Last llama-server output: " + _lastLocalStderrTail))
				};
			}
			if (await IsLlamaEndpointReadyAsync(localBackendPort, cancellationToken))
			{
				WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "ready", "llama-server is responding on the daemon port; checking Port.", GetGatewayPid());
				if (!modelLoadWaitActive)
				{
					progressReporter?.Invoke("AI-FluxMux is testing the public endpoint");
				}
				LocalGenerationTimingResult publicProbe = await ProbePublicEndpointWithShortRetriesAsync(port, selectedLocalModel, cancellationToken);
				if (!(publicProbe?.IsSuccess ?? false))
				{
					string probeDetails = FirstNonEmpty(publicProbe?.Details ?? string.Empty, "Public endpoint check failed.");
					if (IsTransientWarmupProbeFailure(probeDetails))
					{
						WriteLocalRuntimeState(modelPath, localBackendPort, process.Id, "warming", "llama-server is responding on the daemon port; the local model is still loading.");
						WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "warming", probeDetails, GetGatewayPid());
						if (!modelLoadWaitActive)
						{
							modelLoadWaitActive = true;
							progressReporter?.Invoke("Loading model");
						}
						await Task.Delay(2000, cancellationToken);
						continue;
					}

					modelLoadWaitActive = false;

					WriteLocalRuntimeState(modelPath, localBackendPort, process.Id, "ready", "llama-server is responding on the daemon port; Port check failed.");
					WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "ready", probeDetails, GetGatewayPid());
					return new RuntimeActionResult
					{
						IsSuccess = true,
						Status = LocalLaunchStatus.BackendReady,
						Details = "llama-server is running on the daemon port, but a short reply through Port (the public AI-FluxMux address) did not succeed. " + probeDetails
					};
				}
				int readyMs = (int)Math.Min(2147483647L, launchStopwatch.ElapsedMilliseconds);
				WriteLocalRuntimeState(modelPath, localBackendPort, process.Id, "ready", "llama-server on the daemon port and Port are both responding.");
				WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "ready", "Port is ready.", GetGatewayPid());
				SeedForwardCompactFromProfile(selectedLocalModel, selectedVariant);
				RecordLocalLaunchTelemetry(selectedLocalModel, selectedVariant, readyMs, "ready");
				double launchVramAfterGiB = 0;
				bool launchVramAfterSampled = launchVramSampled && LocalGpuVramSample.TryRead(out launchVramAfterGiB, out _, out _);
				if (launchVramAfterSampled)
				{
					RecordLocalMeasuredVram(selectedLocalModel, selectedVariant, launchVramBaselineGiB, launchVramAfterGiB, vision.Enabled);
				}
				string readyDetails = $"Port is available on http://127.0.0.1:{port}; llama-server is ready on daemon port {localBackendPort} (private listen port for llama-server, usually Port+1) with local model {Path.GetFileName(modelPath)} (model profile variant {FirstNonEmpty(selectedVariant, "Variant 1")}, vision {(vision.Enabled ? "on" : "off")}, max-edge {vision.MaxImageEdge}, offload {effectiveOffload}, guardrail {gpuGuardrail.ShortLabel}). Port replied in {publicProbe.LatencyMs} ms. Launch to endpoint took {readyMs} ms.";
				if (launchVramWarning.ShowWarning)
				{
					readyDetails += " " + launchVramWarning.Message;
				}
				if (launchVramAfterSampled)
				{
					double measuredDeltaGiB = Math.Max(0, Math.Round(launchVramAfterGiB - launchVramBaselineGiB, 2));
					if (measuredDeltaGiB > 0)
					{
						readyDetails += " " + LocalVramPressureAdvisor.FormatMeasuredFootnote(measuredDeltaGiB, DateTimeOffset.UtcNow);
					}
				}
				progressReporter?.Invoke("AI-FluxMux and llama-server are ready");
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Local route ready",
					Details = readyDetails
				};
			}
			await Task.Delay(500, cancellationToken);
		}
		if (retryStartup)
		{
			continue;
		}
		if (process != null && !process.HasExited)
		{
			if (IsTcpPortOpen("127.0.0.1", localBackendPort))
			{
				WriteLocalRuntimeState(modelPath, localBackendPort, process.Id, "warming", "Local process running while endpoint warms up.");
				WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "warming", "llama-server is still warming up on the daemon port.", GetGatewayPid());
				return new RuntimeActionResult
				{
					IsSuccess = true,
					Status = "Local route warming up",
					Details = "llama-server is running and its port is open, but llama-server health is not ready yet. Current startup window is about " + FormatDurationMs(launchBudgetMs) + " for this model profile based on prior launches. Wait a little longer, then use Check Connection Health." + (string.IsNullOrWhiteSpace(_lastLocalStderrTail) ? string.Empty : (" Last llama-server output: " + _lastLocalStderrTail))
				};
			}
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch still in progress",
				Details = "llama-server is still running but is not ready yet. llama-server did not finish launching within its previous manual launch timeframe for this model profile (" + FormatDurationMs((estimatedLaunchMs > 0) ? estimatedLaunchMs : launchBudgetMs) + " observed baseline); VRAM availability or other conditions may have changed. Wait and check health before relaunching to avoid duplicate startup attempts." + (string.IsNullOrWhiteSpace(_lastLocalStderrTail) ? string.Empty : (" Last llama-server output: " + _lastLocalStderrTail))
			};
		}
		return new RuntimeActionResult
		{
			IsSuccess = false,
			Status = "Local launch timeout",
			Details = "llama-server did not finish launching within its previous manual launch timeframe for this model profile (" + FormatDurationMs((estimatedLaunchMs > 0) ? estimatedLaunchMs : launchBudgetMs) + " observed baseline); VRAM availability or other conditions may have changed. " + (string.IsNullOrWhiteSpace(_lastLocalStderrTail) ? string.Empty : (" Last llama-server output: " + _lastLocalStderrTail))
		};
		}
		return new RuntimeActionResult
		{
			IsSuccess = false,
			Status = "Local launch failed",
			Details = "llama-server exited during startup." + (string.IsNullOrWhiteSpace(_lastLocalStderrTail) ? string.Empty : (" Last llama-server output: " + _lastLocalStderrTail))
		};
	}

	public async Task<RuntimeActionResult> RestartLocalAsync(string selectedLocalModel, string selectedVariant, string modelDirectory, int port, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge, CancellationToken cancellationToken = default(CancellationToken), Action<string>? progressReporter = null)
	{
		int previousPid = ManagedLocalServerPid;
		int backendPort = port + 1;
		progressReporter?.Invoke("AI-FluxMux is stopping llama-server");
		await StopLocalAsync();
		if (backendPort > 0 && backendPort <= 65535)
		{
			await WaitForPortToCloseAsync(backendPort, cancellationToken, 15000);
			if (await IsLlamaEndpointReadyAsync(backendPort, cancellationToken) && TryStopLocalEndpointOwnerByPort(backendPort, out int _, out string _))
			{
				await WaitForPortToCloseAsync(backendPort, cancellationToken, 15000);
			}
			if (await IsLlamaEndpointReadyAsync(backendPort, cancellationToken) || IsTcpPortOpen("127.0.0.1", backendPort))
			{
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local launch failed",
					Details = "The previous local model was still occupying the daemon port (private listen port for llama-server, usually Port+1) after stop, so this trial would have reused a cached process. Stop that process and try AutoTune again."
				};
			}
		}
		progressReporter?.Invoke("AI-FluxMux is starting llama-server");
		RuntimeActionResult launch = await LaunchLocalAsync(selectedLocalModel, selectedVariant, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken, progressReporter);
		if (!launch.IsSuccess)
		{
			return launch;
		}
		int newPid = ManagedLocalServerPid;
		if (previousPid > 0 && newPid == previousPid)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch failed",
				Details = "Reload reused the same llama-server process, so the trial settings and timings would not be trustworthy."
			};
		}
		return new RuntimeActionResult
		{
			IsSuccess = true,
			Status = launch.Status,
			Details = launch.Details + ((previousPid > 0) ? $" Reloaded from PID {previousPid} to PID {((newPid > 0) ? newPid.ToString() : "unknown")}." : ((newPid > 0) ? $" Started llama-server PID {newPid}." : string.Empty))
		};
	}

	public async Task<RuntimeActionResult> RunSwitchBenchmarkAsync(string cloudProvider, string cloudModel, string selectedLocalModel, string selectedVariant, string modelDirectory, int port, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge, string benchmarkFocus, Action<string>? progressReporter = null, CancellationToken cancellationToken = default(CancellationToken), SwitchBenchmarkRouteSelection? profileA = null, SwitchBenchmarkRouteSelection? profileB = null)
	{
		string focusText = (string.IsNullOrWhiteSpace(benchmarkFocus) ? "Preserve relevant task context across local and cloud switches, filter iterative background chatter from both sides, and compress expanded context back into local chat without meaningful loss or disconnections." : benchmarkFocus.Trim());
		JsonObject configRoot = LoadJsonCached(_configPath);
		(RouteSlotDescriptor Selection1, RouteSlotDescriptor Selection2)? slotPair = (((object)profileA != null && (object)profileB != null) ? new(RouteSlotDescriptor, RouteSlotDescriptor)?((BuildBenchmarkProfileDescriptor(profileA, "Profile A"), BuildBenchmarkProfileDescriptor(profileB, "Profile B"))) : ResolveBenchmarkRoutePair(configRoot, cloudProvider, cloudModel, selectedLocalModel, selectedVariant));
		if (!slotPair.HasValue)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Switch benchmark failed",
				Details = "Could not resolve two model profiles for switch testing."
			};
		}
		RouteSlotDescriptor slot1 = slotPair.Value.Selection1;
		RouteSlotDescriptor slot2 = slotPair.Value.Selection2;
		if (!slot1.IsValid || !slot2.IsValid)
		{
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Switch benchmark failed",
				Details = "Both Profile A and Profile B must have valid route details before running switch benchmark."
			};
		}
		RouteSlotDescriptor benchmarkCloud = new RouteSlotDescriptor[2] { slot1, slot2 }.FirstOrDefault((RouteSlotDescriptor routeSlotDescriptor) => routeSlotDescriptor.RouteType.Equals("cloud", StringComparison.OrdinalIgnoreCase));
		RouteSlotDescriptor benchmarkLocal = new RouteSlotDescriptor[2] { slot1, slot2 }.FirstOrDefault((RouteSlotDescriptor routeSlotDescriptor) => routeSlotDescriptor.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase));
		string benchmarkCloudProvider = benchmarkCloud.CloudProvider ?? cloudProvider;
		string benchmarkCloudModel = benchmarkCloud.CloudModel ?? cloudModel;
		string activeCloudVariant = FirstNonEmpty((profileA?.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ?? false) ? profileA.Variant : (profileB?.Variant ?? string.Empty), GetString(configRoot, "ActiveCloudVariant"), "Variant 1");
		string benchmarkLocalModel = benchmarkLocal.LocalModel ?? selectedLocalModel;
		string benchmarkLocalVariant = benchmarkLocal.LocalVariant ?? selectedVariant;
		JsonObject localVariantSettings = ResolveLocalProfile(configRoot, benchmarkLocalModel, benchmarkLocalVariant);
		JsonObject cloudVariantSettings = ResolveCloudProfile(configRoot, benchmarkCloudProvider, benchmarkCloudModel, activeCloudVariant);
		ContextFidelityTierDefinition[] tierDefinitions = GetContextFidelityTierDefinitions();
		List<ContextFidelityTierResult> tierResults = new List<ContextFidelityTierResult>();
		bool restoreSelection1Needed = true;
		RuntimeActionResult result;
		try
		{
			tierResults.AddRange(await RunBatchedContextFidelityTiersAsync(tierDefinitions, slot1, slot2, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, focusText, progressReporter, cancellationToken));
			ContextFidelityTierResult blockedTier = tierResults.FirstOrDefault((ContextFidelityTierResult contextFidelityTierResult) => !contextFidelityTierResult.IsSuccess);
			if ((object)blockedTier != null)
			{
				string blockerSummary = BuildSwitchBenchmarkBlockerSummary(blockedTier, slot1, slot2);
				bool probeFailure = blockedTier.Status.Equals("Probe failed", StringComparison.OrdinalIgnoreCase);
				string blockedStatus = ((!blockerSummary.Contains("Profile B", StringComparison.OrdinalIgnoreCase)) ? (probeFailure ? "Switch benchmark blocked: Profile A chat probe failed" : "Switch benchmark blocked: Profile A route did not become ready") : (probeFailure ? "Switch benchmark blocked: Profile B chat probe failed" : "Switch benchmark blocked: Profile B route did not become ready"));
				RuntimeActionResult failure = new RuntimeActionResult
				{
					IsSuccess = false,
					Status = blockedStatus,
					Details = blockerSummary + Environment.NewLine + "Technical detail: " + blockedTier.Details
				};
				AppendSwitchBenchmarkLog(benchmarkCloudProvider, benchmarkCloudModel, activeCloudVariant, benchmarkLocalModel, benchmarkLocalVariant, localVariantSettings, cloudVariantSettings, port, focusText, tierResults);
				result = failure;
			}
			else
			{
				bool overallPassed = tierResults.All((ContextFidelityTierResult x) => x.IsSuccess && x.Passed);
				StringBuilder overallSummary = new StringBuilder();
				overallSummary.AppendLine("Context fidelity tiers completed.");
				foreach (ContextFidelityTierResult tierResult in tierResults)
				{
					overallSummary.AppendLine(BuildTierResultSummary(tierResult));
				}
				StringBuilder stringBuilder = overallSummary;
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
				handler.AppendLiteral("Focus: ");
				handler.AppendFormatted(focusText);
				stringBuilder2.AppendLine(ref handler);
				StringBuilder advancedSummary = new StringBuilder();
				advancedSummary.AppendLine("Benchmark diagnostics");
				stringBuilder = advancedSummary;
				StringBuilder stringBuilder3 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(24, 3, stringBuilder);
				handler.AppendLiteral("Route sequence: ");
				handler.AppendFormatted(slot1.DisplayLabel);
				handler.AppendLiteral(" -> ");
				handler.AppendFormatted(slot2.DisplayLabel);
				handler.AppendLiteral(" -> ");
				handler.AppendFormatted(slot1.DisplayLabel);
				stringBuilder3.AppendLine(ref handler);
				stringBuilder = advancedSummary;
				StringBuilder stringBuilder4 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder);
				handler.AppendLiteral("Public endpoint: http://127.0.0.1:");
				handler.AppendFormatted(port);
				stringBuilder4.AppendLine(ref handler);
				ContextFidelityTierResult firstCompletedTier = tierResults.FirstOrDefault();
				if ((object)firstCompletedTier != null)
				{
					advancedSummary.AppendLine("Profile A initial launch: " + firstCompletedTier.LocalLaunchDetails);
					advancedSummary.AppendLine("Profile B launch: " + firstCompletedTier.CloudLaunchDetails);
					advancedSummary.AppendLine("Profile A return launch: " + firstCompletedTier.LocalReturnDetails);
				}
				foreach (ContextFidelityTierResult tierResult2 in tierResults)
				{
					advancedSummary.AppendLine(BuildTierSummary(tierResult2, focusText));
				}
				AppendSwitchBenchmarkLog(benchmarkCloudProvider, benchmarkCloudModel, activeCloudVariant, benchmarkLocalModel, benchmarkLocalVariant, localVariantSettings, cloudVariantSettings, port, focusText, tierResults);
				restoreSelection1Needed = false;
				result = new RuntimeActionResult
				{
					IsSuccess = overallPassed,
					Status = (overallPassed ? "Switch benchmark complete" : "Switch benchmark completed with fidelity warnings"),
					Details = overallSummary.ToString().Trim(),
					AdvancedDetails = advancedSummary.ToString().Trim()
				};
			}
		}
		finally
		{
			if (restoreSelection1Needed)
			{
				try
				{
					await StopCloudAsync();
					await StopLocalAsync();
					await WaitForPortToCloseAsync(port + 1, CancellationToken.None);
					await LaunchRouteSlotAsync(slot1, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, CancellationToken.None);
				}
				catch
				{
				}
			}
		}
		return result;
	}

	private async Task<IReadOnlyList<ContextFidelityTierResult>> RunBatchedContextFidelityTiersAsync(IReadOnlyList<ContextFidelityTierDefinition> tiers, RouteSlotDescriptor slot1, RouteSlotDescriptor slot2, string modelDirectory, int port, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge, string focusText, Action<string>? progressReporter, CancellationToken cancellationToken)
	{
		List<ContextFidelityTierResult> results = new List<ContextFidelityTierResult>();
		Dictionary<int, ChatProbeResult> profileAProbes = new Dictionary<int, ChatProbeResult>();
		Dictionary<int, ChatProbeResult> profileBProbes = new Dictionary<int, ChatProbeResult>();
		string portLabel = $"http://127.0.0.1:{port}";
		int slot1WarmupBudgetMs = GetRouteWarmupBudgetMs(slot1);
		int slot2WarmupBudgetMs = GetRouteWarmupBudgetMs(slot2);
		await StopCloudAsync();
		await StopLocalAsync();
		await WaitForPortToCloseAsync(port + 1, cancellationToken);
		progressReporter?.Invoke("Switch benchmark running: launching Profile A for all fidelity tiers...");
		RuntimeActionResult profileALaunch = await LaunchRouteSlotAsync(slot1, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
		if (!profileALaunch.IsSuccess)
		{
			return new ContextFidelityTierResult[] { BuildTierFailure(tiers[0], portLabel, "profile-a-first", string.Empty, profileALaunch) };
		}
		foreach (ContextFidelityTierDefinition tier in tiers)
		{
			progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " checking Profile A route...");
			ChatProbeResult probe = await SendChatProbeWithWarmupRetryAsync(port, slot1.ProbeModelLabel, BuildSwitchBenchmarkMessages(focusText, tier.Level, "profile-a-first"), "profile-a-first", slot1WarmupBudgetMs, progressReporter, cancellationToken, slot1.RouteType);
			if (!probe.IsSuccess)
			{
				List<ContextFidelityTierResult> list = results;
				int writeOffset = 0;
				ContextFidelityTierResult[] array = new ContextFidelityTierResult[1 + list.Count];
				Span<ContextFidelityTierResult> span = CollectionsMarshal.AsSpan(list);
				span.CopyTo(new Span<ContextFidelityTierResult>(array).Slice(writeOffset, span.Length));
				writeOffset += span.Length;
				array[writeOffset] = BuildTierFailure(tier, portLabel, "profile-a-first", profileALaunch.Details, probe);
				return array;
			}
			profileAProbes[tier.Level] = probe;
		}
		await StopRouteSlotAsync(slot1);
		if (slot1.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			await WaitForPortToCloseAsync(port + 1, cancellationToken);
		}
		progressReporter?.Invoke("Switch benchmark running: launching Profile B for all fidelity tiers...");
		RuntimeActionResult profileBLaunch = await LaunchRouteSlotAsync(slot2, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
		if (!profileBLaunch.IsSuccess)
		{
			return new ContextFidelityTierResult[] { BuildTierFailure(tiers[0], portLabel, "profile-b-switch", profileALaunch.Details, profileBLaunch) };
		}
		foreach (ContextFidelityTierDefinition tier2 in tiers)
		{
			progressReporter?.Invoke("Switch benchmark running: " + tier2.Name + " checking Profile B route...");
			ChatProbeResult probe2 = await SendChatProbeWithWarmupRetryAsync(port, slot2.ProbeModelLabel, BuildSwitchBenchmarkMessages(focusText, tier2.Level, "profile-b-switch", profileAProbes[tier2.Level].Content), "profile-b-switch", slot2WarmupBudgetMs, progressReporter, cancellationToken, slot2.RouteType);
			if (!probe2.IsSuccess)
			{
				return new ContextFidelityTierResult[] { BuildTierFailure(tier2, portLabel, "profile-b-switch", profileBLaunch.Details, probe2) };
			}
			profileBProbes[tier2.Level] = probe2;
		}
		await StopRouteSlotAsync(slot2);
		if (slot2.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			await WaitForPortToCloseAsync(port + 1, cancellationToken);
		}
		progressReporter?.Invoke("Switch benchmark running: returning to Profile A for all fidelity tiers...");
		RuntimeActionResult profileAReturnLaunch = await LaunchRouteSlotAsync(slot1, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
		if (!profileAReturnLaunch.IsSuccess)
		{
			return new ContextFidelityTierResult[] { BuildTierFailure(tiers[0], portLabel, "profile-a-return", profileBLaunch.Details, profileAReturnLaunch) };
		}
		foreach (ContextFidelityTierDefinition tier3 in tiers)
		{
			progressReporter?.Invoke("Switch benchmark running: " + tier3.Name + " checking Profile A return route...");
			ChatProbeResult returnProbe = await SendChatProbeWithWarmupRetryAsync(port, slot1.ProbeModelLabel, BuildSwitchBenchmarkMessages(focusText, tier3.Level, "profile-a-return", profileBProbes[tier3.Level].Content), "profile-a-return", slot1WarmupBudgetMs, progressReporter, cancellationToken, slot1.RouteType);
			if (!returnProbe.IsSuccess)
			{
				List<ContextFidelityTierResult> list = results;
				int writeOffset = 0;
				ContextFidelityTierResult[] array = new ContextFidelityTierResult[1 + list.Count];
				Span<ContextFidelityTierResult> span = CollectionsMarshal.AsSpan(list);
				span.CopyTo(new Span<ContextFidelityTierResult>(array).Slice(writeOffset, span.Length));
				writeOffset += span.Length;
				array[writeOffset] = BuildTierFailure(tier3, portLabel, "profile-a-return", profileAReturnLaunch.Details, returnProbe);
				return array;
			}
			ChatProbeResult profileAProbe = profileAProbes[tier3.Level];
			ChatProbeResult profileBProbe = profileBProbes[tier3.Level];
			ContextFidelityScore score = ComputeAnchorScore(focusText, returnProbe.Content);
			int requiredEssentialHits = Math.Max(1, (int)Math.Ceiling((double)score.EssentialTotal * ((tier3.Level == 1) ? 0.7 : 0.8)));
			bool passed = score.EssentialHits >= requiredEssentialHits && score.NoiseHits <= tier3.MaxNoiseHits;
			long averageLatency = Convert.ToInt64(Math.Round((double)(profileAProbe.LatencyMs + profileBProbe.LatencyMs + returnProbe.LatencyMs) / 3.0, 0));
			string profileADelay = FormatDurationMs((int)Math.Min(2147483647L, Math.Max(0L, profileAProbe.ProbeWindowMs)));
			string profileBDelay = FormatDurationMs((int)Math.Min(2147483647L, Math.Max(0L, profileBProbe.ProbeWindowMs)));
			string profileAReturnDelay = FormatDurationMs((int)Math.Min(2147483647L, Math.Max(0L, returnProbe.ProbeWindowMs)));
			results.Add(new ContextFidelityTierResult(tier3.Level, tier3.Name, passed ? "Pass" : "Needs attention", passed, IsSuccess: true, score.EssentialHits, score.EssentialTotal, score.NoiseHits, score.NoiseTotal, profileALaunch.Details, profileBLaunch.Details, profileAReturnLaunch.Details, profileAProbe.Content, profileBProbe.Content, averageLatency, profileAProbe.PromptTokens.GetValueOrDefault() + profileBProbe.PromptTokens.GetValueOrDefault() + returnProbe.PromptTokens.GetValueOrDefault(), $"{portLabel}; Profile A {profileAProbe.LatencyMs}ms, Profile B {profileBProbe.LatencyMs}ms, Profile A return {returnProbe.LatencyMs}ms. Probe durations: Profile A {profileADelay}, Profile B {profileBDelay}, Profile A return {profileAReturnDelay}. Batched lifecycle: both fidelity tiers ran per loaded route, requiring three route loads instead of five. Chat handoff: Profile A response was transferred to Profile B, then Profile B response was transferred to returning Profile A." + (passed ? string.Empty : $" Thresholds unmet for {tier3.Name}: final-return essential {score.EssentialHits}/{requiredEssentialHits}, distractors {score.NoiseHits}/{tier3.MaxNoiseHits}.")));
		}
		return results;
	}

	private async Task<ContextFidelityTierResult> RunRouteAgnosticContextFidelityTierAsync(ContextFidelityTierDefinition tier, RouteSlotDescriptor slot1, RouteSlotDescriptor slot2, string modelDirectory, int port, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge, string focusText, Action<string>? progressReporter, CancellationToken cancellationToken, bool profileAAlreadyReady)
	{
		string portLabel = $"http://127.0.0.1:{port}";
		int slot1WarmupBudgetMs = GetRouteWarmupBudgetMs(slot1);
		int slot2WarmupBudgetMs = GetRouteWarmupBudgetMs(slot2);
		_ = string.Empty;
		_ = string.Empty;
		string selection1LaunchDetails;
		if (!profileAAlreadyReady)
		{
			await StopCloudAsync();
			await StopLocalAsync();
			await WaitForPortToCloseAsync(port + 1, cancellationToken);
			progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " launching Profile A route...");
			RuntimeActionResult selection1Launch = await LaunchRouteSlotAsync(slot1, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
			if (!selection1Launch.IsSuccess)
			{
				return new ContextFidelityTierResult(tier.Level, tier.Name, selection1Launch.Status, Passed: false, IsSuccess: false, 0, 0, 0, 0, selection1Launch.Details, string.Empty, string.Empty, string.Empty, string.Empty, 0L, 0, selection1Launch.Details);
			}
			selection1LaunchDetails = selection1Launch.Details;
		}
		else
		{
			selection1LaunchDetails = "Reused Profile A already ready from the preceding fidelity tier.";
			progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " reusing ready Profile A route...");
		}
		progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " checking Profile A route...");
		ChatProbeResult selection1Probe = await SendChatProbeWithWarmupRetryAsync(port, slot1.ProbeModelLabel, BuildSwitchBenchmarkMessages(focusText, tier.Level, "profile-a-first"), "profile-a-first", slot1WarmupBudgetMs, progressReporter, cancellationToken, slot1.RouteType);
		if (!selection1Probe.IsSuccess)
		{
			return BuildTierFailure(tier, portLabel, "profile-a-first", selection1LaunchDetails, selection1Probe);
		}
		await StopRouteSlotAsync(slot1);
		if (slot1.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			await WaitForPortToCloseAsync(port + 1, cancellationToken);
		}
		progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " launching Profile B route...");
		RuntimeActionResult selection2Launch = await LaunchRouteSlotAsync(slot2, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
		if (!selection2Launch.IsSuccess)
		{
			return BuildTierFailure(tier, portLabel, "profile-b-switch", selection1LaunchDetails, selection2Launch);
		}
		string selection2LaunchDetails = selection2Launch.Details;
		progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " checking Profile B route...");
		ChatProbeResult selection2Probe = await SendChatProbeWithWarmupRetryAsync(port, slot2.ProbeModelLabel, BuildSwitchBenchmarkMessages(focusText, tier.Level, "profile-b-switch", selection1Probe.Content), "profile-b-switch", slot2WarmupBudgetMs, progressReporter, cancellationToken, slot2.RouteType);
		if (!selection2Probe.IsSuccess)
		{
			return BuildTierFailure(tier, portLabel, "profile-b-switch", selection2LaunchDetails, selection2Probe);
		}
		await StopRouteSlotAsync(slot2);
		if (slot2.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			await WaitForPortToCloseAsync(port + 1, cancellationToken);
		}
		progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " relaunching Profile A route...");
		RuntimeActionResult selection1Relaunch = await LaunchRouteSlotAsync(slot1, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
		if (!selection1Relaunch.IsSuccess)
		{
			return BuildTierFailure(tier, portLabel, "profile-a-return", selection2LaunchDetails, selection1Relaunch);
		}
		string selection1ReturnDetails = selection1Relaunch.Details;
		progressReporter?.Invoke("Switch benchmark running: " + tier.Name + " checking Profile A return route...");
		ChatProbeResult selection1ReturnProbe = await SendChatProbeWithWarmupRetryAsync(port, slot1.ProbeModelLabel, BuildSwitchBenchmarkMessages(focusText, tier.Level, "profile-a-return", selection2Probe.Content), "profile-a-return", slot1WarmupBudgetMs, progressReporter, cancellationToken, slot1.RouteType);
		if (!selection1ReturnProbe.IsSuccess)
		{
			return BuildTierFailure(tier, portLabel, "profile-a-return", selection1ReturnDetails, selection1ReturnProbe);
		}
		ContextFidelityScore score = ComputeAnchorScore(focusText, selection1ReturnProbe.Content);
		double averageLatency = Math.Round((double)(selection1Probe.LatencyMs + selection2Probe.LatencyMs + selection1ReturnProbe.LatencyMs) / 3.0, 0);
		int requiredEssentialHits = Math.Max(1, (int)Math.Ceiling((double)score.EssentialTotal * ((tier.Level == 1) ? 0.7 : 0.8)));
		bool passed = score.EssentialHits >= requiredEssentialHits && score.NoiseHits <= tier.MaxNoiseHits;
		string selection1Delay = FormatDurationMs((int)Math.Min(2147483647L, Math.Max(0L, selection1Probe.ProbeWindowMs)));
		string selection2Delay = FormatDurationMs((int)Math.Min(2147483647L, Math.Max(0L, selection2Probe.ProbeWindowMs)));
		string selection1ReturnDelay = FormatDurationMs((int)Math.Min(2147483647L, Math.Max(0L, selection1ReturnProbe.ProbeWindowMs)));
		return new ContextFidelityTierResult(tier.Level, tier.Name, passed ? "Pass" : "Needs attention", passed, IsSuccess: true, score.EssentialHits, score.EssentialTotal, score.NoiseHits, score.NoiseTotal, selection1LaunchDetails, selection2LaunchDetails, selection1ReturnDetails, selection1Probe.Content, selection2Probe.Content, Convert.ToInt64(averageLatency), selection1Probe.PromptTokens.GetValueOrDefault() + selection2Probe.PromptTokens.GetValueOrDefault() + selection1ReturnProbe.PromptTokens.GetValueOrDefault(), $"{portLabel}; selection1 {selection1Probe.LatencyMs}ms, selection2 {selection2Probe.LatencyMs}ms, selection1-return {selection1ReturnProbe.LatencyMs}ms. Readiness delays: selection1-first {selection1Delay} ({Math.Max(1, selection1Probe.AttemptsUsed)} attempts), selection2-switch {selection2Delay} ({Math.Max(1, selection2Probe.AttemptsUsed)} attempts), selection1-return {selection1ReturnDelay} ({Math.Max(1, selection1ReturnProbe.AttemptsUsed)} attempts)." + " Chat handoff: Profile A response was transferred to Profile B, then Profile B response was transferred to returning Profile A." + (passed ? string.Empty : $". Thresholds unmet for {tier.Name}: final-return essential {score.EssentialHits}/{requiredEssentialHits}, chatter {score.NoiseHits}/{tier.MaxNoiseHits}."));
	}

	private async Task<RuntimeActionResult> LaunchRouteSlotAsync(RouteSlotDescriptor slot, string modelDirectory, int port, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge, CancellationToken cancellationToken)
	{
		if (slot.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			return await LaunchLocalAsync(slot.LocalModel, slot.LocalVariant, modelDirectory, port, localVisionEnabled, localVisionProjectorPath, localVisionMaxImageEdge, cancellationToken);
		}
		return await LaunchCloudAsync(slot.CloudProvider, slot.CloudModel, cancellationToken);
	}

	private async Task StopRouteSlotAsync(RouteSlotDescriptor slot)
	{
		if (slot.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			await StopLocalAsync();
		}
		else
		{
			await StopCloudAsync();
		}
	}

	private int GetRouteWarmupBudgetMs(RouteSlotDescriptor slot)
	{
		if (slot.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase))
		{
			return GetLocalLaunchWarmupBudgetMs(slot.LocalModel, slot.LocalVariant);
		}
		return 45000;
	}

	private (RouteSlotDescriptor Selection1, RouteSlotDescriptor Selection2)? ResolveBenchmarkRoutePair(JsonObject root, string fallbackCloudProvider, string fallbackCloudModel, string fallbackLocalModel, string fallbackLocalVariant)
	{
		if (root["RouteSlots"] is JsonArray { Count: >0 } jsonArray)
		{
			List<JsonObject> list = jsonArray.OfType<JsonObject>().ToList();
			List<string> list2 = ((root["ActiveRouteSlotIds"] is JsonArray source) ? (from x in source
				select x?.ToString() into x
				where !string.IsNullOrWhiteSpace(x)
				select x).Cast<string>().ToList() : new List<string>());
			List<JsonObject> list3 = new List<JsonObject>();
			foreach (string activeId in list2)
			{
				JsonObject jsonObject = list.FirstOrDefault((JsonObject x) => string.Equals(x["slotId"]?.ToString(), activeId, StringComparison.OrdinalIgnoreCase));
				if (jsonObject != null && ParseBool(jsonObject["enabled"]?.ToString() ?? "true", fallback: true))
				{
					list3.Add(jsonObject);
				}
			}
			foreach (JsonObject item5 in list)
			{
				if (list3.Count >= 2)
				{
					break;
				}
				if (ParseBool(item5["enabled"]?.ToString() ?? "true", fallback: true) && !list3.Contains(item5))
				{
					list3.Add(item5);
				}
			}
			if (list3.Count >= 2)
			{
				RouteSlotDescriptor item = BuildRouteSlotDescriptor(list3[0], "Selection 1");
				RouteSlotDescriptor item2 = BuildRouteSlotDescriptor(list3[1], "Selection 2");
				return (item, item2);
			}
		}
		RouteSlotDescriptor item3 = new RouteSlotDescriptor("fallback-selection-1", "Selection 1", "local", string.Empty, string.Empty, fallbackLocalModel, fallbackLocalVariant, "Selection 1 (local)", FirstNonEmpty(Path.GetFileNameWithoutExtension(fallbackLocalModel), Path.GetFileName(fallbackLocalModel), fallbackLocalModel), !string.IsNullOrWhiteSpace(fallbackLocalModel));
		RouteSlotDescriptor item4 = new RouteSlotDescriptor("fallback-selection-2", "Selection 2", "cloud", fallbackCloudProvider, fallbackCloudModel, string.Empty, string.Empty, "Selection 2 (cloud)", fallbackCloudModel, !string.IsNullOrWhiteSpace(fallbackCloudProvider) && !string.IsNullOrWhiteSpace(fallbackCloudModel));
		return (item3, item4);
	}

	private static RouteSlotDescriptor BuildBenchmarkProfileDescriptor(SwitchBenchmarkRouteSelection profile, string label)
	{
		bool isLocal = profile.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase);
		bool isValid = (isLocal ? (!string.IsNullOrWhiteSpace(profile.Model)) : (!string.IsNullOrWhiteSpace(profile.Provider) && !string.IsNullOrWhiteSpace(profile.Model)));
		return new RouteSlotDescriptor("switch-" + label.ToLowerInvariant().Replace(" ", "-"), label, isLocal ? "local" : "cloud", isLocal ? string.Empty : profile.Provider, isLocal ? string.Empty : profile.Model, isLocal ? profile.Model : string.Empty, isLocal ? profile.Variant : string.Empty, profile.DisplayName, isLocal ? FirstNonEmpty(Path.GetFileNameWithoutExtension(profile.Model), Path.GetFileName(profile.Model), profile.Model) : profile.Model, isValid);
	}

	private static RouteSlotDescriptor BuildRouteSlotDescriptor(JsonObject slotNode, string defaultLabel)
	{
		string routeType = FirstNonEmpty(slotNode["routeType"]?.ToString() ?? string.Empty, "local");
		string label = FirstNonEmpty(slotNode["label"]?.ToString() ?? string.Empty, defaultLabel);
		string provider = FirstNonEmpty(slotNode["provider"]?.ToString() ?? string.Empty, string.Empty);
		string cloudModel = FirstNonEmpty(slotNode["cloudModel"]?.ToString() ?? string.Empty, string.Empty);
		string localModel = FirstNonEmpty(slotNode["localModel"]?.ToString() ?? string.Empty, string.Empty);
		string localVariant = FirstNonEmpty(slotNode["localVariant"]?.ToString() ?? string.Empty, string.Empty);
		bool isLocal = routeType.Equals("local", StringComparison.OrdinalIgnoreCase);
		bool isValid = (isLocal ? (!string.IsNullOrWhiteSpace(localModel)) : (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(cloudModel)));
		string probeModelLabel = (isLocal ? FirstNonEmpty(Path.GetFileNameWithoutExtension(localModel), Path.GetFileName(localModel), localModel) : cloudModel);
		string displayLabel = (isLocal ? (label + " (local: " + Path.GetFileName(localModel) + ")") : $"{label} (cloud: {provider} / {cloudModel})");
		return new RouteSlotDescriptor(FirstNonEmpty(slotNode["slotId"]?.ToString() ?? string.Empty, defaultLabel.ToLowerInvariant().Replace(" ", "-")), label, routeType, provider, cloudModel, localModel, localVariant, displayLabel, probeModelLabel, isValid);
	}

	private static async Task<bool> IsLlamaEndpointReadyAsync(int port, CancellationToken cancellationToken)
	{
		if (port < 1 || port > 65535)
		{
			return false;
		}
		if (!IsTcpPortOpen("127.0.0.1", port))
		{
			return false;
		}
		string[] endpoints = new string[2]
		{
			$"http://127.0.0.1:{port}/health",
			$"http://127.0.0.1:{port}/v1/models"
		};
		string[] array = endpoints;
		foreach (string endpoint in array)
		{
			try
			{
				using HttpResponseMessage response = await HttpClient.GetAsync(endpoint, cancellationToken);
				// 503 = llama-server is up but busy (no free slot). That is not "down".
				if ((response.StatusCode >= HttpStatusCode.OK && response.StatusCode < HttpStatusCode.InternalServerError)
					|| response.StatusCode == HttpStatusCode.ServiceUnavailable)
				{
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	private static int[] BuildLocalEndpointDiscoveryCandidates(int preferredPort)
	{
		int[] obj = new int[12]
		{
			0, 11434, 8080, 8000, 7860, 5000, 3000, 1234, 1337, 4173,
			9000, 9090
		};
		obj[0] = preferredPort;
		int[] first = obj;
		IEnumerable<int> second = (from port in GetLoopbackListeningPorts()
			where port >= 1 && port <= 65535
			select port).Take(16);
		return (from port in first.Concat(second)
			where port >= 1 && port <= 65535
			select port).Distinct().ToArray();
	}

	private static IEnumerable<int> GetLoopbackListeningPorts()
	{
		try
		{
			IPGlobalProperties iPGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
			IPEndPoint[] activeTcpListeners = iPGlobalProperties.GetActiveTcpListeners();
			return (from port in (from endpoint in activeTcpListeners
					where endpoint?.Address != null
					where endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Address.Equals(IPAddress.IPv6Loopback)
					where endpoint.Port >= 1024
					select endpoint.Port).Distinct()
				orderby port
				select port).ToList();
		}
		catch
		{
			return Array.Empty<int>();
		}
	}

	private static async Task<(bool Compatible, string Detail)> ProbeLocalEndpointCompatibilityAsync(int port, CancellationToken cancellationToken, int timeoutMs)
	{
		string baseUrl = $"http://127.0.0.1:{port}";
		using (CancellationTokenSource probeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			probeTimeoutCts.CancelAfter(timeoutMs);
			try
			{
				using HttpResponseMessage healthResponse = await HttpClient.GetAsync(baseUrl + "/health", probeTimeoutCts.Token);
				int healthCode = (int)healthResponse.StatusCode;
				if (healthCode >= 500)
				{
					return (Compatible: false, Detail: $"/health returned HTTP {healthCode}");
				}
			}
			catch
			{
			}
			try
			{
				using HttpResponseMessage modelsResponse = await HttpClient.GetAsync(baseUrl + "/v1/models", probeTimeoutCts.Token);
				int modelsCode = (int)modelsResponse.StatusCode;
				string raw = await modelsResponse.Content.ReadAsStringAsync(probeTimeoutCts.Token);
				if (modelsCode < 200 || modelsCode >= 300)
				{
					return (Compatible: false, Detail: $"/v1/models returned HTTP {modelsCode}");
				}
				if (string.IsNullOrWhiteSpace(raw))
				{
					return (Compatible: false, Detail: "/v1/models returned empty response");
				}
				if (!((JsonNode.Parse(raw) as JsonObject)?["data"] is JsonArray { Count: var count }))
				{
					return (Compatible: false, Detail: "/v1/models response missing data[] payload");
				}
				return (Compatible: true, Detail: $"/v1/models responded with {count} model entry(ies)");
			}
			catch (HttpRequestException)
			{
				return (Compatible: false, Detail: "/v1/models probe failed: endpoint is not serving plain HTTP on this port");
			}
			catch (Exception ex2)
			{
				try
				{
					using HttpResponseMessage ollamaTags = await HttpClient.GetAsync(baseUrl + "/api/tags", probeTimeoutCts.Token);
					int code = (int)ollamaTags.StatusCode;
					string raw2 = await ollamaTags.Content.ReadAsStringAsync(probeTimeoutCts.Token);
					if (code >= 200 && code < 300)
					{
						int count2 = ((JsonNode.Parse(raw2) as JsonObject)?["models"] as JsonArray)?.Count ?? 0;
						return (Compatible: false, Detail: $"Detected Ollama-style endpoint (/api/tags with {count2} model entry(ies)); not directly compatible with AI-FluxMux OpenAI-style local route yet.");
					}
				}
				catch
				{
				}
				if (probeTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
				{
					return (Compatible: false, Detail: "Compatibility probe timed out after " + FormatDurationMs(timeoutMs));
				}
				return (Compatible: false, Detail: "/v1/models probe failed: " + ex2.GetType().Name);
			}
		}
	}

	private static async Task<bool> IsLocalEndpointReachableQuickAsync(int port, CancellationToken cancellationToken)
	{
		if (port < 1 || port > 65535)
		{
			return false;
		}
		if (!IsTcpPortOpen("127.0.0.1", port))
		{
			return false;
		}
		string[] endpoints = new string[2]
		{
			$"http://127.0.0.1:{port}/health",
			$"http://127.0.0.1:{port}/v1/models"
		};
		string[] array = endpoints;
		foreach (string endpoint in array)
		{
			try
			{
				using HttpResponseMessage response = await LocalEndpointReadinessClient.GetAsync(endpoint, cancellationToken);
				if (response.StatusCode >= HttpStatusCode.OK && response.StatusCode < HttpStatusCode.InternalServerError)
				{
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	private async Task<ChatProbeResult> SendChatProbeAsync(int port, string model, JsonArray messages, int requestTimeoutMs, CancellationToken cancellationToken, int maxTokens = 256, string? forceRoute = null)
	{
		string endpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
		if (!(await IsLocalEndpointReachableQuickAsync(port, cancellationToken)))
		{
			return new ChatProbeResult
			{
				IsSuccess = false,
				LatencyMs = 0L,
				Content = string.Empty,
				Details = $"Endpoint not responding on http://127.0.0.1:{port} yet; skipped chat probe to avoid waiting for an unstarted local server."
			};
		}
		JsonObject payload = new JsonObject
		{
			["model"] = (string.IsNullOrWhiteSpace(model) ? "benchmark-model" : model),
			["stream"] = false,
			["temperature"] = 0.2,
			["max_tokens"] = Math.Clamp(maxTokens, 1, 4096),
			["messages"] = messages.DeepClone()
		};
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
		};
		request.Headers.ConnectionClose = true;
		if (!string.IsNullOrWhiteSpace(forceRoute))
		{
			request.Headers.TryAddWithoutValidation(CloudEndpointValidationProbe.ForceRouteHeader, forceRoute.Trim());
		}
		Stopwatch sw = Stopwatch.StartNew();
		try
		{
			using CancellationTokenSource probeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			probeTimeoutCts.CancelAfter(requestTimeoutMs);
			using HttpResponseMessage response = await ProbeHttpClient.SendAsync(request, probeTimeoutCts.Token);
			string raw = await response.Content.ReadAsStringAsync(probeTimeoutCts.Token);
			sw.Stop();
			if (!response.IsSuccessStatusCode)
			{
				return new ChatProbeResult
				{
					IsSuccess = false,
					LatencyMs = sw.ElapsedMilliseconds,
					Content = string.Empty,
					Details = HttpErrorResponseFormatter.FormatHttpErrorDetail((int)response.StatusCode, response.ReasonPhrase, raw)
				};
			}
			JsonObject parsed = JsonNode.Parse(raw) as JsonObject;
			JsonObject choice = parsed?["choices"]?[0] as JsonObject;
			JsonObject message = choice?["message"] as JsonObject;
			string content = FirstNonEmpty(message?["content"]?.ToString() ?? string.Empty, message?["reasoning_content"]?.ToString() ?? string.Empty, choice?["text"]?.ToString() ?? string.Empty, raw);
			return new ChatProbeResult
			{
				IsSuccess = true,
				LatencyMs = sw.ElapsedMilliseconds,
				Content = TruncateTail(content, 1200),
				PromptTokens = ParseNullableInt(parsed?["usage"]?["prompt_tokens"]?.ToString()),
				CompletionTokens = ParseNullableInt(parsed?["usage"]?["completion_tokens"]?.ToString())
			};
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			sw.Stop();
			return new ChatProbeResult
			{
				IsSuccess = false,
				LatencyMs = sw.ElapsedMilliseconds,
				Content = string.Empty,
				Details = "Probe request timed out after " + FormatDurationMs(requestTimeoutMs) + " waiting for chat completion."
			};
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			sw.Stop();
			return new ChatProbeResult
			{
				IsSuccess = false,
				LatencyMs = sw.ElapsedMilliseconds,
				Content = string.Empty,
				Details = ex3.GetType().Name + ": " + ex3.GetBaseException().Message
			};
		}
	}

	private async Task<ChatProbeResult> SendEndpointValidationProbeAsync(int port, string model, CancellationToken cancellationToken)
	{
		string endpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
		JsonObject payload = CloudEndpointValidationProbe.CreatePayload(model);
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
		};
		request.Headers.TryAddWithoutValidation(
			CloudEndpointValidationProbe.ForceRouteHeader,
			FluxMuxGatewayRouting.Cloud);
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(45L));
			using HttpResponseMessage response = await ProbeHttpClient.SendAsync(request, timeoutCts.Token);
			string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
			stopwatch.Stop();
			return new ChatProbeResult
			{
				IsSuccess = response.IsSuccessStatusCode,
				LatencyMs = stopwatch.ElapsedMilliseconds,
				Details = (response.IsSuccessStatusCode ? string.Empty : HttpErrorResponseFormatter.FormatHttpErrorDetail((int)response.StatusCode, response.ReasonPhrase, body))
			};
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			stopwatch.Stop();
			return new ChatProbeResult
			{
				IsSuccess = false,
				LatencyMs = stopwatch.ElapsedMilliseconds,
				Details = "The upstream chat probe timed out after 45 seconds."
			};
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			stopwatch.Stop();
			return new ChatProbeResult
			{
				IsSuccess = false,
				LatencyMs = stopwatch.ElapsedMilliseconds,
				Details = ex3.Message
			};
		}
	}

	private static ContextFidelityTierDefinition[] GetContextFidelityTierDefinitions()
	{
		return new ContextFidelityTierDefinition[2]
		{
			new ContextFidelityTierDefinition(1, "Level 1 context fidelity", 9, 2),
			new ContextFidelityTierDefinition(2, "Level 2 context fidelity", 11, 3)
		};
	}

	private static JsonArray BuildSwitchBenchmarkMessages(string focusText, int level, string phase, string carriedHandoff = "")
	{
		string focus = (string.IsNullOrWhiteSpace(focusText) ? "Preserve relevant context while switching between two model profiles and returning to the first without meaningful loss or disconnection." : focusText.Trim());
		string systemPrompt = ((level == 1) ? "You are benchmarking level 1 context fidelity. Ignore unrelated chatter, keep task-essential content, and compress it into a clean memory capsule." : "You are benchmarking level 2 context fidelity. Preserve task-essential content through a noisier multi-step history, filter background chatter from both sides, and avoid inventing new facts.");
		JsonArray jsonArray;
		switch (phase)
		{
		case "profile-a-first":
		{
			JsonArray jsonArray2 = new JsonArray();
			jsonArray2.Add(new JsonObject
			{
				["role"] = "system",
				["content"] = systemPrompt
			});
			JsonArray jsonArray5 = jsonArray2;
			JsonObject jsonObject = new JsonObject { ["role"] = "user" };
			JsonObject jsonObject4 = jsonObject;
			IReadOnlyList<string> chatterLines3;
			if (level != 1)
			{
				IReadOnlyList<string> readOnlyList = new string[] { "Aside: there were repeated confirmations, minor rewrites, and a few off-topic comments between checkpoints.", "Task essential: preserve the continuity cue, profile switching goal, and loss-minimization requirement intact.", "Task essential: keep Profile A from losing context when the benchmark returns after the Profile B pass." };
				chatterLines3 = readOnlyList;
			}
			else
			{
				IReadOnlyList<string> readOnlyList = new string[] { "Aside: lunch and unrelated setup chatter happened between checkpoints.", "Task essential: keep the continuity cue, profile switching goal, and loss-minimization requirement intact." };
				chatterLines3 = readOnlyList;
			}
			jsonObject4["content"] = BuildMessyContextTranscript(focus, level, "We need to compare two model profiles while preserving the original task thread.", chatterLines3, (level == 1) ? "First pass: produce a short memory capsule that filters noise and keeps the task essentials only." : "First pass: produce a compact memory capsule, then restate it with one extra sentence explaining what should survive the next switch.");
			jsonArray5.Add(jsonObject);
			jsonArray2.Add(new JsonObject
			{
				["role"] = "assistant",
				["content"] = "Understood. I will keep task essentials and discard background chatter."
			});
			jsonArray2.Add(new JsonObject
			{
				["role"] = "user",
				["content"] = ((level == 1) ? "Now restate the memory capsule as a compact checklist with no more than five bullets. Keep profile, switch, context, preserve, and loss if relevant." : "Now restate the memory capsule as a compact checklist with the task essential thread preserved and background chatter stripped out. Include the model-switch purpose, the continuity requirement, and the need to rehydrate context on return to Profile A.")
			});
			jsonArray = jsonArray2;
			break;
		}
		case "profile-b-switch":
		{
			JsonArray jsonArray2 = new JsonArray();
			jsonArray2.Add(new JsonObject
			{
				["role"] = "system",
				["content"] = ((level == 1) ? "You are carrying a context capsule across a route switch. Remove chatter, keep the task essential, and do not invent new facts." : "You are carrying a noisier context capsule across a route switch. Filter iterative chatter from both sides, preserve the task thread, and do not invent new facts.")
			});
			JsonArray jsonArray4 = jsonArray2;
			JsonObject jsonObject = new JsonObject { ["role"] = "user" };
			JsonObject jsonObject3 = jsonObject;
			IReadOnlyList<string> chatterLines2;
			if (level != 1)
			{
				IReadOnlyList<string> readOnlyList = new string[] { "Ignore the background chatter about retries, unrelated diagnostics, and off-topic commentary.", "Task essential: preserve the continuity thread and note the different saved settings or model capabilities under comparison.", "Task essential: keep the user's original request intact so it can return to Profile A later." };
				chatterLines2 = readOnlyList;
			}
			else
			{
				IReadOnlyList<string> readOnlyList = new string[] { "Ignore the background chatter about retries, unrelated diagnostics, and off-topic commentary.", "Task essential: preserve the continuity thread and the reason for comparing the two profiles." };
				chatterLines2 = readOnlyList;
			}
			jsonObject3["content"] = BuildMessyContextTranscript(focus, level, "The session is switching from Profile A to Profile B to test context continuity across saved model settings.", chatterLines2, (level == 1) ? "Compress the capsule again for Profile B, using a short paragraph and a one-line memory note." : "Compress the capsule again for Profile B, using a short paragraph, a one-line memory note, and a brief note about what should survive the return to Profile A.");
			jsonArray4.Add(jsonObject);
			jsonArray2.Add(new JsonObject
			{
				["role"] = "assistant",
				["content"] = "Acknowledged. I will preserve the task thread while filtering noise."
			});
			jsonArray2.Add(new JsonObject
			{
				["role"] = "user",
				["content"] = ((level == 1) ? "Restate the retained essentials in a way that would survive the switch back to Profile A." : "Restate the retained essentials in a way that would survive the switch back to Profile A. Keep the noise-filtering rule explicit and avoid drifting into background chatter.")
			});
			jsonArray = jsonArray2;
			break;
		}
		case "profile-a-return":
		{
			JsonArray jsonArray2 = new JsonArray();
			jsonArray2.Add(new JsonObject
			{
				["role"] = "system",
				["content"] = ((level == 1) ? "You are returning to Profile A after a Profile B pass. Reconstruct the essential task context, filter iterative chatter, and keep the chain of intent clear." : "You are returning to Profile A after a Profile B pass with a longer background history. Reconstruct the essential task context, strip chatter from both sides, and keep the chain of intent clear.")
			});
			JsonArray jsonArray3 = jsonArray2;
			JsonObject jsonObject = new JsonObject { ["role"] = "user" };
			JsonObject jsonObject2 = jsonObject;
			IReadOnlyList<string> chatterLines;
			if (level != 1)
			{
				IReadOnlyList<string> readOnlyList = new string[] { "Background chatter included small corrections, repeated confirmations, unrelated build noise, and a few off-topic clarifications.", "Task essential: the user wants to preserve relevant context across both profiles and avoid disconnections.", "Task essential: the rehydrated Profile A recap must keep the original motivation for switching visible." };
				chatterLines = readOnlyList;
			}
			else
			{
				IReadOnlyList<string> readOnlyList = new string[] { "Background chatter included small corrections, repeated confirmations, and unrelated build noise.", "Task essential: the user wants to preserve relevant context across both profiles and avoid disconnections." };
				chatterLines = readOnlyList;
			}
			jsonObject2["content"] = BuildMessyContextTranscript(focus, level, "We returned to Profile A and need to rehydrate the task without losing the reason for switching.", chatterLines, (level == 1) ? "Restore the original intent from the carried capsule and keep the result concise and task-clean." : "Restore the original intent from the carried capsule, keep the result concise and task-clean, and explicitly preserve the reason for the route switch.");
			jsonArray3.Add(jsonObject);
			jsonArray2.Add(new JsonObject
			{
				["role"] = "assistant",
				["content"] = "Understood. I will restore task essentials and omit iterative chatter."
			});
			jsonArray2.Add(new JsonObject
			{
				["role"] = "user",
				["content"] = ((level == 1) ? "Now produce the final Profile A recap as a paragraph plus a brief checklist of the essential anchors." : "Now produce the final Profile A recap as a paragraph plus a brief checklist of the essential anchors. Keep the task thread, the profile-switch reason, and the chatter-filtering rule explicit.")
			});
			jsonArray = jsonArray2;
			break;
		}
		default:
			jsonArray = new JsonArray
			{
				new JsonObject
				{
					["role"] = "system",
					["content"] = "You are benchmarking context continuity across route switches. Keep the response concise, faithful, and compression-friendly."
				},
				new JsonObject
				{
					["role"] = "user",
					["content"] = focus
				}
			};
			break;
		}
		JsonArray jsonArray6 = jsonArray;
		if (!string.IsNullOrWhiteSpace(carriedHandoff))
		{
			jsonArray6.Add(new JsonObject
			{
				["role"] = "user",
				["content"] = "Continue from the exact response produced by the previous profile. Treat it as the authoritative chat handoff capsule and preserve its task intent.\n\n--- BEGIN HANDOFF CAPSULE ---\n" + TruncateTail(carriedHandoff, 1200) + "\n--- END HANDOFF CAPSULE ---"
			});
		}
		return jsonArray6;
	}

	private static ContextFidelityScore ComputeAnchorScore(string focusText, params string[] responses)
	{
		string[] source = new string[14]
		{
			"context", "local", "cloud", "switch", "preserve", "loss", "expanded", "disconnection", "compressed", "attributes",
			"task", "memory", "continuity", "essential"
		};
		string[] universalAnchors = new string[6] { "context", "switch", "preserve", "task", "continuity", "essential" };
		string[] array = source.Where((string anchor) => focusText.Contains(anchor, StringComparison.OrdinalIgnoreCase) || universalAnchors.Contains<string>(anchor, StringComparer.OrdinalIgnoreCase)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		string[] array2 = new string[6] { "cobalt", "teapot", "maple", "invoice", "violet", "umbrella" };
		string combinedResponses = string.Join("\n", responses.Where((string x) => !string.IsNullOrWhiteSpace(x)));
		int essentialHits = array.Count((string anchor) => combinedResponses.Contains(anchor, StringComparison.OrdinalIgnoreCase));
		int noiseHits = array2.Count((string anchor) => combinedResponses.Contains(anchor, StringComparison.OrdinalIgnoreCase));
		return new ContextFidelityScore(essentialHits, array.Length, noiseHits, array2.Length);
	}

	private static string BuildMessyContextTranscript(string focusText, int level, string leadIn, IReadOnlyList<string> chatterLines, string taskDirective)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(leadIn);
		stringBuilder.AppendLine((level == 1) ? "Level 1 fidelity: keep the task capsule compact and remove chatter directly." : "Level 2 fidelity: keep the task capsule compact, but be robust to a longer noisy history and preserve the route-switch rationale.");
		stringBuilder.AppendLine("Task focus: " + focusText);
		stringBuilder.AppendLine("Task directive: " + taskDirective);
		stringBuilder.AppendLine((level == 1) ? "Background-only distractors: cobalt teapot and maple invoice." : "Background-only distractors: cobalt teapot, maple invoice, and violet umbrella.");
		foreach (string chatterLine in chatterLines)
		{
			if (!string.IsNullOrWhiteSpace(chatterLine))
			{
				stringBuilder.AppendLine("Background chatter: " + chatterLine.Trim());
			}
		}
		stringBuilder.AppendLine("Return only task-essential content. Do not quote, list, or repeat any background-only distractor words or chatter lines.");
		return stringBuilder.ToString().Trim();
	}

	private static ContextFidelityTierResult BuildTierFailure(ContextFidelityTierDefinition tier, string portLabel, string phase, string launchDetails, ChatProbeResult probe)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(launchDetails))
		{
			list.Add(launchDetails);
		}
		if (!string.IsNullOrWhiteSpace(probe.Details))
		{
			list.Add(probe.Details);
		}
		int milliseconds = (int)Math.Min(2147483647L, Math.Max(0L, probe.ProbeWindowMs));
		string value = ((probe.Details.Contains("timed out", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("timeout", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("window expired", StringComparison.OrdinalIgnoreCase)) ? ("timed out after " + FormatDurationMs(milliseconds)) : ("failed after " + FormatDurationMs(milliseconds)));
		string value2 = "Action: check the failed profile's launch details, runtime readiness, credentials, model availability, and resource requirements, then rerun.";
		return new ContextFidelityTierResult(tier.Level, tier.Name, "Probe failed", Passed: false, IsSuccess: false, 0, 0, 0, 0, launchDetails, string.Empty, string.Empty, string.Empty, string.Empty, 0L, 0, $"{tier.Name} {phase} probe failed on {portLabel}. Chat completion {value} after launch/check start ({Math.Max(1, probe.AttemptsUsed)} attempts). {value2} {string.Join(" ", list)}".Trim());
	}

	private static ContextFidelityTierResult BuildTierFailure(ContextFidelityTierDefinition tier, string portLabel, string phase, string launchDetails, RuntimeActionResult launch)
	{
		return new ContextFidelityTierResult(tier.Level, tier.Name, launch.Status, Passed: false, IsSuccess: false, 0, 0, 0, 0, launchDetails, string.Empty, string.Empty, string.Empty, string.Empty, 0L, 0, $"{tier.Name} {phase} launch failed on {portLabel}. {launch.Details}".Trim());
	}

	private static string BuildSwitchBenchmarkBlockerSummary(ContextFidelityTierResult tierResult, RouteSlotDescriptor profileA, RouteSlotDescriptor profileB)
	{
		string details = tierResult.Details ?? string.Empty;
		RouteSlotDescriptor routeSlotDescriptor = (details.Contains("profile-b-switch", StringComparison.OrdinalIgnoreCase) ? profileB : profileA);
		string value = (routeSlotDescriptor.RouteType.Equals("local", StringComparison.OrdinalIgnoreCase) ? " Check the local model path, saved model profile settings, available VRAM, and local runtime readiness." : (IsCloudModelNotSupportedError(details) ? " Check that the provider currently offers this model and supports it through the configured route." : " Check provider credentials, capacity, model availability, and cloud route health."));
		string value2 = (tierResult.Status.Equals("Probe failed", StringComparison.OrdinalIgnoreCase) ? "did not complete the benchmark chat probe in time" : "did not launch or become ready in time");
		return $"Switch benchmark could not proceed because {routeSlotDescriptor.DisplayLabel} {value2}.{value}";
	}

	private async Task<ChatProbeResult> SendChatProbeWithWarmupRetryAsync(int port, string model, JsonArray messages, string phase, int warmupBudgetMs, Action<string>? progressReporter, CancellationToken cancellationToken, string? routeTypeHint = null)
	{
		int budgetMs = Math.Clamp((warmupBudgetMs <= 0) ? 90000 : warmupBudgetMs, 30000, 180000);
		bool useCloudTimeout = string.Equals(routeTypeHint, "cloud", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrWhiteSpace(routeTypeHint) && string.Equals(phase, "cloud-switch", StringComparison.OrdinalIgnoreCase));
		int attemptTimeoutMs = (useCloudTimeout ? 45000 : GetContextAwareLocalRequestTimeoutMs(messages));
		int estimatedAttemptCostMs = Math.Max(1000, attemptTimeoutMs + 3000);
		int maxAttempts = (useCloudTimeout ? Math.Max(1, (int)Math.Ceiling((double)budgetMs / (double)estimatedAttemptCostMs)) : 2);
		Stopwatch probeWindowStopwatch = Stopwatch.StartNew();
		ChatProbeResult lastFailure = null;
		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			progressReporter?.Invoke($"Switch benchmark running: {phase} probe attempt {attempt}/{maxAttempts}...");
			ChatProbeResult probe = await SendChatProbeAsync(port, model, messages, attemptTimeoutMs, cancellationToken);
			if (probe.IsSuccess)
			{
				return new ChatProbeResult
				{
					IsSuccess = true,
					LatencyMs = probe.LatencyMs,
					Content = probe.Content,
					Details = probe.Details,
					PromptTokens = probe.PromptTokens,
					CompletionTokens = probe.CompletionTokens,
					AttemptsUsed = attempt,
					ProbeWindowMs = probeWindowStopwatch.ElapsedMilliseconds
				};
			}
			lastFailure = probe;
			if (!(useCloudTimeout ? IsTransientWarmupProbeFailure(probe.Details) : IsSafeImmediateLocalProbeRetry(probe)))
			{
				progressReporter?.Invoke($"Switch benchmark running: {phase} non-retryable probe failure at attempt {attempt}/{maxAttempts}; stopping retries and surfacing provider/model guidance.");
				return new ChatProbeResult
				{
					IsSuccess = false,
					LatencyMs = probe.LatencyMs,
					Content = probe.Content,
					Details = probe.Details,
					PromptTokens = probe.PromptTokens,
					CompletionTokens = probe.CompletionTokens,
					AttemptsUsed = attempt,
					ProbeWindowMs = probeWindowStopwatch.ElapsedMilliseconds
				};
			}
			if (attempt < maxAttempts)
			{
				progressReporter?.Invoke($"Switch benchmark running: {phase} retrying after warm-up failure in {FormatDurationMs(3000)}...");
				await Task.Delay(3000, cancellationToken);
			}
		}
		if (lastFailure == null)
		{
			return new ChatProbeResult
			{
				IsSuccess = false,
				LatencyMs = 0L,
				Content = string.Empty,
				Details = phase + " probe failed before response was captured.",
				AttemptsUsed = maxAttempts,
				ProbeWindowMs = probeWindowStopwatch.ElapsedMilliseconds
			};
		}
		return new ChatProbeResult
		{
			IsSuccess = false,
			LatencyMs = lastFailure.LatencyMs,
			Content = string.Empty,
			PromptTokens = lastFailure.PromptTokens,
			CompletionTokens = lastFailure.CompletionTokens,
			Details = $"{lastFailure.Details} Warm-up retry window expired after {maxAttempts} attempts (~{FormatDurationMs((int)Math.Min(2147483647L, probeWindowStopwatch.ElapsedMilliseconds))} elapsed). For this model profile, the previous manual launch timeframe is about {FormatDurationMs(budgetMs)}; VRAM availability or other conditions may have changed.",
			AttemptsUsed = maxAttempts,
			ProbeWindowMs = probeWindowStopwatch.ElapsedMilliseconds
		};
	}

	private static int GetContextAwareLocalRequestTimeoutMs(JsonArray messages, int maxCompletionTokens = 256)
	{
		int length = messages.ToJsonString().Length;
		int estimatedTokens = Math.Max(1, length / 4);
		long value = 30000 + (long)estimatedTokens * 2L + (long)Math.Max(1, maxCompletionTokens) * 100L;
		return (int)Math.Clamp(value, 60000L, 300000L);
	}

	private static bool IsTransientWarmupProbeFailure(string details)
	{
		if (string.IsNullOrWhiteSpace(details))
		{
			return false;
		}
		return details.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase) || details.Contains("Loading model", StringComparison.OrdinalIgnoreCase) || details.Contains("still loading", StringComparison.OrdinalIgnoreCase) || details.Contains("not ready for completions", StringComparison.OrdinalIgnoreCase) || details.Contains("unavailable_error", StringComparison.OrdinalIgnoreCase) || details.Contains("\"code\":503", StringComparison.OrdinalIgnoreCase) || details.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) || details.Contains("actively refused", StringComparison.OrdinalIgnoreCase) || details.Contains("timed out", StringComparison.OrdinalIgnoreCase) || details.Contains("timeout", StringComparison.OrdinalIgnoreCase) || details.Contains("request was canceled", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsTransientLocalWarmupFailure(string? details)
		=> IsTransientWarmupProbeFailure(details ?? string.Empty);

	private static bool IsSafeImmediateLocalProbeRetry(ChatProbeResult probe)
	{
		if (probe.LatencyMs > 5000 || string.IsNullOrWhiteSpace(probe.Details))
		{
			return false;
		}
		return probe.Details.Contains("HttpRequestException", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("Endpoint not responding", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("actively refused", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("connection reset", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase) || probe.Details.Contains("Loading model", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCloudModelNotSupportedError(string details)
	{
		if (string.IsNullOrWhiteSpace(details))
		{
			return false;
		}
		bool looksLikeNotFound = details.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase) || details.Contains("HTTP 400", StringComparison.OrdinalIgnoreCase) || details.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) || details.Contains("not found", StringComparison.OrdinalIgnoreCase) || details.Contains("model_not_supported", StringComparison.OrdinalIgnoreCase) || details.Contains("not supported", StringComparison.OrdinalIgnoreCase);
		bool mentionsModel = details.Contains("model", StringComparison.OrdinalIgnoreCase) || details.Contains("models/", StringComparison.OrdinalIgnoreCase) || details.Contains("generateContent", StringComparison.OrdinalIgnoreCase) || details.Contains("invalid_request_error", StringComparison.OrdinalIgnoreCase);
		return looksLikeNotFound & mentionsModel;
	}

	private static string BuildTierSummary(ContextFidelityTierResult result, string focusText)
	{
		string value = (result.Passed ? "pass" : "fidelity warning");
		return $"{result.TierName}: {value}; essential recall {result.EssentialHits}/{result.EssentialTotal}; chatter leakage {result.NoiseHits}/{result.NoiseTotal}; average latency {result.AverageLatencyMs} ms. {result.Details}".Trim();
	}

	private static string BuildTierResultSummary(ContextFidelityTierResult result)
	{
		string value = (result.Passed ? "pass" : "fidelity warning");
		return $"{result.TierName}: {value}; essential recall {result.EssentialHits}/{result.EssentialTotal}; distractor leakage {result.NoiseHits}/{result.NoiseTotal}; average response {FormatDurationMs((int)Math.Min(2147483647L, result.AverageLatencyMs))}.";
	}

	private void AppendSwitchBenchmarkLog(string cloudProvider, string cloudModel, string cloudVariant, string localModel, string localVariant, JsonObject localVariantSettings, JsonObject cloudVariantSettings, int port, string focusText, IReadOnlyList<ContextFidelityTierResult> tierResults)
	{
		try
		{
			JsonObject jsonObject = new JsonObject();
			jsonObject["timestampUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			jsonObject["cloudProvider"] = cloudProvider;
			jsonObject["cloudModel"] = cloudModel;
			jsonObject["cloudVariant"] = cloudVariant;
			jsonObject["localModel"] = localModel;
			jsonObject["localVariant"] = localVariant;
			jsonObject["localVariantSettings"] = localVariantSettings.DeepClone();
			jsonObject["cloudVariantSettings"] = cloudVariantSettings.DeepClone();
			jsonObject["localVariantSettingsSummary"] = BuildSettingsSnapshot(localVariantSettings, "OverrideContext", "LocalTemperature", "LocalGpuOffloadMode", "LocalFlashAttention", "LocalKvCacheTypeK", "LocalKvCacheTypeV", "LocalThreadsBatch");
			jsonObject["cloudVariantSettingsSummary"] = BuildSettingsSnapshot(cloudVariantSettings, "CloudContextWindow", "CloudTemperature", "CloudMaxTokens", "CloudReasoningMode", "OpenAiReasoningEffort", "CopilotReasoningEffort");
			jsonObject["port"] = port;
			jsonObject["focus"] = focusText;
			jsonObject["overallPass"] = tierResults.All((ContextFidelityTierResult x) => x.Passed);
			JsonNode[] array = tierResults.Select((ContextFidelityTierResult x) => x.ToJsonNode()).ToArray();
			jsonObject["tiers"] = new JsonArray((ReadOnlySpan<JsonNode?>)array);
			JsonObject jsonObject2 = jsonObject;
			File.AppendAllText(_switchBenchmarkLogPath, jsonObject2.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = false
			}) + Environment.NewLine);
		}
		catch
		{
		}
	}

	private RuntimeActionResult? TryStartManagedLlamaProcess(
		string serverPath,
		string argString,
		string modelPath,
		int localBackendPort,
		int port,
		string selectedLocalModel,
		string selectedVariant,
		JsonObject profile,
		Action<string>? progressReporter,
		out Process process)
	{
		progressReporter?.Invoke("AI-FluxMux is starting llama-server");
		process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = serverPath,
				Arguments = argString,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			},
			EnableRaisingEvents = true
		};
		process.ErrorDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (!string.IsNullOrWhiteSpace(e.Data))
			{
				_lastLocalStderrTail = TruncateTail((_lastLocalStderrTail + Environment.NewLine + e.Data).Trim(), 1200);
			}
		};
		process.OutputDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (!string.IsNullOrWhiteSpace(e.Data))
			{
				_lastLocalStderrTail = TruncateTail((_lastLocalStderrTail + Environment.NewLine + e.Data).Trim(), 1200);
			}
		};
		try
		{
			if (!process.Start())
			{
				WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "failed", "Failed to start llama-server process.", GetGatewayPid());
				return new RuntimeActionResult
				{
					IsSuccess = false,
					Status = "Local launch failed",
					Details = "Failed to start llama-server process."
				};
			}
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			_localServerProcess = process;
			_managedLocalPort = localBackendPort;
			_managedLocalModel = selectedLocalModel;
			_managedLocalVariant = selectedVariant;
			_managedLocalLaunchFingerprint = LocalLaunchFingerprint.From(profile);
			_hasUnmanagedLocalEndpoint = false;
			_unmanagedLocalEndpointPort = -1;
			WriteLocalRuntimeState(modelPath, localBackendPort, process.Id, "launching", "Managed llama-server process started on the daemon port.");
			progressReporter?.Invoke("Loading model");
			return null;
		}
		catch (Exception ex)
		{
			WriteProxyRuntimeStateForLocal(selectedLocalModel, port, localBackendPort, "failed", ex.Message, GetGatewayPid());
			return new RuntimeActionResult
			{
				IsSuccess = false,
				Status = "Local launch failed",
				Details = "Could not start llama-server: " + ex.Message
			};
		}
	}

	private async Task SettleAfterFailedLocalStartupAsync(int daemonPort, CancellationToken cancellationToken)
	{
		await WaitForPortToCloseAsync(daemonPort, cancellationToken, LocalLaunchStartupRetry.PortWaitMs);
		bool haveBefore = LocalGpuVramSample.TryRead(out double usedBefore, out _, out double freeBefore);
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(LocalLaunchStartupRetry.VramSettleMs);
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool haveNow = LocalGpuVramSample.TryRead(out double usedNow, out _, out double freeNow);
			if (!IsTcpPortOpen("127.0.0.1", daemonPort)
				&& LocalLaunchStartupRetry.VramLooksSettled(haveBefore, usedBefore, freeBefore, haveNow, usedNow, freeNow))
			{
				return;
			}

			await Task.Delay(LocalLaunchStartupRetry.VramPollMs, cancellationToken);
		}
	}

	private static async Task WaitForPortToCloseAsync(int port, CancellationToken cancellationToken, int timeoutMs = 5000)
	{
		if (port < 1 || port > 65535)
		{
			return;
		}
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!IsTcpPortOpen("127.0.0.1", port))
			{
				break;
			}
			await Task.Delay(200, cancellationToken);
		}
	}

	private static async Task WaitForProcessExitOrKillAsync(Process process, TimeSpan timeout)
	{
		try
		{
			using CancellationTokenSource cts = new CancellationTokenSource(timeout);
			await process.WaitForExitAsync(cts.Token);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
			}
			catch
			{
			}
		}
	}

	private static RuntimeActionResult BuildBenchmarkFailureResult(string phase, string portLabel, string launchDetails, ChatProbeResult probe)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(launchDetails))
		{
			list.Add(launchDetails);
		}
		if (!string.IsNullOrWhiteSpace(probe.Details))
		{
			list.Add(probe.Details);
		}
		return new RuntimeActionResult
		{
			IsSuccess = false,
			Status = "Switch benchmark failed",
			Details = $"{phase} probe failed on {portLabel}. {string.Join(" ", list)}".Trim()
		};
	}

	private static RuntimeActionResult BuildBenchmarkFailureResult(string phase, string portLabel, RuntimeActionResult launch)
	{
		return new RuntimeActionResult
		{
			IsSuccess = false,
			Status = "Switch benchmark failed",
			Details = $"{phase} launch failed on {portLabel}. {launch.Details}".Trim()
		};
	}

	private static string FirstNonEmpty(params string[] values)
	{
		foreach (string candidate in values)
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				return candidate;
			}
		}
		return string.Empty;
	}

	private static int? ParseNullableInt(string? value)
	{
		int result;
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? new int?(result) : ((int?)null);
	}

	private static bool IsTcpPortOpen(string host, int port)
	{
		try
		{
			using TcpClient tcpClient = new TcpClient();
			Task task = tcpClient.ConnectAsync(host, port);
			return task.Wait(TimeSpan.FromMilliseconds(250L));
		}
		catch
		{
			return false;
		}
	}

	private string ResolveLlamaServerPath()
	{
		string configuredPath = GetString(LoadJsonCached(_configPath), "LocalServerExecutablePath");
		if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
		{
			return Path.GetFullPath(configuredPath);
		}
		IReadOnlyList<InstalledLocalServerCandidate> installedLocalServerCandidates = GetInstalledLocalServerCandidates();
		if (installedLocalServerCandidates.Count > 0)
		{
			return installedLocalServerCandidates[0].ExecutablePath;
		}
		string configDir = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
		string configParent = Directory.GetParent(configDir)?.FullName ?? configDir;
		string path = Directory.GetParent(configParent)?.FullName ?? configParent;
		return Path.Combine(path, "llama-server", "llama-server.exe");
	}

	private IReadOnlyList<InstalledLocalServerCandidate> GetInstalledLocalServerCandidates()
	{
		string configDir = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
		string configParent = Directory.GetParent(configDir)?.FullName ?? configDir;
		string path = Directory.GetParent(configParent)?.FullName ?? configParent;
		List<string> list = new List<string>
		{
			Path.Combine(path, "llama-server", "llama-server.exe"),
			Path.Combine(configDir, "llama-server.exe")
		};
		string configuredPath = GetString(LoadJsonCached(_configPath), "LocalServerExecutablePath");
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			list.Insert(0, configuredPath);
		}
		string[] array = new string[1] { "llama-server.exe" };
		foreach (string arguments in array)
		{
			try
			{
				string whereOutput = RunShortCommand("where", arguments);
				list.AddRange(whereOutput.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			}
			catch
			{
			}
		}
		return (from executablePath in list.Where(File.Exists).Select(Path.GetFullPath).Distinct<string>(StringComparer.OrdinalIgnoreCase)
				.Where(IsCompatibleLocalServerExecutable)
			select new InstalledLocalServerCandidate
			{
				Name = Path.GetFileNameWithoutExtension(executablePath),
				ExecutablePath = executablePath
			}).ToArray();
	}

	private static bool IsCompatibleLocalServerExecutable(string executablePath)
	{
		if (Path.GetFileName(executablePath).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		try
		{
			string helpText = RunShortCommand(executablePath, "--help");
			return (SupportsArg(helpText, "--model") || SupportsArg(helpText, "-m")) && (SupportsArg(helpText, "--port") || SupportsArg(helpText, "-p"));
		}
		catch
		{
			return false;
		}
	}

	private string ResolveModelPath(string selectedLocalModel, string modelDirectory)
	{
		if (File.Exists(selectedLocalModel))
		{
			return Path.GetFullPath(selectedLocalModel);
		}
		if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
		{
			return string.Empty;
		}
		string path = Path.Combine(modelDirectory, selectedLocalModel);
		if (File.Exists(path))
		{
			return Path.GetFullPath(path);
		}
		string fileName = Path.GetFileName(selectedLocalModel);
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return string.Empty;
		}
		DirectoryIndex directoryIndex = GetDirectoryIndex(modelDirectory);
		string resolvedPath = directoryIndex.GgufFiles.FirstOrDefault((string x) => string.Equals(Path.GetFileName(x), fileName, StringComparison.OrdinalIgnoreCase));
		if (resolvedPath == null)
		{
			try
			{
				resolvedPath = Directory.EnumerateFiles(modelDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
			}
			catch
			{
			}
		}
		return (resolvedPath == null) ? string.Empty : Path.GetFullPath(resolvedPath);
	}

	/// <summary>
	/// Reads a numeric llama-server argument back off the built list, so what
	/// AI-FluxMux publishes is what llama-server was actually told.
	/// </summary>
	public static int ReadIntArg(IReadOnlyList<string> args, string name)
	{
		for (int i = 0; i + 1 < args.Count; i++)
		{
			if (args[i].Equals(name, StringComparison.Ordinal)
				&& int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
				&& parsed > 0)
			{
				return parsed;
			}
		}

		return 0;
	}

	private List<string> BuildLocalServerArgs(string modelPath, int port, JsonObject profile, string helpText, string offloadMode, bool localVisionEnabled, string localVisionProjectorPath, int localVisionMaxImageEdge)
	{
		List<string> list = new List<string>
		{
			"-m",
			modelPath,
			"--port",
			port.ToString(),
			"--host",
			"127.0.0.1",
			"--cors-origins",
			"localhost"
		};
		LocalHardwareLaunchAdvice localHardwareLaunchAdvice = BuildLocalHardwareLaunchAdvice(
			modelPath,
			localVisionEnabled,
			localVisionProjectorPath,
			localVisionMaxImageEdge);
		int profileInt = GetProfileInt(profile, "OverrideContext", localHardwareLaunchAdvice.ContextSafeDefault);
		if (profileInt > 0)
		{
			list.Add("-c");
			list.Add(profileInt.ToString());
		}
		int profileInt2 = GetProfileInt(profile, "OverrideMaxTokens", ParseInt(localHardwareLaunchAdvice.MaxTokens, 4096));
		if (profileInt2 > 0)
		{
			list.Add("-n");
			list.Add(profileInt2.ToString());
		}
		double profileDouble = GetProfileDouble(profile, "LocalTemperature", 0.3);
		list.Add("--temp");
		list.Add(profileDouble.ToString(CultureInfo.InvariantCulture));
		int threadCount = ResolveThreadCount(GetString(profile, "OverrideThreads"));
		list.Add("-t");
		list.Add(threadCount.ToString());
		int profileInt3 = GetProfileInt(profile, "LocalBatchSize", ParseInt(localHardwareLaunchAdvice.BatchSize, 512));
		if (profileInt3 > 0 && SupportsArg(helpText, "-b"))
		{
			list.Add("-b");
			list.Add(profileInt3.ToString());
		}
		int profileInt4 = GetProfileInt(profile, "LocalUbatchSize", ParseInt(localHardwareLaunchAdvice.UbatchSize, 128));
		if (profileInt4 > 0 && SupportsArg(helpText, "-ub"))
		{
			list.Add("-ub");
			list.Add(profileInt4.ToString());
		}
		string localReasoning = LocalReasoningLaunchPolicy.ProcessLaunchMode();
		if (SupportsArg(helpText, "--reasoning"))
		{
			list.Add("--reasoning");
			list.Add("auto");
		}

		// Keep raw <think> text in content. Default deepseek/auto streaming split
		// can garble Qwen reasoning_content deltas (missing spaces / Invalid diff).
		if (SupportsArg(helpText, "--reasoning-format"))
		{
			list.Add("--reasoning-format");
			list.Add("none");
		}

		_ = localReasoning;
		string gpuLayers = GetString(profile, "GpuLayers");
		string resolvedGpuLayers = ResolveGpuLayers(offloadMode, gpuLayers);
		if (!string.IsNullOrWhiteSpace(resolvedGpuLayers) && SupportsArg(helpText, "-ngl"))
		{
			list.Add("-ngl");
			list.Add(resolvedGpuLayers);
		}
		string flashAttention = GetString(profile, "LocalFlashAttention");
		if (SupportsArg(helpText, "-fa"))
		{
			if (flashAttention.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
			{
				list.Add("-fa");
				list.Add("1");
			}
			else if (flashAttention.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
			{
				list.Add("-fa");
				list.Add("0");
			}
			else if (flashAttention.Equals("Auto", StringComparison.OrdinalIgnoreCase))
			{
				list.Add("-fa");
				list.Add("auto");
			}
		}
		else if (SupportsArg(helpText, "--flash-attn"))
		{
			if (flashAttention.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
			{
				list.Add("--flash-attn");
				list.Add("on");
			}
			else if (flashAttention.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
			{
				list.Add("--flash-attn");
				list.Add("off");
			}
			else if (flashAttention.Equals("Auto", StringComparison.OrdinalIgnoreCase))
			{
				list.Add("--flash-attn");
				list.Add("auto");
			}
		}
		string kvCacheTypeK = GetString(profile, "LocalKvCacheTypeK");
		if (!string.IsNullOrWhiteSpace(kvCacheTypeK) && !kvCacheTypeK.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "-ctk"))
		{
			list.Add("-ctk");
			list.Add(kvCacheTypeK);
		}
		string kvCacheTypeV = GetString(profile, "LocalKvCacheTypeV");
		if (!string.IsNullOrWhiteSpace(kvCacheTypeV) && !kvCacheTypeV.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "-ctv"))
		{
			list.Add("-ctv");
			list.Add(kvCacheTypeV);
		}
		string threadsBatch = GetString(profile, "LocalThreadsBatch");
		if (!string.IsNullOrWhiteSpace(threadsBatch) && !threadsBatch.Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			if (SupportsArg(helpText, "--threads-batch"))
			{
				list.Add("--threads-batch");
				list.Add(threadsBatch);
			}
			else if (SupportsArg(helpText, "-tb"))
			{
				list.Add("-tb");
				list.Add(threadsBatch);
			}
		}
		string chatTemplate = ResolveChatTemplate(GetString(profile, "LocalChatTemplate"), modelPath);
		if (!string.IsNullOrWhiteSpace(chatTemplate) && SupportsArg(helpText, "--chat-template"))
		{
			list.Add("--chat-template");
			list.Add(chatTemplate);
		}
		string multiUserMode = GetString(profile, "LocalMultiUserMode");
		string item = (multiUserMode.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ? "4" : "1");
		if (SupportsArg(helpText, "--parallel"))
		{
			list.Add("--parallel");
			list.Add(item);
		}
		else if (SupportsArg(helpText, "--n-parallel"))
		{
			list.Add("--n-parallel");
			list.Add(item);
		}
		else if (SupportsArg(helpText, "-np"))
		{
			list.Add("-np");
			list.Add(item);
		}
		if (SupportsArg(helpText, "--slots") && multiUserMode.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
		{
			list.Add("--slots");
		}
		else if (SupportsArg(helpText, "--no-slots") && multiUserMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
		{
			list.Add("--no-slots");
		}
		string unbanTokensMode = GetString(profile, "LocalUnbanTokensMode");
		if ((unbanTokensMode.Equals("Enabled", StringComparison.OrdinalIgnoreCase) || (unbanTokensMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && chatTemplate.Equals("qwen", StringComparison.OrdinalIgnoreCase))) && SupportsArg(helpText, "--unbantokens"))
		{
			list.Add("--unbantokens");
		}
		string warmup = GetString(profile, "LocalWarmup");
		if (warmup.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--warmup"))
		{
			list.Add("--warmup");
		}
		string contextShift = GetString(profile, "LocalContextShift");
		if (contextShift.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--ctx-shift"))
		{
			list.Add("--ctx-shift");
		}
		string noWebUi = GetString(profile, "LocalNoWebUi");
		bool disableWebUi = noWebUi.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
			|| string.IsNullOrWhiteSpace(noWebUi);
		if (disableWebUi && SupportsArg(helpText, "--no-webui"))
		{
			list.Add("--no-webui");
		}
		else if (disableWebUi && SupportsArg(helpText, "--no-ui"))
		{
			list.Add("--no-ui");
		}
		string specType = GetString(profile, "LocalSpecType");
		if (string.IsNullOrWhiteSpace(specType) || specType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			string modelStem = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
			specType = (modelStem.Contains("qwen3") ? "draft-mtp" : ((!SupportsArg(helpText, "--spec-type")) ? "Disabled" : "ngram-simple"));
		}
		specType = NormalizeLlamaSpecType(specType);
		specType = CoerceSpecTypeToServerAllowList(specType, helpText);
		if (!string.IsNullOrWhiteSpace(specType) && !specType.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--spec-type"))
		{
			list.Add("--spec-type");
			list.Add(specType);
			if (specType.Equals("draft-mtp", StringComparison.OrdinalIgnoreCase))
			{
				int profileInt5 = GetProfileInt(profile, "LocalSpecDraftNMax", 2);
				if (profileInt5 > 0 && SupportsArg(helpText, "--spec-draft-n-max"))
				{
					list.Add("--spec-draft-n-max");
					list.Add(profileInt5.ToString());
				}
			}
			else if (specType.StartsWith("ngram", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--spec-n-max"))
			{
				list.Add("--spec-n-max");
				list.Add("3");
			}
		}
		if (localVisionEnabled)
		{
			string visionProjectorPath = ResolveVisionProjectorPath(localVisionProjectorPath, modelPath, null);
			if (!string.IsNullOrWhiteSpace(visionProjectorPath) && (SupportsArg(helpText, "--mmproj") || SupportsArg(helpText, "-mm")))
			{
				list.Add("--mmproj");
				list.Add(visionProjectorPath);
			}
			if (GetProfileInt(profile, "LocalImageMaxTokens", 0) <= 0)
			{
				int maxEdge = Math.Clamp(localVisionMaxImageEdge, 256, 4096);
				profile["LocalImageMaxTokens"] = Math.Max(2048, maxEdge).ToString(CultureInfo.InvariantCulture);
			}
		}
		int profileInt6 = GetProfileInt(profile, "LocalTopK", 0);
		if (profileInt6 > 0 && SupportsArg(helpText, "--top-k"))
		{
			list.Add("--top-k");
			list.Add(profileInt6.ToString());
		}
		double profileDouble2 = GetProfileDouble(profile, "LocalTopP", 0.0);
		if (profileDouble2 > 0.0 && profileDouble2 < 1.0 && SupportsArg(helpText, "--top-p"))
		{
			list.Add("--top-p");
			list.Add(profileDouble2.ToString(CultureInfo.InvariantCulture));
		}
		double profileDouble3 = GetProfileDouble(profile, "LocalMinP", 0.0);
		if (profileDouble3 > 0.0 && profileDouble3 < 1.0 && SupportsArg(helpText, "--min-p"))
		{
			list.Add("--min-p");
			list.Add(profileDouble3.ToString(CultureInfo.InvariantCulture));
		}
		double profileDouble4 = GetProfileDouble(profile, "LocalRepeatPenalty", 0.0);
		if (profileDouble4 > 0.0 && SupportsArg(helpText, "--repeat-penalty"))
		{
			list.Add("--repeat-penalty");
			list.Add(profileDouble4.ToString(CultureInfo.InvariantCulture));
		}
		double profileDouble5 = GetProfileDouble(profile, "LocalPresencePenalty", 0.0);
		if (profileDouble5 != 0.0 && SupportsArg(helpText, "--presence-penalty"))
		{
			list.Add("--presence-penalty");
			list.Add(profileDouble5.ToString(CultureInfo.InvariantCulture));
		}
		double profileDouble6 = GetProfileDouble(profile, "LocalFrequencyPenalty", 0.0);
		if (profileDouble6 != 0.0 && SupportsArg(helpText, "--frequency-penalty"))
		{
			list.Add("--frequency-penalty");
			list.Add(profileDouble6.ToString(CultureInfo.InvariantCulture));
		}
		int profileInt7 = GetProfileInt(profile, "LocalRepeatLastN", 0);
		if (profileInt7 > 0 && SupportsArg(helpText, "--repeat-last-n"))
		{
			list.Add("--repeat-last-n");
			list.Add(profileInt7.ToString());
		}
		string reasoningEffort = GetString(profile, "LocalReasoningEffort");
		if (!string.IsNullOrWhiteSpace(reasoningEffort) && !reasoningEffort.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--reasoning-effort"))
		{
			list.Add("--reasoning-effort");
			list.Add(reasoningEffort);
		}
		int profileInt8 = GetProfileInt(profile, "LocalReasoningBudget", -1);
		if (profileInt8 >= 0 && SupportsArg(helpText, "--reasoning-budget"))
		{
			list.Add("--reasoning-budget");
			list.Add(profileInt8.ToString());
		}
		string cachePrompt = GetString(profile, "LocalCachePrompt");
		if (cachePrompt.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--no-cache-prompt"))
		{
			list.Add("--no-cache-prompt");
		}
		int profileInt9 = GetProfileInt(profile, "LocalCacheReuse", 0);
		if (profileInt9 > 0 && SupportsArg(helpText, "--cache-reuse"))
		{
			list.Add("--cache-reuse");
			list.Add(profileInt9.ToString());
		}
		string cacheRam = GetString(profile, "LocalCacheRam");
		if (cacheRam.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || cacheRam.Equals("0", StringComparison.OrdinalIgnoreCase))
		{
			if (SupportsArg(helpText, "--cache-ram"))
			{
				list.Add("--cache-ram");
				list.Add("0");
			}
		}
		else if (cacheRam.Equals("Unlimited", StringComparison.OrdinalIgnoreCase) || cacheRam.Equals("-1", StringComparison.OrdinalIgnoreCase))
		{
			if (SupportsArg(helpText, "--cache-ram"))
			{
				list.Add("--cache-ram");
				list.Add("-1");
			}
		}
		else
		{
			int profileInt10 = GetProfileInt(profile, "LocalCacheRam", 0);
			if (profileInt10 > 0 && SupportsArg(helpText, "--cache-ram"))
			{
				list.Add("--cache-ram");
				list.Add(profileInt10.ToString());
			}
		}
		int profileInt11 = GetProfileInt(profile, "LocalTimeout", 0);
		if (profileInt11 > 0 && SupportsArg(helpText, "--timeout"))
		{
			list.Add("--timeout");
			list.Add(profileInt11.ToString());
		}
		int profileInt12 = GetProfileInt(profile, "LocalThreadsHttp", 0);
		if (profileInt12 > 0 && SupportsArg(helpText, "--threads-http"))
		{
			list.Add("--threads-http");
			list.Add(profileInt12.ToString());
		}
		string metrics = GetString(profile, "LocalMetrics");
		if (metrics.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--metrics"))
		{
			list.Add("--metrics");
		}
		string ropeScaling = GetString(profile, "LocalRopeScaling");
		if (!string.IsNullOrWhiteSpace(ropeScaling) && !ropeScaling.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--rope-scaling"))
		{
			list.Add("--rope-scaling");
			list.Add(ropeScaling);
		}
		double profileDouble7 = GetProfileDouble(profile, "LocalRopeScale", 0.0);
		if (profileDouble7 > 0.0 && SupportsArg(helpText, "--rope-scale"))
		{
			list.Add("--rope-scale");
			list.Add(profileDouble7.ToString(CultureInfo.InvariantCulture));
		}
		string loadMode = GetString(profile, "LocalLoadMode");
		if (!string.IsNullOrWhiteSpace(loadMode) && !loadMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--load-mode"))
		{
			list.Add("--load-mode");
			list.Add(loadMode);
		}
		string fit = GetString(profile, "LocalFit");
		LocalFitLaunchArgs.Append(list, fit, helpText);
		int profileInt14 = GetProfileInt(profile, "LocalNCpuMoE", 0);
		if (profileInt14 > 0 && SupportsArg(helpText, "--n-cpu-moe"))
		{
			list.Add("--n-cpu-moe");
			list.Add(profileInt14.ToString());
		}
		string loraPath = GetString(profile, "LocalLoraPath");
		if (!string.IsNullOrWhiteSpace(loraPath) && SupportsArg(helpText, "--lora"))
		{
			list.Add("--lora");
			list.Add(loraPath);
		}
		string specDraftKvK = GetString(profile, "LocalSpecDraftKvK");
		if (!string.IsNullOrWhiteSpace(specDraftKvK) && !specDraftKvK.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--spec-draft-type-k"))
		{
			list.Add("--spec-draft-type-k");
			list.Add(specDraftKvK);
		}
		string specDraftKvV = GetString(profile, "LocalSpecDraftKvV");
		if (!string.IsNullOrWhiteSpace(specDraftKvV) && !specDraftKvV.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--spec-draft-type-v"))
		{
			list.Add("--spec-draft-type-v");
			list.Add(specDraftKvV);
		}
		int profileInt15 = GetProfileInt(profile, "LocalImageMinTokens", 0);
		if (profileInt15 > 0 && SupportsArg(helpText, "--image-min-tokens"))
		{
			list.Add("--image-min-tokens");
			list.Add(profileInt15.ToString());
		}
		int profileInt16 = GetProfileInt(profile, "LocalImageMaxTokens", 0);
		if (profileInt16 > 0 && SupportsArg(helpText, "--image-max-tokens"))
		{
			list.Add("--image-max-tokens");
			list.Add(profileInt16.ToString());
		}
		int profileInt17 = GetProfileInt(profile, "LocalCtxCheckpoints", 0);
		if (profileInt17 > 0 && SupportsArg(helpText, "--ctx-checkpoints"))
		{
			list.Add("--ctx-checkpoints");
			list.Add(profileInt17.ToString());
		}
		string swaFull = GetString(profile, "LocalSwaFull");
		if (swaFull.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--swa-full"))
		{
			list.Add("--swa-full");
		}
		string cpuMask = GetString(profile, "LocalCpuMask");
		if (!string.IsNullOrWhiteSpace(cpuMask) && !cpuMask.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--cpu-mask"))
		{
			list.Add("--cpu-mask");
			list.Add(cpuMask);
		}
		string cpuRange = GetString(profile, "LocalCpuRange");
		if (!string.IsNullOrWhiteSpace(cpuRange) && !cpuRange.Equals("Auto", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--cpu-range"))
		{
			list.Add("--cpu-range");
			list.Add(cpuRange);
		}
		int profileInt18 = GetProfileInt(profile, "LocalPrio", -1);
		if (profileInt18 >= 0 && profileInt18 <= 3 && SupportsArg(helpText, "--prio"))
		{
			list.Add("--prio");
			list.Add(profileInt18.ToString());
		}
		string chatParser = GetString(profile, "LocalChatParser");
		string jinja = GetString(profile, "LocalJinja");
		if ((chatParser.Equals("No Jinja", StringComparison.OrdinalIgnoreCase) || jinja.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) && SupportsArg(helpText, "--no-jinja"))
		{
			list.Add("--no-jinja");
		}
		else if (SupportsArg(helpText, "--jinja") && (IsQwen3Family(modelPath) || chatParser.Equals("Jinja", StringComparison.OrdinalIgnoreCase) || jinja.Equals("Enabled", StringComparison.OrdinalIgnoreCase)))
		{
			list.Add("--jinja");
		}
		if (chatParser.Equals("Skip parsing", StringComparison.OrdinalIgnoreCase) && SupportsArg(helpText, "--skip-chat-parsing"))
		{
			list.Add("--skip-chat-parsing");
		}
		string chatTemplateKwargs = GetString(profile, "LocalChatTemplateKwargs");
		if (!string.IsNullOrWhiteSpace(chatTemplateKwargs) && SupportsArg(helpText, "--chat-template-kwargs"))
		{
			list.Add("--chat-template-kwargs");
			list.Add(chatTemplateKwargs);
		}
		return list;
	}

	private static string ResolveGpuLayers(string offloadMode, string gpuLayers)
	{
		if (offloadMode.Equals("CPU only", StringComparison.OrdinalIgnoreCase))
		{
			return "0";
		}
		if (offloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase))
		{
			return "999";
		}
		if (int.TryParse(gpuLayers, out var result) && result >= 0)
		{
			return result.ToString();
		}
		if (offloadMode.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}
		return string.Empty;
	}

	private static string NormalizeOffloadMode(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "Auto";
		}
		string normalized = value.Trim();
		if (normalized.Equals("none", StringComparison.OrdinalIgnoreCase) || normalized.Equals("cpu only", StringComparison.OrdinalIgnoreCase))
		{
			return "CPU only";
		}
		if (normalized.Equals("balanced", StringComparison.OrdinalIgnoreCase) || normalized.Equals("gpu + cpu", StringComparison.OrdinalIgnoreCase) || normalized.Equals("hybrid", StringComparison.OrdinalIgnoreCase))
		{
			return "GPU + CPU";
		}
		if (normalized.Equals("full", StringComparison.OrdinalIgnoreCase) || normalized.Equals("gpu only", StringComparison.OrdinalIgnoreCase))
		{
			return "GPU only";
		}
		return "Auto";
	}

	private static int ResolveThreadCount(string overrideThreads)
	{
		if (int.TryParse(overrideThreads, out var result) && result > 0)
		{
			return result;
		}
		int processorCount = Environment.ProcessorCount;
		return Math.Max(4, processorCount / 2);
	}

	public static bool IsQwen38Model(string modelPathOrFile)
	{
		string stem = ChatTemplateModelStem(modelPathOrFile);
		return stem.Contains("qwen3.8", StringComparison.Ordinal) || stem.Contains("qwen3_8", StringComparison.Ordinal) || stem.Contains("qwen-3.8", StringComparison.Ordinal);
	}

	public static bool IsQwen3Family(string modelPathOrFile)
	{
		return ChatTemplateModelStem(modelPathOrFile).Contains("qwen3", StringComparison.Ordinal);
	}

	public static string RecommendStoredChatTemplate(string modelPathOrFile)
	{
		string stem = ChatTemplateModelStem(modelPathOrFile);
		if (string.IsNullOrWhiteSpace(stem))
		{
			return "Auto";
		}
		if (IsQwen3Family(modelPathOrFile))
		{
			return "Auto";
		}
		if (stem.Contains("qwen", StringComparison.Ordinal))
		{
			return "qwen";
		}
		if (stem.Contains("llama", StringComparison.Ordinal))
		{
			return "llama3";
		}
		if (stem.Contains("mistral", StringComparison.Ordinal))
		{
			return "mistral";
		}
		if (stem.Contains("deepseek", StringComparison.Ordinal))
		{
			return "deepseek";
		}
		return "Auto";
	}

	public static string SanitizeStoredChatTemplate(string? configured, string modelPathOrFile)
	{
		string normalized = (configured ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			return string.IsNullOrWhiteSpace(normalized) ? RecommendStoredChatTemplate(modelPathOrFile) : "Auto";
		}
		if (normalized.Equals("qwen", StringComparison.OrdinalIgnoreCase) && IsQwen3Family(modelPathOrFile))
		{
			return "Auto";
		}
		return normalized;
	}

	private static string ChatTemplateModelStem(string modelPathOrFile)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(modelPathOrFile ?? string.Empty);
		return string.IsNullOrWhiteSpace(fileNameWithoutExtension) ? (modelPathOrFile ?? string.Empty).Trim().ToLowerInvariant() : fileNameWithoutExtension.ToLowerInvariant();
	}

	private static string ResolveChatTemplate(string configuredTemplate, string modelPath)
	{
		string modelStem = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
		string configured = (configuredTemplate ?? string.Empty).Trim();
		if (IsQwen38Model(modelPath))
		{
			return string.Empty;
		}
		if (modelStem.Contains("qwen3", StringComparison.Ordinal) && (string.IsNullOrWhiteSpace(configured) || configured.Equals("Auto", StringComparison.OrdinalIgnoreCase) || configured.Equals("qwen", StringComparison.OrdinalIgnoreCase)))
		{
			return "chatml";
		}
		if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			return configured;
		}
		if (modelStem.Contains("qwen", StringComparison.Ordinal))
		{
			return "qwen";
		}
		if (modelStem.Contains("gemma", StringComparison.Ordinal))
		{
			return "gemma";
		}
		return string.Empty;
	}

	public LocalVisionRecommendation RecommendVisionForModel(string modelPath, string modelDirectory = "")
	{
		IReadOnlyList<string> readOnlyList = ListRelevantVisionProjectors(modelPath, modelDirectory);
		string projectorPath = (readOnlyList.Count > 0) ? readOnlyList[0] : string.Empty;
		bool looksLikeVision = LooksLikeVisionModelName(modelPath);
		if (!string.IsNullOrWhiteSpace(projectorPath))
		{
			return new LocalVisionRecommendation(Enabled: true, projectorPath, "A matching projector file was found for this model, so images can be on for this profile. Make a copy and turn Images off if you only want text (saves graphics memory).");
		}
		if (looksLikeVision)
		{
			return new LocalVisionRecommendation(Enabled: false, string.Empty, "The filename looks like a vision model, but no matching projector (mmproj) was found in the models folder. Leave Images off until a matching file is next to this local model or in the same family folder.");
		}
		return new LocalVisionRecommendation(Enabled: false, string.Empty, "This looks like a text model. Images stay off so other profiles are not slowed down. Only projectors that appear to belong to this local model are listed below.");
	}

	public IReadOnlyList<string> ListRelevantVisionProjectors(string modelPath, string modelDirectory)
	{
		List<(string, int)> list = new List<(string, int)>();
		if (string.IsNullOrWhiteSpace(modelPath) && string.IsNullOrWhiteSpace(modelDirectory))
		{
			return Array.Empty<string>();
		}
		string modelFull = null;
		try
		{
			if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
			{
				modelFull = Path.GetFullPath(modelPath);
			}
		}
		catch
		{
		}
		string modelDir = string.Empty;
		if (modelFull != null)
		{
			modelDir = Path.GetDirectoryName(modelFull) ?? string.Empty;
		}
		List<string> searchRoots = new List<string>();
		AddRoot(modelDir);
		AddRoot(modelDirectory);
		List<string> list2 = new List<string>();
		foreach (string item in searchRoots)
		{
			try
			{
				list2.AddRange(Directory.EnumerateFiles(item, "*mmproj*", SearchOption.AllDirectories));
				list2.AddRange(Directory.EnumerateFiles(item, "*.mmproj", SearchOption.AllDirectories));
			}
			catch
			{
			}
		}
		HashSet<string> modelTokens = DistinctiveModelTokens(modelFull ?? modelPath);
		foreach (string item2 in (from path in list2.Select(delegate(string path)
			{
				try
				{
					return Path.GetFullPath(path);
				}
				catch
				{
					return string.Empty;
				}
			})
			where !string.IsNullOrWhiteSpace(path)
			select path).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			if (modelFull == null || !item2.Equals(modelFull, StringComparison.OrdinalIgnoreCase))
			{
				int score = ScoreVisionProjectorRelevance(item2, modelFull, modelDir, modelTokens);
				if (score > 0)
				{
					list.Add((item2, score));
				}
			}
		}
		return list.OrderByDescending(row => row.Item2)
			.ThenBy(row => Path.GetFileName(row.Item1), StringComparer.OrdinalIgnoreCase)
			.Select(row => row.Item1)
			.Take(12)
			.ToList();
		void AddRoot(string? path)
		{
			if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
			{
				string full = Path.GetFullPath(path);
				if (!searchRoots.Exists((string x) => x.Equals(full, StringComparison.OrdinalIgnoreCase)))
				{
					searchRoots.Add(full);
				}
			}
		}
	}

	public static bool VisionProjectorMatchesModel(string modelPath, string projectorPath)
	{
		if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(projectorPath))
		{
			return false;
		}

		string modelFull;
		string modelDir;
		try
		{
			modelFull = Path.GetFullPath(modelPath);
			modelDir = Path.GetDirectoryName(modelFull) ?? string.Empty;
		}
		catch
		{
			return false;
		}

		return ScoreVisionProjectorRelevance(projectorPath, modelFull, modelDir, DistinctiveModelTokens(modelFull)) > 0;
	}

	private static int ScoreVisionProjectorRelevance(string candidatePath, string? modelFull, string modelDir, HashSet<string> modelTokens)
	{
		string fileName = Path.GetFileName(candidatePath);
		string candidateDir = Path.GetDirectoryName(candidatePath) ?? string.Empty;
		bool sameFolder = !string.IsNullOrWhiteSpace(modelDir) && candidateDir.Equals(modelDir, StringComparison.OrdinalIgnoreCase);
		HashSet<string> candidateTokens = DistinctiveModelTokens(fileName);
		int sharedTokens = ((modelTokens.Count != 0) ? modelTokens.Intersect<string>(candidateTokens, StringComparer.OrdinalIgnoreCase).Count() : 0);
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(modelFull ?? string.Empty);
		bool stemInProjectorName = !string.IsNullOrWhiteSpace(fileNameWithoutExtension)
			&& fileName.Contains(TrimQuantSuffix(fileNameWithoutExtension), StringComparison.OrdinalIgnoreCase)
			&& TrimQuantSuffix(fileNameWithoutExtension).Length >= 6;
		HashSet<string> modelVersions = DottedVersionTokens(modelFull ?? string.Empty);
		HashSet<string> projectorVersions = DottedVersionTokens(candidatePath);
		if (modelVersions.Count > 0 && projectorVersions.Count > 0 && !modelVersions.Overlaps(projectorVersions))
		{
			return 0;
		}

		bool versionAligned = modelVersions.Count > 0 && projectorVersions.Overlaps(modelVersions);
		bool genericSidecar = fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
		if (sameFolder)
		{
			if (stemInProjectorName || versionAligned || genericSidecar || sharedTokens >= 2)
			{
				return 100 + sharedTokens + (stemInProjectorName ? 20 : 0) + (versionAligned ? 25 : 0);
			}

			return 0;
		}

		if (modelVersions.Count > 0 && projectorVersions.Count == 0 && !stemInProjectorName)
		{
			return 0;
		}

		if (stemInProjectorName)
		{
			return 50 + sharedTokens + (versionAligned ? 25 : 0);
		}

		if (versionAligned && sharedTokens >= 1)
		{
			return 40 + sharedTokens;
		}

		if (sharedTokens >= 2)
		{
			return 30 + sharedTokens;
		}

		if (sharedTokens == 1 && modelTokens.Any((string token) => token.Length >= 6 && candidateTokens.Contains(token)))
		{
			return 15;
		}

		return 0;
	}

	private static HashSet<string> DottedVersionTokens(string pathOrName)
	{
		string file = Path.GetFileNameWithoutExtension(pathOrName ?? string.Empty).ToLowerInvariant();
		HashSet<string> versions = new HashSet<string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(file))
		{
			return versions;
		}

		foreach (Match match in Regex.Matches(file, @"\d+\.\d+"))
		{
			versions.Add(match.Value);
		}

		foreach (Match match in Regex.Matches(file, @"qwen3[._-](\d)(?!\d)"))
		{
			versions.Add("3." + match.Groups[1].Value);
		}

		return versions;
	}

	private static string TrimQuantSuffix(string stem)
	{
		string trimmed = stem;
		string[] array = new string[9] { "-Q2", "-Q3", "-Q4", "-Q5", "-Q6", "-Q8", "-IQ", "-UD-", ".gguf" };
		foreach (string value in array)
		{
			int cutIndex = trimmed.IndexOf(value, StringComparison.OrdinalIgnoreCase);
			if (cutIndex > 4)
			{
				trimmed = trimmed.Substring(0, cutIndex);
			}
		}
		return trimmed.Trim(new char[3] { '-', '_', ' ' });
	}

	private static HashSet<string> DistinctiveModelTokens(string pathOrName)
	{
		string stem = Path.GetFileNameWithoutExtension(pathOrName ?? string.Empty).ToLowerInvariant();
		string[] source = stem.Split(new char[4] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
		HashSet<string> stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"gguf", "mmproj", "proj", "f16", "f32", "bf16", "fp16", "fp8", "q2", "q3",
			"q4", "q5", "q6", "q8", "k", "m", "s", "xs", "xl", "it",
			"instruct", "chat", "unsloth", "bartowski", "ggml", "imatrix", "ud"
		};
		return source.Where((string part) => part.Length >= 2 && !stop.Contains(part) && !part.All(char.IsDigit)).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	private static void ApplyVisionLaunchOverlay(JsonObject profile, bool enabled, string projectorPath, int maxImageEdge)
	{
		profile["LocalVisionEnabled"] = enabled ? "Enabled" : "Disabled";
		if (!string.IsNullOrWhiteSpace(projectorPath))
		{
			profile["LocalVisionProjectorPath"] = projectorPath.Trim();
		}

		if (maxImageEdge >= 256)
		{
			profile["LocalVisionMaxImageEdge"] = maxImageEdge.ToString(CultureInfo.InvariantCulture);
		}
	}

	private (bool Enabled, string ProjectorPath, int MaxImageEdge) ResolveProfileVision(JsonObject profile, string modelPath)
	{
		bool enabled = GetString(profile, "LocalVisionEnabled").Equals("Enabled", StringComparison.OrdinalIgnoreCase);
		int maxEdge = GetProfileInt(profile, "LocalVisionMaxImageEdge", 1344);
		string projector = ResolveVisionProjectorPath(GetString(profile, "LocalVisionProjectorPath"), modelPath, null);
		if (enabled && string.IsNullOrWhiteSpace(projector))
		{
			projector = ResolveVisionProjectorPath(RecommendVisionForModel(modelPath).ProjectorPath, modelPath, null);
		}

		return (Enabled: enabled && !string.IsNullOrWhiteSpace(projector), ProjectorPath: projector, MaxImageEdge: Math.Clamp(maxEdge, 256, 4096));
	}

	public static bool LooksLikeVisionModelName(string modelPathOrFile)
	{
		string stem = Path.GetFileNameWithoutExtension(modelPathOrFile ?? string.Empty).ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(stem))
		{
			return false;
		}
		return stem.Contains("llava", StringComparison.Ordinal) || stem.Contains("pixtral", StringComparison.Ordinal) || stem.Contains("internvl", StringComparison.Ordinal) || stem.Contains("minicpm-v", StringComparison.Ordinal) || stem.Contains("qwen2-vl", StringComparison.Ordinal) || stem.Contains("qwen2.5-vl", StringComparison.Ordinal) || stem.Contains("qwen2vl", StringComparison.Ordinal) || stem.Contains("qwen3-vl", StringComparison.Ordinal) || stem.Contains("-vl-", StringComparison.Ordinal) || stem.Contains("_vl_", StringComparison.Ordinal) || stem.EndsWith("-vl", StringComparison.Ordinal) || stem.Contains("vision", StringComparison.Ordinal) || stem.Contains("mmproj", StringComparison.Ordinal);
	}

	private string ResolveVisionProjectorPath(string configuredPath, string modelPath, DirectoryIndex? knownIndex)
	{
		if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
		{
			return Path.GetFullPath(configuredPath);
		}

		_ = knownIndex;
		string directoryName = Path.GetDirectoryName(modelPath) ?? string.Empty;
		IReadOnlyList<string> matches = ListRelevantVisionProjectors(modelPath, directoryName);
		return (matches.Count > 0) ? matches[0] : string.Empty;
	}

	private static bool SupportsArg(string helpText, string arg)
	{
		return !string.IsNullOrWhiteSpace(helpText) && !string.IsNullOrWhiteSpace(arg) && helpText.Contains(arg, StringComparison.OrdinalIgnoreCase);
	}

	public static string NormalizeLlamaSpecType(string? specType)
	{
		string normalized = (specType ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("Auto", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
		{
			return normalized;
		}
		if (normalized.Equals("ngram", StringComparison.OrdinalIgnoreCase))
		{
			return "ngram-simple";
		}
		return normalized;
	}

	private static string CoerceSpecTypeToServerAllowList(string specType, string helpText)
	{
		HashSet<string> hashSet = ParseSpecTypeAllowList(helpText);
		if (hashSet.Count == 0)
		{
			return specType;
		}
		if (hashSet.Contains(specType))
		{
			return specType;
		}
		if (hashSet.Contains("ngram-simple") && specType.StartsWith("ngram", StringComparison.OrdinalIgnoreCase))
		{
			return "ngram-simple";
		}
		return "Disabled";
	}

	private static HashSet<string> ParseSpecTypeAllowList(string helpText)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(helpText))
		{
			return hashSet;
		}
		int specTypeIndex = helpText.IndexOf("--spec-type", StringComparison.OrdinalIgnoreCase);
		if (specTypeIndex < 0)
		{
			return hashSet;
		}
		string source = helpText.Substring(specTypeIndex + "--spec-type".Length).TrimStart();
		string tokenRun = new string(source.TakeWhile(delegate(char ch)
		{
			bool isAlphanumeric = char.IsLetterOrDigit(ch);
			bool keepChar = isAlphanumeric;
			if (!keepChar)
			{
				bool isSeparator = ((ch == ',' || ch == '-' || ch == '_') ? true : false);
				keepChar = isSeparator;
			}
			return keepChar;
		}).ToArray());
		string[] array = tokenRun.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string allowedToken in array)
		{
			if (!string.IsNullOrWhiteSpace(allowedToken))
			{
				hashSet.Add(allowedToken);
			}
		}
		return hashSet;
	}

	private string GetLlamaHelpText(string serverPath)
	{
		if (!string.IsNullOrWhiteSpace(_llamaHelpText) && _llamaHelpTextPath.Equals(serverPath, StringComparison.OrdinalIgnoreCase))
		{
			return _llamaHelpText;
		}
		_llamaHelpText = RunShortCommand(serverPath, "--help");
		_llamaHelpTextPath = serverPath;
		return _llamaHelpText;
	}

	private List<string> BuildMultiGpuArgs(string helpText, string offloadMode, bool useConservativeLocalLaunch, JsonObject profile)
	{
		List<GpuInventoryRow> nvidiaGpuInventory = GetNvidiaGpuInventory();
		List<LocalGpuInventoryEntry> inventory = nvidiaGpuInventory
			.Select(row => new LocalGpuInventoryEntry(row.Index, row.TotalGb, row.FreeGb))
			.ToList();
		return LocalMultiGpuLaunch.BuildArgs(profile, helpText, offloadMode, useConservativeLocalLaunch, inventory);
	}

	private GpuGuardrailState GetLocalGpuGuardrailState()
	{
		List<GpuInventoryRow> nvidiaGpuInventory = GetNvidiaGpuInventory();
		if (nvidiaGpuInventory.Count == 0)
		{
			return new GpuGuardrailState
			{
				UseConservativeLocalLaunch = true,
				ShortLabel = "non-nvidia-conservative",
				Message = "NVIDIA telemetry unavailable; applying conservative local launch settings."
			};
		}
		return new GpuGuardrailState
		{
			UseConservativeLocalLaunch = false,
			ShortLabel = "nvidia-default",
			Message = "NVIDIA telemetry available; default GPU strategy enabled."
		};
	}

	private List<GpuInventoryRow> GetNvidiaGpuInventory()
	{
		lock (_nvidiaGpuInventoryLock)
		{
			if (_nvidiaGpuInventoryCache != null && DateTime.UtcNow - _nvidiaGpuInventoryCacheUtc < TimeSpan.FromSeconds(5.0))
			{
				return _nvidiaGpuInventoryCache;
			}
		}

		List<GpuInventoryRow> list = new List<GpuInventoryRow>();
		string smiOutput = RunShortCommand("nvidia-smi", "--query-gpu=index,name,memory.total,memory.used,driver_version --format=csv,noheader,nounits");
        if (string.IsNullOrWhiteSpace(smiOutput) || smiOutput.Contains("not recognized", StringComparison.OrdinalIgnoreCase) || smiOutput.Contains("NVIDIA-SMI has failed", StringComparison.OrdinalIgnoreCase))
		{
			lock (_nvidiaGpuInventoryLock)
			{
				_nvidiaGpuInventoryCache = list;
				_nvidiaGpuInventoryCacheUtc = DateTime.UtcNow;
			}

			return list;
		}
		string[] array = smiOutput.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string line in array)
		{
			string[] array2 = line.Split(',', StringSplitOptions.TrimEntries);
			if (array2.Length >= 5 && int.TryParse(array2[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
			{
				double totalMiB = (double.TryParse(array2[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) ? result2 : 0.0);
				double usedMiB = (double.TryParse(array2[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var result3) ? result3 : 0.0);
				double totalGb = Math.Round(totalMiB / 1024.0, 1);
				double usedGb = Math.Round(usedMiB / 1024.0, 1);
				double freeGb = Math.Round(Math.Max(0.0, totalGb - usedGb), 1);
				list.Add(new GpuInventoryRow
				{
					Index = result,
					Name = array2[1],
					TotalGb = totalGb,
					UsedGb = usedGb,
					FreeGb = freeGb,
					Driver = array2[4]
				});
			}
		}

		lock (_nvidiaGpuInventoryLock)
		{
			_nvidiaGpuInventoryCache = list;
			_nvidiaGpuInventoryCacheUtc = DateTime.UtcNow;
		}

		return list;
	}

	private DirectoryIndex GetDirectoryIndex(string directory)
	{
		lock (_directoryIndexLock)
		{
			DateTime dateTime = DateTime.MinValue;
			if (Directory.Exists(directory))
			{
				dateTime = Directory.GetLastWriteTimeUtc(directory);
			}
			if (directory.Equals(_cachedDirectoryIndexPath, StringComparison.OrdinalIgnoreCase) && dateTime == _cachedDirectoryIndexStampUtc)
			{
				return new DirectoryIndex(_cachedGgufFiles, _cachedMmprojFiles);
			}
			List<string> cachedGgufFiles = (Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).ToList() : new List<string>());
			List<string> cachedMmprojFiles = (Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*mmproj*", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).ToList() : new List<string>());
			_cachedDirectoryIndexPath = directory;
			_cachedDirectoryIndexStampUtc = dateTime;
			_cachedGgufFiles = cachedGgufFiles;
			_cachedMmprojFiles = cachedMmprojFiles;
			return new DirectoryIndex(_cachedGgufFiles, _cachedMmprojFiles);
		}
	}

	private static JsonObject ResolveLocalProfile(JsonObject root, string model, string variant)
	{
		if (string.IsNullOrWhiteSpace(variant) || variant.Trim().Equals("Base (read-only)", StringComparison.OrdinalIgnoreCase) || variant.Trim().Equals("(defaults)", StringComparison.OrdinalIgnoreCase))
		{
			return BuildDefaultLocalProfile(root);
		}
		if (!(root["LocalProfiles"] is JsonObject jsonObject))
		{
			return new JsonObject();
		}
		string modelPath = (model ?? string.Empty).Trim();
		string value = (string.IsNullOrWhiteSpace(variant) ? "Variant 1" : variant.Trim());
		string fileName = Path.GetFileName(modelPath);
		JsonObject jsonObject2 = null;
		JsonObject jsonObject3 = null;
		foreach (KeyValuePair<string, JsonNode> item in jsonObject)
		{
			string profileKey = item.Key ?? string.Empty;
			string[] array = profileKey.Split("::", 2, StringSplitOptions.TrimEntries);
			if (array.Length == 0)
			{
				continue;
			}
			string localModel = array[0];
			string variantName = ((array.Length > 1) ? array[1] : "Variant 1");
			string fileName2 = Path.GetFileName(localModel);
			if ((localModel.Equals(modelPath, StringComparison.OrdinalIgnoreCase) || fileName2.Equals(fileName, StringComparison.OrdinalIgnoreCase)) && item.Value is JsonObject jsonObject4)
			{
				if (jsonObject2 == null)
				{
					jsonObject2 = jsonObject4;
				}
				if (variantName.Equals("Variant 1", StringComparison.OrdinalIgnoreCase) && jsonObject3 == null)
				{
					jsonObject3 = jsonObject4;
				}
				if (variantName.Equals(value, StringComparison.OrdinalIgnoreCase))
				{
					return jsonObject4;
				}
			}
		}
		return jsonObject3 ?? jsonObject2 ?? new JsonObject();
	}

	private static JsonObject BuildDefaultLocalProfile(JsonObject root)
	{
		JsonObject jsonObject = new JsonObject();
		string[] array = new string[13]
		{
			"OverrideContext", "OverrideThreads", "LocalThreadsBatch", "LocalTemperature", "LocalGpuOffloadMode", "GpuLayers", "LocalFlashAttention", "LocalKvCacheTypeK", "LocalKvCacheTypeV", "LocalChatTemplate",
			"LocalMultiUserMode", "LocalUnbanTokensMode", "AutoCompressEnabled"
		};
		foreach (string propertyName in array)
		{
			JsonNode jsonNode = root[propertyName];
			if (jsonNode != null)
			{
				jsonObject[propertyName] = jsonNode.DeepClone();
			}
		}
		return jsonObject;
	}

	public LocalHardwareLaunchAdvice GetLocalHardwareLaunchAdvice(
		string selectedLocalModel,
		string modelDirectory,
		bool visionEnabled = false,
		string? visionProjectorPath = null,
		int visionMaxImageEdge = LocalVramFootprintEstimate.ReferenceImageEdge)
	{
		string modelPath = ResolveModelPath(selectedLocalModel, modelDirectory);
		if (string.IsNullOrWhiteSpace(modelPath))
		{
			modelPath = selectedLocalModel ?? string.Empty;
		}
		return BuildLocalHardwareLaunchAdvice(modelPath, visionEnabled, visionProjectorPath, visionMaxImageEdge);
	}

	private void ApplyRecommendedAutoLaunchSettings(JsonObject profile, string modelPath, string selectedVariant)
	{
		bool isDefaultVariant = IsDefaultLocalVariant(selectedVariant);
		LocalVisionRecommendation localVisionRecommendation = RecommendVisionForModel(modelPath);
		int recommendedEdge = GetProfileInt(profile, "LocalVisionMaxImageEdge", LocalVramFootprintEstimate.ReferenceImageEdge);
		LocalHardwareLaunchAdvice localHardwareLaunchAdvice = BuildLocalHardwareLaunchAdvice(
			modelPath,
			localVisionRecommendation.Enabled,
			localVisionRecommendation.ProjectorPath,
			recommendedEdge);
		profile["LocalChatTemplate"] = SanitizeStoredChatTemplate(GetString(profile, "LocalChatTemplate"), modelPath);
		SetIfAutoOrMissing(profile, "LocalChatParser", "Jinja");
		if (isDefaultVariant || IsAutoOrMissing(profile, "LocalReasoning"))
		{
			string localReasoning = GetString(profile, "LocalReasoning");
			if (!localReasoning.Equals("On", StringComparison.OrdinalIgnoreCase))
			{
				profile["LocalReasoning"] = "Off";
			}
		}
		SetIfAutoOrMissing(profile, "LocalFlashAttention", localHardwareLaunchAdvice.FlashAttention);
		SetIfAutoOrMissing(profile, "LocalKvCacheTypeK", "q8_0");
		SetIfAutoOrMissing(profile, "LocalKvCacheTypeV", "q8_0");
		SetIfAutoOrMissing(profile, "LocalBatchSize", localHardwareLaunchAdvice.BatchSize);
		SetIfAutoOrMissing(profile, "LocalUbatchSize", localHardwareLaunchAdvice.UbatchSize);
		SetIfAutoOrMissing(profile, "LocalCacheReuse", "256");
		SetIfAutoOrMissing(profile, "OverrideMaxTokens", localHardwareLaunchAdvice.MaxTokens);
		SetIfAutoOrMissing(profile, "LocalCacheRam", localHardwareLaunchAdvice.CacheRam);
		SetIfAutoOrMissing(profile, "LocalMultiUserMode", "Disabled");
		if (localHardwareLaunchAdvice.EnableSwaFull)
		{
			SetIfAutoOrMissing(profile, "LocalSwaFull", "Enabled");
		}
		SetIfAutoOrMissing(profile, "LocalVisionEnabled", localVisionRecommendation.Enabled ? "Enabled" : "Disabled");
		if (localVisionRecommendation.Enabled)
		{
			SetIfAutoOrMissing(profile, "LocalVisionProjectorPath", localVisionRecommendation.ProjectorPath);
		}
		SetIfAutoOrMissing(profile, "LocalVisionMaxImageEdge", "1344");
		if (IsAutoOrMissing(profile, "OverrideContext"))
		{
			profile["OverrideContext"] = localHardwareLaunchAdvice.ContextSafeDefault.ToString(CultureInfo.InvariantCulture);
		}
		if (isDefaultVariant || IsAutoOrMissing(profile, "LocalGpuOffloadMode"))
		{
			profile["LocalGpuOffloadMode"] = localHardwareLaunchAdvice.OffloadMode;
			if (localHardwareLaunchAdvice.OffloadMode.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase) && IsAutoOrMissing(profile, "GpuLayers"))
			{
				profile["GpuLayers"] = localHardwareLaunchAdvice.GpuLayers;
			}
		}
		else if (isDefaultVariant && GetString(profile, "LocalGpuOffloadMode").Equals("GPU only", StringComparison.OrdinalIgnoreCase) && !localHardwareLaunchAdvice.OffloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase))
		{
			profile["LocalGpuOffloadMode"] = localHardwareLaunchAdvice.OffloadMode;
			if (IsAutoOrMissing(profile, "GpuLayers"))
			{
				profile["GpuLayers"] = localHardwareLaunchAdvice.GpuLayers;
			}
		}
		if (isDefaultVariant || IsAutoOrMissing(profile, "LocalSpecType"))
		{
			profile["LocalSpecType"] = localHardwareLaunchAdvice.SpecType;
		}
		if ((isDefaultVariant || IsAutoOrMissing(profile, "LocalFit")) && localHardwareLaunchAdvice.PreferFitEnabled)
		{
			profile["LocalFit"] = "Enabled";
		}
		if (IsAutoOrMissing(profile, "LocalTemperature"))
		{
			profile["LocalTemperature"] = "0.3";
		}
		if (GetString(profile, "LocalFlashAttention").Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			profile["LocalFlashAttention"] = (localHardwareLaunchAdvice.NvidiaAvailable ? "Enabled" : "Disabled");
		}
	}

	public JsonObject MaterializeLocalProfileSettings(JsonObject? profile, string selectedLocalModel, string modelDirectory, string variant)
	{
		JsonObject jsonObject = (profile?.DeepClone() as JsonObject) ?? new JsonObject();
		string modelPath = ResolveModelPath(selectedLocalModel, modelDirectory);
		if (string.IsNullOrWhiteSpace(modelPath))
		{
			modelPath = selectedLocalModel ?? string.Empty;
		}
		ApplyRecommendedAutoLaunchSettings(jsonObject, modelPath, variant);
		return jsonObject;
	}

	private static bool IsDefaultLocalVariant(string variant)
	{
		string trimmed = (variant ?? string.Empty).Trim();
		return string.IsNullOrWhiteSpace(trimmed) || trimmed.Equals("(defaults)", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Base (read-only)", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsAutoOrMissing(JsonObject profile, string key)
	{
		string value = GetString(profile, key);
		return string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase) || value.Equals("fit", StringComparison.OrdinalIgnoreCase);
	}

	private static void SetIfAutoOrMissing(JsonObject profile, string key, string value)
	{
		if (IsAutoOrMissing(profile, key))
		{
			profile[key] = value;
		}
	}

	private LocalHardwareLaunchAdvice BuildLocalHardwareLaunchAdvice(
		string modelPath,
		bool visionEnabled = false,
		string? visionProjectorPath = null,
		int visionMaxImageEdge = LocalVramFootprintEstimate.ReferenceImageEdge)
	{
		List<GpuInventoryRow> nvidiaGpuInventory = GetNvidiaGpuInventory();
		double gpuGb = nvidiaGpuInventory.Select((GpuInventoryRow row) => row.TotalGb).DefaultIfEmpty(0.0).Max();
		bool nvidia = nvidiaGpuInventory.Count > 0 && gpuGb > 0.0;
		double fileGb = 0.0;
		try
		{
			if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
			{
				fileGb = (double)new FileInfo(modelPath).Length / 1073741824.0;
			}
		}
		catch
		{
		}
		GgufModelShape ggufModelShape = TryReadGgufModelShape(modelPath);
		bool isQwen38 = IsQwen38Model(modelPath);
		bool hybridKv = isQwen38 || IsHybridLinearAttentionArchitecture(ggufModelShape);
		int reportedCtx = ((ggufModelShape != null && ggufModelShape.ContextLength > 0) ? ggufModelShape.ContextLength : 0);
		int modelMaxCtx = LocalVramFootprintEstimate.AdviseModelMaxContext(reportedCtx);
		double gbPer8k = EstimateKvGigabytesPer8k(ggufModelShape, fileGb, hybridKv);
		double overheadBaseGb = (nvidia ? Math.Max(0.7, 0.45 + gpuGb * 0.035 + Math.Max(0.0, fileGb) * 0.02) : 0.6);
		double projectorGb = (visionEnabled ? ResolveVisionProjectorGigabytes(visionProjectorPath, modelPath) : 0.0);
		double visionReserveGb = (visionEnabled
			? LocalVramFootprintEstimate.VisionReserveGiB(projectorGb, visionMaxImageEdge)
			: 0.0);
		double leftoverGb = (nvidia ? Math.Max(0.2, gpuGb - fileGb - overheadBaseGb) : 1.0);
		double overheadGb = overheadBaseGb + projectorGb;
		int weightFitContext = ((nvidia && fileGb > 0.0) ? MaxContextForWeights(fileGb) : 4096);
		int gpuFitContext = (nvidia ? MaxContextForWeights(Math.Min((fileGb <= 0.0) ? (gpuGb * 0.5) : fileGb, gpuGb * 0.48)) : 4096);
		if (gpuFitContext < weightFitContext)
		{
			gpuFitContext = weightFitContext;
		}
		int contextPriorityFirst = Math.Min(modelMaxCtx, Math.Max(gpuFitContext, weightFitContext));
		contextPriorityFirst = LocalVramFootprintEstimate.SoftenSaturatedContext(
			contextPriorityFirst,
			modelMaxCtx,
			leftoverGb);
		if (visionEnabled && visionReserveGb > 0.0)
		{
			contextPriorityFirst = LocalVramFootprintEstimate.ApplyVisionContextHaircut(
				contextPriorityFirst,
				leftoverGb,
				visionReserveGb);
		}
		int contextQuarterCap = Math.Min(modelMaxCtx, Math.Max(16384, modelMaxCtx / 4));
		int contextSafeDefault = ((nvidia && weightFitContext >= contextQuarterCap) ? SnapContextTokens(Math.Min(contextPriorityFirst, weightFitContext)) : SnapContextTokens(Math.Min(contextPriorityFirst, (double)contextPriorityFirst * 0.86)));
		int contextPrioritySecond = SnapContextTokens(Math.Min(contextPriorityFirst, Math.Max(contextSafeDefault, (double)contextPriorityFirst * 0.72)));
		int contextPriorityLower = SnapContextTokens(Math.Min(contextSafeDefault, Math.Max(8192.0, (double)contextSafeDefault * 0.42)));
		if (contextPrioritySecond > contextPriorityFirst)
		{
			contextPrioritySecond = contextPriorityFirst;
		}
		if (contextSafeDefault > contextPrioritySecond)
		{
			contextSafeDefault = contextPrioritySecond;
		}
		if (contextPriorityLower > contextSafeDefault)
		{
			contextPriorityLower = contextSafeDefault;
		}
		string offloadMode = OffloadAt(contextSafeDefault);
		string offloadModeAtMaxContext = OffloadAt(contextPriorityFirst);
		double weightsOnGpuGb = (offloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase) ? fileGb : Math.Min((fileGb <= 0.0) ? (gpuGb * 0.5) : fileGb, gpuGb * 0.48));
		double headroom = (nvidia ? Math.Max(0.2, gpuGb - weightsOnGpuGb - KvGb(contextSafeDefault) - overheadGb) : 1.0);
		int batchSize = SnapBatchSize(256.0 * Math.Sqrt(Math.Max(1.0, nvidia ? gpuGb : 4.0)) * Math.Clamp(headroom / 2.0, 0.55, 1.85));
		int val = SnapBatchSize((double)batchSize / 4.0);
		int maxTokens = SnapTokenOption((int)Math.Clamp((double)contextSafeDefault / 6.0, 4096.0, 16384.0));
		double systemRamGb = (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
		string cacheRam = ((systemRamGb >= 24.0 || headroom >= Math.Max(6.0, fileGb * 0.4)) ? "Unlimited" : SnapCacheRamMb((int)Math.Round(Math.Max(4096.0, headroom * 1024.0 * 0.55))));
		bool preferFit = nvidia && fileGb + KvGb(contextSafeDefault) + overheadGb >= gpuGb * 0.78;
		return new LocalHardwareLaunchAdvice
		{
			GpuTotalGb = gpuGb,
			ModelFileGb = fileGb,
			NvidiaAvailable = nvidia,
			ContextPriorityFirst = contextPriorityFirst,
			ContextPrioritySecond = contextPrioritySecond,
			ContextSafeDefault = contextSafeDefault,
			ContextPriorityLower = contextPriorityLower,
			OffloadMode = offloadMode,
			OffloadModeAtMaxContext = offloadModeAtMaxContext,
			GpuLayers = LayersFor(offloadMode),
			BatchSize = batchSize.ToString(CultureInfo.InvariantCulture),
			UbatchSize = Math.Max(64, val).ToString(CultureInfo.InvariantCulture),
			FlashAttention = (nvidia ? "Enabled" : "Auto"),
			PreferFitEnabled = (preferFit || offloadMode.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase)),
			SpecType = ((isQwen38 || LooksLikeMultiTokenPrediction(modelPath)) ? "draft-mtp" : "ngram-simple"),
			EnableSwaFull = hybridKv,
			MaxTokens = maxTokens.ToString(CultureInfo.InvariantCulture),
			CacheRam = cacheRam
		};
		double KvGb(int tokens)
		{
			return gbPer8k * ((double)Math.Max(1, tokens) / 8192.0);
		}
		static string LayersFor(string mode)
		{
			return mode.Equals("GPU only", StringComparison.OrdinalIgnoreCase) ? "999" : (mode.Equals("CPU only", StringComparison.OrdinalIgnoreCase) ? "0" : "Auto");
		}
		int MaxContextForWeights(double weightsOnGpuGb)
		{
			if (!nvidia || gbPer8k <= 0.0)
			{
				return SnapContextTokens(Math.Min(modelMaxCtx, 8192));
			}
			double offloadBudgetGb = gpuGb - weightsOnGpuGb - overheadGb;
			if (offloadBudgetGb < 0.15)
			{
				return 4096;
			}
			return SnapContextTokens(Math.Min(modelMaxCtx, 8192.0 * (offloadBudgetGb / gbPer8k) * 0.92));
		}
		string OffloadAt(int tokens)
		{
			if (!nvidia)
			{
				return "CPU only";
			}
			return (fileGb + KvGb(tokens) + overheadGb <= gpuGb + 0.05) ? "GPU only" : "GPU + CPU";
		}
	}

	private static bool LooksLikeMultiTokenPrediction(string modelPath)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(modelPath ?? string.Empty);
		return fileNameWithoutExtension.Contains("mtp", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsHybridLinearAttentionArchitecture(GgufModelShape? shape)
	{
		if (shape == null)
		{
			return false;
		}
		string architecture = shape.Architecture;
		return architecture.Contains("delta", StringComparison.OrdinalIgnoreCase) || architecture.Contains("linear", StringComparison.OrdinalIgnoreCase) || architecture.Contains("gated", StringComparison.OrdinalIgnoreCase) || (shape.SlidingWindow > 0 && shape.ContextLength > 0 && shape.SlidingWindow < shape.ContextLength);
	}

	private static double EstimateKvGigabytesPer8k(GgufModelShape? shape, double fileGb, bool hybridKv)
	{
		double hybridKvScale = (hybridKv ? 0.38 : 1.0);
		if (shape != null && shape.BlockCount > 0 && shape.EmbeddingLength > 0 && shape.HeadCount > 0)
		{
			int headCountKv = ((shape.HeadCountKv > 0) ? shape.HeadCountKv : shape.HeadCount);
			int headDim = Math.Max(1, shape.EmbeddingLength / Math.Max(1, shape.HeadCount));
			double kvBytesPer8k = 2.0 * (double)shape.BlockCount * (double)headCountKv * (double)headDim * 8192.0 * hybridKvScale;
			return Math.Max(0.08, kvBytesPer8k / 1073741824.0);
		}
		double fallbackKvGb = Math.Max(0.12, 0.28 * ((fileGb <= 0.0) ? 1.0 : (fileGb / 10.0)));
		return fallbackKvGb * hybridKvScale;
	}

	private GgufModelShape? TryReadGgufModelShape(string modelPath)
	{
		if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
		{
			return null;
		}
		FileInfo fileInfo;
		try
		{
			fileInfo = new FileInfo(modelPath);
		}
		catch
		{
			return null;
		}
		string fullPath = Path.GetFullPath(modelPath);
		lock (_ggufShapeLock)
		{
			if (_ggufShapeCache.TryGetValue(fullPath, out GgufModelShape value) && value.FileLength == fileInfo.Length && value.LastWriteUtc == fileInfo.LastWriteTimeUtc)
			{
				return value;
			}
		}
		try
		{
			using FileStream input = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using BinaryReader binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
			byte[] array = binaryReader.ReadBytes(4);
			if (array.Length != 4 || array[0] != 71 || array[1] != 71 || array[2] != 85 || array[3] != 70)
			{
				return null;
			}
			uint version = binaryReader.ReadUInt32();
			if ((version < 1 || version > 3) ? true : false)
			{
				return null;
			}
			binaryReader.ReadUInt64();
			ulong metadataCount = binaryReader.ReadUInt64();
			if (metadataCount > 4096)
			{
				return null;
			}
			string architecture = string.Empty;
			int blockCount = 0;
			int embeddingLength = 0;
			int headCount = 0;
			int headCountKv = 0;
			int contextLength = 0;
			int slidingWindow = 0;
			for (ulong metadataIndex = 0uL; metadataIndex < metadataCount; metadataIndex++)
			{
				string keyName = ReadGgufString(binaryReader);
				uint type = binaryReader.ReadUInt32();
				int value2;
				if (keyName.Equals("general.architecture", StringComparison.Ordinal))
				{
					architecture = ReadGgufValueAsString(binaryReader, type);
				}
				else if (!TryReadGgufValueAsInt(binaryReader, type, out value2))
				{
					SkipGgufValue(binaryReader, type);
				}
				else if (keyName.EndsWith(".block_count", StringComparison.Ordinal))
				{
					blockCount = value2;
				}
				else if (keyName.EndsWith(".embedding_length", StringComparison.Ordinal))
				{
					embeddingLength = value2;
				}
				else if (keyName.EndsWith(".attention.head_count", StringComparison.Ordinal))
				{
					headCount = value2;
				}
				else if (keyName.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
				{
					headCountKv = value2;
				}
				else if (keyName.EndsWith(".context_length", StringComparison.Ordinal))
				{
					contextLength = value2;
				}
				else if (keyName.EndsWith(".attention.sliding_window", StringComparison.Ordinal) || keyName.EndsWith(".sliding_window", StringComparison.Ordinal))
				{
					slidingWindow = value2;
				}
			}
			GgufModelShape ggufModelShape = new GgufModelShape
			{
				Architecture = architecture,
				BlockCount = blockCount,
				EmbeddingLength = embeddingLength,
				HeadCount = headCount,
				HeadCountKv = headCountKv,
				ContextLength = contextLength,
				SlidingWindow = slidingWindow,
				FileLength = fileInfo.Length,
				LastWriteUtc = fileInfo.LastWriteTimeUtc
			};
			lock (_ggufShapeLock)
			{
				_ggufShapeCache[fullPath] = ggufModelShape;
			}
			return ggufModelShape;
		}
		catch
		{
			return null;
		}
	}

	private static string ReadGgufString(BinaryReader reader)
	{
		ulong byteCount = reader.ReadUInt64();
		if (byteCount > 1000000)
		{
			throw new InvalidDataException("GGUF string too large.");
		}
		byte[] bytes = reader.ReadBytes((int)byteCount);
		return Encoding.UTF8.GetString(bytes);
	}

	private static string ReadGgufValueAsString(BinaryReader reader, uint type)
	{
		if (type == 8)
		{
			return ReadGgufString(reader);
		}
		SkipGgufValue(reader, type);
		return string.Empty;
	}

	private static bool TryReadGgufValueAsInt(BinaryReader reader, uint type, out int value)
	{
		value = 0;
		switch (type)
		{
		case 0u:
			value = reader.ReadByte();
			return true;
		case 1u:
			value = reader.ReadSByte();
			return true;
		case 2u:
			value = reader.ReadUInt16();
			return true;
		case 3u:
			value = reader.ReadInt16();
			return true;
		case 4u:
			value = (int)Math.Min(2147483647u, reader.ReadUInt32());
			return true;
		case 5u:
			value = reader.ReadInt32();
			return true;
		case 10u:
			value = (int)Math.Min(2147483647uL, reader.ReadUInt64());
			return true;
		case 11u:
			value = (int)Math.Clamp(reader.ReadInt64(), -2147483648L, 2147483647L);
			return true;
		default:
			return false;
		}
	}

	private static void SkipGgufValue(BinaryReader reader, uint type)
	{
		switch (type)
		{
		case 0u:
		case 1u:
		case 7u:
			reader.ReadByte();
			break;
		case 2u:
		case 3u:
			reader.ReadUInt16();
			break;
		case 4u:
		case 5u:
		case 6u:
			reader.ReadUInt32();
			break;
		case 8u:
			ReadGgufString(reader);
			break;
		case 9u:
		{
			uint type2 = reader.ReadUInt32();
			ulong count = reader.ReadUInt64();
			if (count > 1000000)
			{
				throw new InvalidDataException("GGUF array too large.");
			}
			for (ulong index = 0uL; index < count; index++)
			{
				SkipGgufValue(reader, type2);
			}
			break;
		}
		case 10u:
		case 11u:
		case 12u:
			reader.ReadUInt64();
			break;
		default:
			throw new InvalidDataException("Unknown GGUF value type.");
		}
	}

	private static int SnapTokenOption(int value)
	{
		int[] source = new int[5] { 2048, 4096, 8192, 16384, 32768 };
		return source.OrderBy((int option) => Math.Abs(option - Math.Clamp(value, 2048, 32768))).First();
	}

	private static int SnapContextTokens(double tokens)
		=> LocalVramFootprintEstimate.SnapContextTokens(tokens);

	private double ResolveVisionProjectorGigabytes(string? visionProjectorPath, string modelPath)
	{
		try
		{
			string resolved = ResolveVisionProjectorPath(visionProjectorPath ?? string.Empty, modelPath, null);
			if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
			{
				return Math.Max(
					LocalVramFootprintEstimate.ProjectorGiB,
					(double)new FileInfo(resolved).Length / 1073741824.0);
			}
		}
		catch
		{
		}

		return LocalVramFootprintEstimate.ProjectorGiB;
	}

	private static int SnapBatchSize(double value)
	{
		double a = Math.Clamp(value, 64.0, 2048.0);
		double value2 = Math.Pow(2.0, Math.Round(Math.Log(a, 2.0)));
		return (int)Math.Clamp(value2, 64.0, 2048.0);
	}

	private static string SnapCacheRamMb(int megabytes)
	{
		int[] source = new int[3] { 4096, 8192, 16384 };
		return source.OrderBy((int option) => Math.Abs(option - Math.Max(4096, megabytes))).First().ToString(CultureInfo.InvariantCulture);
	}

	private static JsonObject ResolveCloudProfile(JsonObject root, string provider, string model, string variant)
	{
		if (string.IsNullOrWhiteSpace(variant) || variant.Trim().Equals("Base (read-only)", StringComparison.OrdinalIgnoreCase) || variant.Trim().Equals("(defaults)", StringComparison.OrdinalIgnoreCase))
		{
			return new JsonObject();
		}
		if (!(root["CloudProfiles"] is JsonObject jsonObject))
		{
			return new JsonObject();
		}
		string value = (provider ?? string.Empty).Trim();
		string value2 = (model ?? string.Empty).Trim();
		string value3 = (string.IsNullOrWhiteSpace(variant) ? "Variant 1" : variant.Trim());
		JsonObject jsonObject2 = null;
		JsonObject jsonObject3 = null;
		foreach (KeyValuePair<string, JsonNode> item in jsonObject)
		{
			string profileKey = item.Key ?? string.Empty;
			string[] array = profileKey.Split("::", 3, StringSplitOptions.TrimEntries);
			if (array.Length < 2)
			{
				continue;
			}
			string keyProvider = array[0];
			string keyModel = array[1];
			string keyVariant = ((array.Length >= 3) ? array[2] : "Variant 1");
			if (keyProvider.Equals(value, StringComparison.OrdinalIgnoreCase) && keyModel.Equals(value2, StringComparison.OrdinalIgnoreCase) && item.Value is JsonObject jsonObject4)
			{
				if (jsonObject2 == null)
				{
					jsonObject2 = jsonObject4;
				}
				if (keyVariant.Equals("Variant 1", StringComparison.OrdinalIgnoreCase) && jsonObject3 == null)
				{
					jsonObject3 = jsonObject4;
				}
				if (keyVariant.Equals(value3, StringComparison.OrdinalIgnoreCase))
				{
					return jsonObject4;
				}
			}
		}
		return jsonObject3 ?? jsonObject2 ?? new JsonObject();
	}

	private static string BuildSettingsSnapshot(JsonObject profile, params string[] keys)
	{
		List<string> list = new List<string>();
		foreach (string key in keys)
		{
			string value = GetString(profile, key);
			if (!string.IsNullOrWhiteSpace(value))
			{
				list.Add(key + "=" + value);
			}
		}
		return (list.Count > 0) ? string.Join(", ", list) : "defaults/unspecified";
	}

	private static int GetProfileInt(JsonObject profile, string key, int fallback)
	{
		string s = GetString(profile, key);
		int result;
		return (int.TryParse(s, out result) && result > 0) ? result : fallback;
	}

	private static double GetProfileDouble(JsonObject profile, string key, double fallback)
	{
		string s = GetString(profile, key);
		double result;
		return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
	}

	private static string QuoteArg(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "\"\"";
		}
		return (value.IndexOfAny(new char[3] { ' ', '\t', '"' }) >= 0) ? ("\"" + value.Replace("\"", "\\\"") + "\"") : value;
	}

	private static JsonObject LoadJson(string path)
	{
		if (!File.Exists(path))
		{
			return new JsonObject();
		}
		try
		{
			JsonNode jsonNode = JsonNode.Parse(File.ReadAllText(path));
			return (jsonNode as JsonObject) ?? new JsonObject();
		}
		catch
		{
			return new JsonObject();
		}
	}

	private static string NormalizeTelemetryToken(string value)
	{
		string normalized = (value ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(normalized))
		{
			return string.Empty;
		}
		return normalized.Replace("::", "_").Replace("\r", " ").Replace("\n", " ");
	}

	private static string BuildLocalLaunchTelemetryKey(string model, string variant)
	{
		return NormalizeTelemetryToken(model) + "::" + CanonicalLaunchTelemetryVariant(variant);
	}

	private static string CanonicalLaunchTelemetryVariant(string? variant)
	{
		string trimmed = (variant ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Equals("(defaults)", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Variant 1", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Base (read-only)", StringComparison.OrdinalIgnoreCase))
		{
			return "(defaults)";
		}
		return NormalizeTelemetryToken(trimmed);
	}

	private static string FormatDurationMs(int milliseconds)
	{
		if (milliseconds < 1000)
		{
			return milliseconds + " ms";
		}
		return ((double)milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
	}

	private JsonObject GetOrCreateFluxMuxMeta(JsonObject root)
	{
		JsonObject jsonObject = root["FluxMuxMeta"] as JsonObject;
		if (jsonObject == null)
		{
			jsonObject = (JsonObject)(root["FluxMuxMeta"] = new JsonObject());
		}
		return jsonObject;
	}

	private JsonObject? GetLocalLaunchTelemetryEntry(string model, string variant)
	{
		JsonObject jsonObject = LoadJsonCached(_configPath);
		if (!(jsonObject["FluxMuxMeta"] is JsonObject jsonObject2))
		{
			return null;
		}
		if (!(jsonObject2["LocalLaunchTelemetry"] is JsonObject jsonObject3))
		{
			return null;
		}
		string propertyName = BuildLocalLaunchTelemetryKey(model, variant);
		return jsonObject3[propertyName] as JsonObject;
	}

	public int GetLocalLaunchWarmupBudgetMs(string model, string variant)
	{
		try
		{
			JsonObject localLaunchTelemetryEntry = GetLocalLaunchTelemetryEntry(model, variant);
			int slowestMs = ParseInt(localLaunchTelemetryEntry?["slowest_ms"]?.ToString() ?? string.Empty, 0);
			int medianMs = ParseInt(localLaunchTelemetryEntry?["median_ms"]?.ToString() ?? string.Empty, 0);
			int fastestMs = ParseInt(localLaunchTelemetryEntry?["fastest_ms"]?.ToString() ?? string.Empty, 0);
			int recordedMs = slowestMs > 0 ? slowestMs : (medianMs > 0 ? medianMs : fastestMs);
			if (recordedMs <= 0)
			{
				return 90000;
			}
			return Math.Clamp(recordedMs + 30000, 60000, 180000);
		}
		catch
		{
			return 90000;
		}
	}

	public int GetLocalLaunchFastestMs(string model, string variant)
	{
		try
		{
			return Math.Max(0, ParseInt(GetLocalLaunchTelemetryEntry(model, variant)?["fastest_ms"]?.ToString() ?? string.Empty, 0));
		}
		catch
		{
			return 0;
		}
	}

	public int GetLocalLaunchEstimateMs(string model, string variant)
	{
		try
		{
			JsonObject localLaunchTelemetryEntry = GetLocalLaunchTelemetryEntry(model, variant);
			int slowest = ParseInt(localLaunchTelemetryEntry?["slowest_ms"]?.ToString() ?? string.Empty, 0);
			int median = ParseInt(localLaunchTelemetryEntry?["median_ms"]?.ToString() ?? string.Empty, 0);
			int fastest = ParseInt(localLaunchTelemetryEntry?["fastest_ms"]?.ToString() ?? string.Empty, 0);
			if (slowest > 0)
			{
				return slowest;
			}

			return Math.Max(0, Math.Max(median, fastest));
		}
		catch
		{
			return 0;
		}
	}

	public string GetLocalLaunchTelemetrySummary(string model, string variant)
	{
		try
		{
			JsonObject localLaunchTelemetryEntry = GetLocalLaunchTelemetryEntry(model, variant);
			if (localLaunchTelemetryEntry == null)
			{
				return string.Empty;
			}
			int countdown = GetLocalLaunchEstimateMs(model, variant);
			string duration = ((countdown > 0) ? FormatDurationMs(countdown) : "n/a");
			return "Recorded cold load ~" + duration + " (warm load can finish sooner)";
		}
		catch
		{
			return string.Empty;
		}
	}

	public string GetLocalValidateSpeedSummary(string model, string variant)
	{
		try
		{
			JsonObject root = LoadJsonCached(_configPath);
			if (root["LocalProfiles"] is not JsonObject profiles)
			{
				return string.Empty;
			}

			JsonObject? settings = FindLocalProfile(profiles, model, FirstNonEmpty(variant, "(defaults)"));
			if (settings is null)
			{
				return string.Empty;
			}

			var ttftMs = ParseInt(settings["ValidateTtftMs"]?.ToString() ?? string.Empty, 0);
			var tokPerSec = ParseDouble(settings["ValidateTokensPerSecond"]?.ToString() ?? string.Empty, 0d);
			if (ttftMs <= 0 && tokPerSec <= 0)
			{
				return string.Empty;
			}

			var ttftText = ttftMs >= 1000
				? (ttftMs / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + " s"
				: ttftMs.ToString(CultureInfo.InvariantCulture) + " ms";
			var speedText = tokPerSec > 0
				? tokPerSec.ToString("0.#", CultureInfo.InvariantCulture) + " tok/s"
				: string.Empty;
			return string.IsNullOrWhiteSpace(speedText)
				? "Validate probe TTFT " + ttftText
				: "Validate probe TTFT " + ttftText + " · " + speedText;
		}
		catch
		{
			return string.Empty;
		}
	}

	public string GetLocalLaunchTelemetryDetailText(string model, string variant)
	{
		try
		{
			JsonObject localLaunchTelemetryEntry = GetLocalLaunchTelemetryEntry(model, variant);
			if (localLaunchTelemetryEntry != null)
			{
				int sampleCount = ParseInt(localLaunchTelemetryEntry["sample_count"]?.ToString() ?? string.Empty, 0);
				int fastestMs = ParseInt(localLaunchTelemetryEntry["fastest_ms"]?.ToString() ?? string.Empty, 0);
				int medianMs = ParseInt(localLaunchTelemetryEntry["median_ms"]?.ToString() ?? string.Empty, 0);
				int slowestMs = ParseInt(localLaunchTelemetryEntry["slowest_ms"]?.ToString() ?? string.Empty, 0);
				int lastMs = ParseInt(localLaunchTelemetryEntry["last_ms"]?.ToString() ?? string.Empty, 0);
				string value = FirstNonEmpty(localLaunchTelemetryEntry["last_status"]?.ToString() ?? string.Empty, "ready");
				string value2 = FirstNonEmpty(localLaunchTelemetryEntry["last_updated_utc"]?.ToString() ?? string.Empty, "unknown time");
				string value3 = ((sampleCount == 1) ? "1 launch" : (sampleCount + " launches"));
				string fastestText = ((fastestMs > 0) ? FormatDurationMs(fastestMs) : "n/a");
				string slowestText = ((slowestMs > 0) ? FormatDurationMs(slowestMs) : FormatDurationMs(medianMs));
				return $"Solo launch telemetry: {value3}, cold-load countdown {slowestText}, warm-load fastest {fastestText}, median {FormatDurationMs(medianMs)}, last {FormatDurationMs(lastMs)} ({value}, {value2}). The countdown follows a cold llama-server load so a warm load can finish sooner. VRAM, drivers, and background load can still change it.";
			}
			return $"No launch history recorded yet for {model} / {FirstNonEmpty(variant, "Variant 1")}. Higher VRAM headroom, fewer background tasks, and a fresh launch after driver changes usually produce better startup times.";
		}
		catch
		{
			return $"Could not read launch history for {model} / {FirstNonEmpty(variant, "Variant 1")}. Higher VRAM headroom, fewer background tasks, and a fresh launch after driver changes usually produce better startup times.";
		}
	}

	public void RecordLocalLaunchTelemetry(string model, string variant, int elapsedMs, string status)
	{
		try
		{
			JsonObject jsonObject = LoadJsonCached(_configPath);
			JsonObject orCreateFluxMuxMeta = GetOrCreateFluxMuxMeta(jsonObject);
			JsonObject jsonObject2 = orCreateFluxMuxMeta["LocalLaunchTelemetry"] as JsonObject;
			if (jsonObject2 == null)
			{
				jsonObject2 = (JsonObject)(orCreateFluxMuxMeta["LocalLaunchTelemetry"] = new JsonObject());
			}
			string propertyName = BuildLocalLaunchTelemetryKey(model, variant);
			JsonObject jsonObject3 = jsonObject2[propertyName] as JsonObject;
			if (jsonObject3 == null)
			{
				jsonObject3 = (JsonObject)(jsonObject2[propertyName] = new JsonObject());
			}
			JsonArray jsonArray = jsonObject3["samples"] as JsonArray;
			if (jsonArray == null)
			{
				jsonArray = (JsonArray)(jsonObject3["samples"] = new JsonArray());
			}
			jsonArray.Add(elapsedMs);
			while (jsonArray.Count > LocalLaunchCountdownEstimate.SampleLimit)
			{
				jsonArray.RemoveAt(0);
			}
			int[] array = (from node in jsonArray
				select ParseInt(node?.ToString() ?? string.Empty, 0) into ms
				where ms > 0
				orderby ms
				select ms).ToArray();
			if (array.Length == 0)
			{
				array = new int[1] { elapsedMs };
			}
			var stats = LocalLaunchCountdownEstimate.FromSamples(array);
			int previousSlowest = ParseInt(jsonObject3["slowest_ms"]?.ToString() ?? string.Empty, 0);
			int countdownMs = LocalLaunchCountdownEstimate.CountdownMs(stats, previousSlowest);
			jsonObject3["sample_count"] = stats.Count;
			jsonObject3["last_ms"] = elapsedMs;
			jsonObject3["fastest_ms"] = stats.FastestMs;
			jsonObject3["median_ms"] = stats.MedianMs;
			jsonObject3["slowest_ms"] = countdownMs;
			jsonObject3["last_status"] = status;
			jsonObject3["last_updated_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			File.WriteAllText(_configPath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
			DateTime item = (File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : DateTime.UtcNow);
			lock (_jsonCacheLock)
			{
				_jsonCache[_configPath] = (item, jsonObject);
			}
		}
		catch
		{
		}
	}

	private double EstimateManagedLocalReclaimableVramGiB(string modelDirectory)
	{
		if (string.IsNullOrWhiteSpace(_managedLocalModel))
		{
			return 0;
		}

		try
		{
			JsonObject root = LoadJsonCached(_configPath);
			JsonObject profile = ResolveLocalProfile(root, _managedLocalModel, _managedLocalVariant);
			double measured = GetProfileDouble(profile, "MeasuredVramGiB", 0);
			if (measured > 0)
			{
				return measured;
			}

			string path = ResolveModelPath(_managedLocalModel, modelDirectory);
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return 0;
			}

			double fileGiB = new FileInfo(path).Length / 1073741824.0;
			(bool Enabled, string ProjectorPath, int MaxImageEdge) vision = ResolveProfileVision(profile, path);
			return LocalVramFootprintEstimate.EstimateGiB(profile, fileGiB, vision.Enabled);
		}
		catch
		{
			return 0;
		}
	}

	public void RecordLocalMeasuredVram(string model, string variant, double baselineGiB, double afterGiB, bool imagesOn)
	{
		double deltaGiB = Math.Max(0, Math.Round(afterGiB - baselineGiB, 2));
		if (deltaGiB <= 0)
		{
			return;
		}

		TryUpdateLocalProfileSettings(model, variant, settings =>
		{
			settings["MeasuredVramGiB"] = deltaGiB;
			settings["MeasuredVramBaselineGiB"] = Math.Round(baselineGiB, 2);
			settings["MeasuredVramAfterGiB"] = Math.Round(afterGiB, 2);
			settings["MeasuredVramUpdatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			settings["MeasuredVramImagesOn"] = imagesOn;
		});
	}

	private void TryUpdateLocalProfileSettings(string model, string variant, Action<JsonObject> update)
	{
		try
		{
			JsonObject root = LoadJsonCached(_configPath);
			if (root["LocalProfiles"] is not JsonObject profiles)
			{
				return;
			}

			JsonObject? profile = FindLocalProfile(profiles, model, FirstNonEmpty(variant, "(defaults)"));
			if (profile is null)
			{
				return;
			}

			update(profile);
			File.WriteAllText(_configPath, root.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
			DateTime item = (File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : DateTime.UtcNow);
			lock (_jsonCacheLock)
			{
				_jsonCache[_configPath] = (item, root);
			}
		}
		catch
		{
		}
	}

	private JsonObject LoadJsonCached(string path)
	{
		DateTime dateTime = (File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue);
		lock (_jsonCacheLock)
		{
			if (_jsonCache.TryGetValue(path, out (DateTime, JsonObject) value) && value.Item1 == dateTime)
			{
				return value.Item2;
			}
			JsonObject jsonObject = LoadJson(path);
			_jsonCache[path] = (dateTime, jsonObject);
			return jsonObject;
		}
	}

	private async Task<bool> IsCloudBridgeReadyAsync(int proxyPort, CancellationToken cancellationToken)
	{
		if (proxyPort < 1 || proxyPort > 65535)
		{
			return false;
		}
		if (!IsTcpPortOpen("127.0.0.1", proxyPort))
		{
			return false;
		}
		try
		{
			using HttpResponseMessage response = await HttpClient.GetAsync($"http://127.0.0.1:{proxyPort}/health", cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				return false;
			}
			return string.Equals((JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject)?["gateway"]?.ToString(), "FluxMux", StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private int ResolveOrchestratorPort(JsonObject root)
	{
		string s = GetString(root, "OrchestratorPort");
		int result;
		return (int.TryParse(s, out result) && result >= 1 && result <= 65535) ? result : 8080;
	}

	private void WriteProxyRuntimeStateForCloud(JsonObject configRoot, JsonObject secretsRoot, string provider, string model, int proxyPort, string phase, string details, int bridgePid)
	{
		try
		{
			string endpoint = provider switch
			{
				"Gemini" => "https://generativelanguage.googleapis.com/v1beta/openai", 
				"Anthropic" => "https://api.anthropic.com/v1/messages", 
				"Copilot GitHub" => FirstNonEmpty(GetString(configRoot, "GitHubCopilotEndpoint"), "https://api.githubcopilot.com"), 
				_ => CustomCloudProviderRegistry.IsCustomCompat(provider, configRoot)
					? FirstNonEmpty(CustomCloudProviderRegistry.GetEndpoint(configRoot, provider), "https://api.openai.com/v1")
					: FirstNonEmpty(GetString(configRoot, "OpenAiEndpoint"), "https://api.openai.com/v1"), 
			};
			var isCustomCompat = CustomCloudProviderRegistry.IsCustomCompat(provider, configRoot);
			lock (_proxyStateLock)
			{
				bool preserveLocalState = IsDualHotRouting() && IsManagedLocalAlive();
				JsonObject previous = LoadJson(_proxyRuntimeStatePath);
				JsonObject jsonObject = (preserveLocalState ? previous : new JsonObject());
				if (!preserveLocalState)
				{
					RememberLastLocalCatalog(previous, jsonObject);
					jsonObject["local_phase"] = string.Empty;
					jsonObject["local_preferred_model"] = string.Empty;
					jsonObject["local_model_path"] = string.Empty;
					jsonObject["local_context"] = string.Empty;
					jsonObject["local_profile_key"] = string.Empty;
					jsonObject["local_port"] = proxyPort + 1;
				}
				jsonObject["provider"] = provider;
				jsonObject["endpoint"] = endpoint;
				jsonObject["preferred_model"] = model;
				jsonObject["cloud_preferred_model"] = model;
				jsonObject["cloud_phase"] = phase;
				WriteLastSuccessfulCloud(jsonObject);
				jsonObject["gemini_key"] = FirstNonEmpty(GetString(secretsRoot, "GeminiApiKey"), GetString(configRoot, "GeminiApiKey"));
				jsonObject["anthropic_key"] = FirstNonEmpty(GetString(secretsRoot, "AnthropicApiKey"), GetString(configRoot, "AnthropicApiKey"));
				jsonObject["openai_key"] = FirstNonEmpty(GetString(secretsRoot, "OpenAiApiKey"), GetString(configRoot, "OpenAiApiKey"));
				jsonObject["custom_key"] = isCustomCompat
					? CustomCloudProviderRegistry.GetApiKey(secretsRoot, configRoot, provider)
					: string.Empty;
				jsonObject["custom_endpoint"] = isCustomCompat
					? CustomCloudProviderRegistry.GetEndpoint(configRoot, provider)
					: string.Empty;
				jsonObject["custom_models"] = GetString(configRoot, "CustomCompatModels");
				jsonObject["custom_auth_mode"] = isCustomCompat
					? CustomCloudProviderRegistry.GetAuthMode(configRoot, provider)
					: FirstNonEmpty(GetString(configRoot, "CustomCompatAuthMode"), "Bearer");
				jsonObject["custom_api_key_header"] = isCustomCompat
					? CustomCloudProviderRegistry.GetApiKeyHeader(configRoot, provider)
					: FirstNonEmpty(GetString(configRoot, "CustomCompatApiKeyHeader"), "Authorization");
				jsonObject["custom_models_path"] = isCustomCompat
					? CustomCloudProviderRegistry.GetModelsPath(configRoot, provider)
					: FirstNonEmpty(GetString(configRoot, "CustomCompatModelsPath"), "/models");
				jsonObject["custom_chat_path"] = isCustomCompat
					? CustomCloudProviderRegistry.GetChatPath(configRoot, provider)
					: FirstNonEmpty(GetString(configRoot, "CustomCompatChatPath"), "/chat/completions");
				jsonObject["custom_apply_openai_tweaks"] = isCustomCompat
					? CustomCloudProviderRegistry.GetApplyOpenAiTweaks(configRoot, provider)
					: ParseBool(GetString(configRoot, "CustomCompatApplyOpenAiTweaks"), fallback: true);
				jsonObject["is_custom_compat"] = isCustomCompat;
				jsonObject["copilot_key"] = FirstNonEmpty(GetString(secretsRoot, "GitHubCopilotApiKey"), GetString(configRoot, "GitHubCopilotApiKey"));
				jsonObject["copilot_endpoint"] = FirstNonEmpty(GetString(configRoot, "GitHubCopilotEndpoint"), "https://api.githubcopilot.com");
				jsonObject["temperature"] = ParseDouble(GetString(configRoot, "CloudTemperature"), 0.7);
				jsonObject["max_tokens"] = ParseInt(GetString(configRoot, "CloudMaxTokens"), 2048);
				jsonObject["context_window"] = FirstNonEmpty(GetString(configRoot, "CloudContextWindow"), "Auto");
				jsonObject["reasoning_mode"] = FirstNonEmpty(GetString(configRoot, "CloudReasoningMode"), "Balanced");
				jsonObject["gemini_thinking_mode"] = FirstNonEmpty(GetString(configRoot, "GeminiThinkingMode"), "Balanced");
				jsonObject["gemini_thinking_budget"] = ParseInt(GetString(configRoot, "GeminiThinkingBudget"), 0);
				jsonObject["anthropic_thinking_mode"] = FirstNonEmpty(GetString(configRoot, "AnthropicThinkingMode"), "Standard");
				jsonObject["openai_reasoning_effort"] = FirstNonEmpty(GetString(configRoot, "OpenAiReasoningEffort"), "Auto");
				jsonObject["copilot_reasoning_effort"] = FirstNonEmpty(GetString(configRoot, "CopilotReasoningEffort"), "Auto");
				jsonObject["response_format"] = FirstNonEmpty(GetString(configRoot, "CloudResponseFormat"), "Text");
				ApplyDiscoveredCloudTraits(jsonObject, configRoot, provider, model);
				jsonObject["bridge_pid"] = bridgePid;
				jsonObject["proxy_port"] = proxyPort;
				jsonObject["details"] = details;
				jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
				ApplyResolvedProxyMode(jsonObject);
				SaveProxyRuntimeState(jsonObject);
			}
		}
		catch
		{
		}
	}

	private void WriteProxyRuntimeStateForLocal(string model, int proxyPort, int localPort, string phase, string details, int bridgePid)
	{
		try
		{
			lock (_proxyStateLock)
			{
				bool cloudAlsoHot = _requestRoutingEnabled && IsCloudRouteActive();
				JsonObject jsonObject = (cloudAlsoHot ? LoadJson(_proxyRuntimeStatePath) : new JsonObject());
				if (!cloudAlsoHot)
				{
					jsonObject["cloud_phase"] = string.Empty;
					jsonObject["cloud_preferred_model"] = string.Empty;
					jsonObject["provider"] = "Local";
					jsonObject["endpoint"] = $"http://127.0.0.1:{localPort}/v1";
				}
				jsonObject["local_port"] = localPort;
				jsonObject["local_preferred_model"] = model;
				jsonObject["local_phase"] = phase;
				jsonObject["local_reasoning"] = (string.IsNullOrWhiteSpace(_managedLocalReasoning) ? "Off" : _managedLocalReasoning);
				jsonObject["local_variant"] = FirstNonEmpty(_managedLocalVariant, "(defaults)");
				jsonObject["last_local_preferred_model"] = model;
				jsonObject["last_local_variant"] = FirstNonEmpty(_managedLocalVariant, "(defaults)");
				jsonObject["local_vision"] = _managedLocalVisionEnabled ? "Enabled" : "Disabled";
				jsonObject["local_context"] = _managedLocalContext;
				jsonObject["local_max_tokens"] = _managedLocalMaxTokens;
				jsonObject["bridge_pid"] = bridgePid;
				jsonObject["proxy_port"] = proxyPort;
				jsonObject["details"] = details;
				jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
				if (!cloudAlsoHot)
				{
					jsonObject["preferred_model"] = model;
				}
				WriteLastSuccessfulCloud(jsonObject);
				ApplyResolvedProxyMode(jsonObject);
				SaveProxyRuntimeState(jsonObject);
			}
		}
		catch
		{
		}
	}

	private static string CloudTraitCacheKey(string provider, string model)
		=> (provider ?? string.Empty).Trim() + "\n" + (model ?? string.Empty).Trim();

	private void ApplyDiscoveredCloudTraits(JsonObject state, JsonObject configRoot, string provider, string model)
	{
		var profile = ResolveCloudProfile(configRoot, provider, model, string.Empty);
		var vision = GetString(profile, "CloudVisionEnabled");
		var context = GetString(profile, "CloudContextWindow");
		var maxTokens = GetString(profile, "CloudMaxTokens");
		if (_cloudCatalogTraits.TryGetValue(CloudTraitCacheKey(provider, model), out var traits))
		{
			if (string.IsNullOrWhiteSpace(vision) && traits.Vision.HasValue)
			{
				vision = traits.Vision.Value ? "Enabled" : "Disabled";
			}

			if ((string.IsNullOrWhiteSpace(context) || context.Equals("Auto", StringComparison.OrdinalIgnoreCase))
				&& traits.ContextTokens is > 0)
			{
				context = traits.ContextTokens.Value.ToString(CultureInfo.InvariantCulture);
			}

			if ((string.IsNullOrWhiteSpace(maxTokens) || maxTokens == "2048") && traits.MaxTokens is > 0)
			{
				maxTokens = traits.MaxTokens.Value.ToString(CultureInfo.InvariantCulture);
			}
		}

		if (!string.IsNullOrWhiteSpace(vision))
		{
			state["cloud_vision"] = vision;
		}

		if (!string.IsNullOrWhiteSpace(context) && !context.Equals("Auto", StringComparison.OrdinalIgnoreCase))
		{
			state["context_window"] = context;
		}

		if (!string.IsNullOrWhiteSpace(maxTokens))
		{
			state["max_tokens"] = maxTokens;
		}
	}

	private void WriteLastSuccessfulCloud(JsonObject state)
	{
		if (string.IsNullOrWhiteSpace(_lastSuccessfulCloudProvider)
			|| string.IsNullOrWhiteSpace(_lastSuccessfulCloudModel))
		{
			return;
		}

		state["last_cloud_provider"] = _lastSuccessfulCloudProvider;
		state["last_cloud_model"] = _lastSuccessfulCloudModel;
	}

	private void RememberLastSuccessfulCloudFromState(JsonObject existing)
	{
		if (!string.IsNullOrWhiteSpace(_lastSuccessfulCloudProvider)
			&& !string.IsNullOrWhiteSpace(_lastSuccessfulCloudModel))
		{
			return;
		}

		var provider = GetString(existing, "last_cloud_provider");
		var model = GetString(existing, "last_cloud_model");
		if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
		{
			return;
		}

		_lastSuccessfulCloudProvider = provider;
		_lastSuccessfulCloudModel = model;
	}

	private static void RememberLastLocalCatalog(JsonObject from, JsonObject to)
	{
		if (from is null || to is null)
		{
			return;
		}

		string model = FirstNonEmpty(GetString(from, "last_local_preferred_model"), GetString(from, "local_preferred_model"));
		string variant = FirstNonEmpty(GetString(from, "last_local_variant"), GetString(from, "local_variant"));
		if (!string.IsNullOrWhiteSpace(model))
		{
			to["last_local_preferred_model"] = model;
		}

		if (!string.IsNullOrWhiteSpace(variant))
		{
			to["last_local_variant"] = variant;
		}
	}

	private void DemoteCloudFromProxyState(string reason)
	{
			lock (_proxyStateLock)
			{
				JsonObject jsonObject = LoadJson(_proxyRuntimeStatePath);
				RememberLastSuccessfulCloudFromState(jsonObject);
				jsonObject["cloud_phase"] = string.Empty;
				jsonObject["cloud_preferred_model"] = string.Empty;
				WriteLastSuccessfulCloud(jsonObject);
			jsonObject["stopped_reason"] = reason;
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			ApplyResolvedProxyMode(jsonObject);
			SaveProxyRuntimeState(jsonObject);
		}
	}

	private void DemoteLocalFromProxyState(string reason)
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = LoadJson(_proxyRuntimeStatePath);
			RememberLastLocalCatalog(jsonObject, jsonObject);
			jsonObject["local_phase"] = string.Empty;
			jsonObject["local_preferred_model"] = string.Empty;
			jsonObject["stopped_reason"] = reason;
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			ApplyResolvedProxyMode(jsonObject);
			SaveProxyRuntimeState(jsonObject);
		}
	}

	private void PatchProxyRoutingFlag()
	{
		lock (_proxyStateLock)
		{
			JsonObject jsonObject = LoadJson(_proxyRuntimeStatePath);
			jsonObject["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			ApplyResolvedProxyMode(jsonObject);
			SaveProxyRuntimeState(jsonObject);
		}
	}

	private static string NormalizeCloudRoutingCapacity(string? value)
	{
		string normalized = (value ?? string.Empty).Trim();
		if (normalized.StartsWith("Hold", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Local only", StringComparison.OrdinalIgnoreCase))
		{
			return "Hold";
		}
		if (normalized.StartsWith("Low", StringComparison.OrdinalIgnoreCase) || normalized.Contains("prefer local", StringComparison.OrdinalIgnoreCase) || normalized.Contains("de-emphas", StringComparison.OrdinalIgnoreCase))
		{
			return "Low";
		}
		return "Normal";
	}

	private void ApplyResolvedProxyMode(JsonObject state)
	{
		state["routing_enabled"] = _requestRoutingEnabled;
		state["routing_topology"] = _requestRoutingTopology;
		state["cloud_routing_capacity"] = NormalizeCloudRoutingCapacity(_cloudRoutingCapacity);
		state["endpoint_app"] = _endpointApp;
		state["local_reasoning"] = string.IsNullOrWhiteSpace(_managedLocalReasoning) ? "Off" : _managedLocalReasoning;
		if (_managedLocalMaxTokens > 0)
		{
			state["local_max_tokens"] = _managedLocalMaxTokens;
		}
		JsonArray jsonArray = new JsonArray();
		foreach (var localRequestOverlay in _localRequestOverlays)
		{
			jsonArray.Add(new JsonObject
			{
				["variant"] = localRequestOverlay.Variant,
				["temperature"] = localRequestOverlay.Temperature,
				["max_tokens"] = localRequestOverlay.MaxTokens,
				["reasoning"] = localRequestOverlay.Reasoning
			});
		}
		state["local_overlays"] = jsonArray;
		bool localReady = GetString(state, "local_phase").Equals("ready", StringComparison.OrdinalIgnoreCase);
		bool cloudReady = GetString(state, "cloud_phase").Equals("ready", StringComparison.OrdinalIgnoreCase);
		if (_requestRoutingEnabled && (localReady | cloudReady))
		{
			var resolvedMode = RequestRoutingTopology.ResolveRoutingMode(_requestRoutingEnabled, localReady, cloudReady, _requestRoutingTopology);
			if (resolvedMode == "route")
			{
				state["mode"] = "route";
				state["phase"] = "ready";
				state["preferred_model"] = FirstNonEmpty(GetString(state, "local_preferred_model"), GetString(state, "cloud_preferred_model"));
			}
			else if (resolvedMode == "local")
			{
				state["mode"] = "local";
				state["provider"] = "Local";
				state["phase"] = FirstNonEmpty(GetString(state, "local_phase"), "ready");
				state["preferred_model"] = GetString(state, "local_preferred_model");
				int localPort = ParseInt(GetString(state, "local_port"), 0);
				if (localPort > 0)
				{
					state["endpoint"] = $"http://127.0.0.1:{localPort}/v1";
				}
			}
			else
			{
				state["mode"] = "cloud";
				state["phase"] = FirstNonEmpty(GetString(state, "cloud_phase"), "ready");
				state["preferred_model"] = FirstNonEmpty(GetString(state, "cloud_preferred_model"), GetString(state, "preferred_model"));
			}
			return;
		}
		if (localReady)
		{
			state["mode"] = "local";
			state["provider"] = "Local";
			state["phase"] = GetString(state, "local_phase");
			state["preferred_model"] = GetString(state, "local_preferred_model");
			return;
		}
		if (cloudReady)
		{
			state["mode"] = "cloud";
			state["phase"] = GetString(state, "cloud_phase");
			state["preferred_model"] = FirstNonEmpty(GetString(state, "cloud_preferred_model"), GetString(state, "preferred_model"));
			return;
		}
		string localPhase = GetString(state, "local_phase");
		string cloudPhase = GetString(state, "cloud_phase");
		if (!string.IsNullOrWhiteSpace(localPhase))
		{
			state["mode"] = "local";
			state["provider"] = "Local";
			state["phase"] = localPhase;
			state["preferred_model"] = GetString(state, "local_preferred_model");
		}
		else if (!string.IsNullOrWhiteSpace(cloudPhase))
		{
			state["mode"] = "cloud";
			state["phase"] = cloudPhase;
		}
		else
		{
			state["mode"] = FirstNonEmpty(GetString(state, "mode"), "idle");
			if (string.IsNullOrWhiteSpace(GetString(state, "phase")))
			{
				state["phase"] = "idle";
			}
		}
	}

	private void SaveProxyRuntimeState(JsonObject state)
	{
		AttachLocalReloadPool(state);
		ProxyRuntimeStateSecrets.Capture(state, _proxyRuntimeSecrets);
		File.WriteAllText(_proxyRuntimeStatePath, ProxyRuntimeStateSecrets.RedactCopy(state).ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true
		}));
	}

	private void AttachLocalReloadPool(JsonObject state)
	{
		try
		{
			JsonObject configRoot = LoadJsonCached(_configPath);
			string modelDirectory = GetString(configRoot, "ModelDirectory");
			state["local_variant"] = FirstNonEmpty(GetString(state, "local_variant"), FirstNonEmpty(_managedLocalVariant, "(defaults)"));
			RememberLastLocalCatalog(state, state);
			state["local_reasoning"] = FirstNonEmpty(GetString(state, "local_reasoning"), string.IsNullOrWhiteSpace(_managedLocalReasoning) ? "Off" : _managedLocalReasoning);
			if (string.IsNullOrWhiteSpace(GetString(state, "local_vision")))
			{
				state["local_vision"] = _managedLocalVisionEnabled ? "Enabled" : "Disabled";
			}
			if (ParseInt(GetString(state, "local_context"), 0) <= 0 && _managedLocalContext > 0)
			{
				state["local_context"] = _managedLocalContext;
			}
			if (ParseInt(GetString(state, "local_max_tokens"), 0) <= 0 && _managedLocalMaxTokens > 0)
			{
				state["local_max_tokens"] = _managedLocalMaxTokens;
			}

			JsonObject profiles = (configRoot["LocalProfiles"] as JsonObject) ?? new JsonObject();
			string hotModel = GetString(state, "local_preferred_model");
			string hotVariant = GetString(state, "local_variant");
			JsonObject? hotProfile = FindLocalProfile(profiles, hotModel, hotVariant);
			if (hotProfile is not null)
			{
				state["local_gpu_offload"] = GetString(hotProfile, "LocalGpuOffloadMode");
				state["local_ttft_ms"] = GetProfileInt(hotProfile, "ValidateTtftMs", 0);
			}

			JsonArray pool = new JsonArray();
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void TryAdd(string model, string variant, JsonObject? profile, bool fromQuickSelect)
			{
				model = (model ?? string.Empty).Trim();
				variant = FirstNonEmpty((variant ?? string.Empty).Trim(), "(defaults)");
				if (string.IsNullOrWhiteSpace(model))
				{
					return;
				}

				string identity = model + "::" + variant;
				if (!seen.Add(identity))
				{
					return;
				}

				if (model.Equals(hotModel, StringComparison.OrdinalIgnoreCase)
					&& variant.Equals(hotVariant, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				if (string.IsNullOrWhiteSpace(ResolveModelPath(model, modelDirectory)))
				{
					return;
				}

				profile ??= FindLocalProfile(profiles, model, variant);
				if (!fromQuickSelect)
				{
					if (profile is null || string.IsNullOrWhiteSpace(GetString(profile, "EndpointValidatedUtc")))
					{
						return;
					}

					if (!string.IsNullOrWhiteSpace(GetString(profile, "EndpointWarning")))
					{
						return;
					}
				}

				bool vision = profile is not null && GetString(profile, "LocalVisionEnabled").Equals("Enabled", StringComparison.OrdinalIgnoreCase);
				int context = profile is null ? 0 : GetProfileInt(profile, "OverrideContext", 0);
				string reasoning = profile is null ? "Off" : FirstNonEmpty(GetString(profile, "LocalReasoning"), "Off");
				bool isDefault = variant.Equals("(defaults)", StringComparison.OrdinalIgnoreCase);
				string display = isDefault ? ("Local | " + model + " (default)") : ("Local | " + model + ": " + variant);
				pool.Add(new JsonObject
				{
					["model"] = model,
					["variant"] = variant,
					["displayName"] = display,
					["vision"] = vision ? "Enabled" : "Disabled",
					["context"] = context,
					["reasoning"] = reasoning,
					["sameGguf"] = model.Equals(hotModel, StringComparison.OrdinalIgnoreCase),
					["gpuOffload"] = profile is null ? string.Empty : GetString(profile, "LocalGpuOffloadMode"),
					["ttftMs"] = profile is null ? 0 : GetProfileInt(profile, "ValidateTtftMs", 0)
				});
			}

			if (configRoot["RouteSlots"] is JsonArray slots)
			{
				foreach (JsonObject slot in slots.OfType<JsonObject>())
				{
					if (!GetString(slot, "routeType").Equals("local", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					TryAdd(GetString(slot, "localModel"), GetString(slot, "localVariant"), null, fromQuickSelect: true);
				}
			}

			foreach (var entry in profiles)
			{
				string[] parts = entry.Key.Split("::");
				if (parts.Length < 2 || entry.Value is not JsonObject profile)
				{
					continue;
				}

				TryAdd(parts[0], parts[1], profile, fromQuickSelect: false);
			}

			state["local_reload_pool"] = pool;
		}
		catch
		{
		}
	}

	private static JsonObject? FindLocalProfile(JsonObject profiles, string model, string variant)
	{
		string exact = model + "::" + variant;
		if (profiles[exact] is JsonObject direct)
		{
			return direct;
		}

		foreach (var entry in profiles)
		{
			string[] parts = entry.Key.Split("::");
			if (parts.Length >= 2
				&& parts[0].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
				&& parts[1].Trim().Equals(variant, StringComparison.OrdinalIgnoreCase)
				&& entry.Value is JsonObject profile)
			{
				return profile;
			}
		}

		return null;
	}

	private int GetGatewayPid()
	{
		return _gatewayHost is { IsListening: true } ? Environment.ProcessId : 0;
	}

	private void ClearProxyRuntimeState(string stoppedReason)
	{
		try
		{
			lock (_proxyStateLock)
			{
				JsonObject previous = LoadJson(_proxyRuntimeStatePath);
				RememberLastSuccessfulCloudFromState(previous);
				JsonObject state = new JsonObject
				{
					["mode"] = "idle",
					["provider"] = string.Empty,
					["endpoint"] = string.Empty,
					["preferred_model"] = string.Empty,
					["routing_enabled"] = _requestRoutingEnabled,
					["routing_topology"] = _requestRoutingTopology,
					["cloud_routing_capacity"] = NormalizeCloudRoutingCapacity(_cloudRoutingCapacity),
					["endpoint_app"] = _endpointApp,
					["local_port"] = ((_managedProxyPort > 0) ? (_managedProxyPort + 1) : 8081),
					["local_model_path"] = string.Empty,
					["local_context"] = string.Empty,
					["local_profile_key"] = string.Empty,
					["local_phase"] = string.Empty,
					["local_preferred_model"] = string.Empty,
					["cloud_phase"] = string.Empty,
					["cloud_preferred_model"] = string.Empty,
					["bridge_pid"] = 0,
					["proxy_port"] = ((_managedProxyPort > 0) ? _managedProxyPort : 8080),
					["phase"] = "idle",
					["stopped_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
					["stopped_reason"] = stoppedReason,
					["updatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
				};
				RememberLastLocalCatalog(previous, state);
				WriteLastSuccessfulCloud(state);
				SaveProxyRuntimeState(state);
			}
		}
		catch
		{
		}
	}

	private string BuildProxyExitDetail(int proxyPort, int exitCode)
	{
		string value = BuildProxyTailSummary();
		return string.IsNullOrWhiteSpace(value) ? $"Python proxy exited before opening port {proxyPort} (exit code {exitCode})." : $"Python proxy exited before opening port {proxyPort} (exit code {exitCode}): {value}";
	}

	private string BuildProxyTailSummary()
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(_lastCloudProxyTail))
		{
			list.Add(_lastCloudProxyTail);
		}
		if (File.Exists(_proxyStdErrPath))
		{
			try
			{
				string stderrTail = string.Join(Environment.NewLine, from x in File.ReadLines(_proxyStdErrPath).TakeLast(6)
					where !string.IsNullOrWhiteSpace(x)
					select x);
				if (!string.IsNullOrWhiteSpace(stderrTail))
				{
					list.Add(stderrTail);
				}
			}
			catch
			{
			}
		}
		if (File.Exists(_proxyStdOutPath))
		{
			try
			{
				string stdoutTail = string.Join(Environment.NewLine, from x in File.ReadLines(_proxyStdOutPath).TakeLast(6)
					where !string.IsNullOrWhiteSpace(x)
					select x);
				if (!string.IsNullOrWhiteSpace(stdoutTail))
				{
					list.Add(stdoutTail);
				}
			}
			catch
			{
			}
		}
		return TruncateTail(string.Join(" | ", list.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct()), 1200);
	}

	private string GetRecentUpstreamProxyError(string provider, string model, TimeSpan lookback)
	{
		if (!File.Exists(_proxyContextEventsPath))
		{
			return string.Empty;
		}
		string value = "provider=" + provider;
		string value2 = "model=" + model;
		DateTimeOffset dateTimeOffset = DateTimeOffset.UtcNow - lookback;
		DateTimeOffset? dateTimeOffset2 = null;
		string lastError = string.Empty;
		DateTimeOffset? dateTimeOffset3 = null;
		try
		{
			foreach (string item in File.ReadLines(_proxyContextEventsPath).TakeLast(500))
			{
				if (string.IsNullOrWhiteSpace(item))
				{
					continue;
				}
				JsonObject jsonObject;
				try
				{
					jsonObject = JsonNode.Parse(item) as JsonObject;
				}
				catch
				{
					continue;
				}
				if (jsonObject == null)
				{
					continue;
				}
				string eventType = jsonObject["event_type"]?.ToString() ?? string.Empty;
				string eventDetails = jsonObject["details"]?.ToString() ?? string.Empty;
				if (!eventDetails.Contains(value, StringComparison.OrdinalIgnoreCase) || !eventDetails.Contains(value2, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				int unixTimestamp = ParseInt(jsonObject["timestamp"]?.ToString() ?? string.Empty, 0);
				DateTimeOffset dateTimeOffset4 = ((unixTimestamp > 0) ? DateTimeOffset.FromUnixTimeSeconds(unixTimestamp) : DateTimeOffset.MinValue);
				if (dateTimeOffset4 < dateTimeOffset)
				{
					continue;
				}
				if (eventType.Equals("upstream_error", StringComparison.OrdinalIgnoreCase))
				{
					if (!dateTimeOffset2.HasValue || dateTimeOffset4 > dateTimeOffset2.Value)
					{
						dateTimeOffset2 = dateTimeOffset4;
						lastError = eventDetails;
					}
				}
				else if (eventType.Equals("upstream_ok", StringComparison.OrdinalIgnoreCase) && (!dateTimeOffset3.HasValue || dateTimeOffset4 > dateTimeOffset3.Value))
				{
					dateTimeOffset3 = dateTimeOffset4;
				}
			}
		}
		catch
		{
			return string.Empty;
		}
		if (!dateTimeOffset2.HasValue)
		{
			return string.Empty;
		}
		if (dateTimeOffset3.HasValue && dateTimeOffset3.Value >= dateTimeOffset2.Value)
		{
			return string.Empty;
		}
		return TruncateTail(lastError, 320);
	}

	private string GetRecentLocalContextOverflow(TimeSpan lookback, int currentContext)
	{
		if (!File.Exists(_proxyContextEventsPath))
		{
			return string.Empty;
		}

		DateTimeOffset cutoff = DateTimeOffset.UtcNow - lookback;
		DateTimeOffset? overflowAt = null;
		string overflowDetails = string.Empty;
		DateTimeOffset? laterOkAt = null;
		try
		{
			foreach (string item in File.ReadLines(_proxyContextEventsPath).TakeLast(500))
			{
				if (string.IsNullOrWhiteSpace(item))
				{
					continue;
				}

				JsonObject jsonObject;
				try
				{
					jsonObject = JsonNode.Parse(item) as JsonObject;
				}
				catch
				{
					continue;
				}

				if (jsonObject == null)
				{
					continue;
				}

				int unix = ParseInt(jsonObject["timestamp"]?.ToString() ?? string.Empty, 0);
				DateTimeOffset at = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue;
				if (at < cutoff)
				{
					continue;
				}

				string eventType = jsonObject["event_type"]?.ToString() ?? string.Empty;
				if (eventType.Equals("local_context_overflow", StringComparison.OrdinalIgnoreCase))
				{
					if (!overflowAt.HasValue || at > overflowAt.Value)
					{
						overflowAt = at;
						overflowDetails = jsonObject["details"]?.ToString() ?? string.Empty;
					}
				}
				else if (eventType.Equals("upstream_ok", StringComparison.OrdinalIgnoreCase))
				{
					if (!laterOkAt.HasValue || at > laterOkAt.Value)
					{
						laterOkAt = at;
					}
				}
			}
		}
		catch
		{
			return string.Empty;
		}

		if (!overflowAt.HasValue)
		{
			return string.Empty;
		}

		if (laterOkAt.HasValue && laterOkAt.Value >= overflowAt.Value)
		{
			return string.Empty;
		}

		if (!TryFormatContextOverflowDetails(overflowDetails, out string formatted, out int used, out int overflowCtx))
		{
			return string.Empty;
		}

		if (currentContext > 0 && overflowCtx > 0 && currentContext > overflowCtx)
		{
			return string.Empty;
		}

		if (currentContext > 0 && used > 0 && currentContext >= used)
		{
			return string.Empty;
		}

		return formatted;
	}

	private static bool TryFormatContextOverflowDetails(string details, out string formatted)
		=> TryFormatContextOverflowDetails(details, out formatted, out _, out _);

	private static bool TryFormatContextOverflowDetails(string details, out string formatted, out int used, out int overflowCtx)
	{
		formatted = string.Empty;
		used = 0;
		overflowCtx = 0;
		if (string.IsNullOrWhiteSpace(details))
		{
			return false;
		}

		try
		{
			if (JsonNode.Parse(details) is JsonObject jsonObject)
			{
				used = ParseInt(jsonObject["n_prompt"]?.ToString() ?? string.Empty, 0);
				overflowCtx = ParseInt(jsonObject["n_ctx"]?.ToString() ?? string.Empty, 0);
			}
		}
		catch
		{
		}

		if (used <= 0 || overflowCtx <= 0)
		{
			used = ParseInt(ExtractTaggedInt(details, "n_prompt"), 0);
			overflowCtx = ParseInt(ExtractTaggedInt(details, "n_ctx"), 0);
		}

		if (used <= 0 || overflowCtx <= 0)
		{
			return false;
		}

		int over = Math.Max(0, used - overflowCtx);
		formatted = "request tokens " + used.ToString("N0", CultureInfo.InvariantCulture)
			+ " exceeded context " + overflowCtx.ToString("N0", CultureInfo.InvariantCulture)
			+ " by " + over.ToString("N0", CultureInfo.InvariantCulture) + ".";
		return true;
	}

	private static string ExtractTaggedInt(string details, string tag)
	{
		var match = Regex.Match(details ?? string.Empty, tag + @"\s*=\s*(\d+)", RegexOptions.IgnoreCase);
		return match.Success ? match.Groups[1].Value : string.Empty;
	}

	private static int ParseInt(string value, int fallback)
	{
		int result;
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
	}

	private static double ParseDouble(string value, double fallback)
	{
		double result;
		return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
	}

	private static bool ParseBool(string value, bool fallback)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}
		if (bool.TryParse(value, out var result))
		{
			return result;
		}
		string normalized = value.Trim().ToLowerInvariant();
		bool result2;
		switch (normalized)
		{
		case "1":
		case "yes":
		case "on":
			result2 = true;
			break;
		case "0":
		case "no":
		case "off":
			result2 = false;
			break;
		default:
			result2 = fallback;
			break;
		}
		return result2;
	}

	private static string GetCustomCompatBaseEndpoint(JsonObject root, string? provider = null)
	{
		var name = string.IsNullOrWhiteSpace(provider)
			? CustomCloudProviderRegistry.DefaultCustomProviderName
			: provider;
		string endpoint = CustomCloudProviderRegistry.GetEndpoint(root, name);
		if (!string.IsNullOrWhiteSpace(endpoint))
		{
			return endpoint;
		}
		return FirstNonEmpty(GetString(root, "OpenAiEndpoint"), "https://api.openai.com/v1");
	}

	private int StopStaleManagedProxyFromRuntimeState(bool clearStateIfStopped)
	{
		try
		{
			if (!File.Exists(_proxyRuntimeStatePath))
			{
				return 0;
			}
			JsonObject root = LoadJson(_proxyRuntimeStatePath);
			int bridgePid = ParseInt(GetString(root, "bridge_pid"), 0);
			if (bridgePid <= 0)
			{
				return 0;
			}
			if (bridgePid == Environment.ProcessId || _gatewayHost is { IsListening: true })
			{
				return 0;
			}
			Process processById = Process.GetProcessById(bridgePid);
			if (processById.HasExited)
			{
				return 0;
			}
			string processName = processById.ProcessName;
			if (!processName.Equals("python", StringComparison.OrdinalIgnoreCase)
				&& !processName.Equals("pythonw", StringComparison.OrdinalIgnoreCase)
				&& !processName.Equals("py", StringComparison.OrdinalIgnoreCase))
			{
				return 0;
			}
			processById.Kill(entireProcessTree: true);
			AppendCloudHandshakeLog($"Stopped stale managed bridge PID {bridgePid} from runtime state cache.");
			if (clearStateIfStopped)
			{
				ClearProxyRuntimeState("stale-proxy-cleanup");
			}
			return 1;
		}
		catch
		{
			return 0;
		}
	}

	private void WriteLocalRuntimeState(string modelPath, int localPort, int localPid, string phase, string details)
	{
		try
		{
			JsonObject jsonObject = new JsonObject
			{
				["mode"] = "local",
				["phase"] = phase,
				["updated_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
				["local_port"] = localPort,
				["local_pid"] = localPid,
				["local_model_path"] = modelPath,
				["details"] = details
			};
			File.WriteAllText(_localRuntimeStatePath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}

	private void ClearLocalRuntimeState(string stoppedReason)
	{
		try
		{
			JsonObject jsonObject = new JsonObject
			{
				["mode"] = "local",
				["phase"] = "stopped",
				["updated_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
				["details"] = stoppedReason,
				["local_port"] = -1,
				["local_pid"] = 0,
				["local_model_path"] = string.Empty
			};
			File.WriteAllText(_localRuntimeStatePath, jsonObject.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}

	private int ReadLocalRuntimeStatePort()
	{
		try
		{
			if (!File.Exists(_localRuntimeStatePath))
			{
				return -1;
			}
			JsonObject root = LoadJson(_localRuntimeStatePath);
			return ParseInt(GetString(root, "local_port"), -1);
		}
		catch
		{
			return -1;
		}
	}

	private int StopStaleManagedLocalFromRuntimeState(bool clearStateIfStopped)
	{
		try
		{
			if (!File.Exists(_localRuntimeStatePath))
			{
				return 0;
			}
			JsonObject root = LoadJson(_localRuntimeStatePath);
			if (!GetString(root, "mode").Equals("local", StringComparison.OrdinalIgnoreCase))
			{
				return 0;
			}
			int localPid = ParseInt(GetString(root, "local_pid"), 0);
			if (localPid <= 0)
			{
				return 0;
			}
			Process? localServerProcess = _localServerProcess;
			if (localServerProcess != null && !localServerProcess.HasExited && localServerProcess.Id == localPid)
			{
				return 0;
			}
			Process processById = Process.GetProcessById(localPid);
			if (processById.HasExited || !IsLikelyManagedLlamaProcess(processById))
			{
				return 0;
			}
			processById.Kill(entireProcessTree: true);
			if (clearStateIfStopped)
			{
				ClearLocalRuntimeState("stale-local-cleanup");
			}
			return 1;
		}
		catch
		{
			return 0;
		}
	}

	private static bool IsLikelyManagedLlamaProcess(Process process)
	{
		string processName = process.ProcessName ?? string.Empty;
		return processName.Contains("llama", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryStopLocalEndpointOwnerByPort(int port, out int ownerPid, out string ownerName)
	{
		ownerPid = 0;
		ownerName = string.Empty;
		if (port < 1 || port > 65535)
		{
			return false;
		}
		try
		{
			string netstatOutput = RunShortCommand("netstat", "-ano -p tcp");
			if (string.IsNullOrWhiteSpace(netstatOutput))
			{
				return false;
			}
			string[] array = (from line in netstatOutput.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				where line.Contains($":{port}", StringComparison.OrdinalIgnoreCase) && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)
				select line).ToArray();
			string[] array2 = array;
			foreach (string listeningLine in array2)
			{
				string[] array3 = listeningLine.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (array3.Length < 5 || !int.TryParse(array3[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result <= 0 || result == Environment.ProcessId)
				{
					continue;
				}
				try
				{
					Process processById = Process.GetProcessById(result);
					if (processById.HasExited || !IsLikelyManagedLlamaProcess(processById))
					{
						continue;
					}
					processById.Kill(entireProcessTree: true);
					ownerPid = result;
					ownerName = processById.ProcessName;
					return true;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryGetLocalEndpointOwnerByPort(int port, out int ownerPid, out string ownerName)
	{
		ownerPid = 0;
		ownerName = string.Empty;
		if (port < 1 || port > 65535)
		{
			return false;
		}
		try
		{
			string netstatOutput = RunShortCommand("netstat", "-ano -p tcp");
			if (string.IsNullOrWhiteSpace(netstatOutput))
			{
				return false;
			}
			string[] array = (from line in netstatOutput.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				where line.Contains($":{port}", StringComparison.OrdinalIgnoreCase) && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)
				select line).ToArray();
			string[] array2 = array;
			foreach (string listeningLine in array2)
			{
				string[] array3 = listeningLine.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (array3.Length < 5 || !int.TryParse(array3[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result <= 0)
				{
					continue;
				}
				try
				{
					Process processById = Process.GetProcessById(result);
					if (processById.HasExited)
					{
						continue;
					}
					ownerPid = result;
					ownerName = processById.ProcessName;
					return true;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private RuntimeActionResult BuildCloudFailureResult(string status, string provider, string model, string detail, string? categoryOverride = null)
	{
		string category = (string.IsNullOrWhiteSpace(categoryOverride) ? GetProviderErrorCategory(detail) : categoryOverride);
		string cloudFailureStatusText = GetCloudFailureStatusText(category, provider, model, status);
		string combinedDetail = string.IsNullOrWhiteSpace(detail)
			? cloudFailureStatusText
			: (detail.Contains(cloudFailureStatusText, StringComparison.OrdinalIgnoreCase)
				? detail.Trim()
				: cloudFailureStatusText + " " + detail.Trim());
		return new RuntimeActionResult
		{
			IsSuccess = false,
			Status = status,
			Details = combinedDetail
		};
	}

	private static string GetCloudFailureStatusText(string category, string provider, string model, string fallback)
	{
		string providerLabel = (string.IsNullOrWhiteSpace(provider) ? "Cloud provider" : provider);
		string modelLabel = (string.IsNullOrWhiteSpace(model) ? "selected model" : model);
		string result = category switch
		{
			"Authentication" => providerLabel + " rejected authentication for " + modelLabel + ".", 
			"Model availability" => providerLabel + " model route is unavailable for " + modelLabel + ".", 
			"Rate limit" => providerLabel + " rate-limited requests for " + modelLabel + ".", 
			"Quota/billing" => providerLabel + " quota or billing constraint blocked " + modelLabel + ".", 
			"Endpoint/transport" => providerLabel + " endpoint or transport path failed for " + modelLabel + ".", 
			"Provider timeout" => providerLabel + " timed out while initializing or serving " + modelLabel + ".", 
			"Dependency/missing" => $"Local dependency missing while preparing {providerLabel} route for {modelLabel}.", 
			_ => fallback, 
		};
		return result;
	}

	private static string GetProviderErrorCategory(string message)
	{
		string normalized = (message ?? string.Empty).ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(normalized))
		{
			return "Provider/unknown";
		}
		if (normalized.Contains("api key") || normalized.Contains("invalid api") || normalized.Contains("unauthorized") || normalized.Contains("401") || normalized.Contains("forbidden") || normalized.Contains("403"))
		{
			return "Authentication";
		}
		if (normalized.Contains("404") || normalized.Contains("not found") || normalized.Contains("model_not_supported") || normalized.Contains("does not currently expose") || (normalized.Contains("model") && normalized.Contains("not available")) || normalized.Contains("unknown model") || normalized.Contains("does not exist"))
		{
			return "Model availability";
		}
		if (normalized.Contains("429") || normalized.Contains("rate limit") || normalized.Contains("too many requests"))
		{
			return "Rate limit";
		}
		if (normalized.Contains("quota") || normalized.Contains("billing") || (normalized.Contains("insufficient") && normalized.Contains("credit")) || normalized.Contains("payment"))
		{
			return "Quota/billing";
		}
		if (normalized.Contains("timed out") || normalized.Contains("timeout") || normalized.Contains("504"))
		{
			return "Provider timeout";
		}
		if (normalized.Contains("dns") || normalized.Contains("socket") || normalized.Contains("connection") || normalized.Contains("refused") || normalized.Contains("invalid endpoint") || normalized.Contains("no such host"))
		{
			return "Endpoint/transport";
		}
		if (normalized.Contains("llama-server") && normalized.Contains("not found"))
		{
			return "Dependency/missing";
		}
		return "Provider/unknown";
	}

	private void AppendCloudHandshakeLog(string message)
	{
		try
		{
			string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
			File.AppendAllText(_proxyCloudHandshakeLogPath, line + Environment.NewLine);
		}
		catch
		{
		}
	}

	private static string GetString(JsonObject root, string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return string.Empty;
		}
		try
		{
			if (root[key] is not JsonNode jsonNode)
			{
				return string.Empty;
			}
			if (jsonNode is JsonValue)
			{
				try
				{
					return jsonNode.GetValue<string>()?.Trim() ?? string.Empty;
				}
				catch
				{
					return jsonNode.ToString().Trim();
				}
			}
			return jsonNode.ToString().Trim();
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string RunShortCommand(string fileName, string arguments)
		=> ExternalProcessText.RunOrEmpty(fileName, arguments, 5000);

	private static string RunGpuCommand(string fileName, string arguments)
	{
		try
		{
			using Process process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = fileName,
					Arguments = arguments,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			process.Start();
			if (!process.WaitForExit(7000))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch
				{
				}
				return string.Empty;
			}
			return (process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd()).Trim();
		}
		catch
		{
			return string.Empty;
		}
	}

	public Task<RuntimeActionResult> GetVramStatusAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		StringBuilder stringBuilder = new StringBuilder();
		List<VramProcessListItem> processItems = new List<VramProcessListItem>();
		bool nvidiaAvailable = false;
		int gpuCount = 0;
		string smiOutput = RunGpuCommand("nvidia-smi", "--query-gpu=name,memory.used,memory.free,memory.total --format=csv,noheader,nounits");
		if (!string.IsNullOrWhiteSpace(smiOutput) && !smiOutput.Contains("NVIDIA-SMI has failed") && !smiOutput.Contains("not found") && !smiOutput.Contains("is not recognized") && !smiOutput.Contains("No such file"))
		{
			nvidiaAvailable = true;
			stringBuilder.AppendLine("Graphics card memory:");
			int cardUsedMb = 0;
			string[] array = smiOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			foreach (string line in array)
			{
				string[] array2 = line.Split(',');
				if (array2.Length >= 4)
				{
					gpuCount++;
					stringBuilder.AppendLine($"{array2[0].Trim()}: {array2[1].Trim()} MB used  |  {array2[3].Trim()} MB total  ({array2[2].Trim()} MB free)");
					if (int.TryParse(array2[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int usedMb))
					{
						cardUsedMb += usedMb;
					}
				}
			}

			List<GpuProcessUse> processes = CollectGpuProcessUses();
			List<GpuProcessUse> listed = processes
				.GroupBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase)
				.Select(g => new GpuProcessUse(
					g.Key,
					g.Sum(x => x.Megabytes),
					g.Count(),
					g.Any(x => x.Megabytes <= 0)))
				.OrderByDescending(x => x.Megabytes)
				.ThenBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (listed.Count > 0)
			{
				int listedMb = 0;
				List<GpuProcessUse> userGroups = listed.Where(g => !IsGpuStabilitySensitive(g.FriendlyName)).ToList();
				List<GpuProcessUse> cautionGroups = listed.Where(g => IsGpuStabilitySensitive(g.FriendlyName)).ToList();
				foreach (GpuProcessUse group in listed)
				{
					if (group.Megabytes > 0)
					{
						listedMb += group.Megabytes;
					}
				}

				List<VramProcessListItem> listedItems = new List<VramProcessListItem>();
				foreach (GpuProcessUse group in userGroups)
				{
					listedItems.Add(ToVramProcessListItem(group, caution: false));
				}

				foreach (GpuProcessUse group in cautionGroups)
				{
					listedItems.Add(ToVramProcessListItem(group, caution: true));
				}

				int leftoverMb = cardUsedMb - listedMb;
				if (leftoverMb >= 8)
				{
					listedItems.Add(new VramProcessListItem
					{
						MemoryLabel = $"{leftoverMb} MB",
						NameLabel = "Unlisted (drivers and shared)",
						IsCaution = true
					});
				}

				processItems.AddRange(listedItems);
			}
			else
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("No programs reported as using this graphics card right now.");
			}
		}
		else
		{
			stringBuilder.AppendLine("NVIDIA's nvidia-smi tool is needed to list graphics-card memory by program.");
			stringBuilder.AppendLine("On AMD or Intel cards, Task Manager → Performance → GPU shows similar detail.");
		}
		string details = stringBuilder.ToString().Trim();
		return Task.FromResult(new RuntimeActionResult
		{
			IsSuccess = nvidiaAvailable,
			Status = (nvidiaAvailable ? "Graphics-card memory updated" : "Graphics-card memory detail not available"),
			Details = details,
			VramProcessItems = processItems,
			GpuCardCount = gpuCount
		});
	}

	private static bool IsGpuStabilitySensitive(string friendlyName)
	{
		return friendlyName.Equals("Windows desktop", StringComparison.OrdinalIgnoreCase)
			|| friendlyName.Equals("Windows background processes", StringComparison.OrdinalIgnoreCase)
			|| friendlyName.Equals("NVIDIA overlay", StringComparison.OrdinalIgnoreCase)
			|| friendlyName.StartsWith("Unlisted", StringComparison.OrdinalIgnoreCase);
	}

	private static VramProcessListItem ToVramProcessListItem(GpuProcessUse group, bool caution)
	{
		return new VramProcessListItem
		{
			MemoryLabel = group.Megabytes > 0 ? $"{group.Megabytes} MB" : "—",
			NameLabel = group.FriendlyName,
			CountLabel = FormatGpuProcessCountNote(group),
			IsCaution = caution
		};
	}

	private static string FormatGpuProcessCountNote(GpuProcessUse group)
	{
		if (group.InstanceCount <= 1)
		{
			return string.Empty;
		}

		string countWord = group.FriendlyName.Equals("Windows background processes", StringComparison.OrdinalIgnoreCase)
			? "instances"
			: "running";
		return $"({group.InstanceCount} {countWord})";
	}

	private sealed record GpuProcessUse(string FriendlyName, int Megabytes, int InstanceCount = 1, bool MissingAmount = false);

	private List<GpuProcessUse> CollectGpuProcessUses()
	{
		List<GpuProcessUse> windowsUses = CollectWindowsGpuLocalUsage();
		if (windowsUses.Count > 0)
		{
			return windowsUses;
		}

		Dictionary<int, GpuProcessUse> byPid = new Dictionary<int, GpuProcessUse>();
		foreach (var row in ParseNvidiaComputeApps())
		{
			if (row.Mb <= 0)
			{
				continue;
			}

			string friendlyName = FriendlyGpuProcessName(row.Name, row.Pid);
			if (byPid.TryGetValue(row.Pid, out GpuProcessUse? existing))
			{
				byPid[row.Pid] = existing with { Megabytes = existing.Megabytes + row.Mb };
			}
			else
			{
				byPid[row.Pid] = new GpuProcessUse(friendlyName, row.Mb);
			}
		}

		return byPid.Values.ToList();
	}

	private static List<GpuProcessUse> CollectWindowsGpuLocalUsage()
	{
		List<GpuProcessUse> result = new List<GpuProcessUse>();
		if (!OperatingSystem.IsWindows())
		{
			return result;
		}

		return CollectWindowsGpuLocalUsageCore();
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static List<GpuProcessUse> CollectWindowsGpuLocalUsageCore()
	{
		List<GpuProcessUse> result = new List<GpuProcessUse>();
		try
		{
			if (!PerformanceCounterCategory.Exists("GPU Process Memory"))
			{
				return result;
			}

			PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Process Memory");
			string[] instances = category.GetInstanceNames();
			Dictionary<string, Dictionary<int, long>> bytesByGpuThenPid = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
			foreach (string instance in instances)
			{
				if (!TryParseGpuProcessInstance(instance, out int pid, out string gpuKey) || pid <= 4)
				{
					continue;
				}

				long bytes;
				try
				{
					using PerformanceCounter counter = new PerformanceCounter("GPU Process Memory", "Local Usage", instance, readOnly: true);
					bytes = counter.RawValue;
				}
				catch
				{
					continue;
				}

				if (bytes < 1)
				{
					continue;
				}

				if (!bytesByGpuThenPid.TryGetValue(gpuKey, out Dictionary<int, long>? perPid))
				{
					perPid = new Dictionary<int, long>();
					bytesByGpuThenPid[gpuKey] = perPid;
				}

				perPid[pid] = perPid.GetValueOrDefault(pid) + bytes;
			}

			Dictionary<int, long> mergedByPid = GpuProcessMemoryAggregator.MergePerProcessAcrossGpus(bytesByGpuThenPid.Values);
			if (mergedByPid.Count == 0)
			{
				return result;
			}

			const int minimumMegabytes = 8;
			foreach (KeyValuePair<int, long> entry in mergedByPid)
			{
				int mb = (int)Math.Round(entry.Value / (1024d * 1024d));
				if (mb < minimumMegabytes)
				{
					continue;
				}

				string processName = string.Empty;
				try
				{
					processName = Process.GetProcessById(entry.Key).ProcessName;
				}
				catch
				{
				}

				result.Add(new GpuProcessUse(FriendlyGpuProcessName(processName, entry.Key), mb));
			}
		}
		catch
		{
		}

		return result;
	}

	private static bool TryParseGpuProcessInstance(string instance, out int pid, out string gpuKey)
	{
		pid = 0;
		gpuKey = string.Empty;
		if (string.IsNullOrWhiteSpace(instance))
		{
			return false;
		}

		System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
			instance,
			@"pid_(\d+)_luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)(?:_phys_(\d+))?",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (!match.Success || !int.TryParse(match.Groups[1].Value, out pid) || pid <= 0)
		{
			return false;
		}

		gpuKey = match.Groups[2].Value;
		if (match.Groups[3].Success)
		{
			gpuKey += "_phys_" + match.Groups[3].Value;
		}

		return true;
	}

	private List<(int Pid, string Name, int Mb)> ParseNvidiaComputeApps()
	{
		List<(int Pid, string Name, int Mb)> list = new List<(int, string, int)>();
		string smiOutput = RunGpuCommand("nvidia-smi", "--query-compute-apps=pid,process_name,used_gpu_memory --format=csv,noheader,nounits");
		if (string.IsNullOrWhiteSpace(smiOutput) || smiOutput.Contains("No running compute", StringComparison.OrdinalIgnoreCase))
		{
			return list;
		}

		string[] array = smiOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		foreach (string line in array)
		{
			if (line.TrimStart().StartsWith("No running", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string[] parts = line.Split(',');
			if (parts.Length >= 3 && int.TryParse(parts[0].Trim(), out int pid) && pid > 0)
			{
				int mb = ParseGpuMegabytes(parts[2]);
				list.Add((pid, parts[1].Trim(), mb));
			}
		}

		return list;
	}

	private static int ParseGpuMegabytes(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return 0;
		}

		string normalized = raw.Trim();
		if (normalized.Contains("N/A", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Not Available", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}

		StringBuilder digits = new StringBuilder();
		foreach (char ch in normalized)
		{
			if (char.IsDigit(ch))
			{
				digits.Append(ch);
			}
			else if (digits.Length > 0)
			{
				break;
			}
		}

		return int.TryParse(digits.ToString(), out int mb) ? mb : 0;
	}

	private static string FriendlyGpuProcessName(string processPathOrName, int pid)
	{
		string file = Path.GetFileNameWithoutExtension(processPathOrName ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(file))
		{
			try
			{
				file = Process.GetProcessById(pid).ProcessName;
			}
			catch
			{
				file = "Unknown program";
			}
		}

		string key = file.ToLowerInvariant();
		return key switch
		{
			"chrome" or "msedge" or "msedgewebview2" or "brave" or "opera" or "vivaldi" or "iexplore" => "Web browser",
			"firefox" => "Firefox",
			"dwm" => "Windows desktop",
			"explorer" => "File Explorer",
			"csrss" or "winlogon" or "smss" or "searchhost" or "startmenuexperiencehost" or "shellexperiencehost" or "textinputhost" or "applicationframehost" or "lockapp" or "shellhost" or "crossdeviceresume" => "Windows background processes",
			"cursor" => "Cursor",
			"code" => "VS Code",
			"devenv" => "Visual Studio",
			"llama-server" or "llama-server-cuda" => "AI-FluxMux local model",
			"fluxmux.avalonia" => "AI-FluxMux",
			"nvcontainer" or "nvidia web helper" or "nvidia overlay" or "nvsphelper64" => "NVIDIA overlay",
			"discord" => "Discord",
			"slack" => "Slack",
			"teams" or "ms-teams" => "Microsoft Teams",
			"mscopilot" => "Microsoft Copilot",
			"steam" or "steamwebhelper" => "Steam",
			"snagiteditor" or "snagitcapture" => "Snagit",
			"outlook" => "Outlook",
			"whatsapp.root" or "whatsapp" => "WhatsApp",
			"notepad++" => "Notepad++",
			"obs64" or "obs32" or "obs" => "OBS",
			"photoshop" => "Photoshop",
			"afterfx" => "After Effects",
			"premiere" or "adobe premiere pro" => "Premiere Pro",
			"blender" => "Blender",
			"unity" => "Unity",
			"unrealeditor" => "Unreal Editor",
			"python" or "pythonw" => "Python",
			"powershell" or "pwsh" => "PowerShell",
			_ => PrettifyProcessFileName(file)
		};
	}

	private static string PrettifyProcessFileName(string file)
	{
		if (string.IsNullOrWhiteSpace(file))
		{
			return "Unknown program";
		}

		string spaced = file.Replace('_', ' ').Replace('-', ' ').Trim();
		if (spaced.Length == 0)
		{
			return "Unknown program";
		}

		return char.ToUpperInvariant(spaced[0]) + spaced[1..];
	}


	public Task<RuntimeActionResult> GetGpuAdaptersAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string deviceOutput = RunGpuCommand("powershell.exe", "-ExecutionPolicy Bypass -Command \"Get-PnpDevice -Class Display | Sort-Object FriendlyName | ForEach-Object { $_.FriendlyName + ' — ' + $_.Status }\"");
		List<DisplayAdapterListItem> adapterItems = new List<DisplayAdapterListItem>();
		string details;
		if (string.IsNullOrWhiteSpace(deviceOutput))
		{
			details = "No display adapters detected, or PowerShell query failed.";
		}
		else
		{
			StringBuilder labeled = new StringBuilder();
			foreach (string line in deviceOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				string trimmed = line.Trim();
				adapterItems.Add(DisplayAdapterLabeling.ParseRow(trimmed));
				labeled.AppendLine(DisplayAdapterLabeling.LabelLine(trimmed));
			}

			details = labeled.ToString().Trim();
		}

		return Task.FromResult(new RuntimeActionResult
		{
			IsSuccess = !string.IsNullOrWhiteSpace(deviceOutput),
			Status = "Display adapters refreshed",
			Details = details,
			DisplayAdapterItems = adapterItems
		});
	}

	private static string TruncateTail(string value, int max)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length <= max)
		{
			return value;
		}
		return value.Substring(value.Length - max);
	}
}
