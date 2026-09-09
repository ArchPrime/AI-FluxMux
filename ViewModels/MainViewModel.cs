using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxMux.Avalonia.Models;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Views;

namespace FluxMux.Avalonia.ViewModels;

public enum RouteIndicatorState
{
    Off,
    Amber,
    Live,
    Fault
}

public sealed class DiscoveredLocalServerOption
{
    public int Port { get; init; }

    public string Display { get; init; } = string.Empty;

    public override string ToString() => Display;
}

public sealed class InstalledLocalServerOption
{
    public string ExecutablePath { get; init; } = string.Empty;

    public string Display { get; init; } = string.Empty;

    public override string ToString() => Display;
}

public sealed class ValidatedQuickSelectProfileOption
{
    public required string RouteType { get; init; }

    public string Provider { get; init; } = string.Empty;

    public required string Model { get; init; }

    public required string Variant { get; init; }

    public required string DisplayName { get; init; }

    public string? LogoAssetKey { get; init; }

    public string LogoCompanyName { get; init; } = string.Empty;

    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoAssetKey);

    public bool ShowMissingFileWarning { get; init; }

    public string MissingFileWarningTooltip { get; init; } =
        MainViewModel.MissingLocalFileWarningTooltip;

    public string Key => string.Join("::", RouteType, Provider, Model, NormalizeVariant(Variant));

    internal static string NormalizeVariant(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Equals(MainViewModel.BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            ? MainViewModel.BaseVariantDisplayName
            : trimmed;
    }

    public bool MatchesSavedAssignment(
        string routeType,
        string provider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant)
    {
        if (!RouteType.Equals(routeType ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return Provider.Equals(provider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && Model.Equals(cloudModel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && NormalizeVariant(Variant).Equals(NormalizeVariant(cloudVariant), StringComparison.OrdinalIgnoreCase);
        }

        return Model.Equals(localModel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && NormalizeVariant(Variant).Equals(NormalizeVariant(localVariant), StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => DisplayName;

    public override bool Equals(object? obj)
        => obj is ValidatedQuickSelectProfileOption other
           && Key.Equals(other.Key, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Key);
}

public sealed class SwitchBenchmarkProfileOption
{
    public required string RouteType { get; init; }

    public string Provider { get; init; } = string.Empty;

    public required string Model { get; init; }

    public required string Variant { get; init; }

    public required string DisplayName { get; init; }

    public string? LogoAssetKey { get; init; }

    public string LogoCompanyName { get; init; } = string.Empty;

    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoAssetKey);

    public bool ShowMissingFileWarning { get; init; }

    public string MissingFileWarningTooltip { get; init; } =
        MainViewModel.MissingLocalFileWarningTooltip;

    public string Key => string.Join("::", RouteType, Provider, Model, Variant);

    public override string ToString() => DisplayName;
}

public partial class MainViewModel : ViewModelBase
{
    public event Action<CloudVariantTreeItemViewModel, bool>? ProfileExpanderRevealRequested;

    // Cloud and local expanders both host this view-model as InlineEditor. Only one
    // row may be expanded, or Avalonia moves that visual into the other tree.
    internal bool SuppressProfileExpanderReveal =>
        _suppressVariantExpanderSelection
        || _isSynchronizingAdvancedProfileSelection
        || _holdProfileLayout;

    internal const string BaseVariantDisplayName = "(defaults)";
    internal const string MissingLocalFileWarningTooltip =
        "This local model is not in the folder you set on the Servers tab.";
    internal const string DefaultEndpointWarningTooltip =
        "This profile needs Validate to endpoint again.";
    internal const string SettingsSavedAfterValidateWarning =
        "Settings were saved after the last successful Validate. Run Validate to endpoint again.";
    internal const string ValidatedEndpointStatusText =
        "Validated. Latest saved settings match that test. Listed in the Quick Select dropdown.";
    internal const string NotYetValidatedEndpointStatusText =
        "Not yet offered in Quick Select. Run Validate to endpoint.";
    internal const string UnsavedEditsAfterValidateStatusText =
        "Unsaved edits. Last Validate matches the last Save, not these on-screen changes.";
    internal const string DuplicateProfileSettingsSaveFailedMessage =
        "Save failed - profile settings are identical to another profile.";
    private const string ProfileIsModelDefaultKey = "IsModelDefault";
    private const string ProfileDefaultUnlockedKey = "DefaultUnlocked";
    private const int RouteSlotSchemaVersion = 2;
    private const int DefaultQuickSelectSlotLimit = 10;

    private readonly FluxMuxConfigService _configService;
    private readonly DependencyAuditService _dependencyAuditService;
    private readonly FluxMuxRuntimeService _runtimeService;
    private FluxMuxConfigDocument _config;
    private readonly string _secretsPath;
    private string _lastHealthDetail = string.Empty;
    private bool _dependencyTabInitialized;
    private CancellationTokenSource? _localReadinessPollCts;
    private CancellationTokenSource? _localLaunchCountdownCts;
    private CancellationTokenSource? _activeEndpointHealthCts;
    private bool _benchmarkStatusActive;
    private bool _suppressStatusDiagnostic;
    private string _benchmarkProgressText = string.Empty;
    private string _localLaunchCountdownText = string.Empty;
    private string _localLaunchPhase = "Loading model";
    private int _localLaunchPhaseVersion;
    private readonly List<string> _diagnosticEntries = [];
    private string _lastDiagnosticPayload = string.Empty;
    private bool _isHydratingCloudVariantForm;
    private bool _isApplyingCloudPriorityWizard;
    private bool _isHydratingLocalVariantForm;
    private readonly Dictionary<string, string> _acceptedLocalComboValues = new(StringComparer.OrdinalIgnoreCase);
    private string _hydratedLocalVisionEnabled = "Disabled";
    private string _hydratedLocalVisionProjectorPath = string.Empty;
    private bool _preservingLocalVisionForm;
    private bool _isValidatingLocalProfile;
    private bool _isValidatingCloudProfile;
    private bool _isRefreshingVisionProjectorChoices;
    private bool _isApplyingLocalPriorityWizard;
    private bool _localAutoTuneRunning;
    private bool _isLoadingConfig;
    private bool _isRefreshingCloudApiKeyField;
    private bool _isRefreshingCustomCompatEndpoint;
    private bool _cloudApiKeyShowsStoredMask;
    private bool _isSynchronizingAdvancedProfileSelection;
    private bool _suppressVariantExpanderSelection;
    private bool _holdProfileLayout;
    private bool _lockToggleBusy;
    private string[]? _ggufPathCache;
    private string? _ggufPathCacheDirectory;
    private int _variantTreeRestoreEpoch;
    private string? _pinnedCreatedVariantKey;
    private RouteSlotViewModel? _activeQuickSelectSlot;
    private bool _activeQuickSelectInFlightKnown;
    private string _lastCloudBreakerTelemetrySignature = string.Empty;
    private string _mountedRouteType = string.Empty;
    private string _mountedCloudProvider = string.Empty;
    private string _mountedCloudModel = string.Empty;
    private string _mountedCloudVariant = string.Empty;
    private string _runningLocalModel = string.Empty;
    private string _runningLocalVariant = string.Empty;
    private string _updateDownloadUrl = string.Empty;
    private string _llamaServerZipUrl = string.Empty;
    private string _llamaCudartZipUrl = string.Empty;
    private string _llamaReleaseUrl = string.Empty;
    private int _mountedRouteGeneration;
    private DispatcherTimer? _quickSelectLoadingPulseTimer;
    private CancellationTokenSource? _cloudRecommendCts;
    private CancellationTokenSource? _hostTelemetryCts;
    private readonly HostTelemetrySampler _hostTelemetrySampler = new();
    private int _hostTelemetryBusy;
    private readonly DeepSeekHarnessWebHost _harnessWebHost = new();
    private CancellationTokenSource? _harnessStartCts;
    private string _harnessSyncedProfileFingerprint = string.Empty;
    private string _clineSyncedContextFingerprint = string.Empty;
    private string _endpointSyncedFingerprint = string.Empty;
    private string _localVariantEditorBaselineJson = string.Empty;
    private string _localVariantEditorBaselineModel = string.Empty;
    private string _localVariantEditorBaselineVariant = string.Empty;

    private const int ModelProfilesTabIndex = 2;

    private static readonly string[] RuntimeLocalProfileMetadataKeys =
    [
        "MeasuredVramGiB",
        "MeasuredVramBaselineGiB",
        "MeasuredVramAfterGiB",
        "MeasuredVramUpdatedUtc",
        "MeasuredVramImagesOn",
        "LastVramWarning",
        "LastVramWarningUtc",
        "ValidateTtftMs",
        "ValidateTokensPerSecond",
        "ValidateCompletionTokens",
        "ValidateSpeedUpdatedUtc"
    ];

    public MainViewModel(
        FluxMuxConfigService configService,
        DependencyAuditService dependencyAuditService,
        FluxMuxRuntimeService runtimeService)
    {
        _configService = configService;
        _dependencyAuditService = dependencyAuditService;
        _runtimeService = runtimeService;
        _harnessWebHost.ProcessExited += OnHarnessWebProcessExited;
        _config = _configService.Load();
        var configDir = Path.GetDirectoryName(_configService.ConfigPath) ?? string.Empty;
        _secretsPath = Path.Combine(configDir, "fluxmux_secrets.json");
        CloudCredentialsHelpText = "Cloud credentials file: " + _secretsPath + " (preferred). Fallback keys can also be read from: " + _configService.ConfigPath;
        LoadFromConfig();
        OptionInfoDismissalStore.Attach(_config, () => _configService.Save(_config));
        RefreshInstalledLocalServerOptions();
        QuickSelectSlotLimit = Math.Clamp(_config.GetInt("QuickSelectSlotLimit", DefaultQuickSelectSlotLimit), 1, 100);
        InitializeQuickSelectSlots();
        RefreshQuickSelectVariantLists();
        RestoreQuickSelectSlotSelections();
        _runtimeService.SetRequestRoutingEnabled(RequestRoutingEnabled);
        _runtimeService.SetRequestRoutingTopology(RequestRoutingTopology.DualHot);
        SyncEndpointAppToRuntime();
        _runtimeService.SetCloudRoutingCapacity(SelectedCloudRoutingCapacity);
        _runtimeService.SetIdleTimeout(IdleTimeoutEnabled, (int)IdleTimeoutMinutes);
        _runtimeService.SetGenerationSpeedTelemetryEnabled(GenerationSpeedTelemetryEnabled);
        if (GenerationSpeedTelemetryEnabled)
        {
            RefreshGenerationSpeedTelemetry();
        }
        _runtimeService.IdleTimeoutNotice += message =>
        {
            SafeUiInvoke(() => StatusMessage = message);
        };

        var startupNotice = _runtimeService.ConsumeStartupRuntimeNotice();
        if (!string.IsNullOrWhiteSpace(startupNotice))
        {
            ConnectionHealthText = startupNotice;
            StatusMessage = "Recovered stale runtime processes during startup.";
        }

        if (!string.IsNullOrWhiteSpace(_configService.LastLoadNotice))
        {
            ConnectionHealthText = _configService.LastLoadNotice;
            StatusMessage = _configService.LastLoadNotice;
        }

        _runtimeService.ClearStaleRecoveryPromptsOnStartup();
        StartCloudRecommendMonitor();
        StartHostTelemetryMonitor();
    }

    public void NoteUserInterfaceActivity()
    {
        _runtimeService.NoteUserInterfaceActivity();
    }

    internal void ShutdownManagedRuntimesForExit()
    {
        try
        {
            _cloudRecommendCts?.Cancel();
        }
        catch
        {
        }

        StopQuickSelectLoadingPulseTimer();

        try
        {
            _hostTelemetryCts?.Cancel();
        }
        catch
        {
        }

        CancelActiveEndpointHealthMonitor();
        StopLocalLaunchCountdownMonitor();
        try
        {
            _harnessStartCts?.Cancel();
        }
        catch
        {
        }

        _harnessWebHost.Stop((int)HarnessWebPort);
        _harnessLaunchedByThisFluxMuxSession = false;

        try
        {
            var shutdown = _runtimeService.ShutdownManagedRuntimesAsync(shutdownGateway: true);
            shutdown.Wait(TimeSpan.FromSeconds(8));
        }
        catch
        {
        }
    }

    [ObservableProperty]
    public partial string SelectedCloudProvider { get; set; } = "Gemini";

    [ObservableProperty]
    public partial string SelectedCloudProfile { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedLocalProfile { get; set; } = "Local Balanced";

    [ObservableProperty]
    public partial string SelectedCloudVariant { get; set; } = BaseVariantDisplayName;

    [ObservableProperty]
    public partial string SelectedLocalVariant { get; set; } = BaseVariantDisplayName;

    [ObservableProperty]
    public partial string SelectedSelection1RouteType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedSelection1LocalProfile { get; set; } = "Local Throughput";

    [ObservableProperty]
    public partial string SelectedSelection1LocalVariant { get; set; } = BaseVariantDisplayName;

    [ObservableProperty]
    public partial string SelectedSelection2RouteType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedSelection2CloudProvider { get; set; } = "Gemini";

    [ObservableProperty]
    public partial string SelectedSelection2CloudProfile { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedSelection2CloudVariant { get; set; } = BaseVariantDisplayName;

    [ObservableProperty]
    public partial bool IsFirstProfileBootstrapPending { get; set; } = false;

    [ObservableProperty]
    public partial string FirstProfileBootstrapText { get; set; } = "No profiles yet. Choose the first route type and create your first default model profile for Selection 1.";

    [ObservableProperty]
    public partial bool IsSelectionEditorsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSelection1TemplateChosen { get; set; } = false;

    [ObservableProperty]
    public partial bool ShowSelectionEditors { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowBootstrapTemplateChooser { get; set; } = false;

    [ObservableProperty]
    public partial bool ShowBootstrapCreateFirstProfile { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelection2Enabled { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelection2TemplateChosen { get; set; } = false;

    [ObservableProperty]
    public partial bool HasSelection1Slot { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowAddNewSlotButton { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDeleteSelection1SlotButton { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowSelection2TemplateChooser { get; set; } = false;

    [ObservableProperty]
    public partial bool IsVariantEditingEnabled { get; set; } = false;

    [ObservableProperty]
    public partial string VariantEditingGateText { get; set; } = "Create a local or cloud model profile, then use Validate to endpoint before it appears in Quick Select.";

    [ObservableProperty]
    public partial string LocalServerDiscoveryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DiscoveredLocalServerPort { get; set; } = 0;

    [ObservableProperty]
    public partial bool CanAdoptDiscoveredLocalServer { get; set; } = false;

    [ObservableProperty]
    public partial DiscoveredLocalServerOption? SelectedDiscoveredLocalServer { get; set; }

    [ObservableProperty]
    public partial bool ShowDiscoveredLocalServerPicker { get; set; } = false;

    [ObservableProperty]
    public partial string LocalServerExecutablePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InstalledLocalServerOption? SelectedInstalledLocalServer { get; set; }

    [ObservableProperty]
    public partial string LocalServerInstallationText { get; set; } = "Checking for compatible local server installations...";

    [ObservableProperty]
    public partial bool IsSelection1CloudRouteSelected { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelection1LocalRouteSelected { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelection2CloudRouteSelected { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelection2LocalRouteSelected { get; set; } = false;

    [ObservableProperty]
    public partial bool IsAnyCloudRouteSelected { get; set; } = false;

    [ObservableProperty]
    public partial bool IsAnyLocalRouteSelected { get; set; } = false;

    public int QuickSelectSlotLimit { get; private set; } = DefaultQuickSelectSlotLimit;

    public bool CanAddQuickSelectSlots =>
        QuickSelectSlots.Count < QuickSelectSlotLimit
        && !QuickSelectSlots.Any(slot => slot.HasUnsavedPopulatedAssignment);

    public bool ShowQuickSelectSlotAddButton => QuickSelectSlots.Count < QuickSelectSlotLimit;

    public bool ShowQuickSelectSlotAddBlockedText =>
        ShowQuickSelectSlotAddButton && !CanAddQuickSelectSlots;

    public string QuickSelectSlotAddBlockedText => DescribeQuickSelectSlotAddBlock();

    public string QuickSelectSlotAddButtonTooltip =>
        CanAddQuickSelectSlots
            ? "Adds another shortcut row for a validated model profile."
            : DescribeQuickSelectSlotAddBlock();

    [ObservableProperty]
    public partial string LocalModelDiscoveryText { get; set; } = "Select a local model directory to discover available local models.";

    [ObservableProperty]
    public partial string LocalModelDirectory { get; set; } = "C:\\models";

    [ObservableProperty]
    public partial decimal OrchestratorPort { get; set; } = 8080;

    public IReadOnlyList<string> EndpointAppOptions { get; } =
    [
        "Cline",
        "DeepSeek Harness",
        "Other"
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHarnessSetupPanel))]
    [NotifyPropertyChangedFor(nameof(ShowHarnessServersPanel))]
    [NotifyPropertyChangedFor(nameof(EndpointSettingsIntroText))]
    [NotifyPropertyChangedFor(nameof(ShowClineEndpointHint))]
    [NotifyPropertyChangedFor(nameof(ShowGenericEndpointHint))]
    [NotifyPropertyChangedFor(nameof(EndpointModelAddressText))]
    public partial string SelectedEndpointApp { get; set; } = "Cline";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HarnessChatUrl))]
    [NotifyPropertyChangedFor(nameof(HarnessServersSummaryText))]
    public partial decimal HarnessWebPort { get; set; } = DeepSeekHarnessSetup.DefaultWebPort;

    [ObservableProperty]
    public partial string HarnessWebUiStatusText { get; set; } =
        "Click Harness web chat on the active Quick Select slot after Launch.";

    public bool IsHarnessWebRunning =>
        _harnessWebHost.IsManagedRunning((int)HarnessWebPort);

    public bool IsHarnessWebFromPriorFluxMuxSession =>
        IsHarnessWebRunning
        && !_harnessWebHost.IsRunning
        && !_harnessLaunchedByThisFluxMuxSession;

    public bool IsHarnessWebProcessTracked => _harnessWebHost.IsRunning;

    public bool CanStartHarnessWeb => ShowHarnessSetupPanel;

    private bool _harnessWebUiReachable;
    private bool _harnessWebWasReachableThisSession;
    private bool _harnessWebStopIntentional;
    private int _harnessStatusProbeCounter;
    private bool _harnessLaunchedByThisFluxMuxSession;
    private int _harnessReconcileBusy;

    public bool CanStopHarnessWeb => ShowHarnessSetupPanel && IsHarnessWebRunning;

    public bool ShowHarnessStopUnavailableHint =>
        ShowHarnessSetupPanel && _harnessWebUiReachable && !IsHarnessWebRunning;

    public string HarnessStopUnavailableHint =>
        "Harness web is already running on "
        + HarnessChatUrl
        + " from outside AI-FluxMux (for example dsh web in a terminal). Close that process, then use Harness web chat here.";

    public string HarnessStopButtonTooltip =>
        IsHarnessWebRunning
            ? "DeepSeek Harness only. Stops Harness web that AI-FluxMux started, including after an AI-FluxMux restart. Does not stop llama-server."
            : "DeepSeek Harness only. Available when AI-FluxMux started Harness web on this PC.";

    public string HarnessWebChatButtonText =>
        IsHarnessWebRunning ? "Open Harness chat" : "Start Harness web";

    public string HarnessWebChatButtonTooltip =>
        "Experimental. Syncs Harness settings for the loaded profile, then opens or starts Harness web chat."
        + (IsHarnessWebProcessTracked
            ? " Harness is already running — opens the browser again."
            : IsHarnessWebFromPriorFluxMuxSession
                ? " Harness web from a prior AI-FluxMux run is still up — opens the browser again."
                : IsHarnessWebRunning
                    ? " Harness web is already up — opens the browser again."
                    : " Also starts dsh web if nothing is listening on the Harness web UI port.");

    public string HarnessLaunchModeText => string.IsNullOrWhiteSpace(_harnessWebHost.LaunchMode)
        ? string.Empty
        : "Launcher: " + _harnessWebHost.LaunchMode;

    /// <summary>
    /// Harness Quick Select launcher and its start / stop. Not gated on the Client app
    /// list — Cline and Harness can share Port at once, so a Harness web chat is never
    /// stranded without a way to stop it.
    /// </summary>
    public bool ShowHarnessSetupPanel => true;

    public bool HarnessWebIsInPlay =>
        IsHarnessWebRunning || _harnessWebUiReachable || _harnessLaunchedByThisFluxMuxSession;

    /// <summary>
    /// The Harness block on Servers: install links, web UI port, settings.yaml. Shown
    /// when the Client app list names Harness, so each selection carries its own setup
    /// notes — and also while Harness web is actually up, when its port is what the
    /// operator needs to reach it. Quick Select keeps its launcher either way, so a
    /// running web chat is never left without a way to stop it.
    /// </summary>
    public bool ShowHarnessServersPanel =>
        SelectedEndpointApp.Contains("Harness", StringComparison.OrdinalIgnoreCase)
        || HarnessWebIsInPlay;

    public bool ShowClineEndpointHint =>
        SelectedEndpointApp.Equals("Cline", StringComparison.OrdinalIgnoreCase);

    public bool ShowGenericEndpointHint =>
        SelectedEndpointApp.Equals("Other", StringComparison.OrdinalIgnoreCase);

    public string EndpointSettingsIntroText =>
        "Port is the OpenAI-compatible model API Client apps call. Several Clients can be active at once if they all use this Port."
        + HarnessIntroSentence
        + " Export Endpoint adapters saves Port and the EndpointAdapters JSON for manual editing or adding settings sync for new Client apps, and allows saved settings recovery after a bad edit or an AI-FluxMux reinstall; see Help → Appendix C — Endpoint adapters.";

    /// <summary>
    /// The Harness block is only below while Harness is the nominated client, so this
    /// sentence drops out rather than pointing at a panel that is not there.
    /// </summary>
    private string HarnessIntroSentence =>
        ShowHarnessServersPanel
            ? " DeepSeek Harness adds the Quick Select launcher and the web UI port below."
            : string.Empty;

    public string ClineEndpointHintText =>
        "Cline: install and configure Cline in VS Code — settings: \"OpenAI Compatible\", base URL "
        + BuildPublicEndpointOrigin()
        + " (no /v1 — Cline adds that), model id \"local\", API key: \" \" (one space character). "
        + "Cline defaults Context Window to 128000 and does not read Context from Port. "
        + "Cline 4.x: Launch writes Context and the loaded model profile name to Cline's OpenAI Compatible settings whenever those files already point at Port, even if Harness or another Client app is also using Port. Cline shows that name as the Model ID; Port still gets the turn. It matches this local model profile ("
        + ((int)LocalVariantContext).ToString(CultureInfo.InvariantCulture)
        + "). Then reload the editor so Cline rereads the files. Older Cline: expand MODEL CONFIGURATION (chevron below Azure Identity) and set Context Window Size. "
        + "Launch also turns off Cline Auto compact (use FluxMux Compact) and leaves Supports Images ticked if a validated Images-on profile exists, so a picture can reach Port. "
        + "Start a new Cline task after Launch. If the bar or those toggles are unchanged, reload that editor window. AI-FluxMux keeps running if you close the editor.";

    public string GenericEndpointHintText =>
        HelpHtmlParser.NoteBullet
        + " Point your Client app at Port. The address is the Model API line beside the Client app box, and the model id is local unless your app needs another one.\n"
        + HelpHtmlParser.NoteBullet
        + " Set that app's own Context and reply length to suit the model you load. AI-FluxMux does not write them for you, so revisit them whenever you change models.\n"
        + HelpHtmlParser.NoteBullet
        + " To have them written for you each time you Launch, add an Endpoint adapter; see Help → Appendix C — Endpoint adapters.";

    public string EndpointModelAddressText =>
        "Model API: "
        + BuildPublicEndpointOrigin()
        + "  \u00b7  model id local";

    private string BuildPublicEndpointOrigin()
        => "http://127.0.0.1:" + ((int)OrchestratorPort).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The live values a UI note written in Help.html can name, so the sentence around
    /// them belongs to the operator in Word while the number stays current here.
    /// </summary>
    private void PublishHelpNoteValues()
    {
        HelpUiNotes.SetValue(HelpUiNotes.AddressValue, BuildPublicEndpointOrigin());
        HelpUiNotes.SetValue(
            HelpUiNotes.PortValue,
            ((int)OrchestratorPort).ToString(CultureInfo.InvariantCulture));
        HelpUiNotes.SetValue(
            HelpUiNotes.ProfileContextValue,
            ((int)LocalVariantContext).ToString(CultureInfo.InvariantCulture));
        HelpUiNotes.SetValue(HelpUiNotes.HarnessChatUrlValue, HarnessChatUrl);
    }

    public string HarnessChatUrl => DeepSeekHarnessSetup.BuildChatUrl((int)HarnessWebPort);

    public string HarnessServersSummaryText =>
        "Install DeepSeek Harness (dsh) on this PC first — Install guide below. "
        + "Then Quick Select: Launch the active slot \u2192 Harness web chat (inline with Launch). "
        + "AI-FluxMux updates Harness settings to match the loaded profile and stops Harness web when you Stop the model, switch profiles, or close AI-FluxMux. "
        + "Port above is the model hop; Harness web UI port below is the browser chat address (usually "
        + HarnessChatUrl
        + "). With Route requests on, Harness uses prompted switches. A hop to cloud, or back to an already-loaded local, keeps this Harness chat. Start a new Harness session after a llama-server reload. Approving a local swap on a routing pop-up loads that local and opens Harness chat from that slot. See Help for session and yaml detail.";

    public string HarnessServersAdvancedNote =>
        "Launch already writes these settings for you. Use these buttons only to retry after that fails; see Help → Appendix E — Harness settings.yaml.";

    partial void OnOrchestratorPortChanged(decimal value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _config.SetInt("OrchestratorPort", (int)value);
        OnPropertyChanged(nameof(EndpointModelAddressText));
        OnPropertyChanged(nameof(ClineEndpointHintText));
        PublishHelpNoteValues();
        RefreshClineSettingsStatus();
    }

    partial void OnSelectedEndpointAppChanged(string value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _config.SetString("EndpointApp", value?.Trim() ?? string.Empty);
        SyncEndpointAppToRuntime();
        OnPropertyChanged(nameof(ShowHarnessSetupPanel));
        OnPropertyChanged(nameof(ShowHarnessServersPanel));
        OnPropertyChanged(nameof(EndpointSettingsIntroText));
        OnPropertyChanged(nameof(ShowClineEndpointHint));
        OnPropertyChanged(nameof(ShowGenericEndpointHint));
        RefreshClineSettingsStatus();
        UpdateRequestRoutingModeText();
        NotifyQuickSelectHarnessControlsChanged();
        TryAdoptHarnessFromPriorSession();
        RefreshHarnessProcessState();
        _ = RefreshHarnessWebUiStatusAsync();

        TrySyncEndpointSettingsForMountedRoute();
        RefreshCloudRecommendBanner();
        RefreshLocalReloadRecommendBanner();
    }

    partial void OnHarnessWebPortChanged(decimal value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _config.SetInt("HarnessWebPort", (int)value);
        OnPropertyChanged(nameof(HarnessChatUrl));
        OnPropertyChanged(nameof(HarnessServersSummaryText));
        PublishHelpNoteValues();
    }

    private void TryAdoptHarnessFromPriorSession()
    {
        DeepSeekHarnessWebHost.TryAdoptFluxMuxHarnessOnPort(
            (int)HarnessWebPort,
            uiReachable: DeepSeekHarnessWebHost.IsPortListening((int)HarnessWebPort));
    }

    private async Task RefreshHarnessWebUiStatusAsync()
    {
        var port = (int)HarnessWebPort;
        await Task.Run(() =>
        {
            DeepSeekHarnessWebHost.ReconcilePidRecordForPort(port);
        }).ConfigureAwait(true);

        var reachable = await DeepSeekHarnessSetup.ProbeWebUiReachableAsync(port).ConfigureAwait(true);
        if (reachable)
        {
            await Task.Run(() =>
            {
                DeepSeekHarnessWebHost.TryAdoptFluxMuxHarnessOnPort(port, uiReachable: true);
            }).ConfigureAwait(true);
        }

        SetHarnessWebUiReachable(reachable);
        RefreshHarnessInstalledVersion();
        HarnessWebUiStatusText = reachable
            ? IsHarnessWebFromPriorFluxMuxSession
                ? "Harness web from a prior AI-FluxMux run is still on "
                  + HarnessChatUrl
                  + ". Stop Harness web or click Harness web chat to open the page."
                : "Harness web UI responded on " + HarnessChatUrl + ". Click Harness web chat on the active Quick Select slot to open the page."
            : IsHarnessWebRunning
                ? "DeepSeek Harness is starting on " + HarnessChatUrl + "…"
                : _harnessWebWasReachableThisSession
                    ? "Harness web is not running. A Failed to fetch error in the Harness chat page is that page, not Port. Click Harness web chat on the active Quick Select slot."
                    : "Click Harness web chat on the active Quick Select slot to open the chat page.";
        RefreshHarnessProcessState();
    }

    private void MaybeReconcileHarnessWebState()
    {
        _harnessStatusProbeCounter++;
        var shouldHttpProbe = _harnessStatusProbeCounter >= 10;
        if (shouldHttpProbe)
        {
            _harnessStatusProbeCounter = 0;
            _ = RefreshHarnessWebUiStatusAsync();
            return;
        }

        if (Interlocked.CompareExchange(ref _harnessReconcileBusy, 1, 0) != 0)
        {
            return;
        }

        var port = (int)HarnessWebPort;
        var processTracked = _harnessWebHost.IsRunning;
        var uiReachable = _harnessWebUiReachable;
        var statusText = HarnessWebUiStatusText;
        _ = Task.Run(() =>
        {
            try
            {
                var clearedStalePid = DeepSeekHarnessWebHost.ReconcilePidRecordForPort(port);
                var portListening = DeepSeekHarnessWebHost.IsPortListening(port);
                if (portListening || processTracked || (!uiReachable && !clearedStalePid))
                {
                    return;
                }

                SafeUiInvoke(() =>
                {
                    if (_harnessWebUiReachable)
                    {
                        SetHarnessWebUiReachable(false);
                    }

                    if (statusText.Contains("prior AI-FluxMux run", StringComparison.OrdinalIgnoreCase)
                        || statusText.Contains("Harness web UI responded", StringComparison.OrdinalIgnoreCase)
                        || statusText.Contains("DeepSeek Harness is starting", StringComparison.OrdinalIgnoreCase))
                    {
                        HarnessWebUiStatusText = "Click Harness web chat on the active Quick Select slot to open the chat page.";
                    }

                    RefreshHarnessProcessState();
                });
            }
            finally
            {
                Interlocked.Exchange(ref _harnessReconcileBusy, 0);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanStartHarnessWeb))]
    private async Task StartHarnessWebAsync()
    {
        _harnessStartCts?.Cancel();
        _harnessStartCts = new CancellationTokenSource();
        var token = _harnessStartCts.Token;

        var routeReady = await EnsureHarnessModelRouteReadyAsync(token);
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!routeReady.Ready)
        {
            StatusMessage = routeReady.Message;
            HarnessWebUiStatusText = routeReady.Message;
            RefreshHarnessYamlSurfaces();
            return;
        }

        var merge = DeepSeekHarnessSetup.MergeIntoSettingsFile(BuildHarnessSetupOptions());
        if (!merge.Success)
        {
            StatusMessage = merge.Message;
            HarnessWebUiStatusText = merge.Message;
            return;
        }

        StatusMessage = merge.Message;
        RefreshHarnessYamlSurfaces();

        var port = (int)HarnessWebPort;
        try
        {
            var status = await DeepSeekHarnessSetup.ProbeWebUiStatusAsync(port, token).ConfigureAwait(true);
            var haveAuthUrl = DeepSeekHarnessWebHost.TryReadWebAuthUrlFromLaunchLog(port, out _);
            var startedByFluxMux = false;
            if (DeepSeekHarnessSetup.NeedsFreshWebAuth(status, haveAuthUrl))
            {
                HarnessWebUiStatusText = "Restarting DeepSeek Harness web so the chat page can open…";
                _harnessWebHost.Stop(port);
                await DeepSeekHarnessWebHost.WaitForPortClosedAsync(port, TimeSpan.FromSeconds(8), token)
                    .ConfigureAwait(true);
                var restart = _harnessWebHost.TryStart(port);
                if (!restart.Success)
                {
                    StatusMessage = restart.Message;
                    RefreshHarnessProcessState();
                    return;
                }

                startedByFluxMux = true;
                _harnessLaunchedByThisFluxMuxSession = true;
                RefreshHarnessProcessState();
            }
            else if (!DeepSeekHarnessSetup.IsWebUiReadyToOpen(status, haveAuthUrl)
                     && !IsHarnessWebRunning)
            {
                var result = _harnessWebHost.TryStart(port);
                if (!result.Success)
                {
                    StatusMessage = result.Message;
                    RefreshHarnessProcessState();
                    return;
                }

                startedByFluxMux = true;
                _harnessLaunchedByThisFluxMuxSession = true;
                HarnessWebUiStatusText = "DeepSeek Harness is starting on " + HarnessChatUrl + "…";
                RefreshHarnessProcessState();
            }

            if (startedByFluxMux
                || !DeepSeekHarnessSetup.IsWebUiReadyToOpen(
                    status,
                    DeepSeekHarnessWebHost.TryReadWebAuthUrlFromLaunchLog(port, out _)))
            {
                var ready = await DeepSeekHarnessWebHost.WaitForWebUiAsync(
                    port,
                    TimeSpan.FromMinutes(2),
                    token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (!ready)
                {
                    var logTail = DeepSeekHarnessWebHost.ReadLaunchLogTail();
                    var settingsHint = logTail.Contains("settings.yaml", StringComparison.OrdinalIgnoreCase)
                        ? " Fix %USERPROFILE%\\.dsh\\settings.yaml (restore settings.yaml.bak or click Write settings.yaml again)."
                        : string.Empty;
                    HarnessWebUiStatusText = "DeepSeek Harness did not open on "
                        + HarnessChatUrl
                        + "."
                        + settingsHint;
                    if (!string.IsNullOrWhiteSpace(logTail))
                    {
                        StatusMessage = "Harness launch log: " + logTail.Replace('\n', ' ').Replace('\r', ' ');
                    }

                    return;
                }
            }

            await OpenSyncedHarnessChatAsync(token, startedByFluxMux || IsHarnessWebRunning).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            RefreshHarnessProcessState();
        }
    }

    private async Task OpenSyncedHarnessChatAsync(CancellationToken token, bool startedByFluxMux)
    {
        var reachable = await DeepSeekHarnessSetup.ProbeWebUiReachableAsync((int)HarnessWebPort, token)
            .ConfigureAwait(true);
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!reachable)
        {
            HarnessWebUiStatusText = startedByFluxMux
                ? "DeepSeek Harness is still starting on " + HarnessChatUrl + "…"
                : "No Harness web UI on " + HarnessChatUrl + ". Click Start Harness web to launch dsh.";
            return;
        }

        var port = (int)HarnessWebPort;
        var status = await DeepSeekHarnessSetup.ProbeWebUiStatusAsync(port, token).ConfigureAwait(true);
        var haveAuthUrl = DeepSeekHarnessWebHost.TryReadWebAuthUrlFromLaunchLog(port, out _);
        if (DeepSeekHarnessSetup.NeedsFreshWebAuth(status, haveAuthUrl))
        {
            HarnessWebUiStatusText = "DeepSeek Harness needs a fresh chat URL. Click Harness web chat again.";
            return;
        }

        var openUrl = DeepSeekHarnessSetup.ResolveOpenChatUrl(port);
        HarnessWebUiStatusText = "Harness web UI is ready on "
            + HarnessChatUrl
            + ". Harness settings were updated to match the loaded local profile capacity.";
        _harnessSyncedProfileFingerprint = BuildHarnessProfileFingerprint();
        OpenExternalLink(openUrl);
        SetHarnessWebUiReachable(true);
        RefreshHarnessProcessState();
    }

    private void SetHarnessWebUiReachable(bool reachable)
    {
        if (_harnessWebUiReachable == reachable)
        {
            return;
        }

        _harnessWebUiReachable = reachable;
        OnPropertyChanged(nameof(ShowHarnessSetupPanel));
        OnPropertyChanged(nameof(ShowHarnessServersPanel));
        OnPropertyChanged(nameof(EndpointSettingsIntroText));
        OnPropertyChanged(nameof(ShowHarnessStopUnavailableHint));
        OnPropertyChanged(nameof(HarnessStopUnavailableHint));
        OnPropertyChanged(nameof(HarnessStopButtonTooltip));
        OnPropertyChanged(nameof(IsHarnessWebRunning));
        OnPropertyChanged(nameof(IsHarnessWebFromPriorFluxMuxSession));
        OnPropertyChanged(nameof(CanStopHarnessWeb));
        StopHarnessWebCommand.NotifyCanExecuteChanged();
        NotifyQuickSelectHarnessControlsChanged();
        if (reachable)
        {
            _harnessWebWasReachableThisSession = true;
            _harnessWebStopIntentional = false;
            ClearHarnessWebHealthReportIfShowing();
            return;
        }

        if (_harnessWebWasReachableThisSession && !_harnessWebStopIntentional)
        {
            PublishHarnessWebDownIfModelsAreMounted();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopHarnessWeb))]
    private void StopHarnessWeb()
    {
        _harnessStartCts?.Cancel();
        _harnessWebStopIntentional = true;
        _harnessWebHost.Stop((int)HarnessWebPort);
        _harnessLaunchedByThisFluxMuxSession = false;
        SetHarnessWebUiReachable(false);
        HarnessWebUiStatusText = "Stopped DeepSeek Harness web started by AI-FluxMux.";
        StatusMessage = "DeepSeek Harness web stopped.";
        RefreshHarnessProcessState();
        _ = RefreshHarnessWebUiStatusAsync();
    }

    private void OnHarnessWebProcessExited()
    {
        SafeUiInvoke(() =>
        {
            DeepSeekHarnessWebHost.ReconcilePidRecordForPort((int)HarnessWebPort);
            // Launcher (npx/dsh) often exits while node keeps serving the UI — not a real stop.
            if (IsHarnessWebRunning)
            {
                RefreshHarnessProcessState();
                _ = RefreshHarnessWebUiStatusAsync();
                return;
            }

            _harnessLaunchedByThisFluxMuxSession = false;
            SetHarnessWebUiReachable(false);
            HarnessWebUiStatusText = "DeepSeek Harness web exited. Click Harness web chat on the active Quick Select slot to launch it again.";
            if (_harnessWebWasReachableThisSession)
            {
                StatusMessage = LocalHealthGuidance.HarnessWebDownHeadline;
            }

            RefreshHarnessProcessState();
        });
    }

    private void RefreshHarnessProcessState()
    {
        OnPropertyChanged(nameof(IsHarnessWebRunning));
        OnPropertyChanged(nameof(IsHarnessWebFromPriorFluxMuxSession));
        OnPropertyChanged(nameof(IsHarnessWebProcessTracked));
        OnPropertyChanged(nameof(ShowHarnessSetupPanel));
        OnPropertyChanged(nameof(ShowHarnessServersPanel));
        OnPropertyChanged(nameof(EndpointSettingsIntroText));
        OnPropertyChanged(nameof(CanStartHarnessWeb));
        OnPropertyChanged(nameof(CanStopHarnessWeb));
        OnPropertyChanged(nameof(HarnessLaunchModeText));
        OnPropertyChanged(nameof(HarnessWebChatButtonText));
        OnPropertyChanged(nameof(HarnessWebChatButtonTooltip));
        OnPropertyChanged(nameof(HarnessStopButtonTooltip));
        OnPropertyChanged(nameof(ShowHarnessStopUnavailableHint));
        OnPropertyChanged(nameof(HarnessStopUnavailableHint));
        RefreshHarnessYamlSurfaces();
        NotifyQuickSelectHarnessControlsChanged();
        StartHarnessWebCommand.NotifyCanExecuteChanged();
        StopHarnessWebCommand.NotifyCanExecuteChanged();
    }

    private bool _harnessVersionProbeStarted;

    private void RefreshHarnessInstalledVersion()
    {
        if (_harnessVersionProbeStarted)
        {
            return;
        }

        _harnessVersionProbeStarted = true;
        _ = Task.Run(() =>
        {
            var result = DeepSeekHarnessUpdateCheck.ReadInstalled();
            Dispatcher.UIThread.Post(() => ApplyHarnessInstalledVersionOnly(result));
        });
    }

    /// <summary>
    /// Reads Cline's own settings off the UI thread and says whether they call Port.
    /// Cline is an editor extension, so there is no version to report the way there is
    /// for dsh — what the operator needs to know is whether Launch can reach it.
    /// </summary>
    private void RefreshClineSettingsStatus()
    {
        var port = (int)OrchestratorPort;
        _ = Task.Run(() =>
        {
            var status = ClineContextSync.ReadPortStatus(port);
            Dispatcher.UIThread.Post(() => ClineSettingsStatusText = DescribeClineSettings(status, port));
        });
    }

    private static string DescribeClineSettings(ClinePortStatus status, int port)
    {
        var address = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        return status switch
        {
            ClinePortStatus.PointsAtPort =>
                "Cline settings on this PC point at Port " + port.ToString(CultureInfo.InvariantCulture)
                + ". Launch keeps Cline Context and the loaded model profile name in step.",
            ClinePortStatus.PointsElsewhere =>
                "Cline settings were found on this PC, but they do not point at Port "
                + port.ToString(CultureInfo.InvariantCulture)
                + ". Set Cline OpenAI Compatible base URL to " + address
                + " so Launch can keep its Context in step.",
            _ =>
                "No Cline settings were found on this PC. Install Cline in VS Code, then set "
                + "OpenAI Compatible base URL to " + address + " and model id local."
        };
    }

    private void ApplyHarnessInstalledVersionOnly(DeepSeekHarnessUpdateResult result)
    {
        if (string.IsNullOrWhiteSpace(result.InstalledVersion)
            && HarnessInstalledVersionText.StartsWith("DeepSeek Harness on this PC:", StringComparison.Ordinal))
        {
            return;
        }

        HarnessInstalledVersionText = string.IsNullOrWhiteSpace(result.InstalledVersion)
            ? result.StatusText
            : "DeepSeek Harness on this PC: " + result.InstalledVersion
                + (string.IsNullOrWhiteSpace(result.LaunchMode) ? string.Empty : " (" + result.LaunchMode + ")");
    }

    private void NotifyQuickSelectHarnessControlsChanged()
    {
        foreach (var slot in QuickSelectSlots)
        {
            slot.NotifyHarnessPanelChanged();
        }
    }

    [RelayCommand]
    private async Task CopyHarnessSettingsYamlAsync()
    {
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null)
        {
            StatusMessage = "Clipboard unavailable in this session.";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(DeepSeekHarnessSetup.BuildSettingsYaml(BuildHarnessSetupOptions()));
            StatusMessage = "Harness settings snippet copied.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Clipboard copy failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task WriteHarnessSettingsYamlAsync()
    {
        await Task.Yield();
        var routeReady = IsHarnessModelRouteReady(out var blocked);
        var result = DeepSeekHarnessSetup.MergeIntoSettingsFile(BuildHarnessSetupOptions());
        StatusMessage = routeReady
            ? result.Message
            : blocked + " " + result.Message;
        if (result.Success)
        {
            RefreshHarnessYamlSurfaces();
        }
    }

    private bool PreferApprovedCloudRoute()
        => RequestRoutingEnabled
            && _runtimeService.IsApprovedCloudWindowActive()
            && _runtimeService.IsCloudRouteActive()
            && !string.IsNullOrWhiteSpace(_mountedCloudProvider)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel);

    private bool HarnessYamlPacksFromLocal()
        => DeepSeekHarnessSetup.YamlPackingFollowsLiveLocal(
            _runtimeService.IsManagedLocalAlive() && !string.IsNullOrWhiteSpace(_runningLocalModel));

    private bool HarnessYamlFollowsLocalProfile()
        => HarnessYamlPacksFromLocal() && !PreferApprovedCloudRoute();

    private DeepSeekHarnessSetupOptions BuildHarnessSetupOptions()
    {
        return DeepSeekHarnessSetupOptions.Create(
            (int)OrchestratorPort,
            ResolveHarnessYamlContextWindow(),
            ResolveHarnessYamlMaxTokens(),
            ResolveHarnessYamlImagesOn(),
            ResolveHarnessYamlReasoningOn(),
            ResolveHarnessYamlModelDisplayName());
    }

    private bool ResolveHarnessYamlReasoningOn()
    {
        var loadedReasoningOn = false;
        if (HarnessYamlPacksFromLocal()
            || IsLocalProfileCurrentlyLoaded(_runningLocalModel, _runningLocalVariant))
        {
            var settings = GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel);
            loadedReasoningOn = (settings?["LocalReasoning"]?.ToString() ?? string.Empty)
                .Equals("On", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            loadedReasoningOn = LocalVariantReasoning.Equals("On", StringComparison.OrdinalIgnoreCase);
        }

        return DeepSeekHarnessSetup.ShouldAdvertiseCapability(
            loadedReasoningOn,
            HasOfferableLocalProfile(profile =>
                (profile["LocalReasoning"]?.ToString() ?? string.Empty)
                    .Equals("On", StringComparison.OrdinalIgnoreCase)));
    }

    private string ResolveHarnessYamlModelDisplayName()
    {
        if (HarnessYamlFollowsLocalProfile())
        {
            var live = _runtimeService.GetManagedLocalIdentity();
            return FormatHarnessModelDisplayName(
                FirstNonEmpty(live.Model, _runningLocalModel),
                FirstNonEmpty(live.Variant, _runningLocalVariant));
        }

        if (_runtimeService.IsCloudRouteActive()
            && !string.IsNullOrWhiteSpace(_mountedCloudProvider)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel))
        {
            return FormatHarnessModelDisplayName(
                _mountedCloudProvider + " / " + _mountedCloudModel,
                _mountedCloudVariant);
        }

        if (!string.IsNullOrWhiteSpace(_runningLocalModel))
        {
            return FormatHarnessModelDisplayName(_runningLocalModel, _runningLocalVariant);
        }

        if (!string.IsNullOrWhiteSpace(SelectedLocalProfile))
        {
            return FormatHarnessModelDisplayName(SelectedLocalProfile, SelectedLocalVariant);
        }

        return "local";
    }

    private string FormatHarnessModelDisplayName(string modelLabel, string? variant)
        => FluxMuxGatewayModels.FormatProfileId(modelLabel, variant);

    private bool IsHarnessModelRouteReady(out string blockedMessage)
    {
        var localAlive = _runtimeService.IsManagedLocalAlive();
        var cloudActive = _runtimeService.IsCloudRouteActive();
        if (localAlive || cloudActive)
        {
            blockedMessage = string.Empty;
            return true;
        }

        blockedMessage =
            "No local or cloud model is running in AI-FluxMux yet. On Quick Select, Launch a validated model profile (or Validate to endpoint on Model Profiles), then try again.";
        return false;
    }

    private async Task<(bool Ready, string Message)> EnsureHarnessModelRouteReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsHarnessModelRouteReady(out var blocked))
        {
            return (false, blocked);
        }

        var apiReady = await DeepSeekHarnessSetup.ProbeModelApiReachableAsync((int)OrchestratorPort, cancellationToken)
            .ConfigureAwait(true);
        if (apiReady)
        {
            return (true, string.Empty);
        }

        return (false,
            "AI-FluxMux is not responding on "
            + DeepSeekHarnessSetup.BuildModelBaseUrl((int)OrchestratorPort)
            + " yet. Wait for Launch to finish, or check Port on Servers.");
    }

    private int ResolveHarnessYamlContextWindow()
    {
        if (HarnessYamlPacksFromLocal())
        {
            var settings = GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel);
            if (settings is not null)
            {
                return AdvertiseHarnessYamlContext(
                    ParseInt(settings["OverrideContext"]?.ToString() ?? string.Empty, (int)LocalVariantContext));
            }
        }

        if (_runtimeService.IsCloudRouteActive()
            && !string.IsNullOrWhiteSpace(_mountedCloudProvider)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel))
        {
            var cloudSettings = GetLiveCloudProfileSettings(
                _mountedCloudVariant,
                _mountedCloudProvider,
                _mountedCloudModel);
            if (cloudSettings is not null)
            {
                return DeepSeekHarnessSetup.ResolveNumericSetting(
                    cloudSettings["CloudContextWindow"]?.ToString(),
                    128000);
            }
        }

        if (IsLocalProfileCurrentlyLoaded(_runningLocalModel, _runningLocalVariant))
        {
            var settings = GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel);
            if (settings is not null)
            {
                return AdvertiseHarnessYamlContext(
                    ParseInt(settings["OverrideContext"]?.ToString() ?? string.Empty, (int)LocalVariantContext));
            }
        }

        return AdvertiseHarnessYamlContext((int)LocalVariantContext);
    }

    private int ResolveHarnessYamlMaxTokens()
    {
        if (HarnessYamlPacksFromLocal())
        {
            var settings = GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel);
            if (settings is not null)
            {
                return AdvertiseHarnessYamlMaxTokens(
                    DeepSeekHarnessSetup.ResolveNumericSetting(
                        settings["OverrideMaxTokens"]?.ToString(),
                        4096));
            }
        }

        if (_runtimeService.IsCloudRouteActive()
            && !string.IsNullOrWhiteSpace(_mountedCloudProvider)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel))
        {
            var cloudSettings = GetLiveCloudProfileSettings(
                _mountedCloudVariant,
                _mountedCloudProvider,
                _mountedCloudModel);
            if (cloudSettings is not null)
            {
                return DeepSeekHarnessSetup.ResolveNumericSetting(
                    cloudSettings["CloudMaxTokens"]?.ToString(),
                    4096);
            }
        }

        if (int.TryParse(LocalVariantMaxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return AdvertiseHarnessYamlMaxTokens(parsed);
        }

        return AdvertiseHarnessYamlMaxTokens(4096);
    }

    private bool ResolveHarnessYamlImagesOn()
    {
        var loadedImagesOn = false;
        if (HarnessYamlPacksFromLocal()
            || IsLocalProfileCurrentlyLoaded(_runningLocalModel, _runningLocalVariant))
        {
            var settings = GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel);
            loadedImagesOn = (settings?["LocalVisionEnabled"]?.ToString() ?? string.Empty)
                .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            loadedImagesOn = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }

        return DeepSeekHarnessSetup.ShouldAdvertiseImages(
            loadedImagesOn,
            HasOfferableLocalProfile(profile =>
                (profile["LocalVisionEnabled"]?.ToString() ?? string.Empty)
                    .Equals("Enabled", StringComparison.OrdinalIgnoreCase)));
    }

    private int AdvertiseHarnessYamlContext(int loadedContext)
        => DeepSeekHarnessSetup.AdvertiseNumericCapacity(loadedContext, MaxOfferableLocalNumeric("OverrideContext"));

    private int AdvertiseHarnessYamlMaxTokens(int loadedMaxTokens)
        => DeepSeekHarnessSetup.AdvertiseNumericCapacity(loadedMaxTokens, MaxOfferableLocalNumeric("OverrideMaxTokens"));

    private int MaxOfferableLocalNumeric(string settingsKey)
    {
        var max = 0;
        ForEachOfferableLocalProfile(profile =>
        {
            var value = ParseInt(profile[settingsKey]?.ToString() ?? string.Empty, 0);
            if (value > max)
            {
                max = value;
            }
        });
        return max;
    }

    private bool HasOfferableLocalProfile(Func<JsonObject, bool> matches)
    {
        var found = false;
        ForEachOfferableLocalProfile(profile =>
        {
            if (matches(profile))
            {
                found = true;
            }
        });
        return found;
    }

    private void ForEachOfferableLocalProfile(Action<JsonObject> visit)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return;
        }

        foreach (var entry in profiles)
        {
            if (entry.Value is not JsonObject profile)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(profile["EndpointWarning"]?.ToString()))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(profile["EndpointValidatedUtc"]?.ToString()))
            {
                continue;
            }

            visit(profile);
        }
    }

    private void RefreshHarnessYamlSurfaces()
    {
        OnPropertyChanged(nameof(HarnessServersSummaryText));
    }

    private void StopManagedHarnessWebForHygiene(string? harnessStatusText = null)
    {
        if (!_harnessWebHost.IsManagedRunning((int)HarnessWebPort))
        {
            return;
        }

        _harnessStartCts?.Cancel();
        var port = (int)HarnessWebPort;
        _harnessSyncedProfileFingerprint = string.Empty;
        if (!string.IsNullOrWhiteSpace(harnessStatusText))
        {
            HarnessWebUiStatusText = harnessStatusText;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _harnessWebHost.Stop(port);
            }
            catch
            {
            }

            SafeUiInvoke(() =>
            {
                RefreshHarnessProcessState();
                _ = RefreshHarnessWebUiStatusAsync();
            });
        });
    }

    private string BuildHarnessProfileFingerprint()
    {
        if (!IsHarnessModelRouteReady(out _))
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            _runningLocalModel,
            _runningLocalVariant,
            ResolveHarnessYamlContextWindow().ToString(CultureInfo.InvariantCulture),
            ResolveHarnessYamlMaxTokens().ToString(CultureInfo.InvariantCulture),
            ResolveHarnessYamlImagesOn() ? "1" : "0",
            ResolveHarnessYamlReasoningOn() ? "1" : "0");
    }

    private void TrySyncHarnessSettingsForMountedRoute()
        => TrySyncEndpointSettingsForMountedRoute();

    private void TrySyncClineContextForMountedRoute()
        => TrySyncEndpointSettingsForMountedRoute();

    private void TrySyncEndpointSettingsForMountedRoute()
    {
        if (!_runtimeService.IsManagedLocalAlive() && !_runtimeService.IsCloudRouteActive())
        {
            RefreshHarnessYamlSurfaces();
            return;
        }

        var snapshot = BuildEndpointSettingsSnapshot();
        if (snapshot.LoadedContextWindow <= 0 && snapshot.AdvertisedContextWindow <= 0)
        {
            RefreshHarnessYamlSurfaces();
            return;
        }

        var fingerprint = snapshot.Fingerprint();
        if (string.Equals(_endpointSyncedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            RefreshHarnessYamlSurfaces();
            return;
        }

        var harnessFingerprint = BuildHarnessProfileFingerprint();
        var harnessCapacityChanged = !string.IsNullOrEmpty(_harnessSyncedProfileFingerprint)
            && !string.Equals(_harnessSyncedProfileFingerprint, harnessFingerprint, StringComparison.Ordinal);

        var batch = EndpointSettingsSync.Apply(snapshot, _config);
        RefreshHarnessYamlSurfaces();

        var failed = batch.Results.FirstOrDefault(result => !result.Success && !string.IsNullOrWhiteSpace(result.Message));
        if (!string.IsNullOrWhiteSpace(failed.Id) && !failed.Success)
        {
            StatusMessage = failed.Message;
            return;
        }

        if (harnessCapacityChanged && _harnessWebHost.IsManagedRunning((int)HarnessWebPort))
        {
            const string freshSessionHint =
                "Harness settings were updated for the new profile. Close old Harness browser tabs, then click Harness web chat for a fresh session.";
            StopManagedHarnessWebForHygiene(freshSessionHint);
            StatusMessage = freshSessionHint;
        }

        _endpointSyncedFingerprint = fingerprint;
        _harnessSyncedProfileFingerprint = harnessFingerprint;
        _clineSyncedContextFingerprint = fingerprint;

        var changed = batch.Results.FirstOrDefault(result => result.Changed && !string.IsNullOrWhiteSpace(result.Message));
        if (changed.Changed
            && !StatusMessage.Contains("Harness settings were updated for the new profile", StringComparison.Ordinal))
        {
            StatusMessage = StatusMessage.Equals(LocalLaunchStatus.RouteReady, StringComparison.OrdinalIgnoreCase)
                ? LocalLaunchStatus.RouteReady + " " + changed.Message
                : changed.Message;
        }
    }

    private EndpointSettingsSnapshot BuildEndpointSettingsSnapshot()
        => new(
            (int)OrchestratorPort,
            ResolveClineContextWindow(),
            ResolveHarnessYamlContextWindow(),
            ResolveHarnessYamlMaxTokens(),
            ResolveClineImagesOn(),
            ResolveHarnessYamlReasoningOn(),
            ResolveHarnessYamlModelDisplayName(),
            FluxMuxGatewayModels.LocalModelId,
            SelectedEndpointApp.Equals("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
                || HarnessWebIsInPlay);

    private int ResolveClineContextWindow()
    {
        if (PreferApprovedCloudRoute()
            || (_runtimeService.IsCloudRouteActive()
                && !string.IsNullOrWhiteSpace(_mountedCloudModel)
                && !_runtimeService.IsManagedLocalAlive()))
        {
            var cloudSettings = GetLiveCloudProfileSettings(
                _mountedCloudVariant,
                _mountedCloudProvider,
                _mountedCloudModel);
            if (cloudSettings is not null)
            {
                return DeepSeekHarnessSetup.ResolveNumericSetting(
                    cloudSettings["CloudContextWindow"]?.ToString(),
                    128000);
            }
        }

        var settings = GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel);
        if (settings is not null)
        {
            return ParseInt(settings["OverrideContext"]?.ToString() ?? string.Empty, (int)LocalVariantContext);
        }

        return (int)LocalVariantContext;
    }

    private bool ResolveClineImagesOn()
        => ResolveHarnessYamlImagesOn();

    [ObservableProperty]
    public partial bool IdleTimeoutEnabled { get; set; }

    [ObservableProperty]
    public partial decimal IdleTimeoutMinutes { get; set; } = IdleTimeoutPolicy.DefaultMinutes;

    [ObservableProperty]
    public partial bool GenerationSpeedTelemetryEnabled { get; set; }

    [ObservableProperty]
    public partial string GenerationSpeedText { get; set; } = string.Empty;

    public bool ShowGenerationSpeedText => GenerationSpeedTelemetryEnabled;

    partial void OnGenerationSpeedTelemetryEnabledChanged(bool value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _runtimeService.SetGenerationSpeedTelemetryEnabled(value);
        SaveCurrentSelections();
        if (!value)
        {
            GenerationSpeedText = string.Empty;
        }
        else
        {
            RefreshGenerationSpeedTelemetry();
        }

        OnPropertyChanged(nameof(ShowGenerationSpeedText));
    }

    partial void OnIdleTimeoutEnabledChanged(bool value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        SaveCurrentSelections();
        _runtimeService.SetIdleTimeout(value, (int)IdleTimeoutMinutes);
        StatusMessage = value
            ? "Idle timeout on: after " + (int)IdleTimeoutMinutes + " minutes with no chat, AI-FluxMux stops the local model. A reply still being prepared does not count as idle."
            : "Idle timeout off: the local model stays loaded until you stop it.";
    }

    partial void OnIdleTimeoutMinutesChanged(decimal value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        var minutes = IdleTimeoutPolicy.ClampMinutes((int)value);
        if (minutes != (int)IdleTimeoutMinutes)
        {
            IdleTimeoutMinutes = minutes;
            return;
        }

        SaveCurrentSelections();
        _runtimeService.SetIdleTimeout(IdleTimeoutEnabled, minutes);
    }

    [ObservableProperty]
    public partial bool RequestRoutingEnabled { get; set; }

    [ObservableProperty]
    public partial string RequestRoutingModeText { get; set; } = string.Empty;

    public bool ShowRequestRoutingModeText =>
        RequestRoutingEnabled && !string.IsNullOrWhiteSpace(RequestRoutingModeText);

    [ObservableProperty]
    public partial string SelectedCloudRoutingCapacity { get; set; } = "Normal";

    public ObservableCollection<string> CloudRoutingCapacityOptions { get; } =
    [
        "Normal",
        "Low (prefer local)",
        "Hold (local only)"
    ];

    [ObservableProperty]
    public partial string RoutingPoolStatusText { get; set; } =
        "Routing needs at least two validated strengths (different local settings, or local plus cloud). None are counted yet.";

    partial void OnRequestRoutingEnabledChanged(bool value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _runtimeService.SetRequestRoutingTopology(RequestRoutingTopology.DualHot);
        UpdateRequestRoutingModeText();
        if (value && !InterventionPopupsEnabled)
        {
            InterventionPopupsEnabled = true;
        }

        SaveCurrentSelections();
        _ = ApplyRequestRoutingPreferenceAsync(value);
    }

    private void SyncEndpointAppToRuntime()
    {
        _runtimeService.SetEndpointApp(SelectedEndpointApp ?? string.Empty);
    }

    partial void OnSelectedCloudRoutingCapacityChanged(string value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        SaveCurrentSelections();
        _runtimeService.SetCloudRoutingCapacity(value);
        StatusMessage = value.StartsWith("Hold", StringComparison.OrdinalIgnoreCase)
            ? "Cloud Usage Mode Hold: stay on the loaded local while it is working. If local is down, cloud can still be used."
            : value.StartsWith("Low", StringComparison.OrdinalIgnoreCase)
                ? "Cloud Usage Mode Low: prefer the loaded local. Cloud can still be used if local is down or the prompt asks for a cloud model."
                : "Cloud Usage Mode Normal: large or clearly cloud-named requests may go to the ready cloud model.";
        UpdateRoutingPoolStatus();
    }

    [ObservableProperty]
    public partial bool LocalVisionEnabled { get; set; }

    [ObservableProperty]
    public partial string LocalVisionProjectorPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial decimal LocalVisionMaxImageEdge { get; set; } = 1344;

    [ObservableProperty]
    public partial string CloudVariantSettingsJson { get; set; } = "{}";

    [ObservableProperty]
    public partial string LocalVariantSettingsJson { get; set; } = "{}";

    [ObservableProperty]
    public partial string CloudVariantBaseTarget { get; set; } = "Base target: no cloud profile selected.";

    [ObservableProperty]
    public partial string CloudBaseFootprintText { get; set; } = "Local VRAM: none; cloud model weights are provider-hosted.";

    [ObservableProperty]
    public partial string CloudVariantFootprintText { get; set; } = "Variant local VRAM: none; request settings may affect provider-side token usage, latency, and cost.";

    [ObservableProperty]
    public partial string CloudVariantSelectorPrefix { get; set; } = "Cloud model";

    [ObservableProperty]
    public partial string CloudModelCatalogStatusText { get; set; } = "Provider model catalog is curated in AI-FluxMux.";

    [ObservableProperty]
    public partial string CloudVariantDraftName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCloudVariantEditable { get; set; }
    = true;

    [ObservableProperty]
    public partial bool CanValidateActiveCloudProfile { get; set; }

    [ObservableProperty]
    public partial bool CanDeleteActiveCloudProfile { get; set; }

    [ObservableProperty]
    public partial bool ShowCloudCreateProfileTip { get; set; }
    = false;

    [ObservableProperty]
    public partial CloudVariantTreeItemViewModel? SelectedCloudVariantItem { get; set; }

    [ObservableProperty]
    public partial CloudVariantTreeItemViewModel? SelectedLocalVariantItem { get; set; }

    [ObservableProperty]
    public partial string SelectedProfileTemplateRouteType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ProfileTemplateSourceViewModel? SelectedProfileTemplateSource { get; set; }

    [ObservableProperty]
    public partial bool IsCloudProfileTemplateSelected { get; set; }

    [ObservableProperty]
    public partial bool IsLocalProfileTemplateSelected { get; set; }

    [ObservableProperty]
    public partial bool IsCloudProfileEditorVisible { get; set; }

    [ObservableProperty]
    public partial bool IsLocalProfileEditorVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProfileTemplateSourceSelected { get; set; }

    [ObservableProperty]
    public partial string NewProfileVariantName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewCloudProfileProvider { get; set; } = "Gemini";

    [ObservableProperty]
    public partial string NewCloudProfileSelectedModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewCloudProfileCustomModelId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudFamilyModelIdDraft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudFamilyProviderDraft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudFamilyModelIdPaste { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewLocalProfileModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileTemplateGuidanceText { get; set; } = "Choose Cloud or Local to begin a model profile variant.";

    [ObservableProperty]
    public partial string SelectedCloudPriority { get; set; } = "Stability";

    [ObservableProperty]
    public partial string CloudIntentPresetInfoText { get; set; } =
        "AutoTune has not been run yet.";

    [ObservableProperty]
    public partial string CloudPriorityMinimalEffectNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalVariantBaseTarget { get; set; } = "Base target: no local profile selected.";

    [ObservableProperty]
    public partial string LocalBaseFootprintText { get; set; } = "Select a discovered local model to estimate its base footprint.";

    [ObservableProperty]
    public partial string LocalVariantFootprintText { get; set; } = "Select a local model and variant to estimate VRAM use.";

    [ObservableProperty]
    public partial string LocalVariantPerformanceEstimateText { get; set; } = "Performance estimate unavailable until a local model is selected.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalVariantVramWarning))]
    public partial string LocalVariantVramWarningText { get; set; } = string.Empty;

    public bool ShowLocalVariantVramWarning => !string.IsNullOrWhiteSpace(LocalVariantVramWarningText);

    [ObservableProperty]
    public partial string LocalVariantSelectorPrefix { get; set; } = "Local model";

    [ObservableProperty]
    public partial string LocalVariantDraftName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLocalVariantEditable { get; set; }
    = true;

    [ObservableProperty]
    public partial bool CanValidateActiveLocalProfile { get; set; }

    [ObservableProperty]
    public partial bool CanEditLocalVariantSettings { get; set; }
    = false;

    [ObservableProperty]
    public partial bool ShowLocalCreateProfileTip { get; set; }
    = false;

    [ObservableProperty]
    public partial decimal CloudVariantTemperature { get; set; } = 0.7m;

    [ObservableProperty]
    public partial decimal CloudVariantMaxTokens { get; set; } = 2048;

    [ObservableProperty]
    public partial string CloudVariantContextWindow { get; set; } = "Auto";

    [ObservableProperty]
    public partial string CloudVariantReasoningMode { get; set; } = "Balanced";

    [ObservableProperty]
    public partial string CloudVariantResponseFormat { get; set; } = "Text";

    [ObservableProperty]
    public partial string CloudVariantOpenAiReasoningEffort { get; set; } = "Auto";

    [ObservableProperty]
    public partial string CloudVariantCopilotReasoningEffort { get; set; } = "Auto";

    [ObservableProperty]
    public partial string CloudVariantGeminiThinkingMode { get; set; } = "Balanced";

    [ObservableProperty]
    public partial decimal CloudVariantGeminiThinkingBudget { get; set; }
    = 0;

    [ObservableProperty]
    public partial string CloudVariantAnthropicThinkingMode { get; set; } = "Standard";

    [ObservableProperty]
    public partial decimal LocalVariantContext { get; set; } = 32768;

    [ObservableProperty]
    public partial string LocalVariantOverrideThreads { get; set; } = DefaultLocalThreadCount();

    [ObservableProperty]
    public partial string LocalVariantThreadsBatch { get; set; } = DefaultLocalBatchThreadCount();

    [ObservableProperty]
    public partial decimal LocalVariantTemperature { get; set; } = 0.3m;

    [ObservableProperty]
    public partial string LocalVariantGpuOffloadMode { get; set; } = "GPU + CPU";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalMultiGpuManualSettings))]
    public partial string LocalVariantMultiGpuMode { get; set; } = "Auto";

    [ObservableProperty]
    public partial string LocalVariantSplitMode { get; set; } = "Auto";

    [ObservableProperty]
    public partial string LocalVariantMainGpu { get; set; } = "Auto";

    [ObservableProperty]
    public partial string LocalVariantTensorSplit { get; set; } = "Auto";

    public bool ShowLocalMultiGpuManualSettings =>
        LocalVariantMultiGpuMode.Equals("Manual", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial string LocalVariantGpuLayers { get; set; } = "Auto";

    [ObservableProperty]
    public partial string LocalVariantFlashAttention { get; set; } = "Enabled";

    [ObservableProperty]
    public partial string LocalVariantKvCacheTypeK { get; set; } = "q8_0";

    [ObservableProperty]
    public partial string LocalVariantKvCacheTypeV { get; set; } = "q8_0";

    [ObservableProperty]
    public partial string LocalVariantChatTemplate { get; set; } = "Auto";

    [ObservableProperty]
    public partial string LocalVariantMultiUserMode { get; set; } = "Disabled";

    [ObservableProperty]
    public partial string LocalVariantUnbanTokensMode { get; set; } = "Disabled";

    [ObservableProperty]
    public partial bool LocalVariantAutoCompressEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string LocalVariantMaxTokens { get; set; } = "16384";

    [ObservableProperty]
    public partial string LocalVariantBatchSize { get; set; } = "1024";

    [ObservableProperty]
    public partial string LocalVariantUbatchSize { get; set; } = "256";

    [ObservableProperty]
    public partial string LocalVariantSpecType { get; set; } = "ngram-simple";

    [ObservableProperty]
    public partial string LocalVariantCacheReuse { get; set; } = "256";

    [ObservableProperty]
    public partial string LocalVariantCacheRam { get; set; } = "8192";

    [ObservableProperty]
    public partial string LocalVariantFit { get; set; } = "Enabled";

    [ObservableProperty]
    public partial bool LocalVariantFitEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string LocalVariantSwaFull { get; set; } = "Enabled";

    [ObservableProperty]
    public partial string LocalVariantReasoning { get; set; } = "Off";

    [ObservableProperty]
    public partial string LocalVariantChatParser { get; set; } = "Jinja";

    [ObservableProperty]
    public partial string LocalVariantVisionEnabled { get; set; } = "Disabled";

    [ObservableProperty]
    public partial string LocalVariantVisionProjectorPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial decimal LocalVariantVisionMaxImageEdge { get; set; } = 1344;

    [ObservableProperty]
    public partial string LocalVariantVisionHintText { get; set; } =
        "Images are per profile. Text models stay off so they do not pay extra graphics memory.";

    [ObservableProperty]
    public partial string LocalVariantImagesHeadroomNote { get; set; } =
        "Images are off. AutoTune uses leftover graphics memory for Context. The wizard does not change Images.";

    [ObservableProperty]
    public partial LocalVisionProjectorChoice? SelectedLocalVariantVisionProjectorChoice { get; set; }

    [ObservableProperty]
    public partial string SelectedLocalPriority { get; set; } = "Stability";

    [ObservableProperty]
    public partial string LocalQuickAutoTuneInfoText { get; set; } =
        "AutoTune has not been run yet.";

    [ObservableProperty]
    public partial bool LocalExperimentalHardwareTuneEnabled { get; set; }

    partial void OnLocalExperimentalHardwareTuneEnabledChanged(bool value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _config.SetBool("LocalExperimentalHardwareTuneEnabled", value);
        _configService.Save(_config);
    }

    [ObservableProperty]
    public partial string LocalPriorityMinimalEffectNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudSummary { get; set; } = "Cloud family notes: this describes the selected provider/model family at a high level; individual family members can vary widely in context window, speed, cost, and modality support.";

    [ObservableProperty]
    public partial string LocalSummary { get; set; } = "Local family notes: this describes the selected local family/profile at a high level; individual family members can vary widely in quantization, context, speed, quality, and feature support.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalLaunchTelemetryText))]
    public partial string LocalLaunchTelemetryText { get; set; } = string.Empty;

    public bool ShowLocalLaunchTelemetryText => !string.IsNullOrWhiteSpace(LocalLaunchTelemetryText);

    [ObservableProperty]
    public partial string LocalLaunchTelemetryDetailText { get; set; } = "No launch history recorded yet for this model profile. Higher VRAM headroom, fewer background tasks, and a fresh launch after driver changes usually produce better startup times.";

    [ObservableProperty]
    public partial RouteIndicatorState CloudRouteIndicatorState { get; set; }

    partial void OnCloudRouteIndicatorStateChanged(RouteIndicatorState value)
    {
        OnPropertyChanged(nameof(CloudRouteIndicatorBrush));
        OnPropertyChanged(nameof(CloudRouteIndicatorOpacity));
        RefreshRouteSlotIndicators();
    }

    [ObservableProperty]
    public partial RouteIndicatorState LocalRouteIndicatorState { get; set; }

    partial void OnLocalRouteIndicatorStateChanged(RouteIndicatorState value)
    {
        OnPropertyChanged(nameof(LocalRouteIndicatorBrush));
        OnPropertyChanged(nameof(LocalRouteIndicatorOpacity));
        RefreshRouteSlotIndicators();
    }

    [ObservableProperty]
    public partial IBrush StatusMessageBrush { get; set; } = Brushes.Black;

    public IBrush CloudRouteIndicatorBrush => ResolveRouteIndicatorBrush(CloudRouteIndicatorState);

    public IBrush LocalRouteIndicatorBrush => ResolveRouteIndicatorBrush(LocalRouteIndicatorState);

    public double CloudRouteIndicatorOpacity => ResolveRouteIndicatorOpacity(CloudRouteIndicatorState);

    public double LocalRouteIndicatorOpacity => ResolveRouteIndicatorOpacity(LocalRouteIndicatorState);

    [ObservableProperty]
    public partial string ActiveRouteStatusLabel { get; set; } = "Idle";

    [ObservableProperty]
    public partial IBrush ActiveRouteStatusBrush { get; set; } = Brushes.DarkGray;

    [ObservableProperty]
    public partial double ActiveRouteStatusOpacity { get; set; } = 0.25;

    [ObservableProperty]
    public partial string ActiveRouteStatusTooltip { get; set; } = "No profile is mounted. Launch a Quick Select profile to light this indicator.";

    [ObservableProperty]
    public partial bool IsModelActive { get; set; }

    [ObservableProperty]
    public partial bool QuickSelectLoadingPulseOn { get; set; }

    [ObservableProperty]
    public partial string LastRouteExplanationText { get; set; } = string.Empty;

    public bool ShowLastRouteExplanationText => !string.IsNullOrWhiteSpace(LastRouteExplanationText);

    [ObservableProperty]
    public partial bool CloudRecommendVisible { get; set; }

    [ObservableProperty]
    public partial string CloudRecommendText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RecoverySwitchLocalVisible { get; set; }

    [ObservableProperty]
    public partial string RecoverySwitchLocalLabel { get; set; } = RouteRecoveryPolicy.FormatSwitchLabel("local");

    [ObservableProperty]
    public partial bool RecoverySwitchCloudVisible { get; set; }

    [ObservableProperty]
    public partial string RecoverySwitchCloudLabel { get; set; } = RouteRecoveryPolicy.FormatSwitchLabel("cloud");

    [ObservableProperty]
    public partial string RecoveryResumeLabel { get; set; } = RouteRecoveryPolicy.ResumeWaitingLabel;

    [ObservableProperty]
    public partial string RecoveryResumeTooltip { get; set; } = RouteRecoveryPolicy.FormatResumeTooltip("Cline", turnContinues: false);

    [ObservableProperty]
    public partial string RecoverySwitchLocalTooltip { get; set; } = RouteRecoveryPolicy.FormatSwitchTooltip("Cline", cloud: false, turnContinues: false);

    [ObservableProperty]
    public partial string RecoverySwitchCloudTooltip { get; set; } = RouteRecoveryPolicy.FormatSwitchTooltip("Cline", cloud: true, turnContinues: false);

    public string RecoveryWaitLongerLabel => RouteRecoveryPolicy.WaitLongerLabel;

    [ObservableProperty]
    public partial bool RecoveryWaitLongerVisible { get; set; }

    [ObservableProperty]
    public partial bool RecoveryHarnessRelaunchVisible { get; set; }

    public string RecoveryHarnessRelaunchHint => RouteRecoveryPolicy.HarnessRelaunchHint;

    [ObservableProperty]
    public partial bool InterventionPopupsEnabled { get; set; }

    partial void OnInterventionPopupsEnabledChanged(bool value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        _config.SetBool("InterventionPopupsEnabled", value);
        SaveCurrentSelections();
    }

    [RelayCommand]
    private Task ApproveCloudRecommend()
        => ApproveChosenCloudAsync(null, null);

    private async Task ApproveChosenCloudAsync(string? provider, string? model)
    {
        if (_approvingChosenCloud)
        {
            return;
        }

        _approvingChosenCloud = true;
        try
        {
            var variant = BaseVariantDisplayName;
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            {
                TryResolveLaunchableCloud(out provider, out model, out variant);
            }
            else
            {
                variant = ResolveCloudVariantForPick(provider, model);
            }

            var sameCloudAlreadyHot = RecoveryReplacementPolicy.CloudPickIsAlreadyHot(
                _runtimeService.IsCloudRouteActive(),
                _mountedCloudProvider,
                _mountedCloudModel,
                provider,
                model);
            _runtimeService.AnswerCloudRecommend(useCloud: true);
            _runtimeService.AnswerLocalReloadRecommend(loadSuggested: false);
            HideRecoveryPrompt();
            LocalReloadRecommendVisible = false;
            if (!sameCloudAlreadyHot && !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model))
            {
                StatusMessage = "Launching " + provider + " / " + model + ". This Client-app chat can continue after it is ready.";
                await LaunchCloudRouteAsync("Ask before cloud", provider, model, variant);
            }
            StatusMessage = _runtimeService.IsCloudRouteActive()
                ? "This turn continues on the cloud. This Client-app chat can continue."
                : "Switching to " + provider + " / " + model + ". This Client-app chat can continue after it is ready.";

            PostRecoveryFollowThrough(() =>
            {
                BindActiveQuickSelectSlotToPreferredRoute(preferCloud: true);
                RefreshRouteSlotIndicators();
                TrySyncEndpointSettingsForMountedRoute();
                RefreshCloudRecommendBanner();
            });
        }
        finally
        {
            _approvingChosenCloud = false;
        }
    }

    private string ResolveCloudVariantForPick(string provider, string model)
    {
        foreach (var slot in QuickSelectSlots)
        {
            if (slot.IsCloudRoute
                && slot.CloudProvider.Equals(provider, StringComparison.OrdinalIgnoreCase)
                && slot.CloudModel.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                return FirstNonEmpty(slot.CloudVariant, BaseVariantDisplayName);
            }
        }

        return BaseVariantDisplayName;
    }

    [RelayCommand]
    private void DeclineCloudRecommend()
    {
        ResumeCurrentModelRecovery();
    }

    [RelayCommand]
    private void ResumeCurrentModelRecovery()
    {
        var snapshot = _runtimeService.ReadCloudRecommend();
        if (RouteRecoveryPolicy.IsCloudFailureSource(snapshot?.Source))
        {
            _runtimeService.DismissCloudRecommend();
            StatusMessage = RouteRecoveryPolicy.FormatKeepCurrentStatus(SelectedEndpointApp);
        }
        else if (CloudReturnToLocalPolicy.IsReturnToLocalSource(snapshot?.Source))
        {
            _runtimeService.DeclineReturnToLocal();
            StatusMessage = "Staying on the ready cloud. This turn continues.";
        }
        else if (RouteRecoveryPolicy.IsHotHopSource(snapshot?.Source)
            && !CloudReturnToLocalPolicy.IsReturnToLocalSource(snapshot?.Source))
        {
            _runtimeService.AnswerCloudRecommend(useCloud: false);
            StatusMessage = "Staying on the loaded local. This turn continues.";
        }
        else
        {
            _runtimeService.AnswerCloudRecommend(useCloud: false);
            StatusMessage = RouteRecoveryPolicy.FormatKeepCurrentStatus(SelectedEndpointApp);
        }

        HideRecoveryPrompt();
        PostRecoveryFollowThrough(RefreshCloudRecommendBanner);
    }

    partial void OnSelectedRecoveryReplacementChanged(RecoveryReplacementOption? value)
    {
        if (_isRefreshingRecoveryReplacement)
        {
            return;
        }

        UpdateRecoveryReplacementAdvice();
        if (value is not null)
        {
            _ = ApplyRecoveryReplacement();
        }
    }

    [RelayCommand]
    private async Task ApplyRecoveryReplacement()
    {
        if (_applyingRecoveryReplacement)
        {
            return;
        }

        var pick = SelectedRecoveryReplacement;
        if (pick is null)
        {
            return;
        }

        _applyingRecoveryReplacement = true;
        try
        {
            if (pick.IsCloud)
            {
                await ApproveChosenCloudAsync(pick.CloudProvider, pick.CloudModel);
                return;
            }

            _runtimeService.AnswerCloudRecommend(useCloud: false);
            _runtimeService.AnswerLocalReloadRecommend(loadSuggested: false);
            HideRecoveryPrompt();
            LocalReloadRecommendVisible = false;
            RecoveryReplacementVisible = false;
            var label = pick.DisplayName;
            StatusMessage = RecoveryReplacementAdvice + " Loading " + label + ".";
            var mustReload = LocalSwapRequiresLlamaServerReload(pick.LocalModel, pick.LocalVariant);
            await LaunchLocalRouteAsync("Ask before local reload", pick.LocalModel, pick.LocalVariant);
            await OpenHarnessFromNewLocalSlotIfNeededAsync(pick.LocalModel, pick.LocalVariant, mustReload);
            RefreshLocalReloadRecommendBanner();
            RefreshCloudRecommendBanner();
        }
        finally
        {
            _applyingRecoveryReplacement = false;
        }
    }

    [RelayCommand]
    private void ContinueWaitingRecovery()
    {
        _runtimeService.AnswerHangWait();
        HideRecoveryPrompt();
        StatusMessage = "Continuing to wait on this turn. llama-server is still working. The same choices will appear again if it stays quiet.";
        PostRecoveryFollowThrough(RefreshCloudRecommendBanner);
    }

    [RelayCommand]
    private async Task SwitchRecoveryLocal()
    {
        var hotHop = RouteRecoveryPolicy.IsHotHopSource(_runtimeService.ReadCloudRecommend()?.Source)
            || _runtimeService.IsManagedLocalAlive();
        _runtimeService.AnswerCloudRecommend(useCloud: false);
        HideRecoveryPrompt();
        var reload = _runtimeService.ReadLocalReloadRecommend();
        if (!_runtimeService.IsManagedLocalAlive() && !string.IsNullOrWhiteSpace(reload?.Model))
        {
            StatusMessage = "Switching to " + (string.IsNullOrWhiteSpace(reload.DisplayName) ? reload.Model : reload.DisplayName) + ".";
            await ApproveLocalReloadRecommend();
            return;
        }

        var followHint = hotHop
            ? RecoveryReplacementPolicy.ContinueAdvice
            : ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice(SelectedEndpointApp);
        StatusMessage = "Next turns on Port use the loaded local. " + followHint;
        PostRecoveryFollowThrough(() =>
        {
            BindActiveQuickSelectSlotToPreferredRoute(preferCloud: false);
            RefreshRouteSlotIndicators();
            TrySyncEndpointSettingsForMountedRoute();
            RefreshCloudRecommendBanner();
            RefreshLocalReloadRecommendBanner();
        });
    }

    private void HideRecoveryPrompt()
    {
        HideCloudRecommendSurfaces();
        RecoveryReplacementVisible = false;
    }

    private void HideCloudRecommendSurfaces()
    {
        CloudRecommendVisible = false;
        RecoveryWaitLongerVisible = false;
        RecoverySwitchLocalVisible = false;
        RecoverySwitchCloudVisible = false;
        RecoveryHarnessRelaunchVisible = false;
        RecoveryResumeLabel = RouteRecoveryPolicy.ResumeWaitingLabel;
    }

    private void RefreshRecoveryReplacementChoices()
    {
        if (RecoveryChooserDropDownOpen)
        {
            return;
        }

        if (!CloudRecommendVisible && !LocalReloadRecommendVisible)
        {
            if (RecoveryReplacementVisible || RecoveryReplacementOptions.Count > 0)
            {
                RecoveryReplacementVisible = false;
                RecoveryReplacementOptions.Clear();
                SelectedRecoveryReplacement = null;
                RecoveryReplacementAdvice = string.Empty;
            }

            return;
        }

        var keepKey = SelectedRecoveryReplacement?.Key;
        var options = new List<RecoveryReplacementOption>();
        void add(RecoveryReplacementOption option)
        {
            if (options.Any(existing => existing.Key.Equals(option.Key, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            options.Add(option);
        }

        var reload = _runtimeService.ReadLocalReloadRecommend();
        if (!string.IsNullOrWhiteSpace(reload?.Model))
        {
            var variant = FirstNonEmpty(reload.Variant, BaseVariantDisplayName);
            add(new RecoveryReplacementOption(
                "local|" + reload.Model + "|" + variant,
                FirstNonEmpty(reload.DisplayName, LocalReloadRouting.FormatPackLabel(reload.Model, variant)),
                isCloud: false,
                localModel: reload.Model,
                localVariant: variant));
        }

        if (TryResolveLaunchableCloud(out var provider, out var model, out _))
        {
            add(new RecoveryReplacementOption(
                "cloud|" + provider + "|" + model,
                string.IsNullOrWhiteSpace(provider) ? model : provider + " / " + model,
                isCloud: true,
                cloudProvider: provider,
                cloudModel: model));
        }

        var runningModel = FirstNonEmpty(_runningLocalModel, string.Empty);
        var runningVariant = ProfileConfigVariantName(_runningLocalVariant);
        foreach (var slot in QuickSelectSlots)
        {
            if (!slot.HasSavedAssignment)
            {
                continue;
            }

            if (slot.IsCloudRoute)
            {
                if (string.IsNullOrWhiteSpace(slot.CloudModel))
                {
                    continue;
                }

                add(new RecoveryReplacementOption(
                    "cloud|" + slot.CloudProvider + "|" + slot.CloudModel,
                    string.IsNullOrWhiteSpace(slot.CloudProvider)
                        ? slot.CloudModel
                        : slot.CloudProvider + " / " + slot.CloudModel,
                    isCloud: true,
                    cloudProvider: slot.CloudProvider,
                    cloudModel: slot.CloudModel));
                continue;
            }

            if (string.IsNullOrWhiteSpace(slot.LocalModel))
            {
                continue;
            }

            var variant = FirstNonEmpty(slot.LocalVariant, BaseVariantDisplayName);
            if (slot.LocalModel.Equals(runningModel, StringComparison.OrdinalIgnoreCase)
                && ProfileConfigVariantName(variant).Equals(runningVariant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            add(new RecoveryReplacementOption(
                "local|" + slot.LocalModel + "|" + variant,
                LocalReloadRouting.FormatPackLabel(slot.LocalModel, variant),
                isCloud: false,
                localModel: slot.LocalModel,
                localVariant: variant));
        }

        if (RecoveryReplacementPolicy.SameChooserKeysAndNames(
            RecoveryReplacementOptions.Select(option => option.Key).ToList(),
            RecoveryReplacementOptions.Select(option => option.DisplayName).ToList(),
            options.Select(option => option.Key).ToList(),
            options.Select(option => option.DisplayName).ToList()))
        {
            if (SelectedRecoveryReplacement is null && RecoveryReplacementOptions.Count > 0)
            {
                _isRefreshingRecoveryReplacement = true;
                try
                {
                    SelectedRecoveryReplacement = RecoveryReplacementOptions[0];
                }
                finally
                {
                    _isRefreshingRecoveryReplacement = false;
                }
            }

            RecoveryReplacementVisible = RecoveryReplacementOptions.Count > 0;
            UpdateRecoveryReplacementAdvice();
            return;
        }

        _isRefreshingRecoveryReplacement = true;
        try
        {
            RecoveryReplacementOptions.Clear();
            foreach (var option in options)
            {
                RecoveryReplacementOptions.Add(option);
            }

            SelectedRecoveryReplacement = RecoveryReplacementOptions.FirstOrDefault(option =>
                    option.Key.Equals(keepKey, StringComparison.OrdinalIgnoreCase))
                ?? RecoveryReplacementOptions.FirstOrDefault();
            RecoveryReplacementVisible = RecoveryReplacementOptions.Count > 0;
        }
        finally
        {
            _isRefreshingRecoveryReplacement = false;
        }

        UpdateRecoveryReplacementAdvice();
    }

    private void UpdateRecoveryReplacementAdvice()
    {
        var pick = SelectedRecoveryReplacement;
        if (pick is null)
        {
            RecoveryReplacementAdvice = string.Empty;
            return;
        }

        var cloud = _runtimeService.ReadCloudRecommend();
        var waiting = CloudRecommendVisible
            && RouteRecoveryPolicy.IsHotHopSource(cloud?.Source);
        var turnStillOpen = RecoveryReplacementPolicy.TurnStillOpen(
            waiting,
            cloud?.FailOnTimeout == true,
            cloud?.Source);
        var mustReload = !pick.IsCloud
            && RecoveryReplacementPolicy.LlamaServerMustReload(
                LocalLaunchFingerprint.From(GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel)),
                LocalLaunchFingerprint.From(GetLiveLocalProfileSettings(pick.LocalVariant, pick.LocalModel)));
        var cloudAlreadyReady = pick.IsCloud
            && _runtimeService.IsCloudRouteActive()
            && pick.CloudProvider.Equals(_mountedCloudProvider, StringComparison.OrdinalIgnoreCase)
            && pick.CloudModel.Equals(_mountedCloudModel, StringComparison.OrdinalIgnoreCase);
        var follow = RecoveryReplacementPolicy.Decide(
            SelectedEndpointApp,
            new RecoveryReplacementChoice(
                pick.IsCloud,
                mustReload,
                turnStillOpen,
                cloudAlreadyReady,
                _runtimeService.IsManagedLocalAlive()));
        RecoveryReplacementAdvice = follow == RecoveryChatFollowThrough.ContinueSameTaskNextMessage
            ? ClineSwitchSurvivalPolicy.FormatReloadAdvice(SelectedEndpointApp)
            : RecoveryReplacementPolicy.FormatAdvice(follow);
    }

    private bool RecoveryNeedsHarnessRelaunch()
        => HarnessWebIsInPlay;

    private bool LocalSwapRequiresLlamaServerReload(string model, string variant)
    {
        if (_runtimeService.IsManagedLocalProfileLive(model, variant))
        {
            return false;
        }

        return RecoveryReplacementPolicy.LlamaServerMustReload(
            LocalLaunchFingerprint.From(GetLiveLocalProfileSettings(_runningLocalVariant, _runningLocalModel)),
            LocalLaunchFingerprint.From(GetLiveLocalProfileSettings(variant, model)));
    }

    private async Task OpenHarnessFromNewLocalSlotIfNeededAsync(string model, string variant, bool llamaServerMustReload)
    {
        await OpenHarnessFromApprovedSlotIfNeededAsync(
            preferCloud: false,
            launchedNewRuntime: llamaServerMustReload,
            targetReady: _runtimeService.IsManagedLocalProfileLive(model, variant),
            readyStatus: "Loaded the new local model profile. Opening a new Harness chat from that Quick Select slot. Do not continue the old Harness chat.");
    }

    private async Task<bool> OpenHarnessFromApprovedSlotIfNeededAsync(
        bool preferCloud,
        bool launchedNewRuntime,
        bool targetReady,
        string readyStatus)
    {
        if (!RecoveryReplacementPolicy.ShouldOpenHarnessAfterPromptedLaunch(
                RecoveryNeedsHarnessRelaunch(),
                launchedNewRuntime)
            || !targetReady)
        {
            return false;
        }

        BindActiveQuickSelectSlotToPreferredRoute(preferCloud);
        RefreshRouteSlotIndicators();
        StatusMessage = readyStatus;

        var port = (int)HarnessWebPort;
        if (_harnessWebHost.IsManagedRunning(port))
        {
            _harnessStartCts?.Cancel();
            _harnessWebHost.Stop(port);
            _harnessLaunchedByThisFluxMuxSession = false;
            SetHarnessWebUiReachable(false);
        }

        await StartHarnessWebAsync();
        return true;
    }

    private static void PostRecoveryFollowThrough(Action action)
    {
        Dispatcher.UIThread.Post(action, DispatcherPriority.ContextIdle);
    }

    private void QueueRecoveryHarnessSettingsSync()
    {
        if (!IsHarnessModelRouteReady(out _))
        {
            RefreshHarnessYamlSurfaces();
            return;
        }

        if (!HarnessWebIsInPlay
            && !SelectedEndpointApp.Equals("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(DeepSeekHarnessSetup.ResolveSettingsPath()))
        {
            RefreshHarnessYamlSurfaces();
            return;
        }

        var options = BuildHarnessSetupOptions();
        _ = Task.Run(() =>
        {
            try
            {
                DeepSeekHarnessSetup.MergeIntoSettingsFile(options);
            }
            catch
            {
            }

            SafeUiInvoke(RefreshHarnessYamlSurfaces);
        });
    }

    private void StartCloudRecommendMonitor()
    {
        _cloudRecommendCts?.Cancel();
        _cloudRecommendCts = new CancellationTokenSource();
        var token = _cloudRecommendCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(750, token);
                    SafeUiInvoke(() =>
                    {
                        RefreshCloudRecommendBanner();
                        RefreshLocalReloadRecommendBanner();
                        RefreshCloudBreakerTelemetryIfChanged();
                        if (GenerationSpeedTelemetryEnabled)
                        {
                            RefreshGenerationSpeedTelemetry();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void StartHostTelemetryMonitor()
    {
        _hostTelemetryCts?.Cancel();
        _hostTelemetryCts = new CancellationTokenSource();
        var token = _hostTelemetryCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (Interlocked.CompareExchange(ref _hostTelemetryBusy, 1, 0) == 0)
                    {
                        try
                        {
                            var snapshot = _hostTelemetrySampler.Sample();
                            SafeUiInvoke(() => ApplyHostTelemetry(snapshot));
                        }
                        catch
                        {
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _hostTelemetryBusy, 0);
                        }
                    }

                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void ApplyHostTelemetry(HostTelemetrySnapshot snapshot)
    {
        if (snapshot.GpuAvailable && snapshot.GpuVramTotalGiB > 0)
        {
            var vramPercent = Math.Clamp(100.0 * snapshot.GpuVramUsedGiB / snapshot.GpuVramTotalGiB, 0, 100);
            GpuVramGauge.Percent = vramPercent;
            GpuVramGauge.PercentText = vramPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
            GpuVramGauge.DetailText = snapshot.GpuVramUsedGiB.ToString("0.0", CultureInfo.InvariantCulture)
                + " / " + snapshot.GpuVramTotalGiB.ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
            GpuVramGauge.Tooltip = snapshot.GpuName + " VRAM "
                + GpuVramGauge.DetailText + " (" + GpuVramGauge.PercentText + "). Updated about once a second.";
        }
        else
        {
            GpuVramGauge.Percent = 0;
            GpuVramGauge.PercentText = "n/a";
            GpuVramGauge.DetailText = "NVIDIA readout unavailable";
            GpuVramGauge.Tooltip = "nvidia-smi is needed for live GPU VRAM. AMD/Intel cards are not shown here yet.";
        }

        if (snapshot.GpuAvailable)
        {
            GpuUtilGauge.Percent = snapshot.GpuUtilizationPercent;
            GpuUtilGauge.PercentText = snapshot.GpuUtilizationPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
            GpuUtilGauge.Badge = "CUDA";
            var parts = new List<string>();
            if (snapshot.GpuTemperatureC is { } temp)
            {
                parts.Add(temp.ToString("0", CultureInfo.InvariantCulture) + " \u00b0C");
            }

            if (snapshot.GpuPowerW is { } watts)
            {
                parts.Add(watts.ToString("0", CultureInfo.InvariantCulture) + " W");
            }

            GpuUtilGauge.DetailText = parts.Count > 0 ? string.Join(" \u00b7 ", parts) : snapshot.GpuName;
            GpuUtilGauge.Tooltip = snapshot.GpuName + " util " + GpuUtilGauge.PercentText
                + (parts.Count > 0 ? " \u00b7 " + GpuUtilGauge.DetailText : string.Empty)
                + ". Updated about once a second.";
        }
        else
        {
            GpuUtilGauge.Percent = 0;
            GpuUtilGauge.PercentText = "n/a";
            GpuUtilGauge.Badge = string.Empty;
            GpuUtilGauge.DetailText = "NVIDIA readout unavailable";
            GpuUtilGauge.Tooltip = "nvidia-smi is needed for live GPU utilization.";
        }

        if (snapshot.RamTotalGiB > 0)
        {
            RamGauge.Percent = snapshot.RamPercent;
            RamGauge.PercentText = snapshot.RamPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
            RamGauge.DetailText = snapshot.RamUsedGiB.ToString("0.0", CultureInfo.InvariantCulture)
                + " / " + snapshot.RamTotalGiB.ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
            RamGauge.Tooltip = "System RAM " + RamGauge.DetailText + " (" + RamGauge.PercentText + "). Updated about once a second.";
        }
        else
        {
            RamGauge.Percent = 0;
            RamGauge.PercentText = "n/a";
            RamGauge.DetailText = "RAM readout unavailable";
            RamGauge.Tooltip = "Windows did not return system memory totals.";
        }

        CpuGauge.Percent = snapshot.CpuPercent;
        CpuGauge.PercentText = snapshot.CpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        CpuGauge.Badge = snapshot.CpuCores.ToString(CultureInfo.InvariantCulture) + " CORES";
        CpuGauge.DetailText = snapshot.CpuGhz is { } ghz
            ? ghz.ToString("0.0", CultureInfo.InvariantCulture) + " GHz"
            : snapshot.CpuCores.ToString(CultureInfo.InvariantCulture) + " logical processors";
        CpuGauge.Tooltip = "CPU " + CpuGauge.PercentText + " across " + snapshot.CpuCores
            + " logical processors. Updated about once a second.";
        NoteActiveQuickSelectInFlightTransition();
        RefreshGenerationSpeedTelemetry();
        RefreshLastRouteExplanation();
        if (_runtimeService.InFlightChatCount == 0
            && IdleTimeoutEnabled
            && _activeQuickSelectSlot is { IsLocalRoute: true })
        {
            RefreshActiveQuickSelectTelemetry();
        }

        MaybeReconcileHarnessWebState();
    }

    private void RefreshGenerationSpeedTelemetry()
    {
        if (!GenerationSpeedTelemetryEnabled)
        {
            return;
        }

        var snapshot = _runtimeService.ReadGenerationSpeedSnapshot();
        var next = snapshot?.DisplayText
            ?? (_runtimeService.InFlightChatCount > 0
                ? "waiting for reply…"
                : "waiting for first reply");
        if (!string.Equals(GenerationSpeedText, next, StringComparison.Ordinal))
        {
            GenerationSpeedText = next;
        }
        else
        {
            OnPropertyChanged(nameof(GenerationSpeedText));
        }

        OnPropertyChanged(nameof(ShowGenerationSpeedText));
    }

    private void RefreshLastRouteExplanation()
    {
        var snapshot = _runtimeService.ReadLastRouteExplanation();
        var next = snapshot?.Line ?? string.Empty;
        if (!string.Equals(LastRouteExplanationText, next, StringComparison.Ordinal))
        {
            LastRouteExplanationText = next;
        }
        else
        {
            OnPropertyChanged(nameof(LastRouteExplanationText));
        }

        OnPropertyChanged(nameof(ShowLastRouteExplanationText));
    }

    private void NoteActiveQuickSelectInFlightTransition()
    {
        if (_activeQuickSelectSlot is null)
        {
            _activeQuickSelectInFlightKnown = false;
            return;
        }

        var inFlight = _runtimeService.InFlightChatCount > 0;
        if (inFlight == _activeQuickSelectInFlightKnown)
        {
            return;
        }

        _activeQuickSelectInFlightKnown = inFlight;
        RefreshActiveQuickSelectTelemetry();
        RefreshGenerationSpeedTelemetry();
        RefreshLastRouteExplanation();
        if (!inFlight && _activeQuickSelectSlot is not null)
        {
            RefreshQuickSelectSlotFacts(_activeQuickSelectSlot);
        }
    }

    private void ResetActiveQuickSelectRuntimePresentation()
    {
        _activeQuickSelectInFlightKnown = false;
        _lastCloudBreakerTelemetrySignature = string.Empty;
    }
    private void RefreshActiveQuickSelectTelemetry()
    {
        if (_activeQuickSelectSlot is null)
        {
            return;
        }

        var state = ResolveActiveRouteIndicatorState();
        if (state is RouteIndicatorState.Off)
        {
            return;
        }

        RefreshQuickSelectSlotActiveStates(state);
    }

    private void RefreshCloudBreakerTelemetryIfChanged()
    {
        if (_activeQuickSelectSlot is not { } slot)
        {
            _lastCloudBreakerTelemetrySignature = string.Empty;
            return;
        }

        var signature = BuildCloudBreakerTelemetrySignature(slot);
        if (signature.Equals(_lastCloudBreakerTelemetrySignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastCloudBreakerTelemetrySignature = signature;
        RefreshActiveQuickSelectTelemetry();
    }

    private string BuildCloudBreakerTelemetrySignature(RouteSlotViewModel slot)
    {
        var block = TryGetCloudBreakerForSlot(slot);
        return block is null
            ? string.Empty
            : block.Reason + "|" + block.OpenUntilUtc.ToString("o", CultureInfo.InvariantCulture);
    }

    private CloudBreakerBlock? TryGetCloudBreakerForSlot(RouteSlotViewModel slot)
    {
        if (slot.IsCloudRoute)
        {
            return _runtimeService.ReadCloudBreakerBlock(slot.CloudProvider, slot.CloudModel);
        }

        if (!RequestRoutingEnabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_mountedCloudProvider)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel))
        {
            var mountedBlock = _runtimeService.ReadCloudBreakerBlock(_mountedCloudProvider, _mountedCloudModel);
            if (mountedBlock is not null)
            {
                return mountedBlock;
            }
        }

        foreach (var cloudSlot in QuickSelectSlots)
        {
            if (!cloudSlot.IsCloudRoute || string.IsNullOrWhiteSpace(cloudSlot.CloudModel))
            {
                continue;
            }

            var block = _runtimeService.ReadCloudBreakerBlock(cloudSlot.CloudProvider, cloudSlot.CloudModel);
            if (block is not null)
            {
                return block;
            }
        }

        return null;
    }

    private static string FormatCloudBreakerActionable(CloudBreakerBlock block)
    {
        var untilLocal = block.OpenUntilUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        return block.Reason switch
        {
            "quota_exhausted" =>
                $"Cloud quota paused until {untilLocal} — stay on local or Launch again after quota returns",
            "rate_limited" =>
                $"Cloud rate limit pause — retry after {block.RetryAfterSeconds}s or stay on local",
            _ =>
                $"Cloud route paused until {untilLocal} — stay on local or wait"
        };
    }

    private void RefreshCloudRecommendBanner()
    {
        var snapshot = _runtimeService.ReadCloudRecommend();
        if (snapshot is null)
        {
            HideCloudRecommendSurfaces();
            RefreshRecoveryReplacementChoices();
            return;
        }

        var cloudReady = _runtimeService.IsCloudRouteActive()
            && !string.IsNullOrWhiteSpace(_mountedCloudProvider)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel);
        if (_approvingChosenCloud || _applyingRecoveryReplacement)
        {
            return;
        }

        if (RouteRecoveryPolicy.IsCapacitySource(snapshot.Source)
            && !RouteRecoveryPolicy.IsLocalCannotSource(snapshot.Source)
            && !RouteRecoveryPolicy.ShouldShowCapacityPrompt(cloudReady)
            && !HasLaunchableCloud())
        {
            _runtimeService.DismissCloudRecommend();
            HideCloudRecommendSurfaces();
            RefreshRecoveryReplacementChoices();
            return;
        }

        var reload = _runtimeService.ReadLocalReloadRecommend();
        var localLabel = ResolveRecoveryLocalLabel(reload);
        var cloudLabel = ResolveRecoveryCloudLabel();
        var cloudFail = RouteRecoveryPolicy.IsCloudFailureSource(snapshot.Source);
        var localKnown = _runtimeService.IsManagedLocalAlive()
            || !string.IsNullOrWhiteSpace(_runningLocalModel)
            || !string.IsNullOrWhiteSpace(reload?.Model);
        var nextWaitLonger = RouteRecoveryPolicy.IsLocalHangSource(snapshot.Source);
        var returnToLocal = CloudReturnToLocalPolicy.IsReturnToLocalSource(snapshot.Source);
        var liveConsent = RouteRecoveryPolicy.IsHotHopSource(snapshot.Source);
        var pictureMiss = (snapshot.Reason ?? string.Empty)
            .StartsWith(FluxMuxGatewayRouting.LocalCannotCloudConsentReason, StringComparison.Ordinal);
        var readyCloudTakesImages = CloudProfileAllowsImages(_mountedCloudVariant, _mountedCloudProvider, _mountedCloudModel);
        var nextSwitchCloud = !cloudFail
            && !returnToLocal
            && (cloudReady || HasLaunchableCloud())
            && (!pictureMiss || readyCloudTakesImages || HasLaunchableVisionCloud());
        var nextSwitchLocal = returnToLocal || cloudFail
            ? localKnown
            : !string.IsNullOrWhiteSpace(reload?.Model);
        var harnessRelaunch = RecoveryNeedsHarnessRelaunch();
        var switchNeedsRelaunch = harnessRelaunch && !liveConsent && !returnToLocal && !nextWaitLonger;
        var nextHarnessHint = RouteRecoveryPolicy.ShouldShowHarnessRelaunchHint(
            harnessRelaunch,
            nextWaitLonger,
            snapshot.Source);
        var nextResumeLabel = RouteRecoveryPolicy.FormatResumeLabel(snapshot.Source);
        var nextSwitchLocalLabel = RouteRecoveryPolicy.FormatSwitchLabel(localLabel, switchNeedsRelaunch);
        var nextSwitchCloudLabel = RouteRecoveryPolicy.FormatSwitchLabel(cloudLabel, switchNeedsRelaunch);
        var nextResumeTooltip = RouteRecoveryPolicy.FormatResumeTooltip(SelectedEndpointApp, liveConsent);
        var nextSwitchLocalTooltip = RouteRecoveryPolicy.FormatSwitchTooltip(SelectedEndpointApp, cloud: false, liveConsent);
        var nextSwitchCloudTooltip = RouteRecoveryPolicy.FormatSwitchTooltip(SelectedEndpointApp, cloud: true, liveConsent);
        var nextText = snapshot.Reason ?? string.Empty;

        if (CloudRecommendVisible
            && RecoveryWaitLongerVisible == nextWaitLonger
            && RecoverySwitchLocalVisible == nextSwitchLocal
            && RecoverySwitchCloudVisible == nextSwitchCloud
            && RecoveryHarnessRelaunchVisible == nextHarnessHint
            && string.Equals(RecoveryResumeLabel, nextResumeLabel, StringComparison.Ordinal)
            && string.Equals(RecoveryResumeTooltip, nextResumeTooltip, StringComparison.Ordinal)
            && string.Equals(RecoverySwitchLocalTooltip, nextSwitchLocalTooltip, StringComparison.Ordinal)
            && string.Equals(RecoverySwitchCloudTooltip, nextSwitchCloudTooltip, StringComparison.Ordinal)
            && string.Equals(CloudRecommendText, nextText, StringComparison.Ordinal)
            && string.Equals(RecoverySwitchLocalLabel, nextSwitchLocalLabel, StringComparison.Ordinal)
            && string.Equals(RecoverySwitchCloudLabel, nextSwitchCloudLabel, StringComparison.Ordinal))
        {
            RefreshRecoveryReplacementChoices();
            return;
        }

        CloudRecommendVisible = true;
        RecoveryWaitLongerVisible = nextWaitLonger;
        RecoverySwitchLocalVisible = nextSwitchLocal;
        RecoverySwitchCloudVisible = nextSwitchCloud;
        RecoveryHarnessRelaunchVisible = nextHarnessHint;
        RecoveryResumeLabel = nextResumeLabel;
        RecoveryResumeTooltip = nextResumeTooltip;
        RecoverySwitchLocalLabel = nextSwitchLocalLabel;
        RecoverySwitchCloudLabel = nextSwitchCloudLabel;
        RecoverySwitchLocalTooltip = nextSwitchLocalTooltip;
        RecoverySwitchCloudTooltip = nextSwitchCloudTooltip;
        CloudRecommendText = nextText;
        RefreshRecoveryReplacementChoices();
    }

    private string ResolveRecoveryLocalLabel(LocalReloadRecommendSnapshot? reload)
    {
        if (!string.IsNullOrWhiteSpace(reload?.DisplayName))
        {
            return reload.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(_runningLocalModel))
        {
            return LocalReloadRouting.FormatPackLabel(_runningLocalModel, _runningLocalVariant);
        }

        return "local";
    }

    private string ResolveRecoveryCloudLabel()
    {
        if (TryResolveLaunchableCloud(out var provider, out var model, out _))
        {
            return string.IsNullOrWhiteSpace(provider) ? model : provider + " / " + model;
        }

        return "cloud";
    }

    private bool HasLaunchableCloud()
        => TryResolveLaunchableCloud(out _, out _, out _);

    private bool HasLaunchableVisionCloud()
    {
        foreach (var slot in QuickSelectSlots)
        {
            if (slot.IsCloudRoute
                && !string.IsNullOrWhiteSpace(slot.CloudModel)
                && CloudProfileAllowsImages(slot.CloudVariant, slot.CloudProvider, slot.CloudModel))
            {
                return true;
            }
        }

        return false;
    }

    private bool CloudProfileAllowsImages(string? variant, string? provider, string? model)
    {
        var settings = GetLiveCloudProfileSettings(
            FirstNonEmpty(variant, BaseVariantDisplayName),
            provider,
            model);
        return CloudCatalogVision.FlagAllowsImages(settings?["CloudVisionEnabled"]?.ToString());
    }

    private bool TryResolveLaunchableCloud(out string provider, out string model, out string variant)
    {
        if (!string.IsNullOrWhiteSpace(_mountedCloudProvider) && !string.IsNullOrWhiteSpace(_mountedCloudModel))
        {
            provider = _mountedCloudProvider;
            model = _mountedCloudModel;
            variant = FirstNonEmpty(_mountedCloudVariant, BaseVariantDisplayName);
            return true;
        }

        var last = _runtimeService.GetLastSuccessfulCloudIdentity();
        if (!string.IsNullOrWhiteSpace(last.Model))
        {
            provider = last.Provider;
            model = last.Model;
            variant = BaseVariantDisplayName;
            foreach (var slot in QuickSelectSlots)
            {
                if (slot.IsCloudRoute
                    && slot.CloudModel.Equals(model, StringComparison.OrdinalIgnoreCase)
                    && slot.CloudProvider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                {
                    variant = FirstNonEmpty(slot.CloudVariant, variant);
                    break;
                }
            }

            return true;
        }

        foreach (var slot in QuickSelectSlots)
        {
            if (!slot.IsCloudRoute || string.IsNullOrWhiteSpace(slot.CloudModel))
            {
                continue;
            }

            provider = slot.CloudProvider;
            model = slot.CloudModel;
            variant = FirstNonEmpty(slot.CloudVariant, BaseVariantDisplayName);
            return true;
        }

        provider = string.Empty;
        model = string.Empty;
        variant = BaseVariantDisplayName;
        return false;
    }

    [ObservableProperty]
    public partial bool LocalReloadRecommendVisible { get; set; }

    [ObservableProperty]
    public partial string LocalReloadRecommendText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalReloadRecommendTooltip { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LocalReloadLoadVisible { get; set; }

    [ObservableProperty]
    public partial bool LocalReloadCompactVisible { get; set; }

    public ObservableCollection<RecoveryReplacementOption> RecoveryReplacementOptions { get; } = [];

    [ObservableProperty]
    public partial RecoveryReplacementOption? SelectedRecoveryReplacement { get; set; }

    [ObservableProperty]
    public partial bool RecoveryReplacementVisible { get; set; }

    [ObservableProperty]
    public partial string RecoveryReplacementAdvice { get; set; } = string.Empty;

    private bool _isRefreshingRecoveryReplacement;
    private bool _applyingRecoveryReplacement;
    private bool _approvingChosenCloud;
    public bool RecoveryChooserDropDownOpen { get; set; }

    [RelayCommand]
    private async Task ApproveLocalReloadRecommend()
    {
        var snapshot = _runtimeService.ReadLocalReloadRecommend();
        _runtimeService.AnswerLocalReloadRecommend(loadSuggested: true);
        LocalReloadRecommendVisible = false;
        var model = snapshot?.Model ?? string.Empty;
        var variant = snapshot?.Variant ?? BaseVariantDisplayName;
        var label = string.IsNullOrWhiteSpace(snapshot?.DisplayName) ? model : snapshot.DisplayName;
        if (string.IsNullOrWhiteSpace(model))
        {
            StatusMessage = "Local reload approved, but the suggested profile could not be resolved.";
            RefreshLocalReloadRecommendBanner();
            return;
        }

        StatusMessage = RecoveryNeedsHarnessRelaunch()
            ? "Loading " + label + ". After it starts, AI-FluxMux opens Harness chat from that Quick Select slot. Do not try to continue this chat."
            : "Loading " + label + ". This unloads the current local model and you wait while the new one starts.";
        var mustReload = LocalSwapRequiresLlamaServerReload(model, variant);
        await LaunchLocalRouteAsync("Ask before local reload", model, variant);
        await OpenHarnessFromNewLocalSlotIfNeededAsync(model, variant, mustReload);
        RefreshLocalReloadRecommendBanner();
    }

    [RelayCommand]
    private async Task CompactLocalReloadRecommend()
    {
        var snapshot = _runtimeService.ReadLocalReloadRecommend();
        var loadSuggested = snapshot is not null
            && snapshot.DestinationTight
            && !string.IsNullOrWhiteSpace(snapshot.Model);
        _runtimeService.AnswerLocalReloadRecommend(loadSuggested, enableCompact: true);
        LocalReloadRecommendVisible = false;
        if (!loadSuggested)
        {
            StatusMessage = "AI-FluxMux will shorten older turns it forwards to the local model. The Client app still has the full chat. Retry this turn.";
            RefreshLocalReloadRecommendBanner();
            return;
        }

        var model = snapshot!.Model;
        var variant = string.IsNullOrWhiteSpace(snapshot.Variant) ? BaseVariantDisplayName : snapshot.Variant;
        var label = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? model : snapshot.DisplayName;
        StatusMessage = RecoveryNeedsHarnessRelaunch()
            ? "Shortening older turns, then loading " + label + ". After it starts, AI-FluxMux opens Harness chat from that Quick Select slot. Do not try to continue this chat."
            : "Shortening older turns, then loading " + label + ". Retry this turn after the new local starts.";
        var mustReload = LocalSwapRequiresLlamaServerReload(model, variant);
        await LaunchLocalRouteAsync("Ask before local reload", model, variant);
        await OpenHarnessFromNewLocalSlotIfNeededAsync(model, variant, mustReload);
        RefreshLocalReloadRecommendBanner();
    }

    [RelayCommand]
    private void DeclineLocalReloadRecommend()
    {
        _runtimeService.AnswerLocalReloadRecommend(loadSuggested: false);
        LocalReloadRecommendVisible = false;
        StatusMessage = "Keeping the current local profile. Similar reload suggestions are suppressed for 15 minutes.";
        RefreshLocalReloadRecommendBanner();
    }

    private void RefreshLocalReloadRecommendBanner()
    {
        var snapshot = _runtimeService.ReadLocalReloadRecommend();
        if (snapshot is null || snapshot.CloudFailover)
        {
            LocalReloadRecommendVisible = false;
            LocalReloadLoadVisible = false;
            LocalReloadCompactVisible = false;
            RefreshRecoveryReplacementChoices();
            return;
        }

        var localAlive = _runtimeService.IsManagedLocalAlive();
        var cloudFailoverLaunch = !localAlive && !string.IsNullOrWhiteSpace(snapshot.Model);
        if (!localAlive && !cloudFailoverLaunch)
        {
            LocalReloadRecommendVisible = false;
            LocalReloadLoadVisible = false;
            LocalReloadCompactVisible = false;
            RefreshRecoveryReplacementChoices();
            return;
        }

        var nextText = snapshot.Reason ?? string.Empty;
        var nextLoad = !string.IsNullOrWhiteSpace(snapshot.Model);
        var nextCompact = localAlive && snapshot.CompactRecommended && !snapshot.ForwardCompactActive;
        var nextTip = cloudFailoverLaunch
            ? "Cloud quota or rate limits paused the cloud route. **Load suggested model profile** launches that local profile from Quick Select."
                + (RecoveryNeedsHarnessRelaunch() ? " " + RouteRecoveryPolicy.HarnessRelaunchFromNewSlotAdvice : string.Empty)
            : nextLoad
            ? "The blue note compares the model profile that is running now with a suggested model profile (validated, even if it is not in Quick Select). **Load suggested model profile** unloads the current local model and starts the suggested one."
                + (RecoveryNeedsHarnessRelaunch() ? " " + RouteRecoveryPolicy.HarnessRelaunchFromNewSlotAdvice : string.Empty)
            : "This chat is filling the loaded Context. **Compact** shortens older turns AI-FluxMux forwards when the window is actually tight. The Client app still has the full chat.";
        if (LocalReloadRecommendVisible
            && LocalReloadLoadVisible == nextLoad
            && LocalReloadCompactVisible == nextCompact
            && string.Equals(LocalReloadRecommendText, nextText, StringComparison.Ordinal)
            && string.Equals(LocalReloadRecommendTooltip, nextTip, StringComparison.Ordinal))
        {
            RefreshRecoveryReplacementChoices();
            return;
        }

        LocalReloadRecommendText = nextText;
        LocalReloadLoadVisible = nextLoad;
        LocalReloadCompactVisible = nextCompact;
        LocalReloadRecommendTooltip = nextTip;
        LocalReloadRecommendVisible = true;
        RefreshRecoveryReplacementChoices();
    }

    [ObservableProperty]
    public partial string SwitchBenchmarkFocusText { get; set; } = "Preserve relevant task context while switching between two model profiles. Retain task anchors, filter background chatter, and return to the first profile without meaningful drift or disconnection.";

    [ObservableProperty]
    public partial SwitchBenchmarkProfileOption? SelectedSwitchBenchmarkProfileA { get; set; }

    [ObservableProperty]
    public partial SwitchBenchmarkProfileOption? SelectedSwitchBenchmarkProfileB { get; set; }

    [ObservableProperty]
    public partial string SelectedSwitchBenchmarkOrder { get; set; } = "Profile A -> Profile B -> Profile A";

    [ObservableProperty]
    public partial string SwitchBenchmarkResultText { get; set; } = "No switch benchmark has run yet.";

    [ObservableProperty]
    public partial string SwitchBenchmarkSummaryText { get; set; } = "Switching benchmark between selected model profiles not yet run.";

    [ObservableProperty]
    public partial string SwitchBenchmarkRecommendationText { get; set; } = "Switching benchmark between selected model profiles not yet run.";

    [ObservableProperty]
    public partial string SwitchBenchmarkAdvancedDetailsText { get; set; } = "Advanced details will appear here after a benchmark run.";

    [ObservableProperty]
    public partial string ConnectionHealthText { get; set; } = "No cloud/local connection checks have run yet.";

    [ObservableProperty]
    public partial string HealthReportText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HealthReportVisible { get; set; }

    [ObservableProperty]
    public partial string DiagnosticTerminalText { get; set; } = "Diagnostics will appear here as llama-server, AI-FluxMux, and connection actions run.";

    private bool _suppressConnectionHealthLog;

    partial void OnConnectionHealthTextChanged(string value)
    {
        if (!_benchmarkStatusActive && !_suppressConnectionHealthLog)
        {
            AppendDiagnosticEntry("DETAIL", value);
        }
    }

    public LiveUtilizationGaugeViewModel GpuVramGauge { get; } = new() { Title = "GPU VRAM" };

    public LiveUtilizationGaugeViewModel GpuUtilGauge { get; } = new() { Title = "GPU util" };

    public LiveUtilizationGaugeViewModel RamGauge { get; } = new() { Title = "CPU RAM" };

    public LiveUtilizationGaugeViewModel CpuGauge { get; } = new() { Title = "CPU util" };

    [ObservableProperty]
    public partial string VramStatusText { get; set; } = "Open this panel to load graphics-card memory and display adapters.";

    [ObservableProperty]
    public partial string VramProcessListHeaderText { get; set; } = "Programs using the graphics card (high to low):";

    [ObservableProperty]
    public partial ObservableCollection<VramProcessListItem> VramProcessRows { get; set; } = new();

    [ObservableProperty]
    public partial bool HasVramProcessList { get; set; }

    [ObservableProperty]
    public partial bool ShowGpuAdapterStatusColumn { get; set; }

    [ObservableProperty]
    public partial bool HasGpuAdapterRows { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<DisplayAdapterListItem> GpuAdapterRows { get; set; } = new();

    [RelayCommand]
    private async Task RefreshVramStatusAsync()
    {
        StatusMessage = "Checking graphics-card memory...";
        var result = await _runtimeService.GetVramStatusAsync();
        ApplyVramStatusResult(result);
        StatusMessage = result.Status;
    }

    [RelayCommand]
    private async Task LoadGpuVramFlyoutAsync()
    {
        StatusMessage = "Checking graphics cards and memory...";
        var adapters = await _runtimeService.GetGpuAdaptersAsync();
        ApplyGpuAdapterResult(adapters);
        var result = await _runtimeService.GetVramStatusAsync();
        ApplyVramStatusResult(result);
        StatusMessage = result.IsSuccess ? "Graphics cards and memory updated" : result.Status;
    }

    private void ApplyVramStatusResult(RuntimeActionResult result)
    {
        VramStatusText = result.Details;
        VramProcessListHeaderText = result.GpuCardCount > 1
            ? "Programs using graphics cards (combined across cards, high to low):"
            : "Programs using the graphics card (high to low):";
        VramProcessRows.Clear();
        foreach (var row in result.VramProcessItems)
        {
            VramProcessRows.Add(row);
        }

        HasVramProcessList = VramProcessRows.Count > 0;
    }

    private void ApplyGpuAdapterResult(RuntimeActionResult result)
    {
        GpuAdapterRows.Clear();
        foreach (var row in result.DisplayAdapterItems)
        {
            GpuAdapterRows.Add(row);
        }

        HasGpuAdapterRows = GpuAdapterRows.Count > 0;
    }

    [RelayCommand]
    private async Task RefreshGpuAdaptersAsync()
    {
        StatusMessage = "Checking graphics cards...";
        var result = await _runtimeService.GetGpuAdaptersAsync();
        ApplyGpuAdapterResult(result);
        StatusMessage = result.Status;
    }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    partial void OnStatusMessageChanged(string value)
    {
        if (!_benchmarkStatusActive && !_suppressStatusDiagnostic)
        {
            AppendDiagnosticEntry("STATUS", value);
        }
    }

    private void AppendDiagnosticEntry(string category, string message, string? entity = null)
    {
        var normalized = ControlLabelMarkup.Strip(message).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var sourceName = FirstNonEmpty(entity, InferDiagnosticEntity(category, normalized));
        var payload = category + "|" + sourceName + "|" + normalized;
        if (payload.Equals(_lastDiagnosticPayload, StringComparison.Ordinal))
        {
            return;
        }

        _lastDiagnosticPayload = payload;
        var indented = normalized
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", Environment.NewLine + "    ", StringComparison.Ordinal);
        var source = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : " [" + sourceName + "]";
        _diagnosticEntries.Add($"[{DateTime.Now:HH:mm:ss}] {category,-6}{source} {indented}");
        if (_diagnosticEntries.Count > 300)
        {
            _diagnosticEntries.RemoveRange(0, _diagnosticEntries.Count - 300);
        }

        DiagnosticTerminalText = string.Join(Environment.NewLine, _diagnosticEntries);
    }

    private static string InferDiagnosticEntity(string category, string message)
    {
        if (message.Contains("llama-server", StringComparison.OrdinalIgnoreCase)
            || message.Contains(".gguf", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Last llama-server output", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("Loading ", StringComparison.OrdinalIgnoreCase))
        {
            return "llama-server";
        }

        if (category.Equals("TUNE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AutoTune", StringComparison.OrdinalIgnoreCase))
        {
            return "AutoTune";
        }

        if (message.Contains("Client app", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("AI-FluxMux", StringComparison.OrdinalIgnoreCase))
        {
            return "Client app";
        }

        if (message.Contains("AI-FluxMux", StringComparison.OrdinalIgnoreCase)
            || category.Equals("HEALTH", StringComparison.OrdinalIgnoreCase))
        {
            return "AI-FluxMux";
        }

        return string.Empty;
    }

    private bool _healthReportDismissed;

    private void PublishHealthReport(string statusLine, string report, bool keepVisible = true, string? entity = null)
    {
        var normalized = (report ?? string.Empty).Trim();
        _lastHealthDetail = normalized;
        _suppressConnectionHealthLog = true;
        try
        {
            ConnectionHealthText = string.IsNullOrWhiteSpace(normalized) ? statusLine : normalized;
        }
        finally
        {
            _suppressConnectionHealthLog = false;
        }

        StatusMessage = statusLine;
        if (_healthReportDismissed && keepVisible)
        {
            return;
        }

        if (keepVisible && !string.IsNullOrWhiteSpace(normalized))
        {
            HealthReportText = normalized;
            HealthReportVisible = true;
            AppendDiagnosticEntry("HEALTH", normalized, entity);
            return;
        }

        if (!keepVisible)
        {
            _healthReportDismissed = false;
            ClearHealthReport();
        }

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AppendDiagnosticEntry("HEALTH", normalized, entity);
        }
    }

    [RelayCommand]
    private void DismissHealthReport()
    {
        _healthReportDismissed = true;
        ClearHealthReport();
    }

    private void ClearHealthReport()
    {
        HealthReportVisible = false;
        HealthReportText = string.Empty;
    }

    private async Task PublishHealthReportWithHarnessWebAsync(
        string headline,
        string report,
        bool modelsOk,
        string entity)
    {
        if (modelsOk)
        {
            if (HarnessWebIsInPlay || _harnessWebWasReachableThisSession)
            {
                await RefreshHarnessWebUiStatusAsync().ConfigureAwait(true);
                if (!_harnessWebUiReachable && _harnessWebWasReachableThisSession)
                {
                    PublishHarnessWebDown(report);
                    return;
                }
            }

            PublishHealthReport(headline, report, keepVisible: false, entity);
            return;
        }

        PublishHealthReport(headline, report, keepVisible: true, entity);
    }

    private void PublishHarnessWebDown(string? priorReport = null)
    {
        var miss = LocalHealthGuidance.BuildHarnessWebDown();
        var report = string.IsNullOrWhiteSpace(priorReport)
            ? miss
            : priorReport.Trim() + Environment.NewLine + miss;
        PublishHealthReport(
            LocalHealthGuidance.HarnessWebDownHeadline,
            report,
            keepVisible: true,
            entity: "Harness web");
    }

    private void PublishHarnessWebDownIfModelsAreMounted()
    {
        if (!_harnessWebWasReachableThisSession || string.IsNullOrWhiteSpace(_mountedRouteType))
        {
            return;
        }

        if (_healthReportDismissed)
        {
            return;
        }

        if (HealthReportVisible
            && HealthReportText.Contains("Harness web is not running", StringComparison.Ordinal))
        {
            return;
        }

        PublishHarnessWebDown();
    }

    private void ClearHarnessWebHealthReportIfShowing()
    {
        if (HealthReportVisible
            && HealthReportText.Contains("Harness web is not running", StringComparison.Ordinal))
        {
            ClearHealthReport();
        }
    }

    private void ReportTuneProgress(string message)
    {
        var normalized = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReportTuneProgress(normalized));
            return;
        }

        _suppressStatusDiagnostic = true;
        try
        {
            StatusMessage = normalized;
        }
        finally
        {
            _suppressStatusDiagnostic = false;
        }

        AppendDiagnosticEntry("TUNE", normalized);
    }

    private static Task FlushUiAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    private async Task<LocalGenerationTimingResult> TimeShortLocalReplyWithProgressAsync(
        string progressPrefix,
        CancellationToken cancellationToken,
        bool throughPublicEndpoint = false)
    {
        var startedUtc = DateTime.UtcNow;
        var timingTask = throughPublicEndpoint
            ? _runtimeService.TimeShortLocalReplyThroughPublicEndpointAsync(
                (int)OrchestratorPort,
                FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile),
                cancellationToken)
            : _runtimeService.TimeShortLocalReplyAsync(
                (int)OrchestratorPort,
                FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile),
                cancellationToken);
        while (!timingTask.IsCompleted)
        {
            var elapsedSeconds = Math.Max(0, (int)(DateTime.UtcNow - startedUtc).TotalSeconds);
            ReportTuneProgress(progressPrefix + " Waiting for the short test reply (" + elapsedSeconds + "s).");
            await FlushUiAsync();
            await Task.WhenAny(timingTask, Task.Delay(10000, cancellationToken));
        }

        return await timingTask;
    }

    [ObservableProperty]
    public partial string SelectedUiScale { get; set; } = "Normal";

    public ObservableCollection<string> UiScaleOptions { get; } = ["Normal", "Compact", "Very Compact"];

    [ObservableProperty]
    public partial string SelectedUiTheme { get; set; } = "Light";

    public ObservableCollection<string> UiThemeOptions { get; } = ["Light", "Dark"];

    public double UiScaleFactor => SelectedUiScale switch
    {
        "Compact"      => 0.85,
        "Very Compact" => 0.72,
        _              => 1.0
    };

    partial void OnSelectedUiScaleChanged(string value)
    {
        OnPropertyChanged(nameof(UiScaleFactor));
        _config.SetString("UiScale", value);
        _configService.Save(_config);
    }

    partial void OnSelectedUiThemeChanged(string value)
    {
        global::FluxMux.Avalonia.App.ApplyRequestedTheme(value);
        _config.SetString("UiTheme", value);
        _configService.Save(_config);
        RefreshEnvironmentRowStripes();
    }

    public void ApplySavedWindowPreferences()
    {
        var savedScale = _config.GetString("UiScale", "Normal");
        if (UiScaleOptions.Contains(savedScale))
            SelectedUiScale = savedScale;

        var savedTheme = _config.GetString("UiTheme", "Light");
        if (UiThemeOptions.Contains(savedTheme))
            SelectedUiTheme = savedTheme;
        global::FluxMux.Avalonia.App.ApplyRequestedTheme(SelectedUiTheme);
    }

    public void SaveWindowGeometry(int x, int y, int width, int height)
    {
        _config.SetInt("WindowX", x);
        _config.SetInt("WindowY", y);
        _config.SetInt("WindowWidth", Math.Clamp(width, 640, 3840));
        _config.SetInt("WindowHeight", Math.Clamp(height, 600, 2160));
        _configService.Save(_config);
    }

    public (int X, int Y, int Width, int Height) LoadWindowGeometry()
    {
        var x = _config.GetInt("WindowX", int.MinValue);
        var y = _config.GetInt("WindowY", int.MinValue);
        var w = _config.GetInt("WindowWidth", 1100);
        var h = _config.GetInt("WindowHeight", 820);
        return (x, y, Math.Clamp(w, 640, 3840), Math.Clamp(h, 600, 2160));
    }

    [ObservableProperty]
    public partial string DependencySummary { get; set; } = "Environment check has not run yet.";

    [ObservableProperty]
    public partial string CloudCredentialsHelpText { get; set; } = "Cloud credentials are read from .vscode/fluxmux_secrets.json (preferred) and can fall back to .vscode/fluxmux_config.json.";

    [ObservableProperty]
    public partial string CloudModelIdHelpText { get; set; } =
        "Before trying to set up any cloud model, you must have already stored the API key or secret supplied by your cloud provider, in the **Servers** tab. **Refresh Models** asks that provider for current ids and fills the Model id dropdown — that list is not proof a profile will validate. Only **Validate to endpoint** confirms chat works on AI-FluxMux. Copilot GitHub is especially broad: its catalog can list Gemini, Claude, and GPT ids before the chat route accepts them. If **Validate to endpoint** fails, change Provider or Model id in **Detailed settings**, click **Save profile**, then **Validate to endpoint** again.";

    [ObservableProperty]
    public partial string CloudApiKeyEntry { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedCredentialCloudProvider { get; set; } = "Gemini";

    [ObservableProperty]
    public partial string CustomCompatEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewCustomCloudProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewCustomCloudProviderEndpoint { get; set; } = string.Empty;

    public bool ShowCustomOpenAiCompatibleSettings =>
        CustomCloudProviderRegistry.IsCustomCompat(SelectedCredentialCloudProvider, _config.Root);

    public bool CanRemoveSelectedCustomCloudProvider =>
        CustomCloudProviderRegistry.IsCustomCompat(SelectedCredentialCloudProvider, _config.Root)
        && !string.Equals(
            SelectedCredentialCloudProvider,
            CustomCloudProviderRegistry.DefaultCustomProviderName,
            StringComparison.OrdinalIgnoreCase)
        && !CustomCloudProviderRegistry.IsReadyMade(SelectedCredentialCloudProvider);

    partial void OnSelectedCredentialCloudProviderChanged(string value)
    {
        OnPropertyChanged(nameof(ShowCustomOpenAiCompatibleSettings));
        OnPropertyChanged(nameof(CanRemoveSelectedCustomCloudProvider));
        RefreshCustomCompatEndpointFromStore();
        RefreshCloudApiKeyFieldFromStore();
    }

    partial void OnCloudApiKeyEntryChanged(string value)
    {
        if (_isRefreshingCloudApiKeyField || _isLoadingConfig)
        {
            return;
        }

        if (!_cloudApiKeyShowsStoredMask)
        {
            return;
        }

        var stored = ReadStoredCloudApiKey(SelectedCredentialCloudProvider);
        if (string.Equals(value, BuildStoredApiKeyMask(stored), StringComparison.Ordinal))
        {
            return;
        }

        _cloudApiKeyShowsStoredMask = false;
    }

    partial void OnCustomCompatEndpointChanged(string value)
    {
        if (_isLoadingConfig || _isRefreshingCustomCompatEndpoint)
        {
            return;
        }

        if (!CustomCloudProviderRegistry.IsCustomCompat(SelectedCredentialCloudProvider, _config.Root))
        {
            return;
        }

        CustomCloudProviderRegistry.SetEndpoint(_config.Root, SelectedCredentialCloudProvider, value);
        _configService.Save(_config);
    }

    [RelayCommand]
    private void SaveCloudApiKey()
    {
        if (_cloudApiKeyShowsStoredMask)
        {
            StatusMessage = $"A key is already saved for {SelectedCredentialCloudProvider}. Paste a new key to replace it.";
            return;
        }

        var key = (CloudApiKeyEntry ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key) || IsApiKeyMaskText(key))
        {
            StatusMessage = "Enter an API key first.";
            return;
        }

        if (!TryResolveCustomProviderForCredentialAction(requireNamedCustom: true, out var provider, out var resolveError))
        {
            StatusMessage = resolveError;
            return;
        }

        JsonObject secrets;
        try
        {
            secrets = File.Exists(_secretsPath)
                ? (JsonNode.Parse(File.ReadAllText(_secretsPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();
        }
        catch
        {
            secrets = new JsonObject();
        }

        if (CustomCloudProviderRegistry.IsCustomCompat(provider, _config.Root))
        {
            var endpoint = CustomCloudProviderRegistry.GetEndpoint(_config.Root, provider);
            if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                StatusMessage = "Enter a valid base URL for the custom OpenAI-compatible server first, for example https://host:port/v1";
                return;
            }

            CustomCloudProviderRegistry.SetApiKey(secrets, provider, key);
            _configService.Save(_config);
            try
            {
                File.WriteAllText(_secretsPath, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                RefreshCloudProvidersList();
                SelectedCredentialCloudProvider = provider;
                RefreshCloudApiKeyFieldFromStore();
                StatusMessage = $"API key saved for \"{provider}\" and the provider is in the custom registry.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Failed to save API key: " + ex.Message;
            }

            return;
        }

        var keyField = CloudApiKeyFieldName(provider);
        if (keyField is null)
        {
            StatusMessage = "No key field defined for this provider.";
            return;
        }

        secrets[keyField] = key;
        try
        {
            File.WriteAllText(_secretsPath, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            RefreshCloudApiKeyFieldFromStore();
            StatusMessage = $"API key saved for {provider}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to save API key: " + ex.Message;
        }
    }

    /// <summary>
    /// Resolves which provider a Servers credential action applies to.
    /// Named Add-custom drafts (name + base URL) are registered into CustomCloudProviders first.
    /// The legacy unnamed Custom OpenAI-Compatible slot cannot be used for new customs.
    /// </summary>
    private bool TryResolveCustomProviderForCredentialAction(
        bool requireNamedCustom,
        out string provider,
        out string error)
    {
        provider = FirstNonEmpty(SelectedCredentialCloudProvider, SelectedCloudProvider);
        error = string.Empty;

        var draftName = (NewCustomCloudProviderName ?? string.Empty).Trim();
        var draftEndpoint = FirstNonEmpty(
            (NewCustomCloudProviderEndpoint ?? string.Empty).Trim(),
            string.Empty);
        // Do not fall back to the selected provider's Base URL box for a new Name —
        // both Name and Base URL under Add custom must be filled together.

        var hasDraftName = !string.IsNullOrWhiteSpace(draftName);
        var hasDraftEndpoint = !string.IsNullOrWhiteSpace(draftEndpoint);
        if (hasDraftName || hasDraftEndpoint)
        {
            if (!hasDraftName || !hasDraftEndpoint)
            {
                error = "Enter both a Name and Base URL for this custom OpenAI-compatible provider before Add, Validate, or Save Key.";
                return false;
            }

            if (!CustomCloudProviderRegistry.TryRegisterNamedProvider(
                    _config.Root,
                    draftName,
                    draftEndpoint,
                    updateExisting: true,
                    out error))
            {
                return false;
            }

            _configService.Save(_config);
            RefreshCloudProvidersList();
            SelectedCredentialCloudProvider = draftName;
            NewCustomCloudProviderName = string.Empty;
            NewCustomCloudProviderEndpoint = string.Empty;
            RefreshCustomCompatEndpointFromStore();
            provider = draftName;
            return true;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            error = "Choose a cloud provider first.";
            return false;
        }

        if (requireNamedCustom
            && provider.Equals(
                CustomCloudProviderRegistry.DefaultCustomProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Give every custom OpenAI-compatible server a name. Enter Name and Base URL under Add custom provider, then Add custom provider (or Validate Cloud Key / Save Key).";
            return false;
        }

        if (CustomCloudProviderRegistry.IsCustomCompat(provider, _config.Root))
        {
            var endpoint = (CustomCompatEndpoint ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                CustomCloudProviderRegistry.SetEndpoint(_config.Root, provider, endpoint);
                _configService.Save(_config);
            }
        }

        return true;
    }

    private void RefreshCustomCompatEndpointFromStore()
    {
        _isRefreshingCustomCompatEndpoint = true;
        try
        {
            CustomCompatEndpoint = CustomCloudProviderRegistry.IsCustomCompat(SelectedCredentialCloudProvider, _config.Root)
                ? CustomCloudProviderRegistry.GetEndpoint(_config.Root, SelectedCredentialCloudProvider)
                : string.Empty;
        }
        finally
        {
            _isRefreshingCustomCompatEndpoint = false;
        }
    }

    private void RefreshCloudApiKeyFieldFromStore()
    {
        var stored = ReadStoredCloudApiKey(SelectedCredentialCloudProvider);
        _isRefreshingCloudApiKeyField = true;
        try
        {
            _cloudApiKeyShowsStoredMask = !string.IsNullOrWhiteSpace(stored);
            CloudApiKeyEntry = _cloudApiKeyShowsStoredMask
                ? BuildStoredApiKeyMask(stored)
                : string.Empty;
        }
        finally
        {
            _isRefreshingCloudApiKeyField = false;
        }
    }

    private string ReadStoredCloudApiKey(string? provider)
    {
        if (CustomCloudProviderRegistry.IsCustomCompat(provider, _config.Root))
        {
            try
            {
                var secrets = File.Exists(_secretsPath)
                    ? (JsonNode.Parse(File.ReadAllText(_secretsPath)) as JsonObject ?? new JsonObject())
                    : new JsonObject();
                return CustomCloudProviderRegistry.GetApiKey(secrets, _config.Root, provider);
            }
            catch
            {
                return CustomCloudProviderRegistry.GetApiKey(null, _config.Root, provider);
            }
        }

        var keyField = CloudApiKeyFieldName(provider);
        if (string.IsNullOrWhiteSpace(keyField))
        {
            return string.Empty;
        }

        try
        {
            if (File.Exists(_secretsPath))
            {
                var secrets = JsonNode.Parse(File.ReadAllText(_secretsPath)) as JsonObject;
                var fromSecrets = secrets?[keyField]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(fromSecrets))
                {
                    return fromSecrets;
                }
            }
        }
        catch
        {
        }

        return (_config.GetString(keyField, string.Empty) ?? string.Empty).Trim();
    }

    private static string? CloudApiKeyFieldName(string? provider) => provider switch
    {
        "Gemini" => "GeminiApiKey",
        "Anthropic" => "AnthropicApiKey",
        "OpenAI" => "OpenAiApiKey",
        "Copilot GitHub" => "GitHubCopilotApiKey",
        _ => null
    };

    [RelayCommand]
    private void AddCustomCloudProvider()
    {
        var name = (NewCustomCloudProviderName ?? string.Empty).Trim();
        var endpoint = (NewCustomCloudProviderEndpoint ?? string.Empty).Trim();
        if (!CustomCloudProviderRegistry.TryRegisterNamedProvider(
                _config.Root,
                name,
                endpoint,
                updateExisting: false,
                out var error))
        {
            StatusMessage = error;
            return;
        }

        _configService.Save(_config);
        RefreshCloudProvidersList();
        SelectedCredentialCloudProvider = name;
        NewCustomCloudProviderName = string.Empty;
        NewCustomCloudProviderEndpoint = string.Empty;
        RefreshCustomCompatEndpointFromStore();
        RefreshCloudApiKeyFieldFromStore();

        // If the operator already typed a key for this new named provider, keep it with the registry entry.
        var typedKey = (CloudApiKeyEntry ?? string.Empty).Trim();
        if (!_cloudApiKeyShowsStoredMask && !string.IsNullOrWhiteSpace(typedKey) && !IsApiKeyMaskText(typedKey))
        {
            try
            {
                var secrets = File.Exists(_secretsPath)
                    ? (JsonNode.Parse(File.ReadAllText(_secretsPath)) as JsonObject ?? new JsonObject())
                    : new JsonObject();
                CustomCloudProviderRegistry.SetApiKey(secrets, name, typedKey);
                File.WriteAllText(_secretsPath, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                RefreshCloudApiKeyFieldFromStore();
                StatusMessage =
                    $"Custom provider \"{name}\" added to the registry with its base URL and API key. Use Validate Cloud Key when you want a live check.";
                return;
            }
            catch (Exception ex)
            {
                StatusMessage =
                    $"Custom provider \"{name}\" was added to the registry, but saving the API key failed: {ex.Message}";
                return;
            }
        }

        StatusMessage =
            $"Custom provider \"{name}\" added to the registry. Paste its API key, Validate Cloud Key, then Save Key.";
    }

    [RelayCommand]
    private void RemoveCustomCloudProvider()
    {
        var name = SelectedCredentialCloudProvider;
        if (CustomCloudProviderRegistry.HasCloudProfilesForProvider(_config.Root, name))
        {
            StatusMessage =
                $"Cannot remove \"{name}\" while Model Profiles still use it. Delete those cloud profiles first.";
            return;
        }

        JsonObject? secrets = null;
        try
        {
            if (File.Exists(_secretsPath))
            {
                secrets = JsonNode.Parse(File.ReadAllText(_secretsPath)) as JsonObject;
            }
        }
        catch
        {
            secrets = new JsonObject();
        }

        secrets ??= new JsonObject();
        if (!CustomCloudProviderRegistry.TryRemoveProvider(_config.Root, secrets, name, out var error))
        {
            StatusMessage = error;
            return;
        }

        _configService.Save(_config);
        try
        {
            File.WriteAllText(_secretsPath, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            StatusMessage = "Removed the provider from config, but failed to update secrets: " + ex.Message;
            RefreshCloudProvidersList();
            return;
        }

        RefreshCloudProvidersList();
        SelectedCredentialCloudProvider =
            CloudProviders.FirstOrDefault(p => CustomCloudProviderRegistry.IsCustomCompat(p, _config.Root))
            ?? CloudProviders.FirstOrDefault()
            ?? "Gemini";
        RefreshCustomCompatEndpointFromStore();
        RefreshCloudApiKeyFieldFromStore();
        StatusMessage = $"Custom cloud provider \"{name}\" removed.";
    }

    private void RefreshCloudProvidersList()
    {
        var keepSelected = SelectedCloudProvider;
        var keepCredential = SelectedCredentialCloudProvider;
        var keepNew = NewCloudProfileProvider;
        var keepFamily = CloudFamilyProviderDraft;

        CloudProviders.Clear();
        foreach (var builtIn in CustomCloudProviderRegistry.BuiltInProviders)
        {
            CloudProviders.Add(builtIn);
        }

        foreach (var custom in CustomCloudProviderRegistry.ListCustomProviderNames(_config.Root))
        {
            AddIfMissing(CloudProviders, custom);
        }

        // Profiles may reference a custom name that was removed from the registry; keep it visible until cleaned up.
        foreach (var key in _config.GetObjectKeys("CloudProfiles"))
        {
            var parts = key.Split("::");
            if (parts.Length >= 1)
            {
                AddIfMissing(CloudProviders, parts[0].Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(keepSelected))
        {
            AddIfMissing(CloudProviders, keepSelected);
            SelectedCloudProvider = keepSelected;
        }

        if (!string.IsNullOrWhiteSpace(keepCredential))
        {
            AddIfMissing(CloudProviders, keepCredential);
            SelectedCredentialCloudProvider = keepCredential;
        }

        if (!string.IsNullOrWhiteSpace(keepNew))
        {
            AddIfMissing(CloudProviders, keepNew);
            NewCloudProfileProvider = keepNew;
        }

        if (!string.IsNullOrWhiteSpace(keepFamily))
        {
            AddIfMissing(CloudProviders, keepFamily);
            CloudFamilyProviderDraft = keepFamily;
        }

        OnPropertyChanged(nameof(ShowCustomOpenAiCompatibleSettings));
        OnPropertyChanged(nameof(CanRemoveSelectedCustomCloudProvider));
    }

    private static string BuildStoredApiKeyMask(string storedKey)
    {
        var length = storedKey.Trim().Length;
        if (length < 8)
        {
            length = 8;
        }
        else if (length > 40)
        {
            length = 40;
        }

        return new string('*', length);
    }

    private static bool IsApiKeyMaskText(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length > 0 && text.All(ch => ch == '*');
    }

    [ObservableProperty]
    public partial string DependencyScopeHelpText { get; set; } = "Results apply to the selected provider, model folder, and llama-server path.";

    [ObservableProperty]
    public partial string DependencyPinPolicyText { get; set; } = "After you update llama-server or the GPU driver, launch a known profile to confirm it still works.";

    [ObservableProperty]
    public partial string DependencyDriftSummaryText { get; set; } = "No known-good snapshot captured. Capture one after a working launch if you want later path or llama-server changes highlighted here.";

    [ObservableProperty]
    public partial string UpdateFeedUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateCheckStatusText { get; set; } =
        "No feed URL yet. Paste one when you have a public host. Until then, you can still replace Help.html next to AI-FluxMux.exe by hand.";

    [ObservableProperty]
    public partial bool UpdateDownloadAvailable { get; set; }

    [ObservableProperty]
    public partial string LlamaServerUpdateStatusText { get; set; } =
        "Check for updates also looks at the llama-server folder already in use, and only offers a newer zip of that same Windows CUDA / Vulkan / CPU family. Confirm that folder is still the right install for this PC before you update — hardware may have changed.";

    [ObservableProperty]
    public partial string HarnessUpdateStatusText { get; set; } =
        "Check for updates can also read this PC's DeepSeek Harness version (dsh --version, or npx) and compare it to npm latest / next. AI-FluxMux does not install or upgrade DeepSeek Harness.";

    [ObservableProperty]
    public partial string HarnessInstalledVersionText { get; set; } =
        "DeepSeek Harness version has not been read yet. Use Check for updates on Environment, or Start Harness web after dsh is installed.";

    [ObservableProperty]
    public partial string ClineSettingsStatusText { get; set; } =
        "Cline settings on this PC have not been read yet.";

    [ObservableProperty]
    public partial bool LlamaServerZipAvailable { get; set; }

    [ObservableProperty]
    public partial bool LlamaCudartZipAvailable { get; set; }

    [ObservableProperty]
    public partial bool LlamaReleasePageAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedMainTabIndex { get; set; }
    = 0;

    public ObservableCollection<string> CloudProviders { get; } = [];

    public ObservableCollection<string> CloudProfiles { get; } =
    [];

    public ObservableCollection<string> NewCloudProfileModels { get; } = [];

    public ObservableCollection<string> CloudFamilyModelIdChoices { get; } = [];

    public ObservableCollection<ValidatedQuickSelectProfileOption> ValidatedQuickSelectProfiles { get; } = [];

    public ObservableCollection<RouteSlotViewModel> QuickSelectSlots { get; } = [];

    public ObservableCollection<string> Selection2CloudProfiles { get; } =
    [];

    public ObservableCollection<string> LocalProfiles { get; } =
    [
        "Local Balanced",
        "Local Throughput",
        "Local Max Quality"
    ];

    public ObservableCollection<string> CloudVariants { get; } =
    [];

    public ObservableCollection<CloudVariantTreeItemViewModel> CloudVariantTreeItems { get; } = [];

    public ObservableCollection<CloudVariantTreeItemViewModel> LocalVariantTreeItems { get; } = [];

    public ObservableCollection<ProfileTemplateSourceViewModel> ProfileTemplateSources { get; } = [];

    public ObservableCollection<string> LocalVariants { get; } =
    [
        BaseVariantDisplayName
    ];


    public ObservableCollection<string> RouteTypeOptions { get; } =
    [
        "Cloud",
        "Local"
    ];

    public ObservableCollection<SwitchBenchmarkProfileOption> SwitchBenchmarkProfiles { get; } = [];

    public ObservableCollection<DependencyMatrixRow> CoreDependencyRows { get; } = [];

    public ObservableCollection<DependencyMatrixRow> ProviderAddonRows { get; } = [];

    public ObservableCollection<InstallGuideRow> CoreInstallGuideRows { get; } = [];

    public ObservableCollection<InstallGuideRow> SdkInstallGuideRows { get; } = [];

    public ObservableCollection<string> LocalPriorityOrder { get; } =
    [
        "Stability",
        "Speed",
        "Fidelity",
        "Context length",
        "Reply length",
        "Reasoning depth"
    ];

    private static readonly string[] LocalPriorityNames =
    [
        "Stability",
        "Speed",
        "Fidelity",
        "Context length",
        "Reply length",
        "Reasoning depth"
    ];

    public ObservableCollection<string> CloudPriorityOrder { get; } =
    [
        "Stability",
        "Speed",
        "Reasoning depth",
        "Context length",
        "Reply length",
        "Token Cost Economy"
    ];

    private static readonly string[] CloudPriorityNames =
    [
        "Stability",
        "Speed",
        "Reasoning depth",
        "Context length",
        "Reply length",
        "Token Cost Economy"
    ];

    public ObservableCollection<string> CloudContextWindowOptions { get; } =
    ["Auto", "8192", "16384", "32768", "65536", "131072", "2097152"];

    public ObservableCollection<string> CloudReasoningModeOptions { get; } =
    ["Balanced", "Fast", "Deep"];

    public ObservableCollection<string> CloudResponseFormatOptions { get; } =
    ["Text", "Json"];

    public ObservableCollection<string> CloudReasoningEffortOptions { get; } =
    ["Auto", "Low", "Medium", "High"];

    public ObservableCollection<string> SwitchBenchmarkOrderOptions { get; } =
    ["Profile A -> Profile B -> Profile A", "Profile B -> Profile A -> Profile B"];

    public ObservableCollection<string> CloudGeminiThinkingModeOptions { get; } =
    ["Balanced", "Off", "On"];

    public ObservableCollection<string> CloudAnthropicThinkingModeOptions { get; } =
    ["Standard", "Off", "Extended"];

    public ObservableCollection<string> LocalThreadOptions { get; } =
    ["4", "6", "8", "10", "12", "16", "20", "24", "32"];

    public ObservableCollection<string> LocalThreadBatchOptions { get; } =
    ["1", "2", "4", "8", "12", "16", "24", "32"];

    public ObservableCollection<string> LocalGpuOffloadModeOptions { get; } =
    ["CPU only", "GPU + CPU", "GPU only"];

    public ObservableCollection<string> LocalMultiGpuModeOptions { get; } =
    ["Auto", "Disabled", "Manual"];

    public ObservableCollection<string> LocalSplitModeOptions { get; } =
    ["Auto", "layer", "row", "tensor"];

    public ObservableCollection<string> LocalMainGpuOptions { get; } =
    ["Auto", "0", "1", "2", "3"];

    public ObservableCollection<string> LocalGpuLayersOptions { get; } =
    ["Auto", "0", "20", "40", "70", "999"];

    public IReadOnlyList<string> LocalGpuLayersChoices =>
        BuildSettingChoices(LocalGpuLayersOptions, includeAuto: IsLocalVariantEditable, autoDisplayReplacement: "fit");

    public IReadOnlyList<string> CloudContextWindowChoices =>
        BuildSettingChoices(CloudContextWindowOptions, includeAuto: IsCloudVariantEditable, autoDisplayReplacement: "Provider default");

    public IReadOnlyList<string> CloudReasoningEffortChoices =>
        BuildSettingChoices(CloudReasoningEffortOptions, includeAuto: IsCloudVariantEditable, autoDisplayReplacement: "Provider default");

    public ObservableCollection<string> LocalOnOffAutoOptions { get; } =
    ["Auto", "Enabled", "Disabled"];

    public ObservableCollection<string> LocalOnOffOptions { get; } =
    ["Enabled", "Disabled"];

    public ObservableCollection<LocalVisionProjectorChoice> LocalVariantVisionProjectorChoices { get; } = [];

    public ObservableCollection<string> LocalKvCacheOptions { get; } =
    ["f16", "q8_0", "q6_k", "q5_1", "q4_1"];

    public ObservableCollection<string> LocalChatTemplateOptions { get; } =
    ["Auto", "chatml", "qwen", "llama3", "mistral", "deepseek"];

    public ObservableCollection<string> LocalMaxTokenOptions { get; } =
    ["2048", "4096", "8192", "16384", "32768"];

    public ObservableCollection<string> LocalBatchSizeOptions { get; } =
    ["256", "512", "1024", "2048", "4096"];

    public ObservableCollection<string> LocalUbatchSizeOptions { get; } =
    ["64", "128", "256", "512"];

    public ObservableCollection<string> LocalSpecTypeOptions { get; } =
    ["Disabled", "draft-mtp", "ngram-simple"];

    public ObservableCollection<string> LocalCacheReuseOptions { get; } =
    ["0", "64", "128", "256", "512"];

    public ObservableCollection<string> LocalCacheRamOptions { get; } =
    ["Disabled", "4096", "8192", "16384", "Unlimited"];

    public ObservableCollection<string> LocalReasoningOptions { get; } =
    ["Off", "On"];

    public ObservableCollection<string> LocalChatParserOptions { get; } =
    ["Jinja", "No Jinja", "Skip parsing"];

    public ObservableCollection<ProviderRequirementRow> ProviderRequirementRows { get; } = [];

    public ObservableCollection<PathStatusRow> HardRequiredPathRows { get; } = [];

    public ObservableCollection<PathStatusRow> RuntimePathRows { get; } = [];

    public ObservableCollection<LocalModelCapabilityRow> LocalModelCapabilityRows { get; } = [];

    public ObservableCollection<DiscoveredLocalServerOption> DiscoveredLocalServerOptions { get; } = [];

    public ObservableCollection<InstalledLocalServerOption> InstalledLocalServerOptions { get; } = [];

    [RelayCommand]
    private async Task ValidateCloudAsync()
    {
        if (!TryResolveCustomProviderForCredentialAction(requireNamedCustom: true, out var provider, out var resolveError))
        {
            StatusMessage = resolveError;
            return;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            StatusMessage = "Choose a credential provider first.";
            return;
        }

        if (CustomCloudProviderRegistry.IsCustomCompat(provider, _config.Root))
        {
            var endpoint = CustomCloudProviderRegistry.GetEndpoint(_config.Root, provider);
            if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                StatusMessage = "Enter a valid base URL for the custom OpenAI-compatible server first, for example https://host:port/v1";
                return;
            }
        }

        StatusMessage = $"Checking the {provider} key with the provider...";
        var typedKey = _cloudApiKeyShowsStoredMask ? string.Empty : CloudApiKeyEntry;
        var catalog = await _runtimeService.ListCloudModelsAsync(provider, keyOverride: typedKey);
        if (catalog.IsSuccess && catalog.Models.Count > 0)
        {
            SaveCloudModelCatalog(provider, catalog.Models);
            StampDiscoveredCloudProfileTraits(provider, catalog.TraitsByModel);
            RefreshCloudProfilesForProvider();
            RefreshNewCloudProfileModelOptions();
            RefreshCloudFamilyModelIdEditor();
        }

        if (catalog.IsSuccess)
        {
            var unsaved = !string.IsNullOrWhiteSpace(typedKey) && !IsApiKeyMaskText(typedKey);
            StatusMessage = catalog.Models.Count > 0
                ? $"{provider}: Refresh Models found {catalog.Models.Count} chat-oriented model id(s)."
                : $"{provider} accepted this key.";
            if (CustomCloudProviderRegistry.IsCustomCompat(provider, _config.Root))
            {
                StatusMessage += $" \"{provider}\" is registered under CustomCloudProviders.";
            }

            ConnectionHealthText = catalog.Models.Count > 0
                ? catalog.Details + (unsaved ? " Click Save Key if you want AI-FluxMux to keep the typed key." : string.Empty)
                : unsaved
                    ? "This check only confirms the key. Click Save Key if you want AI-FluxMux to keep it."
                    : "This check only confirms the key.";
            _lastHealthDetail = ConnectionHealthText;
            return;
        }

        StatusMessage = $"{provider} did not accept this key.";
        ConnectionHealthText = catalog.Details;
        _lastHealthDetail = catalog.Details;
    }

    [RelayCommand]
    private async Task TestLocalServerReadinessAsync()
    {
        SaveCurrentSelections();
        StatusMessage = "Checking whether llama-server, the model folder, and ports are ready...";

        var result = await _runtimeService.TestLocalServerReadinessAsync(
            LocalModelDirectory,
            (int)OrchestratorPort);

        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = result.Status;
    }

    [RelayCommand]
    private void CheckUpdatedCloudModelOptions()
    {
        var provider = FirstNonEmpty(SelectedCloudProvider, "Cloud provider");
        var before = CloudProfiles.Count;

        RefreshCloudProfilesForProvider();

        var after = CloudProfiles.Count;
        if (after > before)
        {
            StatusMessage = $"Checked updated cloud model options for {provider}. Added {after - before} model option(s).";
            return;
        }

        if (after < before)
        {
            StatusMessage = $"Checked updated cloud model options for {provider}. Removed {before - after} stale model option(s).";
            return;
        }

        StatusMessage = $"Checked updated cloud model options for {provider}. No list changes detected.";
    }

    [RelayCommand]
    private void RefreshSelection1CloudModelOptions() => RefreshCloudModelOptionsForSlot(
        "Selection 1", SelectedCloudProvider, CloudProfiles, RefreshCloudProfilesForProvider);

    [RelayCommand]
    private void RefreshSelection2CloudModelOptions() => RefreshCloudModelOptionsForSlot(
        "Selection 2", SelectedSelection2CloudProvider, Selection2CloudProfiles, RefreshSelection2CloudProfilesForProvider);

    private void RefreshCloudModelOptionsForSlot(
        string slotLabel,
        string provider,
        ObservableCollection<string> models,
        Action refresh)
    {
        var before = models.Count;
        refresh();
        var change = models.Count - before;
        StatusMessage = change switch
        {
            > 0 => $"{slotLabel}: refreshed {provider} models; added {change} option(s).",
            < 0 => $"{slotLabel}: refreshed {provider} models; removed {-change} stale option(s).",
            _ => $"{slotLabel}: refreshed {provider} models; no list changes detected."
        };
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        SaveCurrentSelections();
        UpdateDownloadAvailable = false;
        LlamaServerZipAvailable = false;
        LlamaCudartZipAvailable = false;
        LlamaReleasePageAvailable = false;
        _llamaServerZipUrl = string.Empty;
        _llamaCudartZipUrl = string.Empty;
        _llamaReleaseUrl = string.Empty;
        UpdateCheckStatusText = "Checking for updates...";
        LlamaServerUpdateStatusText = "Checking llama-server nightlies for this install's hardware family...";
        HarnessUpdateStatusText = "Checking this PC's DeepSeek Harness version...";
        StatusMessage = "Checking for updates...";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);
        try
        {
            var helpPath = HelpHtmlFile.FindNewest()
                ?? Path.Combine(AppContext.BaseDirectory, HelpHtmlFile.FileNames[0]);
            var appResult = await UpdateCheck.RunAsync(
                http,
                UpdateFeedUrl,
                FluxMuxAppInfo.Version,
                helpPath,
                Path.Combine(HelpHtmlFile.UserDirectory, HelpHtmlFile.FileNames[0]));
            UpdateCheckStatusText = appResult.StatusText;
            UpdateDownloadAvailable = appResult.AppUpdateAvailable && !string.IsNullOrWhiteSpace(appResult.DownloadUrl);
            _updateDownloadUrl = appResult.DownloadUrl;
            StatusMessage = appResult.HelpReplaced
                ? "Help.html was updated from the feed."
                : appResult.StatusText;
        }
        catch (Exception ex)
        {
            UpdateCheckStatusText = "Could not check for AI-FluxMux updates: " + ex.Message;
            StatusMessage = UpdateCheckStatusText;
        }

        try
        {
            var llamaResult = await LlamaServerUpdateCheck.CheckAsync(http, LocalServerExecutablePath);
            LlamaServerUpdateStatusText = llamaResult.StatusText;
            _llamaServerZipUrl = llamaResult.ServerZipUrl ?? string.Empty;
            _llamaCudartZipUrl = llamaResult.CudartZipUrl ?? string.Empty;
            _llamaReleaseUrl = llamaResult.ReleaseUrl ?? string.Empty;
            LlamaServerZipAvailable = !string.IsNullOrWhiteSpace(_llamaServerZipUrl);
            LlamaCudartZipAvailable = !string.IsNullOrWhiteSpace(_llamaCudartZipUrl);
            LlamaReleasePageAvailable = !string.IsNullOrWhiteSpace(_llamaReleaseUrl);
            if (llamaResult.NewerAvailable)
            {
                StatusMessage = "A matching llama-server zip is available. Confirm this folder still matches this GPU before downloading.";
            }
        }
        catch (Exception ex)
        {
            LlamaServerUpdateStatusText = "Could not check llama-server updates: " + ex.Message;
        }

        try
        {
            HarnessUpdateStatusText = "Checking this PC's DeepSeek Harness version...";
            var harnessResult = await DeepSeekHarnessUpdateCheck.CheckAsync(http);
            ApplyHarnessVersionResult(harnessResult);
        }
        catch (Exception ex)
        {
            HarnessUpdateStatusText = "Could not check DeepSeek Harness updates: " + ex.Message;
        }
    }

    private void ApplyHarnessVersionResult(DeepSeekHarnessUpdateResult result)
    {
        _harnessVersionProbeStarted = true;
        HarnessUpdateStatusText = result.StatusText;
        ApplyHarnessInstalledVersionOnly(result);
    }

    [RelayCommand]
    private void OpenHarnessReleases()
        => OpenExternalLink(DeepSeekHarnessUpdateCheck.ReleasesUrl);

    [RelayCommand]
    private void OpenLlamaServerZip()
        => OpenExternalLink(string.IsNullOrWhiteSpace(_llamaServerZipUrl) ? null : _llamaServerZipUrl);

    [RelayCommand]
    private void OpenLlamaCudartZip()
        => OpenExternalLink(string.IsNullOrWhiteSpace(_llamaCudartZipUrl) ? null : _llamaCudartZipUrl);

    [RelayCommand]
    private void OpenLlamaReleasePage()
        => OpenExternalLink(string.IsNullOrWhiteSpace(_llamaReleaseUrl) ? null : _llamaReleaseUrl);

    [RelayCommand]
    private void OpenUpdateDownload()
    {
        if (string.IsNullOrWhiteSpace(_updateDownloadUrl))
        {
            StatusMessage = "No download page is listed in the update feed.";
            return;
        }

        OpenExternalLink(_updateDownloadUrl);
    }

    [RelayCommand]
    private async Task CheckDependenciesAsync()
    {
        SaveCurrentSelections();
        StatusMessage = "Checking environment...";
        LocalModelCapabilityRows.Clear();
        LocalModelCapabilityRows.Add(new LocalModelCapabilityRow
        {
            Model = "Scanning configured model directory...",
            Format = "local model",
            VisionSupport = "Checking",
            Notes = "Searching the configured folder and its subfolders.",
            Guidance = "The result is a filename and projector-sidecar estimate; runtime validation remains authoritative."
        });

        var report = await _dependencyAuditService.BuildReportAsync(
            SelectedCloudProvider,
            LocalModelDirectory,
            (int)OrchestratorPort,
            LocalServerExecutablePath);

        PopulateRows(CoreDependencyRows, report.CoreDependencies);
        PopulateRows(ProviderAddonRows, report.ProviderAddons);
        PopulateRows(CoreInstallGuideRows, report.CoreInstallGuide);
        PopulateRows(SdkInstallGuideRows, report.SdkInstallGuide);
        PopulateRows(ProviderRequirementRows, report.ProviderRequirements.Where(row =>
            row.Provider.Equals(SelectedCloudProvider, StringComparison.OrdinalIgnoreCase)
            || row.Provider.Equals("Local model route", StringComparison.OrdinalIgnoreCase)).ToList());
        PopulateRows(HardRequiredPathRows, report.HardRequiredPaths);
        PopulateRows(RuntimePathRows, report.RuntimePaths);
        PopulateRows(LocalModelCapabilityRows, report.LocalModelCapabilities);

        var likelyVisionCount = report.LocalModelCapabilities.Count(x =>
            x.VisionSupport.Contains("vision", StringComparison.OrdinalIgnoreCase));

        DependencySummary = report.HardwareSummary +
                            $"\nActive provider: {SelectedCloudProvider} | Orchestrator port: {OrchestratorPort}" +
                            $"\nLocal models with possible vision support: {likelyVisionCount}";
        DependencyScopeHelpText =
            $"Scope: provider={SelectedCloudProvider}, model-directory={LocalModelDirectory}, port={OrchestratorPort}, scanned-at={DateTime.Now:G}.";

        ConnectionHealthText = "Environment check complete. Open the Environment tab for details.";
        UpdateDependencyRiskPanels();
        StatusMessage = "Environment check updated.";
    }

    partial void OnSelectedMainTabIndexChanged(int value)
    {
        if (value == ModelProfilesTabIndex)
        {
            RefreshLocalProfilePresentationAfterRuntimeChange();
        }

        if (value == 4 && !_dependencyTabInitialized)
        {
            _dependencyTabInitialized = true;
            _ = CheckDependenciesAsync();
        }
    }

    private static void SafeUiInvoke(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.UIThread.Post(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CancelLocalRuntimeMonitors()
    {
        try
        {
            _localReadinessPollCts?.Cancel();
        }
        catch
        {
        }

        _localReadinessPollCts?.Dispose();
        _localReadinessPollCts = null;
        StopLocalLaunchCountdownMonitor();
    }

    [RelayCommand]
    private async Task LaunchCloudAsync()
        => await LaunchCloudRouteAsync("Selection 1", SelectedCloudProvider, SelectedCloudProfile, SelectedCloudVariant);

    [RelayCommand]
    private async Task LaunchSelection1CloudAsync()
        => await LaunchCloudRouteAsync("Selection 1", SelectedCloudProvider, SelectedCloudProfile, SelectedCloudVariant);

    [RelayCommand]
    private async Task LaunchSelection2CloudAsync()
        => await LaunchCloudRouteAsync("Selection 2", SelectedSelection2CloudProvider, SelectedSelection2CloudProfile, SelectedSelection2CloudVariant);

    private async Task LaunchCloudRouteAsync(string slotLabel, string provider, string model, string variant)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            ConnectionHealthText = slotLabel + ": expand the cloud profile in Model Profiles so AI-FluxMux has both a provider and a model id.";
            StatusMessage = "Validate to endpoint did not succeed. Provider and model must both be selected.";
            CloudRouteIndicatorState = RouteIndicatorState.Fault;
            return;
        }

        SaveCurrentSelections();
        CancelLocalRuntimeMonitors();
        var dualHotAlongsideLocal = RequestRoutingEnabled && _runtimeService.IsManagedLocalAlive();
        StartLocalLaunchCountdownMonitor(
            $"{provider} / {model}",
            string.Empty,
            includeLoadEstimate: false,
            dualHotAlongsideLocal: dualHotAlongsideLocal);
        try
        {
            if (!RequestRoutingEnabled && _runtimeService.IsManagedLocalAlive())
            {
                UpdateLocalLaunchPhase("AI-FluxMux is stopping llama-server so this cloud profile can use the public endpoint");
                await _runtimeService.StopLocalAsync();
                LocalRouteIndicatorState = RouteIndicatorState.Off;
            }
            else if (dualHotAlongsideLocal)
            {
                UpdateLocalLaunchPhase("AI-FluxMux is keeping llama-server loaded; adding cloud for hot routing");
            }
            else
            {
                UpdateLocalLaunchPhase("AI-FluxMux is switching to this cloud profile");
            }

            CloudRouteIndicatorState = RouteIndicatorState.Amber;
            RememberMountedCloud(provider, model, variant);

            var result = await _runtimeService.LaunchCloudAsync(provider, model, progressReporter: UpdateLocalLaunchPhase);
            _lastHealthDetail = result.Details;
            ConnectionHealthText = result.Details;
            StatusMessage = result.Status;
            if (result.IsSuccess)
            {
                ApplySuccessfulCloudRouteLaunch();
            }
            else
            {
                CloudRouteIndicatorState = RouteIndicatorState.Fault;
                NoteCloudProfileConnectionFailure(
                    provider,
                    model,
                    variant,
                    FirstNonEmpty(result.Details, result.Status));
            }

            UpdateRoutingPoolStatus();
        }
        finally
        {
            StopLocalLaunchCountdownMonitor();
        }
    }

    [RelayCommand]
    private async Task CheckCloudHealthAsync()
    {
        await CheckCloudRouteHealthAsync("Selection 1", SelectedCloudProvider, SelectedCloudProfile);
    }

    [RelayCommand]
    private async Task CheckSelection1CloudHealthAsync()
    {
        await CheckCloudRouteHealthAsync("Selection 1", SelectedCloudProvider, SelectedCloudProfile);
    }

    [RelayCommand]
    private async Task CheckSelection2CloudHealthAsync()
    {
        await CheckCloudRouteHealthAsync("Selection 2", SelectedSelection2CloudProvider, SelectedSelection2CloudProfile);
    }

    private async Task<bool> CheckCloudRouteHealthAsync(string slotLabel, string provider, string model, string variant = BaseVariantDisplayName)
    {
        StatusMessage = $"{slotLabel}: validating cloud route to endpoint...";

        var result = await _runtimeService.ValidateCloudEndpointAsync(provider, model);
        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = result.Status;
        CloudRouteIndicatorState = result.IsSuccess
            ? RouteIndicatorState.Live
            : IsRouteNotStartedResult(result)
                ? RouteIndicatorState.Off
                : RouteIndicatorState.Fault;
        if (result.IsSuccess)
        {
            RememberMountedCloud(provider, model, variant);
            StartMountedEndpointHealthMonitor();
            MarkMatchingDefaultProfilesValidated(
                routeType: "cloud",
                provider: provider,
                cloudModel: model,
                cloudVariant: variant,
                localModel: string.Empty,
                localVariant: string.Empty);
            if (!NormalizeVariantName(variant).Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                MarkMatchingDefaultProfilesValidated(
                    routeType: "cloud",
                    provider: provider,
                    cloudModel: model,
                    cloudVariant: BaseVariantDisplayName,
                    localModel: string.Empty,
                    localVariant: string.Empty);
            }
        }
        else if (!IsRouteNotStartedResult(result)
            && (_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(_mountedRouteType)))
        {
            RememberMountedCloud(provider, model, variant);
        }

        return result.IsSuccess;
    }

    [RelayCommand]
    private async Task StopCloudAsync()
        => await StopCloudRouteAsync("Selection 1");

    [RelayCommand]
    private async Task StopSelection1CloudAsync() => await StopCloudRouteAsync("Selection 1");

    [RelayCommand]
    private async Task StopSelection2CloudAsync() => await StopCloudRouteAsync("Selection 2");

    private async Task StopCloudRouteAsync(string slotLabel)
    {
        StatusMessage = $"{slotLabel}: stopping the active cloud route...";
        var result = await _runtimeService.StopCloudAsync();
        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = result.Status;
        CloudRouteIndicatorState = RouteIndicatorState.Off;
        if (RequestRoutingEnabled && !string.IsNullOrWhiteSpace(_runningLocalModel))
        {
            _mountedCloudProvider = string.Empty;
            _mountedCloudModel = string.Empty;
            _mountedCloudVariant = string.Empty;
            _mountedRouteType = "Local";
            BindActiveQuickSelectSlotToMountedRoute();
            RefreshRouteSlotIndicators();
        }
        else if (_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            ClearMountedRoute();
        }

        UpdateRoutingPoolStatus();
    }

    [RelayCommand]
    private async Task RestartCloudAsync()
        => await RestartCloudRouteAsync("Selection 1", SelectedCloudProvider, SelectedCloudProfile);

    [RelayCommand]
    private async Task RestartSelection1CloudAsync()
        => await RestartCloudRouteAsync("Selection 1", SelectedCloudProvider, SelectedCloudProfile);

    [RelayCommand]
    private async Task RestartSelection2CloudAsync()
        => await RestartCloudRouteAsync("Selection 2", SelectedSelection2CloudProvider, SelectedSelection2CloudProfile);

    private async Task RestartCloudRouteAsync(string slotLabel, string provider, string model)
    {
        SaveCurrentSelections();
        StatusMessage = $"{slotLabel}: restarting cloud route...";
        CloudRouteIndicatorState = RouteIndicatorState.Amber;

        var result = await _runtimeService.RestartCloudAsync(provider, model);
        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = result.Status;
        var variant = provider.Equals(_mountedCloudProvider, StringComparison.OrdinalIgnoreCase)
            && model.Equals(_mountedCloudModel, StringComparison.OrdinalIgnoreCase)
            ? FirstNonEmpty(_mountedCloudVariant, BaseVariantDisplayName)
            : BaseVariantDisplayName;
        RememberMountedCloud(provider, model, variant);
        if (result.IsSuccess)
        {
            ApplySuccessfulCloudRouteLaunch();
        }
        else
        {
            CloudRouteIndicatorState = RouteIndicatorState.Fault;
            NoteCloudProfileConnectionFailure(
                provider,
                model,
                variant,
                FirstNonEmpty(result.Details, result.Status));
        }
    }

    private void ApplySuccessfulCloudRouteLaunch()
    {
        CloudRouteIndicatorState = RouteIndicatorState.Live;
        StartMountedEndpointHealthMonitor();
    }

    [RelayCommand]
    private async Task LaunchLocalAsync()
    {
        var target = ResolveActiveLocalTarget();
        await LaunchLocalRouteAsync("Active local slot", target.Model, target.Variant);
    }

    [RelayCommand]
    private async Task LaunchSelection1LocalAsync()
        => await LaunchLocalRouteAsync(
            "Selection 1",
            FirstNonEmpty(SelectedSelection1LocalProfile, SelectedLocalProfile),
            FirstNonEmpty(SelectedSelection1LocalVariant, FirstNonEmpty(SelectedLocalVariant, BaseVariantDisplayName)));

    [RelayCommand]
    private async Task LaunchSelection2LocalAsync()
        => await LaunchLocalRouteAsync(
            "Selection 2",
            FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile),
            FirstNonEmpty(SelectedLocalVariant, FirstNonEmpty(SelectedSelection1LocalVariant, BaseVariantDisplayName)));

    private async Task LaunchLocalRouteAsync(string slotLabel, string model, string variant)
        => await RunLocalRouteAsync(slotLabel, model, variant, restart: false);

    private async Task RestartLocalRouteAsync(string slotLabel, string model, string variant)
        => await RunLocalRouteAsync(slotLabel, model, variant, restart: true);

    private async Task RunLocalRouteAsync(string slotLabel, string model, string variant, bool restart)
    {
        SaveCurrentSelections();
        var sameLocalProfileAlreadyRunning = !restart
            && _runtimeService.IsManagedLocalProfileLive(model, variant);
        if (sameLocalProfileAlreadyRunning)
        {
            RememberMountedLocal(model, variant);
            LocalRouteIndicatorState = RouteIndicatorState.Live;
            RegisterCompatibleQuickSelectLocalOverlays(model);
            StatusMessage = $"{slotLabel}: this profile is already running on the public endpoint.";
            StartMountedEndpointHealthMonitor();
            UpdateRoutingPoolStatus();
            return;
        }

        if (!restart && TryAttachCompatibleLocalOverlay(slotLabel, model, variant))
        {
            return;
        }

        if (!restart && !RequestRoutingEnabled)
        {
            StatusMessage = $"{slotLabel}: stopping active cloud route before local launch...";
            await _runtimeService.StopCloudAsync();
            CloudRouteIndicatorState = RouteIndicatorState.Off;
            StatusMessage = $"{slotLabel}: launching local model endpoint...";
        }
        else if (!restart)
        {
            StatusMessage = $"{slotLabel}: launching local model endpoint...";
        }
        else
        {
            StatusMessage = $"{slotLabel}: restarting local model endpoint...";
        }

        if (ResolveLocalModelPath(model) is null)
        {
            RememberMountedLocal(model, variant);
            LocalRouteIndicatorState = RouteIndicatorState.Fault;
            StatusMessage = $"{slotLabel}: the local model for '{model}' is not in the model folder. Restore the local model, or delete this profile from Model Profiles.";
            return;
        }

        RememberMountedLocal(model, variant);
        LocalRouteIndicatorState = RouteIndicatorState.Amber;
        _runtimeService.ResetLocalRequestOverlays();

        var launchStartedUtc = DateTime.UtcNow;
        StartLocalLaunchCountdownMonitor(model, variant);
        var keepCountdownRunning = false;
        var vision = GetLocalVisionLaunchSettings(model, variant);

        try
        {
            var result = restart
                ? await _runtimeService.RestartLocalAsync(
                    model,
                    variant,
                    LocalModelDirectory,
                    (int)OrchestratorPort,
                    vision.Enabled,
                    vision.ProjectorPath,
                    vision.MaxImageEdge,
                    progressReporter: UpdateLocalLaunchPhase)
                : await _runtimeService.LaunchLocalAsync(
                    model,
                    variant,
                    LocalModelDirectory,
                    (int)OrchestratorPort,
                    vision.Enabled,
                    vision.ProjectorPath,
                    vision.MaxImageEdge,
                    progressReporter: UpdateLocalLaunchPhase);

            ApplyLocalLaunchOutcome(result, model, variant, launchStartedUtc, out keepCountdownRunning);
        }
        finally
        {
            if (!keepCountdownRunning)
            {
                StopLocalLaunchCountdownMonitor();
            }
        }
    }

    private void ApplyLocalLaunchOutcome(
        RuntimeActionResult result,
        string model,
        string variant,
        DateTime launchStartedUtc,
        out bool keepCountdownRunning)
    {
        UpdateLocalLaunchTelemetrySummary();
        if (result.IsSuccess && result.IsLocalRouteReady)
        {
            ApplyLocalPublicEndpointSuccess(
                model,
                variant,
                result.Details,
                launchElapsedMs: null,
                recordLaunchTelemetry: false);
        }
        else
        {
            _lastHealthDetail = result.Details;
            ConnectionHealthText = result.Details;
            StatusMessage = result.Status;
            LocalRouteIndicatorState = result.IsSuccess ? RouteIndicatorState.Amber : RouteIndicatorState.Fault;
            RememberMountedLocal(model, variant);
            if (!result.IsSuccess && !result.IsLocalRouteWarming)
            {
                NoteLocalProfileConnectionFailure(
                    model,
                    variant,
                    FirstNonEmpty(result.Details, result.Status));
            }
        }

        keepCountdownRunning = result.IsLocalRouteWarming
            || (result.IsSuccess
                && result.Status.Equals(LocalLaunchStatus.BackendReady, StringComparison.OrdinalIgnoreCase)
                && FluxMuxRuntimeService.IsTransientLocalWarmupFailure(result.Details));
        if (keepCountdownRunning)
        {
            StartLocalReadinessMonitor((int)OrchestratorPort, model, variant, launchStartedUtc);
        }
    }

    private void ApplyLocalPublicEndpointSuccess(
        string model,
        string variant,
        string details,
        int? launchElapsedMs,
        bool recordLaunchTelemetry,
        bool alsoMarkBaseVariant = false)
    {
        _lastHealthDetail = details;
        ConnectionHealthText = details;
        if (recordLaunchTelemetry && launchElapsedMs is int elapsedMs && elapsedMs > 0)
        {
            _runtimeService.RecordLocalLaunchTelemetry(model, variant, elapsedMs, "ready");
            UpdateLocalLaunchTelemetrySummary();
        }

        StatusMessage = LocalLaunchStatus.RouteReady;
        LocalRouteIndicatorState = RouteIndicatorState.Live;
        RememberMountedLocal(model, variant);
        var isDefaultVariant = NormalizeVariantName(variant).Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(model) && isDefaultVariant)
        {
            MarkLocalDefaultProfilesValidated(model, variant ?? string.Empty, alsoMarkBaseVariant);
        }

        if (_activeQuickSelectSlot is { IsLocalRoute: true } activeSlot
            && activeSlot.LocalModel.Equals(model, StringComparison.OrdinalIgnoreCase)
            && NormalizeVariantName(activeSlot.LocalVariant).Equals(NormalizeVariantName(variant), StringComparison.OrdinalIgnoreCase))
        {
            MarkQuickSelectSlotValidated(activeSlot);
        }

        StartMountedEndpointHealthMonitor();
        StopLocalLaunchCountdownMonitor();
        RegisterCompatibleQuickSelectLocalOverlays(model);
        UpdateRoutingPoolStatus();
        RefreshLocalProfilePresentationAfterRuntimeChange(model, variant);
    }

    private bool TryAttachCompatibleLocalOverlay(string slotLabel, string model, string variant)
    {
        if (!_runtimeService.IsManagedLocalAlive())
        {
            return false;
        }

        var live = _runtimeService.GetManagedLocalIdentity();
        if (string.IsNullOrWhiteSpace(live.Model)
            || !live.Model.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requestedSettings = ResolveLocalVariantObjectFromConfig(model, variant);
        var liveSettings = ResolveLocalVariantObjectFromConfig(live.Model, live.Variant);
        if (!GetLocalLaunchCriticalFingerprint(requestedSettings)
            .Equals(GetLocalLaunchCriticalFingerprint(liveSettings), StringComparison.Ordinal))
        {
            return false;
        }

        if (GetLiveLocalProfileSettings(variant, model) is null
            || GetLiveLocalProfileSettings(live.Variant, live.Model) is null)
        {
            return false;
        }

        RememberMountedLocal(model ?? string.Empty, variant);
        LocalRouteIndicatorState = RouteIndicatorState.Live;
        RegisterCompatibleQuickSelectLocalOverlays(model);
        var overlayName = NormalizeVariantName(variant);
        StatusMessage = $"{slotLabel}: kept the running local model and attached '{overlayName}' request settings (temperature / max tokens). Context, GPU layers, KV cache, and template already match.";
        StartMountedEndpointHealthMonitor();
        UpdateRoutingPoolStatus();
        return true;
    }

    private void RegisterLocalRequestOverlay(string? model, string? variant)
    {
        var settings = ResolveLocalVariantObjectFromConfig(model, variant ?? string.Empty);
        var temperature = ParseDouble(settings["LocalTemperature"]?.ToString(), 0.3);
        var maxTokens = ParseInt(settings["OverrideMaxTokens"]?.ToString(), 2048);
        _runtimeService.UpsertLocalRequestOverlay(NormalizeVariantName(variant), temperature, maxTokens);
    }

    private void RegisterCompatibleQuickSelectLocalOverlays(string? model)
    {
        var live = _runtimeService.GetManagedLocalIdentity();
        var liveModel = FirstNonEmpty(live.Model, model);
        var liveSettings = GetLiveLocalProfileSettings(live.Variant, liveModel)
            ?? ResolveLocalVariantObjectFromConfig(liveModel, live.Variant);
        var liveFingerprint = GetLocalLaunchCriticalFingerprint(liveSettings);
        if (string.IsNullOrWhiteSpace(liveModel) || string.IsNullOrWhiteSpace(liveFingerprint))
        {
            RegisterLocalRequestOverlay(model, FirstNonEmpty(live.Variant, string.Empty));
            return;
        }

        foreach (var slot in QuickSelectSlots)
        {
            if (!slot.HasSavedAssignment || !slot.IsLocalRoute)
            {
                continue;
            }

            if (!slot.LocalModel.Equals(liveModel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var settings = GetLiveLocalProfileSettings(slot.LocalVariant, slot.LocalModel);
            if (settings is null)
            {
                continue;
            }

            if (!GetLocalLaunchCriticalFingerprint(settings).Equals(liveFingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            RegisterLocalRequestOverlay(slot.LocalModel, slot.LocalVariant);
        }

        if (!string.IsNullOrWhiteSpace(live.Variant))
        {
            RegisterLocalRequestOverlay(liveModel, live.Variant);
        }
    }

    private void MarkLocalDefaultProfilesValidated(string model, string variant, bool alsoMarkBaseVariant = false)
    {
        MarkMatchingDefaultProfilesValidated(
            routeType: "local",
            provider: string.Empty,
            cloudModel: string.Empty,
            cloudVariant: string.Empty,
            localModel: model,
            localVariant: variant);
        if (alsoMarkBaseVariant
            && !NormalizeVariantName(variant).Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            MarkMatchingDefaultProfilesValidated(
                routeType: "local",
                provider: string.Empty,
                cloudModel: string.Empty,
                cloudVariant: string.Empty,
                localModel: model,
                localVariant: BaseVariantDisplayName);
        }
    }

    private void StartLocalReadinessMonitor(int port, string model, string variant, DateTime launchStartedUtc)
    {
        _localReadinessPollCts?.Cancel();
        _localReadinessPollCts = new CancellationTokenSource();
        var cts = _localReadinessPollCts;
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(180);
                var publicEndpointReady = false;
                while (!cts.Token.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(4000, cts.Token);
                    var health = await _runtimeService.CheckLocalHealthAsync(port, cts.Token);
                    if (!(health.IsSuccess || health.Status == "Local health check passed"))
                    {
                        SafeUiInvoke(() =>
                        {
                            if (_localLaunchCountdownCts is null)
                            {
                                StatusMessage = "Local route warming up... waiting for health check";
                            }
                        });
                        continue;
                    }

                    var publicProbe = await _runtimeService.TimeShortLocalReplyThroughPublicEndpointAsync(
                        port,
                        model,
                        cts.Token,
                        requestTimeoutMs: FluxMuxRuntimeService.PublicEndpointReadyProbeTimeoutMs);
                    if (!publicProbe.IsSuccess)
                    {
                        SafeUiInvoke(() =>
                        {
                            _lastHealthDetail = publicProbe.Details;
                            ConnectionHealthText = publicProbe.Details;
                            StatusMessage = "llama-server is ready on the daemon port, but the Port check did not succeed yet.";
                            LocalRouteIndicatorState = RouteIndicatorState.Amber;
                        });
                        continue;
                    }

                    var elapsedMs = (int)Math.Min(int.MaxValue, (DateTime.UtcNow - launchStartedUtc).TotalMilliseconds);
                    SafeUiInvoke(() => ApplyLocalPublicEndpointSuccess(
                        model,
                        variant,
                        publicProbe.Details,
                        elapsedMs,
                        recordLaunchTelemetry: true));
                    publicEndpointReady = true;
                    break;
                }

                if (!publicEndpointReady)
                {
                    SafeUiInvoke(() =>
                    {
                        if (!cts.Token.IsCancellationRequested)
                        {
                            StatusMessage = LocalLaunchStatus.StillInProgress;
                            StopLocalLaunchCountdownMonitor();
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    [RelayCommand]
    private async Task CheckLocalHealthAsync()
    {
        await CheckLocalRouteHealthAsync("Active local slot");
    }

    [RelayCommand]
    private async Task CheckSelection1LocalHealthAsync()
    {
        await CheckLocalRouteHealthAsync("Selection 1");
    }

    [RelayCommand]
    private async Task CheckSelection2LocalHealthAsync()
    {
        await CheckLocalRouteHealthAsync("Selection 2");
    }

    private async Task<bool> CheckLocalRouteHealthAsync(
        string slotLabel,
        string model = "",
        string variant = BaseVariantDisplayName)
    {
        StatusMessage = $"{slotLabel}: validating local route to endpoint...";

        var probeModel = FirstNonEmpty(model, FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile));
        var result = await _runtimeService.TimeShortLocalReplyThroughPublicEndpointAsync(
            (int)OrchestratorPort,
            probeModel);
        if (result.IsSuccess)
        {
            ApplyLocalPublicEndpointSuccess(
                FirstNonEmpty(model, probeModel),
                variant,
                result.Details,
                launchElapsedMs: null,
                recordLaunchTelemetry: false,
                alsoMarkBaseVariant: !string.IsNullOrWhiteSpace(model));
            return true;
        }

        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = FirstNonEmpty(result.Details, "The public endpoint check did not succeed.");
        LocalRouteIndicatorState = RouteIndicatorState.Fault;
        return false;
    }

    [RelayCommand]
    private async Task CheckConnectionHealthAsync()
    {
        if (string.IsNullOrWhiteSpace(_mountedRouteType))
        {
            PublishHealthReport(
                "Health: nothing is mounted.",
                "Nothing is mounted in AI-FluxMux. Launch a Quick Select model profile, or Validate to endpoint on Model Profiles.",
                entity: "AI-FluxMux");
            return;
        }

        ShowGpuAdapterStatusColumn = true;
        var profileName = FormatMountedRouteProfileName();
        _healthReportDismissed = false;
        StatusMessage = "Health: checking " + profileName + "...";

        if (_mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            var localHealth = await _runtimeService.CheckLocalHealthAsync((int)OrchestratorPort);
            var cloudHealth = await _runtimeService.CheckCloudHealthAsync(_mountedCloudProvider, _mountedCloudModel);
            LocalRouteIndicatorState = localHealth.IsSuccess
                ? RouteIndicatorState.Live
                : IsRouteNotStartedResult(localHealth)
                    ? RouteIndicatorState.Off
                    : localHealth.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase)
                        ? RouteIndicatorState.Amber
                        : RouteIndicatorState.Fault;
            CloudRouteIndicatorState = cloudHealth.IsSuccess
                ? RouteIndicatorState.Live
                : IsRouteNotStartedResult(cloudHealth)
                    ? RouteIndicatorState.Off
                    : RouteIndicatorState.Fault;
            var bothOk = localHealth.IsSuccess && cloudHealth.IsSuccess;
            var report = localHealth.Status + Environment.NewLine + localHealth.Details
                + Environment.NewLine + cloudHealth.Status + Environment.NewLine + cloudHealth.Details;
            await PublishHealthReportWithHarnessWebAsync(
                bothOk
                    ? "Health passed: the local model and the cloud model both replied."
                    : HeadlineFromHealth(localHealth, cloudHealth),
                report,
                modelsOk: bothOk,
                entity: "AI-FluxMux");
            RefreshRouteSlotIndicators();
            return;
        }

        if (_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            var cloud = await _runtimeService.CheckCloudHealthAsync(_mountedCloudProvider, _mountedCloudModel);
            CloudRouteIndicatorState = cloud.IsSuccess
                ? RouteIndicatorState.Live
                : IsRouteNotStartedResult(cloud)
                    ? RouteIndicatorState.Off
                    : RouteIndicatorState.Fault;
            await PublishHealthReportWithHarnessWebAsync(
                cloud.IsSuccess
                    ? "Health passed: " + profileName
                    : IsRouteNotStartedResult(cloud)
                        ? "Health: that cloud model profile is not mounted yet."
                        : FirstNonEmpty(cloud.Status, "Health needs attention: " + profileName),
                FirstNonEmpty(cloud.Details, cloud.Status),
                modelsOk: cloud.IsSuccess,
                entity: "cloud model");
            return;
        }

        var local = await _runtimeService.CheckLocalHealthAsync((int)OrchestratorPort);
        LocalGenerationTimingResult? publicProbe = null;
        if (local.IsSuccess || local.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase))
        {
            publicProbe = await _runtimeService.TimeShortLocalReplyThroughPublicEndpointAsync(
                (int)OrchestratorPort,
                FirstNonEmpty(_runningLocalModel, SelectedLocalProfile));
        }

        var publicOk = publicProbe is { IsSuccess: true };
        var attention = local.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase);
        if ((local.IsSuccess || attention) && publicOk)
        {
            LocalRouteIndicatorState = attention ? RouteIndicatorState.Amber : RouteIndicatorState.Live;
            await PublishHealthReportWithHarnessWebAsync(
                attention
                    ? "Health needs attention: a recent request did not fit llama-server Context. Raise Context or load a model profile with a larger Context."
                    : "Health passed: " + profileName,
                attention ? local.Details : local.Details + Environment.NewLine + publicProbe!.Details,
                modelsOk: !attention,
                entity: "llama-server");
            return;
        }

        if (local.IsSuccess || attention)
        {
            LocalRouteIndicatorState = RouteIndicatorState.Amber;
            var miss = LocalHealthGuidance.BuildPublicGatewayMiss(
                local.Details,
                (int)OrchestratorPort);
            PublishHealthReport(
                "Health needs attention: the AI-FluxMux public address did not reply.",
                miss + (string.IsNullOrWhiteSpace(publicProbe?.Details) ? string.Empty : Environment.NewLine + publicProbe!.Details),
                entity: "AI-FluxMux");
            return;
        }

        if (IsRouteNotStartedResult(local))
        {
            LocalRouteIndicatorState = RouteIndicatorState.Off;
            PublishHealthReport(
                "Health: " + profileName + " is no longer running.",
                local.Details,
                entity: "llama-server");
            return;
        }

        LocalRouteIndicatorState = RouteIndicatorState.Fault;
        PublishHealthReport(
            local.Status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                ? "Health needs attention: llama-server is not answering. Launch the local model profile again."
                : "Health needs attention: " + profileName,
            local.Details,
            entity: "llama-server");
    }

    private static string HeadlineFromHealth(RuntimeActionResult localHealth, RuntimeActionResult cloudHealth)
    {
        if (!localHealth.IsSuccess)
        {
            return localHealth.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase)
                ? "Health needs attention: llama-server Context overflow or a llama-server warning. See the note under Diagnostics."
                : "Health needs attention: llama-server did not pass Health. See the note under Diagnostics.";
        }

        if (!cloudHealth.IsSuccess)
        {
            return "Health needs attention: the cloud model did not pass Health. See the note under Diagnostics.";
        }

        return "Health: routing is on; the local model or the cloud model needs attention.";
    }

    [RelayCommand]
    private async Task RunSwitchBenchmarkAsync()
    {
        SaveCurrentSelections();
        if (!ValidateSwitchBenchmarkSelections(out var validationError))
        {
            var detail = string.IsNullOrWhiteSpace(validationError)
                ? "Switch benchmark selection is not valid."
                : validationError;
            SwitchBenchmarkResultText = "Switch benchmark not started.\n\n" + detail;
            SwitchBenchmarkSummaryText = "Benchmark blocked by selection validation.";
            SwitchBenchmarkRecommendationText = detail;
            SwitchBenchmarkAdvancedDetailsText = detail;
            StatusMessage = "Switch benchmark blocked by selection validation";
            return;
        }

        var profileA = SelectedSwitchBenchmarkProfileA!;
        var profileB = SelectedSwitchBenchmarkProfileB!;
        if (SelectedSwitchBenchmarkOrder.Equals("Profile B -> Profile A -> Profile B", StringComparison.Ordinal))
        {
            (profileA, profileB) = (profileB, profileA);
        }

        StatusMessage = "Stopping active routes before switch benchmark...";
        await _runtimeService.ShutdownManagedRuntimesAsync();
        CloudRouteIndicatorState = RouteIndicatorState.Off;
        LocalRouteIndicatorState = RouteIndicatorState.Off;
        ClearMountedRoute();
        StatusMessage = "Running model profile switch benchmark...";
        _benchmarkStatusActive = true;
        _benchmarkProgressText = StatusMessage;
        SwitchBenchmarkResultText = "Switch benchmark running...";
        SwitchBenchmarkSummaryText = "Test underway. Waiting for benchmark tiers to complete.";
        SwitchBenchmarkRecommendationText = "Test underway. Final recommendation will appear when the benchmark finishes.";
        SwitchBenchmarkAdvancedDetailsText = "Benchmark in progress. Detailed tier results will replace this text after completion.";

        try
        {
            void UpdateBenchmarkProgress(string message)
            {
                var normalized = message ?? string.Empty;
                var cloudState = RouteIndicatorState.Off;
                var localState = RouteIndicatorState.Off;
                var profileAPhase = normalized.Contains("Profile A", StringComparison.OrdinalIgnoreCase);
                var profileBPhase = normalized.Contains("Profile B", StringComparison.OrdinalIgnoreCase);
                var activeRouteType = profileAPhase ? profileA.RouteType : profileBPhase ? profileB.RouteType : string.Empty;
                var localPhase = activeRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase);
                var cloudPhase = activeRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase);

                if (cloudPhase)
                {
                    cloudState = normalized.Contains("checking", StringComparison.OrdinalIgnoreCase) || normalized.Contains("probe", StringComparison.OrdinalIgnoreCase)
                        ? RouteIndicatorState.Amber
                        : RouteIndicatorState.Amber;
                }

                if (localPhase)
                {
                    localState = normalized.Contains("checking", StringComparison.OrdinalIgnoreCase) || normalized.Contains("probe", StringComparison.OrdinalIgnoreCase)
                        ? RouteIndicatorState.Amber
                        : RouteIndicatorState.Amber;
                }

                if (normalized.Contains("stopping active routes", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("preparing Profile A route", StringComparison.OrdinalIgnoreCase))
                {
                    cloudState = RouteIndicatorState.Off;
                    localState = RouteIndicatorState.Off;
                }

                if (localPhase)
                {
                    var processingContext = normalized.Contains("checking", StringComparison.OrdinalIgnoreCase)
                                            || normalized.Contains("probe", StringComparison.OrdinalIgnoreCase);
                    if (processingContext)
                    {
                        UpdateLocalLaunchPhase("llama-server is processing context");
                    }
                    else if (_localLaunchCountdownCts is null)
                    {
                        var localProfile = profileAPhase ? profileA : profileB;
                        StartLocalLaunchCountdownMonitor(localProfile.Model, localProfile.Variant);
                    }
                }
                else if (cloudPhase)
                {
                    var cloudProfile = profileAPhase ? profileA : profileB;
                    if (_localLaunchCountdownCts is null)
                    {
                        StartLocalLaunchCountdownMonitor(cloudProfile.DisplayName, string.Empty, includeLoadEstimate: false);
                    }

                    var processingContext = normalized.Contains("checking", StringComparison.OrdinalIgnoreCase)
                                            || normalized.Contains("probe", StringComparison.OrdinalIgnoreCase);
                    UpdateLocalLaunchPhase(processingContext ? "llama-server is processing context" : "AI-FluxMux is switching the cloud model");
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _benchmarkProgressText = normalized;
                    RefreshCompositeStatusMessage();
                    UpdateRouteLiveIndicators(cloudState, localState);
                });
            }

            var localVision = GetLocalVisionLaunchSettings(SelectedLocalProfile, SelectedLocalVariant);
            var result = await _runtimeService.RunSwitchBenchmarkAsync(
                SelectedCloudProvider,
                SelectedCloudProfile,
                SelectedLocalProfile,
                SelectedLocalVariant,
                LocalModelDirectory,
                (int)OrchestratorPort,
                localVision.Enabled,
                localVision.ProjectorPath,
                localVision.MaxImageEdge,
                SwitchBenchmarkFocusText,
                progressReporter: UpdateBenchmarkProgress,
                profileA: new SwitchBenchmarkRouteSelection(profileA.RouteType, profileA.Provider, profileA.Model, profileA.Variant, profileA.DisplayName),
                profileB: new SwitchBenchmarkRouteSelection(profileB.RouteType, profileB.Provider, profileB.Model, profileB.Variant, profileB.DisplayName));

            _lastHealthDetail = result.Details;
            ConnectionHealthText = result.Details;
            SwitchBenchmarkResultText = "Test complete.\n\n" + result.Details;
            UpdateSwitchBenchmarkDisplay(result);
            _benchmarkStatusActive = false;
            _benchmarkProgressText = string.Empty;
            StopLocalLaunchCountdownMonitor();
            StatusMessage = result.IsSuccess ? "Switch benchmark test complete" : result.Status;
        }
        catch (Exception ex)
        {
            var detail = "Switch benchmark terminated unexpectedly: " + ex.Message;
            _lastHealthDetail = detail;
            ConnectionHealthText = detail;
            SwitchBenchmarkResultText = detail;
            _benchmarkStatusActive = false;
            _benchmarkProgressText = string.Empty;
            StopLocalLaunchCountdownMonitor();
            StatusMessage = "Switch benchmark failed";
        }
    }

    [RelayCommand]
    private async Task StopLocalAsync()
        => await StopLocalRouteAsync("Active local slot");

    [RelayCommand]
    private async Task StopSelection1LocalAsync() => await StopLocalRouteAsync("Selection 1");

    [RelayCommand]
    private async Task StopSelection2LocalAsync() => await StopLocalRouteAsync("Selection 2");

    private async Task StopLocalRouteAsync(string slotLabel)
    {
        CancelLocalRuntimeMonitors();
        StatusMessage = $"{slotLabel}: stopping the active managed local runtime...";
        var stoppedModel = FirstNonEmpty(_runningLocalModel, _mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase) ? SelectedLocalProfile : string.Empty);
        var stoppedVariant = FirstNonEmpty(_runningLocalVariant, BaseVariantDisplayName);
        RuntimeActionResult result;
        try
        {
            result = await _runtimeService.StopLocalAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = slotLabel + ": stop failed, but AI-FluxMux is still running. " + ex.Message;
            ConnectionHealthText = StatusMessage;
            return;
        }
        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = result.Status;
        LocalRouteIndicatorState = RouteIndicatorState.Off;
        if (RequestRoutingEnabled && !string.IsNullOrWhiteSpace(_mountedCloudProvider))
        {
            _runningLocalModel = string.Empty;
            _runningLocalVariant = string.Empty;
            _mountedRouteType = "Cloud";
            BindActiveQuickSelectSlotToMountedRoute();
            RefreshRouteSlotIndicators();
        }
        else if (_mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || _mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            ClearMountedRoute();
        }
        else
        {
            _runningLocalModel = string.Empty;
            _runningLocalVariant = string.Empty;
        }

        UpdateRoutingPoolStatus();
        RefreshLocalProfilePresentationAfterRuntimeChange(stoppedModel, stoppedVariant);
        if (string.IsNullOrWhiteSpace(_runningLocalModel))
        {
            StopManagedHarnessWebForHygiene(
                "Harness web stopped because the local model was unloaded.");
        }
    }

    [RelayCommand]
    private async Task RestartLocalAsync()
    {
        var target = ResolveActiveLocalTarget();
        await RestartLocalRouteAsync("Active local slot", target.Model, target.Variant);
    }

    [RelayCommand]
    private async Task RestartSelection1LocalAsync()
        => await RestartLocalRouteAsync(
            "Selection 1",
            FirstNonEmpty(SelectedSelection1LocalProfile, SelectedLocalProfile),
            FirstNonEmpty(SelectedSelection1LocalVariant, FirstNonEmpty(SelectedLocalVariant, BaseVariantDisplayName)));

    [RelayCommand]
    private async Task RestartSelection2LocalAsync()
        => await RestartLocalRouteAsync(
            "Selection 2",
            FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile),
            FirstNonEmpty(SelectedLocalVariant, FirstNonEmpty(SelectedSelection1LocalVariant, BaseVariantDisplayName)));

    [RelayCommand]
    private async Task BrowseLocalServerExecutableAsync()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;
        if (owner is null || owner.StorageProvider is null)
        {
            StatusMessage = "Server executable picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            var currentDirectory = Path.GetDirectoryName(LocalServerExecutablePath);
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            {
                startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(currentDirectory);
            }
        }
        catch
        {
        }

        var selected = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select llama-server.exe",
            SuggestedStartLocation = startLocation,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("llama-server executable")
                {
                    Patterns = ["llama-server.exe"]
                },
                new FilePickerFileType("Executable files")
                {
                    Patterns = ["*.exe"]
                }
            ]
        });

        var file = selected.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        var executablePath = file.Path.LocalPath;
        if (!_runtimeService.IsSupportedManagedLocalServer(executablePath))
        {
            StatusMessage = "Selected file is not a supported llama-server.exe executable.";
            return;
        }

        LocalServerExecutablePath = executablePath;
        _config.SetString("LocalServerExecutablePath", executablePath);
        _configService.Save(_config);
        RefreshInstalledLocalServerOptions();
        StatusMessage = "Updated llama-server installation path.";
    }

    [RelayCommand]
    private async Task ExportEndpointAdaptersAsync()
    {
        var owner = GetMainWindowStorageProvider();
        if (owner is null)
        {
            StatusMessage = "Endpoint adapters export picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            {
                startLocation = await owner.TryGetFolderFromPathAsync(documents);
            }
        }
        catch
        {
        }

        var suggestedName = EndpointAdapterPack.FilePrefix + "-"
            + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            + ".json";
        var picked = await owner.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Endpoint adapters",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startLocation,
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("Endpoint adapters pack")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        if (picked is null)
        {
            return;
        }

        try
        {
            var pack = EndpointAdapterPack.Build(
                _config,
                (int)OrchestratorPort,
                SelectedEndpointApp,
                (int)HarnessWebPort);
            var path = picked.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(Path.GetExtension(path)))
            {
                path += ".json";
            }

            EndpointAdapterPack.WritePack(path, pack);
            StatusMessage = "Endpoint adapters exported to " + path + ". Keep that file outside AI-FluxMux so a reinstall can Import it.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not export Endpoint adapters: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportEndpointAdaptersAsync()
    {
        var owner = GetMainWindowStorageProvider();
        if (owner is null)
        {
            StatusMessage = "Endpoint adapters import picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            {
                startLocation = await owner.TryGetFolderFromPathAsync(documents);
            }
        }
        catch
        {
        }

        var selected = await owner.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Endpoint adapters",
            SuggestedStartLocation = startLocation,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Endpoint adapters pack")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        var file = selected.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(file.Path.LocalPath);
            if (!EndpointAdapterPack.TryRead(json, out var pack, out var error))
            {
                StatusMessage = error;
                return;
            }

            var backup = EndpointAdapterPack.BackupConfigFile(_configService.ConfigPath);
            var message = EndpointAdapterPack.Apply(_config, pack);

            _isLoadingConfig = true;
            try
            {
                var port = _config.GetInt("OrchestratorPort", (int)OrchestratorPort);
                if (port is >= 1 and <= 65535)
                {
                    OrchestratorPort = port;
                }

                SelectedEndpointApp = FirstNonEmpty(
                    _config.GetString("EndpointApp", SelectedEndpointApp),
                    "Cline");
                SyncEndpointAppToRuntime();
                var harnessWebPort = _config.GetInt("HarnessWebPort", (int)HarnessWebPort);
                if (harnessWebPort is >= 1 and <= 65535)
                {
                    HarnessWebPort = harnessWebPort;
                }

                OnPropertyChanged(nameof(EndpointModelAddressText));
                OnPropertyChanged(nameof(ClineEndpointHintText));
                PublishHelpNoteValues();
                OnPropertyChanged(nameof(ShowHarnessSetupPanel));
                OnPropertyChanged(nameof(ShowHarnessServersPanel));
                OnPropertyChanged(nameof(EndpointSettingsIntroText));
                OnPropertyChanged(nameof(ShowClineEndpointHint));
                OnPropertyChanged(nameof(ShowGenericEndpointHint));
                OnPropertyChanged(nameof(HarnessChatUrl));
                OnPropertyChanged(nameof(HarnessServersSummaryText));
            }
            finally
            {
                _isLoadingConfig = false;
            }

            _configService.Save(_config);
            _endpointSyncedFingerprint = string.Empty;
            UpdateRequestRoutingModeText();
            NotifyQuickSelectHarnessControlsChanged();
            TrySyncEndpointSettingsForMountedRoute();

            StatusMessage = string.IsNullOrWhiteSpace(backup)
                ? message
                : message + " Previous fluxmux_config.json saved as " + backup;
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not import Endpoint adapters: " + ex.Message;
        }
    }

    private static IStorageProvider? GetMainWindowStorageProvider()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return desktop?.MainWindow?.StorageProvider;
    }

    [RelayCommand]
    private async Task ExportModelProfilesAsync()
    {
        var owner = GetMainWindowStorageProvider();
        if (owner is null)
        {
            StatusMessage = "Model profiles export picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            {
                startLocation = await owner.TryGetFolderFromPathAsync(documents);
            }
        }
        catch
        {
        }

        var suggestedName = ModelProfilePack.FilePrefix + "-"
            + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            + ".json";
        var picked = await owner.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export model profiles",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startLocation,
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("Model profile pack")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        if (picked is null)
        {
            return;
        }

        try
        {
            var pack = ModelProfilePack.Build(_config);
            var path = picked.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(Path.GetExtension(path)))
            {
                path += ".json";
            }

            ModelProfilePack.WritePack(path, pack);
            StatusMessage = "Model profiles exported to " + path
                + ". That file is a temporary pack — the live settings stay in fluxmux_config.json. Keep it outside AI-FluxMux so a reinstall can Import it.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not export model profiles: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportModelProfilesAsync()
    {
        var owner = GetMainWindowStorageProvider();
        if (owner is null)
        {
            StatusMessage = "Model profiles import picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            {
                startLocation = await owner.TryGetFolderFromPathAsync(documents);
            }
        }
        catch
        {
        }

        var selected = await owner.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import model profiles",
            SuggestedStartLocation = startLocation,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Model profile pack")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        var file = selected.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(file.Path.LocalPath);
            if (!ModelProfilePack.TryRead(json, out var pack, out var error))
            {
                StatusMessage = error;
                return;
            }

            string preImportExport;
            try
            {
                preImportExport = ModelProfilePack.WritePreImportBackup(_config, _configService.ConfigPath);
            }
            catch (Exception ex)
            {
                StatusMessage = "Import stopped: could not write a pre-import export backup (" + ex.Message
                    + "). The live config was not changed.";
                return;
            }

            AppendDiagnosticEntry(
                "IMPORT",
                "Pre-import export saved as " + preImportExport,
                "AI-FluxMux");

            var result = ModelProfilePack.Apply(_config, pack, LocalModelDirectory);
            foreach (var skip in result.SkipDetails)
            {
                AppendDiagnosticEntry("IMPORT", skip.Message, "AI-FluxMux");
            }

            var baseline = " Pre-import export saved as " + preImportExport + ".";
            if (!result.Changed)
            {
                StatusMessage = result.Message + baseline;
                return;
            }

            var backup = ModelProfilePack.BackupConfigFile(_configService.ConfigPath);
            _configService.Save(_config);
            RefreshLocalProfilesFromModelDirectory();
            RefreshCloudProfilesForProvider();
            RefreshLocalVariantsForSelection();
            RefreshCloudVariantsForSelection();

            StatusMessage = result.Message + baseline
                + (string.IsNullOrWhiteSpace(backup)
                    ? string.Empty
                    : " Previous fluxmux_config.json saved as " + backup + ".");
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not import model profiles: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task BrowseModelDirectoryAsync()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;
        if (owner is null || owner.StorageProvider is null)
        {
            StatusMessage = "Model directory picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(LocalModelDirectory) && Directory.Exists(LocalModelDirectory))
            {
                startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(LocalModelDirectory);
            }
        }
        catch
        {
        }

        var selected = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Local Model Directory",
            SuggestedStartLocation = startLocation,
            AllowMultiple = false
        });

        var folder = selected.FirstOrDefault();
        if (folder is not null)
        {
            LocalModelDirectory = folder.Path.LocalPath;
            RefreshLocalProfilesFromModelDirectory();
            RefreshLocalVariantTreeItems();
            SaveCurrentSelections();
            StatusMessage = "Updated local model directory.";
        }
    }

    [RelayCommand]
    private async Task BrowseVisionProjectorAsync()
    {
        if (!IsLocalVariantEditable)
        {
            StatusMessage = "Use Make New Variant, then choose a projector on the editable model profile variant.";
            return;
        }

        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;
        if (owner is null || owner.StorageProvider is null)
        {
            StatusMessage = "Projector file picker unavailable in current session.";
            return;
        }

        IStorageFolder? startLocation = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(LocalModelDirectory) && Directory.Exists(LocalModelDirectory))
            {
                startLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(LocalModelDirectory);
            }
        }
        catch
        {
        }

        var selected = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Vision Projector (mmproj)",
            SuggestedStartLocation = startLocation,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Projector Files")
                {
                    Patterns = ["*.mmproj", "*.bin", "*.gguf"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        var file = selected.FirstOrDefault();
        if (file is not null)
        {
            LocalVariantVisionProjectorPath = file.Path.LocalPath;
            LocalVariantVisionEnabled = "Enabled";
            RefreshLocalVisionHint();
            StatusMessage = "This profile will load images using that projector. Save, then Validate.";
        }
    }

    [RelayCommand]
    private async Task CopyLastErrorAsync()
    {
        var text = string.IsNullOrWhiteSpace(_lastHealthDetail) ? ConnectionHealthText : _lastHealthDetail;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "No error details are currently available.";
            return;
        }

        ConnectionHealthText = text;
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var clipboard = desktop?.MainWindow?.Clipboard;
        if (clipboard is null)
        {
            StatusMessage = "Clipboard unavailable in this session. Error text is shown in Connection Health for manual copy.";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            StatusMessage = "Latest connection error copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Clipboard copy failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CopyDiagnosticTerminalAsync()
    {
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is null)
        {
            StatusMessage = "Clipboard unavailable in this session.";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(DiagnosticTerminalText);
            StatusMessage = "Diagnostic log copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Diagnostic log copy failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearDiagnosticTerminal()
    {
        _diagnosticEntries.Clear();
        _lastDiagnosticPayload = string.Empty;
        DiagnosticTerminalText = "Diagnostic log cleared.";
    }

    [RelayCommand]
    private void RefreshLocalProfiles()
    {
        RefreshLocalProfilesFromModelDirectory();
        RefreshLocalVariantsForSelection();
        UpdateVariantBaseTargets();
        StatusMessage = LocalModelDiscoveryText;
    }

    [RelayCommand]
    private void SaveCloudVariant()
    {
        AdoptExpandedCloudEditorSelection();
        if (!IsCloudVariantEditable)
        {
            StatusMessage = "Unlock this default first, or Make New Variant to create an editable model profile variant.";
            return;
        }

        if (!TryCommitPendingCloudFamilyModelId())
        {
            return;
        }

        var variantName = NormalizeVariantName(SelectedCloudVariant);
        if (RejectDuplicateProfileSettings(FindCloudVariantWithMatchingSettings(BuildCloudVariantSettingsObject(), variantName)))
        {
            return;
        }

        var settingsChanged = ProfileSettingsDiffer(
            ResolveCloudVariantObjectFromConfig(variantName),
            BuildCloudVariantSettingsObject());
        ApplyCloudVariantSettings(variantName, reloadEditor: false);
        SaveCurrentSelections();
        if (settingsChanged)
        {
            var profileKey =
                $"{(SelectedCloudProvider ?? string.Empty).Trim()}::{(SelectedCloudProfile ?? string.Empty).Trim()}::{ProfileConfigVariantName(variantName)}";
            SetCloudVariantEndpointValidated(
                variantName,
                validated: false,
                warning: IsSavedProfileEndpointValidated("CloudProfiles", profileKey)
                    ? SettingsSavedAfterValidateWarning
                    : null);
            RefreshProfileWarningSurfaces();
        }

        RevealExpandedCloudProfile(
            SelectedCloudProvider ?? string.Empty,
            SelectedCloudProfile ?? string.Empty,
            variantName);
        RefreshCloudFamilyModelIdEditor(bindDraftToCurrent: true);
        StatusMessage = "Cloud profile saved, including Provider and Model id when you changed them. In Model Profiles, click Validate to endpoint to test these settings and offer this profile in Quick Select.";
    }

    [RelayCommand]
    private async Task ValidateCloudProfileToEndpoint(CloudVariantTreeItemViewModel? profile)
    {
        if (CloudFamilyIdentityDraftDiffersFromSaved())
        {
            StatusMessage = "Save profile first to apply Provider and Model id changes, then run Validate to endpoint.";
            ConnectionHealthText = StatusMessage;
            return;
        }

        _isValidatingCloudProfile = true;
        _suppressVariantExpanderSelection = true;
        _holdProfileLayout = true;
        ReleaseLocalProfileEditor();
        string provider = string.Empty;
        string model = string.Empty;
        try
        {
            profile = ResolveActiveCloudEditorItem() ?? profile ?? SelectedCloudVariantItem;
            if (!CloudProfileLaunchTarget.TryResolve(
                    profile,
                    SelectedCloudProvider,
                    SelectedCloudProfile,
                    SelectedCloudVariant,
                    BaseVariantDisplayName,
                    out provider,
                    out model,
                    out var resolvedVariant))
            {
                StatusMessage = "Validate to endpoint did not succeed. Expand the cloud profile in Model Profiles so AI-FluxMux has both a provider and a model id.";
                ConnectionHealthText = StatusMessage;
                return;
            }

            if (!string.Equals(SelectedCloudProvider, provider, StringComparison.OrdinalIgnoreCase))
            {
                SelectedCloudProvider = provider;
            }

            if (!string.Equals(SelectedCloudProfile, model, StringComparison.OrdinalIgnoreCase))
            {
                SelectedCloudProfile = model;
            }

            if (!string.Equals(SelectedCloudVariant, resolvedVariant, StringComparison.OrdinalIgnoreCase))
            {
                SelectedCloudVariant = resolvedVariant;
            }

            var variantName = NormalizeVariantName(resolvedVariant);
            _variantTreeRestoreEpoch++;
            if (IsCloudVariantEditable)
            {
                ApplyCloudVariantSettings(variantName, reloadEditor: false);
                SaveCurrentSelections();
            }

            ReportTuneProgress("Validate to endpoint: launching " + provider + " / " + model + " with the current settings.");
            await FlushUiAsync();
            await LaunchCloudRouteAsync("Model Profiles", provider, model, variantName);

            StatusMessage = "Validate to endpoint: sending a tiny test request to " + provider + " / " + model + "...";
            var ok = await CheckCloudRouteHealthAsync(
                "Saved cloud profile",
                provider,
                model,
                variantName);
            SetCloudVariantEndpointValidated(
                provider,
                model,
                variantName,
                validated: ok,
                warning: ok ? null : FirstNonEmpty(
                    FirstNonEmpty(ConnectionHealthText, StatusMessage),
                    DefaultEndpointWarningTooltip));
            RefreshCloudVariantTreeItemSummariesInPlace();
            RefreshProfileWarningSurfaces();
            var offeredSlot = ok
                ? OfferValidatedProfileOnEmptyQuickSelectSlot("Cloud", provider, model, variantName)
                : null;
            var tested = provider + " / " + model;
            StatusMessage = ok
                ? "Validate to endpoint succeeded for " + tested + ". " + DescribeValidatedProfileQuickSelectFollowThrough(offeredSlot)
                : "Validate to endpoint did not succeed for " + tested + ". This profile is not yet listed in the Quick Select dropdown. "
                  + AppendCloudValidateFailureGuidance(provider, FirstNonEmpty(ConnectionHealthText, StatusMessage));
        }
        finally
        {
            _isValidatingCloudProfile = false;
            _suppressVariantExpanderSelection = false;
            if (!string.IsNullOrWhiteSpace(model))
            {
                AddIfMissing(CloudFamilyModelIdChoices, model);
                CloudFamilyProviderDraft = provider;
                CloudFamilyModelIdDraft = model;
                CloudFamilyModelIdPaste = string.Empty;
            }

            RefreshProfileEndpointStatusSurfaces();
        }
    }

    [RelayCommand]
    private void RevertCloudVariant()
    {
        LoadCloudVariantSettingsEditor();
        RefreshProfileEndpointStatusSurfaces();
        StatusMessage = "Cloud model profile variant settings and priorities reverted to the last saved state.";
    }

    [RelayCommand]
    private void UseCloudModelDefaults()
    {
        if (!IsCloudVariantEditable)
            return;

        var settings = ResolveCloudVariantObjectFromConfig(FindCloudModelDefaultVariantName(SelectedCloudProvider, SelectedCloudProfile) ?? BaseVariantDisplayName);
        LoadCloudVariantSettingsForm(settings);
        LoadCloudPriorityOrder(settings);
        RefreshProfileEndpointStatusSurfaces();
        StatusMessage = "Model defaults loaded into this cloud model profile variant. Click Save profile to keep them.";
    }

    [RelayCommand]
    private void CreateCloudVariant()
    {
        var variantName = NormalizeVariantName(CloudVariantDraftName);
        if (string.IsNullOrWhiteSpace(variantName))
        {
            StatusMessage = "Enter a cloud model profile variant name first.";
            return;
        }

        // Allow create as a settings copy; SaveCloudVariant still rejects identical packages.
        if (!EnsureCloudVariantEntry(variantName))
        {
            StatusMessage = "Cloud model profile variant already exists.";
            return;
        }

        SelectedCloudVariant = variantName;
        CloudVariantDraftName = variantName;
        SaveCurrentSelections();
        EvaluateFirstProfileBootstrapState();
        RevealExpandedCloudProfile(SelectedCloudProvider, SelectedCloudProfile, variantName);
        StatusMessage =
            "Cloud model profile variant created from this profile's settings. Change at least one setting, then Save profile — AI-FluxMux will not keep two variants with the same settings package.";
    }

    [RelayCommand]
    private void SelectFirstCloudTemplate()
    {
        ApplyCloudTemplateDefaults();
        SelectedSelection1RouteType = "Cloud";
        IsSelection1TemplateChosen = true;
        UpdateSelectionRouteVisibilityStates();
        EnsureSelection1SlotAssigned("cloud", SelectedCloudProfile, SelectedCloudVariant);
        SaveCurrentSelections();
        EvaluateFirstProfileBootstrapState();
        StatusMessage = "Cloud template selected for Selection 1 with default settings. Add only the provider API key if needed, then launch to validate.";
    }

    [RelayCommand]
    private void SelectFirstLocalTemplate()
    {
        ApplyLocalTemplateDefaultsForSelection1();
        SelectedSelection1RouteType = "Local";
        IsSelection1TemplateChosen = true;
        UpdateSelectionRouteVisibilityStates();
        EnsureSelection1SlotAssigned("local", SelectedSelection1LocalProfile, SelectedSelection1LocalVariant);
        SaveCurrentSelections();
        EvaluateFirstProfileBootstrapState();
        StatusMessage = "Local template selected for Selection 1 with default settings. Confirm local model directory, then launch to validate.";
    }

    [RelayCommand]
    private void AddNewSlot()
    {
        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            slots = new JsonArray();
            _config.Root["RouteSlots"] = slots;
        }

        EnsureSelection1SlotAssigned(
            NormalizeSelectionRouteType(SelectedSelection1RouteType).Equals("Local", StringComparison.OrdinalIgnoreCase) ? "local" : "cloud",
            NormalizeSelectionRouteType(SelectedSelection1RouteType).Equals("Local", StringComparison.OrdinalIgnoreCase) ? SelectedSelection1LocalProfile : SelectedCloudProfile,
            NormalizeSelectionRouteType(SelectedSelection1RouteType).Equals("Local", StringComparison.OrdinalIgnoreCase) ? SelectedSelection1LocalVariant : SelectedCloudVariant);

        var candidateFingerprint = BuildSelection2CandidateFingerprint();
        if (!string.IsNullOrWhiteSpace(candidateFingerprint))
        {
            var duplicate = slots
                .OfType<JsonObject>()
                .Where(x => ParseBool(x["enabled"]?.ToString(), true))
                .FirstOrDefault(x => BuildSlotSettingsFingerprint(x).Equals(candidateFingerprint, StringComparison.Ordinal));

            if (duplicate is not null)
            {
                StatusMessage = "You already have a slot with those exact model settings";
                return;
            }
        }

        EnsureSelection2SlotExists(slots);
        IsSelection2Enabled = true;
        SyncPrimaryRouteSlotsFromCurrentSelections();
        _config.SetBool("Selection2Enabled", true);
        _configService.Save(_config);
        RefreshSlotPresenceAndActions();
        StatusMessage = "Added Selection 2 slot.";
    }

    [RelayCommand]
    private void SelectSelection2CloudTemplate()
    {
        ApplyCloudTemplateDefaults();
        SelectedSelection2CloudProfile = FirstNonEmpty(SelectedSelection2CloudProfile, SelectedCloudProfile);
        SelectedSelection2CloudVariant = FirstNonEmpty(SelectedSelection2CloudVariant, SelectedCloudVariant);
        SelectedSelection2RouteType = "Cloud";
        IsSelection2TemplateChosen = true;
        UpdateSelectionRouteVisibilityStates();
        SyncPrimaryRouteSlotsFromCurrentSelections();
        _config.SetBool("Selection2TemplateChosen", true);
        _configService.Save(_config);
        StatusMessage = "Selection 2 cloud template invoked with defaults. Add only the provider API key if needed, then launch to validate.";
    }

    [RelayCommand]
    private void SelectSelection2LocalTemplate()
    {
        ApplyLocalTemplateDefaultsForSelection2();
        SelectedSelection2RouteType = "Local";
        IsSelection2TemplateChosen = true;
        UpdateSelectionRouteVisibilityStates();
        SyncPrimaryRouteSlotsFromCurrentSelections();
        _config.SetBool("Selection2TemplateChosen", true);
        _configService.Save(_config);
        StatusMessage = "Selection 2 local template invoked with defaults. Confirm local model directory, then launch to validate.";
    }

    [RelayCommand]
    private async Task DiscoverLocalServerAsync()
    {
        StatusMessage = "Checking local server installation and any already-running model servers...";
        var result = await _runtimeService.DiscoverLocalEndpointAsync(
            (int)OrchestratorPort,
            progress =>
            {
                StatusMessage = progress;
            });
        ApplyLocalDiscoveryResult(result);
        StatusMessage = result.Status;
    }

    [RelayCommand]
    private void AdoptDiscoveredLocalServerPort()
    {
        var selectedPort = SelectedDiscoveredLocalServer?.Port ?? DiscoveredLocalServerPort;
        if (!CanAdoptDiscoveredLocalServer || selectedPort < 1 || selectedPort > 65535)
        {
            StatusMessage = "No compatible running model server was found to copy a port from.";
            return;
        }

        OrchestratorPort = selectedPort;
        SaveCurrentSelections();
        StatusMessage = $"Copied discovered model-server port {selectedPort} into Port. That other program still owns the process; AI-FluxMux will not start or stop it.";
    }

    [RelayCommand]
    private void DeleteSelection2Slot()
    {
        if (!ConfirmSlotDeletion("Selection 2"))
        {
            return;
        }

        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            return;
        }

        var slot2 = slots.OfType<JsonObject>().FirstOrDefault(x =>
            string.Equals(x["slotId"]?.ToString(), "slot-local-1", StringComparison.OrdinalIgnoreCase));
        if (slot2 is not null)
        {
            slots.Remove(slot2);
        }

        IsSelection2Enabled = false;
        _config.SetBool("Selection2Enabled", false);
        UpdateActiveRouteSlotIdsForCurrentSlotEnablement();
        _configService.Save(_config);
        RefreshSlotPresenceAndActions();
        StatusMessage = "Deleted Selection 2 slot.";
    }

    [RelayCommand]
    private void DeleteSelection1Slot()
    {
        if (!ConfirmSlotDeletion("Selection 1"))
        {
            return;
        }

        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            slots = new JsonArray();
            _config.Root["RouteSlots"] = slots;
        }

        var slot1 = slots.OfType<JsonObject>().FirstOrDefault(x =>
            string.Equals(x["slotId"]?.ToString(), "slot-cloud-1", StringComparison.OrdinalIgnoreCase));
        var slot2 = slots.OfType<JsonObject>().FirstOrDefault(x =>
            string.Equals(x["slotId"]?.ToString(), "slot-local-1", StringComparison.OrdinalIgnoreCase));

        if (IsSelection2Enabled && slot2 is not null)
        {
            PromoteSelection2IntoSelection1(slot2);
            slots.Remove(slot2);
            IsSelection2Enabled = false;
            _config.SetBool("Selection2Enabled", false);
            SyncPrimaryRouteSlotsFromCurrentSelections();
            _configService.Save(_config);
            RefreshSlotPresenceAndActions();
            StatusMessage = "Deleted Selection 1 slot and promoted Selection 2 into Selection 1.";
            return;
        }

        if (slot1 is not null)
        {
            slots.Remove(slot1);
        }

        HasSelection1Slot = false;
        IsSelection1TemplateChosen = false;
        ShowSelectionEditors = false;
        ShowBootstrapTemplateChooser = true;
        ShowBootstrapCreateFirstProfile = false;
        ShowDeleteSelection1SlotButton = false;
        _config.SetStringArray("ActiveRouteSlotIds", []);
        _config.SetInt("MaxVisibleRouteSlots", 0);
        _configService.Save(_config);
        StatusMessage = "Deleted Selection 1 slot.";
    }

    private void CreateFirstCloudProfileInternal()
    {
        var cloudModel = FirstNonEmpty(SelectedCloudProfile, CloudProfiles.FirstOrDefault() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cloudModel))
        {
            StatusMessage = "Pick a cloud model profile first.";
            return;
        }

        SelectedCloudProfile = cloudModel;
        SelectedCloudVariant = BaseVariantDisplayName;
        AddIfMissing(CloudProfiles, cloudModel);
        AddIfMissing(CloudVariants, BaseVariantDisplayName);
        EnsureCloudVariantEntry(BaseVariantDisplayName);

        SelectedSelection1RouteType = "Cloud";
        EnsureSelection1SlotAssigned("cloud", cloudModel, BaseVariantDisplayName);
        SaveCurrentSelections();
        IsSelection1TemplateChosen = true;
        EvaluateFirstProfileBootstrapState();
        StatusMessage = "Created first cloud default model profile and assigned it to Selection 1.";
    }

    private void CreateFirstLocalProfileInternal()
    {
        var localModel = FirstNonEmpty(
            SelectedSelection1LocalProfile,
            FirstNonEmpty(SelectedLocalProfile, LocalProfiles.FirstOrDefault() ?? string.Empty));
        if (string.IsNullOrWhiteSpace(localModel))
        {
            localModel = "Local Profile 1";
        }

        SelectedSelection1LocalProfile = localModel;
        SelectedLocalProfile = FirstNonEmpty(SelectedLocalProfile, localModel);
        SelectedSelection1LocalVariant = BaseVariantDisplayName;
        SelectedLocalVariant = FirstNonEmpty(SelectedLocalVariant, BaseVariantDisplayName);
        AddIfMissing(LocalProfiles, localModel);
        AddIfMissing(LocalVariants, BaseVariantDisplayName);
        EnsureLocalVariantEntry(BaseVariantDisplayName);

        SelectedSelection1RouteType = "Local";
        EnsureSelection1SlotAssigned("local", localModel, BaseVariantDisplayName);
        SaveCurrentSelections();
        IsSelection1TemplateChosen = true;
        EvaluateFirstProfileBootstrapState();
        StatusMessage = "Created first local default model profile and assigned it to Selection 1.";
    }

    [RelayCommand]
    private void RenameCloudVariant()
    {
        AdoptExpandedCloudEditorSelection();
        if (!IsCloudVariantEditable)
        {
            StatusMessage = "Unlock this default first, or Make New Variant to create an editable model profile variant.";
            return;
        }

        var newName = NormalizeVariantName(CloudVariantDraftName);
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusMessage = "Enter a new cloud model profile variant name first.";
            return;
        }

        if (newName.Equals(NormalizeVariantName(SelectedCloudVariant), StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Enter a different name for this cloud profile.";
            return;
        }

        if (newName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            && !(SelectedCloudVariantItem?.IsBase ?? false))
        {
            StatusMessage = "Choose a different name. (defaults) is reserved for the model default.";
            return;
        }

        if (!TryRenameCloudVariantEntry(SelectedCloudVariant, newName))
        {
            StatusMessage = "Cloud model profile variant rename failed.";
            return;
        }

        if (SelectedCloudVariantItem is { IsBase: true })
        {
            SetLiveCloudProfileLockState(newName, unlocked: true, markAsModelDefault: true);
        }

        _variantTreeRestoreEpoch++;
        _isSynchronizingAdvancedProfileSelection = true;
        try
        {
            SelectedCloudVariantItem?.ApplyVariantName(newName);
            SelectedCloudVariant = newName;
            CloudVariantDraftName = newName;
            RefreshCloudVariantsForSelection(rebuildTree: false);
        }
        finally
        {
            _isSynchronizingAdvancedProfileSelection = false;
        }

        SaveCurrentSelections();
        StatusMessage = "Cloud model profile variant renamed.";
    }

    [RelayCommand]
    private Task ToggleProfileDefaultLock(CloudVariantTreeItemViewModel? profile)
        => ToggleDefaultLockAsync(profile, isCloud: !string.IsNullOrWhiteSpace(profile?.Provider));

    [RelayCommand]
    private Task ToggleCloudDefaultLock(CloudVariantTreeItemViewModel? profile)
        => ToggleDefaultLockAsync(profile, isCloud: true);

    [RelayCommand]
    private Task ToggleLocalDefaultLock(CloudVariantTreeItemViewModel? profile)
        => ToggleDefaultLockAsync(profile, isCloud: false);

    private async Task ToggleDefaultLockAsync(CloudVariantTreeItemViewModel? profile, bool isCloud)
    {
        if (_lockToggleBusy)
        {
            return;
        }

        if (profile is not { IsBase: true })
        {
            StatusMessage = "Select a default model profile first.";
            return;
        }

        _lockToggleBusy = true;
        var unlocking = !profile.IsUnlocked;
        StatusMessage = unlocking ? "Unlocking default model profile..." : "Locking default model profile...";
        await FlushUiAsync();
        try
        {

            if (isCloud)
            {
                SetLiveCloudProfileLockState(
                    profile.Name,
                    unlocking,
                    markAsModelDefault: true,
                    provider: profile.Provider,
                    modelName: profile.ModelName);
            }
            else
            {
                SetLiveLocalProfileLockState(
                    profile.Name,
                    unlocking,
                    markAsModelDefault: true,
                    modelName: profile.ModelName);
            }

            var saved = isCloud
                ? GetLiveCloudProfileSettings(profile.Name, profile.Provider, profile.ModelName)
                : GetLiveLocalProfileSettings(profile.Name, profile.ModelName);
            if (saved is null || IsProfileDefaultUnlocked(saved) != unlocking)
            {
                StatusMessage = "Could not change the lock on this default model profile.";
                return;
            }

            var live = isCloud
                ? CloudVariantTreeItems.FirstOrDefault(item =>
                    item.Provider.Equals(profile.Provider, StringComparison.OrdinalIgnoreCase)
                    && item.ModelName.Equals(profile.ModelName, StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                : LocalVariantTreeItems.FirstOrDefault(item =>
                    item.ModelName.Equals(profile.ModelName, StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            var target = live ?? profile;
            target.IsUnlocked = unlocking;

            _isSynchronizingAdvancedProfileSelection = true;
            try
            {
                if (isCloud)
                {
                    SelectedCloudVariantItem = target;
                }
                else
                {
                    SelectedLocalVariantItem = target;
                }
            }
            finally
            {
                _isSynchronizingAdvancedProfileSelection = false;
            }

            SyncVariantTreeSelectionState(
                isCloud ? CloudVariantTreeItems : LocalVariantTreeItems,
                target);
            UpdateVariantEditability();
            if (unlocking)
            {
                if (isCloud)
                {
                    CloudVariantDraftName = RenameDraftFromProfileName(profile.Name);
                }
                else
                {
                    LocalVariantDraftName = RenameDraftFromProfileName(profile.Name);
                }
            }

            await Task.Run(() => _configService.Save(_config));
            StatusMessage = unlocking
                ? (isCloud
                    ? "Cloud default unlocked. Click the padlock again to lock it."
                    : "Local default unlocked. Click the padlock again to lock it.")
                : (isCloud
                    ? "Cloud default locked again. Your settings are kept."
                    : "Local default locked again. Your settings are kept.");
        }
        finally
        {
            _lockToggleBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteCloudVariant(CloudVariantTreeItemViewModel? profile)
    {
        var target = ResolveActiveCloudEditorItem() ?? profile ?? SelectedCloudVariantItem;
        if (target is null)
        {
            StatusMessage = "Select a cloud profile to delete.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.Provider)
            && !string.Equals(SelectedCloudProvider, target.Provider, StringComparison.OrdinalIgnoreCase))
        {
            SelectedCloudProvider = target.Provider;
        }

        if (!string.IsNullOrWhiteSpace(target.ModelName)
            && !string.Equals(SelectedCloudProfile, target.ModelName, StringComparison.OrdinalIgnoreCase))
        {
            SelectedCloudProfile = target.ModelName;
        }

        if (!string.Equals(SelectedCloudVariant, target.Name, StringComparison.OrdinalIgnoreCase))
        {
            SelectedCloudVariant = target.Name;
        }

        var variantName = ProfileConfigVariantName(target.Name);
        var missingModel = string.IsNullOrWhiteSpace(target.ModelName);
        var familyProvider = target.Provider ?? string.Empty;
        var familyModel = (target.ModelName ?? string.Empty).Trim();
        var deletingDefault = target.IsBase
            || variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        if (deletingDefault && !target.IsUnlocked && !missingModel)
        {
            StatusMessage = "Unlock this default first, or Make New Variant to create an editable model profile variant.";
            return;
        }

        var label = target.DisplayName;
        if (deletingDefault)
        {
            var copyCount = CountCloudProfileCopies(familyProvider, familyModel);
            var confirmName = missingModel
                ? FirstNonEmpty(familyProvider, "Cloud") + " / missing model id"
                : $"{familyProvider} / {familyModel}";
            if (!ConfirmDefaultModelProfileDeletion(
                    confirmName,
                    copyCount,
                    fileMissing: false,
                    isLocal: false))
            {
                StatusMessage = "Cloud profile delete cancelled.";
                return;
            }

            if (!TryDeleteCloudProfileFamily(familyProvider, familyModel)
                && !TryDeleteCloudProfileByKey(target.ConfigKey))
            {
                StatusMessage = "Cloud profile not found.";
                return;
            }

            RefreshCloudVariantTreeItems();
            SaveCurrentSelections();
            StatusMessage = copyCount > 0
                ? $"Removed {confirmName} and {copyCount} cop{(copyCount == 1 ? "y" : "ies")} from AI-FluxMux."
                : $"Removed {confirmName} from AI-FluxMux.";
            return;
        }

        if (!ConfirmProfileVariantDeletion(label))
        {
            StatusMessage = "Cloud profile delete cancelled.";
            return;
        }

        if (!TryDeleteCloudVariantEntry(variantName))
        {
            StatusMessage = "Cloud model profile variant not found.";
            return;
        }

        RefreshCloudVariantsForSelection();
        SelectedCloudVariant = BaseVariantDisplayName;
        SaveCurrentSelections();
        StatusMessage = "Cloud profile deleted: " + label + ".";
    }

    [RelayCommand]
    private void SaveLocalVariant()
    {
        var intended = SelectedLocalVariantItem;
        AdoptExpandedLocalEditorSelection();
        if (intended is { IsExpanded: true }
            && !ReferenceEquals(SelectedLocalVariantItem, intended))
        {
            SelectedLocalVariantItem = intended;
        }
        if (!IsLocalVariantEditable)
        {
            StatusMessage = "Unlock this default first, or Make New Variant to create an editable model profile variant.";
            return;
        }

        var variantName = NormalizeVariantName(SelectedLocalVariant);
        RestoreBlankLocalComboSelections();
        if (RejectDuplicateProfileSettings(FindLocalVariantWithMatchingSettings(BuildLocalVariantSettingsObject(), variantName)))
        {
            return;
        }

        var settingsChanged = !GetLocalLaunchCriticalFingerprint(
                ResolveLocalVariantObjectFromConfig(variantName))
            .Equals(GetLocalLaunchCriticalFingerprint(BuildLocalVariantSettingsObject()), StringComparison.Ordinal);
        ApplyLocalVariantSettings(variantName, reloadEditor: false);
        if (IsLocalProfileCurrentlyLoaded(SelectedLocalProfile, variantName))
        {
            _runtimeService.ApplyProfileForwardCompactSetting(SelectedLocalProfile, ProfileConfigVariantName(variantName));
        }
        SaveCurrentSelections();
        CaptureLocalVariantEditorBaseline();
        RefreshQuickSelectVariantLists();
        if (settingsChanged)
        {
            var profileKey =
                $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{ProfileConfigVariantName(variantName)}";
            SetLocalVariantEndpointValidated(
                variantName,
                validated: false,
                warning: IsSavedProfileEndpointValidated("LocalProfiles", profileKey)
                    ? SettingsSavedAfterValidateWarning
                    : null);
            RefreshProfileWarningSurfaces();
        }

        StatusMessage = "Local profile saved. In Model Profiles, click Validate to endpoint to test these settings and offer this profile in Quick Select.";
        ReassertLocalVariantEditor(intended ?? SelectedLocalVariantItem);
    }

    [RelayCommand]
    private async Task ValidateLocalProfileToEndpoint(CloudVariantTreeItemViewModel? profile)
    {
        var intended = SelectedLocalVariantItem;
        AdoptExpandedLocalEditorSelection();
        if (intended is { IsExpanded: true }
            && !ReferenceEquals(SelectedLocalVariantItem, intended))
        {
            SelectedLocalVariantItem = intended;
        }

        var targetItem = ResolveActiveLocalEditorItem() ?? profile ?? SelectedLocalVariantItem;
        var targetModel = FirstNonEmpty(targetItem?.ModelName, SelectedLocalProfile);
        var targetVariant = ProfileConfigVariantName(targetItem?.Name ?? SelectedLocalVariant);
        var snapshot = BuildLocalVariantSettingsObject();
        var visionEnabled = snapshot["LocalVisionEnabled"]?.ToString() ?? LocalVariantVisionEnabled;
        var visionProjector = snapshot["LocalVisionProjectorPath"]?.ToString() ?? LocalVariantVisionProjectorPath ?? string.Empty;
        var visionMaxEdge = ParseInt(snapshot["LocalVisionMaxImageEdge"]?.ToString(), (int)LocalVariantVisionMaxImageEdge);
        _isValidatingLocalProfile = true;
        _preservingLocalVisionForm = true;
        _suppressVariantExpanderSelection = true;
        _holdProfileLayout = true;
        ActivateLocalAdvancedProfile(targetItem);
        var variantName = targetVariant;
        var model = FirstNonEmpty(targetModel, FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile));
        if (IsLocalGgufMissing(model))
        {
            RememberMountedLocal(model, variantName);
            LocalRouteIndicatorState = RouteIndicatorState.Fault;
            StatusMessage = "This local model is not in the model folder. Restore the local model, or delete this profile from AI-FluxMux.";
            RestoreLocalVariantForm(snapshot);
            _preservingLocalVisionForm = false;
            _isValidatingLocalProfile = false;
            _suppressVariantExpanderSelection = false;
            return;
        }
        if (LocalHealthGuidance.IsVisionProjectorFile(model))
        {
            var projectorWarning = LocalHealthGuidance.FormatVisionProjectorAsModelWarning(model);
            RememberMountedLocal(model, variantName);
            LocalRouteIndicatorState = RouteIndicatorState.Fault;
            SetLocalVariantEndpointValidated(model, variantName, validated: false, warning: projectorWarning);
            RefreshLocalVariantTreeItemSummariesInPlace();
            RefreshProfileWarningSurfaces();
            ReportTuneProgress(projectorWarning);
            StatusMessage = projectorWarning;
            RestoreLocalVariantForm(snapshot);
            _preservingLocalVisionForm = false;
            _isValidatingLocalProfile = false;
            _suppressVariantExpanderSelection = false;
            return;
        }
        _variantTreeRestoreEpoch++;
        RestoreLocalVariantForm(snapshot);
        if (targetItem is null ? IsLocalVariantEditable : targetItem.IsEditable)
        {
            PersistLocalProfileSnapshot(model, variantName, snapshot);
        }

        RememberLocalVisionForm(visionEnabled, visionProjector, visionMaxEdge);
        RestoreLocalVariantForm(snapshot);

        var managedIdentity = _runtimeService.GetManagedLocalIdentity();
        var sameLocalModel = _runtimeService.IsManagedLocalAlive()
            && managedIdentity.Model.Equals(model, StringComparison.OrdinalIgnoreCase);
        var validateLaunch = LocalValidateLaunchPolicy.Decide(
            localAlive: _runtimeService.IsManagedLocalAlive(),
            sameLocalModel: sameLocalModel,
            sameLaunchFingerprint: GetLocalLaunchCriticalFingerprint(snapshot)
                .Equals(_runtimeService.GetManagedLocalLaunchFingerprint(), StringComparison.Ordinal));
        if (validateLaunch == LocalValidateLaunchAction.Reload && _runtimeService.IsManagedLocalAlive())
        {
            await FlushUiAsync();
            if (!ConfirmStopLocalForValidate(_runtimeService.InFlightChatCount > 0))
            {
                ReportTuneProgress(LocalValidateLaunchPolicy.ValidateCancelledGuidance);
                StatusMessage = LocalValidateLaunchPolicy.ValidateCancelledGuidance;
                RestoreLocalVariantForm(snapshot);
                _preservingLocalVisionForm = false;
                _isValidatingLocalProfile = false;
                _suppressVariantExpanderSelection = false;
                return;
            }
        }

        var skipReload = validateLaunch == LocalValidateLaunchAction.ProbeRunning;
        ReportTuneProgress(skipReload
            ? "Validate to endpoint: llama-server is already running this local model with matching launch settings. Checking Port without unloading it."
            : "Validate to endpoint: launching this profile with the current settings.");
        await FlushUiAsync();
        RestoreLocalVariantForm(snapshot);
        RememberLocalVisionForm(visionEnabled, visionProjector, visionMaxEdge);

        CancelLocalRuntimeMonitors();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var launchStartedUtc = DateTime.UtcNow;
        if (!skipReload)
        {
            StartLocalLaunchCountdownMonitor(model, variantName);
        }

        try
        {
            CloudRouteIndicatorState = RouteIndicatorState.Off;
            RememberMountedLocal(model, variantName);
            RuntimeActionResult result;
            if (skipReload)
            {
                LocalRouteIndicatorState = RouteIndicatorState.Live;
                _runtimeService.ApplyProfileForwardCompactSetting(model, ProfileConfigVariantName(variantName));
                result = new RuntimeActionResult
                {
                    IsSuccess = true,
                    Status = _runtimeService.InFlightChatCount > 0
                        ? LocalLaunchStatus.RouteReady
                        : LocalLaunchStatus.BackendReady,
                    Details = "llama-server is already running this local model with matching launch settings."
                };
            }
            else
            {
                LocalRouteIndicatorState = RouteIndicatorState.Amber;
                var vision = GetLocalVisionLaunchSettings(model, variantName);
                if (visionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                {
                    vision = (
                        true,
                        FirstNonEmpty(vision.ProjectorPath, visionProjector),
                        vision.MaxImageEdge < 256 ? visionMaxEdge : vision.MaxImageEdge);
                }

                result = await _runtimeService.RestartLocalAsync(
                    model,
                    variantName,
                    LocalModelDirectory,
                    (int)OrchestratorPort,
                    vision.Enabled,
                    vision.ProjectorPath,
                    vision.MaxImageEdge,
                    timeout.Token,
                    progressReporter: UpdateLocalLaunchPhase);
            }

            LocalGenerationTimingResult? publicProbe = null;
            if (result.IsLocalRouteReady)
            {
                ApplyLocalPublicEndpointSuccess(
                    model,
                    variantName,
                    result.Details,
                    launchElapsedMs: null,
                    recordLaunchTelemetry: false);
                publicProbe = new LocalGenerationTimingResult { IsSuccess = true, Details = result.Details };
            }
            else if (result.Status.Equals(LocalLaunchStatus.BackendReady, StringComparison.OrdinalIgnoreCase)
                || result.IsSuccess
                || result.IsLocalRouteWarming)
            {
                _lastHealthDetail = result.Details;
                ConnectionHealthText = result.Details;
                StatusMessage = result.Status;
                LocalRouteIndicatorState = RouteIndicatorState.Amber;
                var waitLead = result.Status.Equals(LocalLaunchStatus.BackendReady, StringComparison.OrdinalIgnoreCase)
                    && FluxMuxRuntimeService.IsTransientLocalWarmupFailure(result.Details)
                    ? "Validate to endpoint: llama-server is up but the local model is still loading. Waiting for the public endpoint."
                    : "Validate to endpoint: waiting for the public endpoint after launch.";
                ReportTuneProgress(waitLead);
                publicProbe = await WaitUntilLocalPublicEndpointReadyAsync(model, timeout.Token);
                if (publicProbe is { IsSuccess: true })
                {
                    var elapsedMs = (int)Math.Min(int.MaxValue, (DateTime.UtcNow - launchStartedUtc).TotalMilliseconds);
                    ApplyLocalPublicEndpointSuccess(
                        model,
                        variantName,
                        publicProbe.Details,
                        elapsedMs,
                        recordLaunchTelemetry: true);
                }
            }
            else
            {
                _lastHealthDetail = result.Details;
                ConnectionHealthText = CompactEndpointWarning(result.Details);
                StatusMessage = result.Status;
                LocalRouteIndicatorState = RouteIndicatorState.Fault;
                RememberMountedLocal(model, variantName);
            }

            var ok = publicProbe is { IsSuccess: true };
            var skippedSpeedBecauseBusy = skipReload && ok && _runtimeService.InFlightChatCount > 0;
            if (ok && !skippedSpeedBecauseBusy)
            {
                ReportTuneProgress("Validate to endpoint: measuring reply speed on the public endpoint...");
                await FlushUiAsync();
                var speed = await _runtimeService.ProbeValidateSpeedThroughPublicEndpointAsync(
                    (int)OrchestratorPort,
                    model,
                    timeout.Token);
                if (speed.IsSuccess)
                {
                    _runtimeService.RecordLocalValidateSpeedMetrics(model, variantName, speed);
                    UpdateProfileFootprintIndicators();
                }
            }

            var stamped = SetLocalVariantEndpointValidated(
                model,
                variantName,
                validated: ok,
                warning: ok ? null : FirstNonEmpty(
                    FirstNonEmpty(publicProbe?.Details ?? string.Empty, result.Details),
                    DefaultEndpointWarningTooltip));
            RefreshLocalVariantTreeItemSummariesInPlace();
            RefreshProfileWarningSurfaces();
            UpdateLocalLaunchTelemetrySummary();
            var profileLabel = variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
                ? model + " (default)"
                : model + ": " + variantName;
            var offeredSlot = ok && stamped
                ? OfferValidatedProfileOnEmptyQuickSelectSlot("Local", string.Empty, model, variantName)
                : null;
            var keptRunningNote = skipReload
                ? (skippedSpeedBecauseBusy
                    ? " llama-server was not reloaded. Speed was not re-measured because the Client app is using it."
                    : " llama-server was not reloaded.")
                : string.Empty;
            ReportTuneProgress(ok
                ? (stamped
                    ? "Validate to endpoint succeeded. '" + profileLabel + "'. " + DescribeValidatedProfileQuickSelectFollowThrough(offeredSlot) + keptRunningNote
                    : "Validate to endpoint succeeded, but AI-FluxMux could not mark '" + profileLabel + "' for Quick Select. Click Save profile, then Validate again.")
                : "Validate to endpoint did not succeed, so '" + profileLabel + "' is not in Quick Select yet. " + CompactEndpointWarning(FirstNonEmpty(publicProbe?.Details ?? string.Empty, result.Details)));
            StatusMessage = ok
                ? (stamped
                    ? "'" + profileLabel + "'. " + DescribeValidatedProfileQuickSelectFollowThrough(offeredSlot)
                    : "Validate succeeded, but Quick Select was not updated. Click Save profile, then Validate again.")
                : "Validate to endpoint did not succeed, so this copy is not in Quick Select yet.";
            RestoreLocalVariantForm(snapshot);
            CaptureLocalVariantEditorBaseline();
        }
        catch (OperationCanceledException)
        {
            SetLocalVariantEndpointValidated(
                model,
                variantName,
                validated: false,
                warning: "Validate to endpoint stopped because it took too long.");
            RememberMountedLocal(model, variantName);
            LocalRouteIndicatorState = RouteIndicatorState.Fault;
            RefreshProfileWarningSurfaces();
            ReportTuneProgress("Validate to endpoint stopped because it took too long. '" + model + ": " + variantName + "' is not yet in Quick Select.");
            RestoreLocalVariantForm(snapshot);
        }
        finally
        {
            RestoreLocalVariantForm(snapshot);
            RememberLocalVisionForm(visionEnabled, visionProjector, visionMaxEdge);
            StopLocalLaunchCountdownMonitor();
            UpdateVariantEditability();
            _preservingLocalVisionForm = false;
            _isValidatingLocalProfile = false;
            RefreshProfileEndpointStatusSurfaces();
            _suppressVariantExpanderSelection = false;
        }
    }

    private async Task<LocalGenerationTimingResult?> WaitUntilLocalPublicEndpointReadyAsync(
        string model,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var health = await _runtimeService.CheckLocalHealthAsync((int)OrchestratorPort, cancellationToken);
            if (health.IsSuccess || health.Status == "Local health check passed")
            {
                var probe = await _runtimeService.TimeShortLocalReplyThroughPublicEndpointAsync(
                    (int)OrchestratorPort,
                    model,
                    cancellationToken,
                    requestTimeoutMs: FluxMuxRuntimeService.PublicEndpointReadyProbeTimeoutMs);
                if (probe.IsSuccess)
                {
                    return probe;
                }

                ReportTuneProgress("Validate to endpoint: public endpoint is not free yet (it may be answering another request). Retrying. " + probe.Details);
            }
            else
            {
                var elapsedSeconds = Math.Max(0, (int)(DateTime.UtcNow - startedUtc).TotalSeconds);
                if (elapsedSeconds == 0 || elapsedSeconds % 5 == 0)
                {
                    ReportTuneProgress("Validate to endpoint: waiting for this profile to finish loading (" + elapsedSeconds + "s).");
                }
            }

            await FlushUiAsync();
            await Task.Delay(4000, cancellationToken);
        }

        return null;
    }

    [RelayCommand]
    private void RevertLocalVariant()
    {
        LoadLocalVariantSettingsEditor();
        RefreshProfileEndpointStatusSurfaces();
        StatusMessage = "Local model profile variant settings and priorities reverted to the last saved state.";
    }

    [RelayCommand]
    private void UseLocalModelDefaults()
    {
        if (!IsLocalVariantEditable)
            return;

        JsonObject settings;
        if (SelectedLocalVariantItem is { IsBase: true })
        {
            settings = _runtimeService.MaterializeLocalProfileSettings(
                new JsonObject(),
                FirstNonEmpty(SelectedLocalProfile, string.Empty),
                LocalModelDirectory,
                NormalizeVariantName(SelectedLocalVariant));
            StatusMessage = "AI-FluxMux auto settings for this local model loaded as unsaved changes. Click Save profile to keep them.";
        }
        else
        {
            settings = ResolveLocalVariantObjectFromConfig(FindLocalModelDefaultVariantName(SelectedLocalProfile) ?? BaseVariantDisplayName);
            StatusMessage = "Model defaults loaded into this local model profile variant. Click Save profile to keep them.";
        }

        LoadLocalVariantSettingsForm(settings);
        LoadLocalPriorityOrder(settings);
        RefreshProfileEndpointStatusSurfaces();
    }

    [RelayCommand]
    private void CreateLocalVariant()
    {
        var variantName = NormalizeVariantName(LocalVariantDraftName);
        if (string.IsNullOrWhiteSpace(variantName))
        {
            StatusMessage = "Enter a local model profile variant name first.";
            return;
        }

        // Allow create as a settings copy; SaveLocalVariant still rejects identical packages.
        if (!EnsureLocalVariantEntry(variantName))
        {
            StatusMessage = "Local model profile variant already exists.";
            return;
        }

        SelectedLocalVariant = variantName;
        LocalVariantDraftName = variantName;
        SaveCurrentSelections();
        EvaluateFirstProfileBootstrapState();
        RevealCreatedLocalVariant(variantName);
        var contextTokens = (int)LocalVariantContext;
        var advice = CurrentLocalHardwareAdvice();
        var gpuGb = advice.GpuTotalGb;
        var copyHint =
            " Change at least one setting, then Save profile — AI-FluxMux will not keep two variants with the same settings package.";
        StatusMessage = contextTokens >= advice.ContextPriorityFirst && gpuGb > 0
            ? $"Local model profile variant created from this profile's settings. Context is first, so the window is {contextTokens} for this GPU ({gpuGb:0.#} GB).{copyHint}"
            : gpuGb > 0
                ? $"Local model profile variant created from this profile's settings. This GPU ({gpuGb:0.#} GB) keeps the context window at {contextTokens}.{copyHint}"
                : "Local model profile variant created from this profile's settings." + copyHint;
    }

    [RelayCommand]
    private void RenameLocalVariant()
    {
        AdoptExpandedLocalEditorSelection();
        if (_isValidatingLocalProfile)
        {
            StatusMessage = "Wait until Validate to endpoint finishes before renaming this model profile.";
            return;
        }

        if (!IsLocalVariantEditable)
        {
            StatusMessage = "Unlock this default first, or Make New Variant to create an editable model profile variant.";
            return;
        }

        var newName = NormalizeVariantName(LocalVariantDraftName);
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusMessage = "Enter a new local model profile variant name first.";
            return;
        }

        if (newName.Equals(NormalizeVariantName(SelectedLocalVariant), StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Enter a different name for this local profile.";
            return;
        }

        if (newName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            && !(SelectedLocalVariantItem?.IsBase ?? false))
        {
            StatusMessage = "Choose a different name. (defaults) is reserved for the model default.";
            return;
        }

        var previousName = NormalizeVariantName(SelectedLocalVariant);
        if (!TryRenameLocalVariantEntry(SelectedLocalVariant, newName))
        {
            StatusMessage = "Local model profile variant rename failed.";
            return;
        }

        if (SelectedLocalVariantItem is { IsBase: true })
        {
            SetLiveLocalProfileLockState(newName, unlocked: true, markAsModelDefault: true);
        }

        _variantTreeRestoreEpoch++;
        _isSynchronizingAdvancedProfileSelection = true;
        try
        {
            SelectedLocalVariantItem?.ApplyVariantName(newName);
            SelectedLocalVariant = newName;
            LocalVariantDraftName = newName;
            RefreshLocalVariantsForSelection(rebuildTree: false);
        }
        finally
        {
            _isSynchronizingAdvancedProfileSelection = false;
        }

        RetargetLocalVariantReferences(SelectedLocalProfile, previousName, newName);
        SaveCurrentSelections();
        var runningThisProfile = IsLocalProfileCurrentlyLoaded(SelectedLocalProfile, newName);
        StatusMessage = runningThisProfile
            ? "Local model profile variant renamed. llama-server is still the running local model; launch settings were not changed."
            : "Local model profile variant renamed.";
    }

    [RelayCommand]
    private async Task DeleteLocalVariant(CloudVariantTreeItemViewModel? profile)
    {
        ActivateLocalAdvancedProfile(profile);

        var target = profile ?? SelectedLocalVariantItem;
        var variantName = ProfileConfigVariantName(target?.Name ?? SelectedLocalVariant);
        var deletingDefault = target is { IsBase: true }
            || variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        if (deletingDefault && target is not { IsUnlocked: true })
        {
            StatusMessage = "Unlock this default first, or Make New Variant to create an editable model profile variant.";
            return;
        }
        if (target is null && string.IsNullOrWhiteSpace(NormalizeVariantName(SelectedLocalVariant)))
        {
            StatusMessage = "Select a local profile to delete.";
            return;
        }

        var label = target?.DisplayName ?? (SelectedLocalProfile + ": " + variantName);
        var fileMissing = IsLocalGgufMissing(SelectedLocalProfile);
        if (deletingDefault)
        {
            var copyCount = CountLocalProfileCopies(SelectedLocalProfile);
            if (!ConfirmDefaultModelProfileDeletion(SelectedLocalProfile, copyCount, fileMissing, isLocal: true))
            {
                StatusMessage = "Local profile delete cancelled.";
                return;
            }

            if (IsLocalModelCurrentlyRunning(SelectedLocalProfile))
            {
                await StopLocalRouteAsync("Deleted local profile");
            }

            if (!TryDeleteLocalProfileFamily(SelectedLocalProfile))
            {
                StatusMessage = "Local profile not found.";
                return;
            }

            RefreshLocalVariantTreeItems();
            SaveCurrentSelections();
            StatusMessage = copyCount > 0
                ? $"Removed {SelectedLocalProfile} and {copyCount} cop{(copyCount == 1 ? "y" : "ies")} from AI-FluxMux. The local model on disk was not deleted."
                : $"Removed {SelectedLocalProfile} from AI-FluxMux. The local model on disk was not deleted.";
            return;
        }

        if (!ConfirmProfileVariantDeletion(label))
        {
            StatusMessage = "Local profile delete cancelled.";
            return;
        }

        if (IsLocalModelCurrentlyRunning(SelectedLocalProfile)
            && NormalizeVariantName(_runningLocalVariant).Equals(variantName, StringComparison.OrdinalIgnoreCase))
        {
            await StopLocalRouteAsync("Deleted local profile");
        }

        if (!TryDeleteLocalVariantEntry(variantName))
        {
            StatusMessage = "Local model profile variant not found.";
            return;
        }

        RefreshLocalVariantsForSelection();
        SelectedLocalVariant = BaseVariantDisplayName;
        SaveCurrentSelections();
        StatusMessage = "Local profile deleted: " + label + ".";
    }

    [RelayCommand]
    private void AddGuidedProfileVariant(string? routeType)
    {
        var isCloud = routeType?.Equals("Cloud", StringComparison.OrdinalIgnoreCase) == true;
        var isLocal = routeType?.Equals("Local", StringComparison.OrdinalIgnoreCase) == true;
        if (!isCloud && !isLocal)
        {
            StatusMessage = "Choose whether to create a cloud or local model profile variant.";
            return;
        }

        if (isLocal)
        {
            AdoptExpandedLocalEditorSelection();
        }
        else
        {
            AdoptExpandedCloudEditorSelection();
        }

        var name = NormalizeVariantName(NewProfileVariantName);
        if (string.IsNullOrWhiteSpace(name))
        {
            ThemedDialog.Warn(
                "Make New Variant",
                "Name your variant first. Type a name in the box beside **Make New Variant**, then click the button again.");
            StatusMessage = "Name your variant first.";
            return;
        }

        // New variants always start from the settings currently shown for this profile.
        if (isCloud)
        {
            CloudVariantDraftName = name;
            CreateCloudVariant();
        }
        else
        {
            LocalVariantDraftName = name;
            CreateLocalVariant();
        }

        if (!StatusMessage.StartsWith("Save failed", StringComparison.OrdinalIgnoreCase)
            && !StatusMessage.Equals("Cloud model profile variant already exists.", StringComparison.Ordinal)
            && !StatusMessage.Equals("Local model profile variant already exists.", StringComparison.Ordinal))
        {
            NewProfileVariantName = string.Empty;
        }
        RefreshProfileTemplateSources();
    }

    [RelayCommand]
    private async Task RefreshNewCloudProfileModels()
    {
        var provider = FirstNonEmpty(NewCloudProfileProvider, SelectedCloudProvider);
        if (string.IsNullOrWhiteSpace(provider))
        {
            StatusMessage = "Choose a cloud provider first.";
            return;
        }

        NewCloudProfileProvider = provider;
        StatusMessage = $"Asking {provider} for current model ids...";
        var catalog = await _runtimeService.ListCloudModelsAsync(provider);
        if (catalog.IsSuccess && catalog.Models.Count > 0)
        {
            SaveCloudModelCatalog(provider, catalog.Models);
            StampDiscoveredCloudProfileTraits(provider, catalog.TraitsByModel);
        }

        RefreshNewCloudProfileModelOptions();
        StatusMessage = catalog.Details;
    }

    [RelayCommand]
    private void CreateNewCloudModelProfile()
    {
        var provider = FirstNonEmpty(NewCloudProfileProvider, SelectedCloudProvider);
        var model = FirstNonEmpty(NewCloudProfileCustomModelId.Trim(), NewCloudProfileSelectedModel);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            StatusMessage = "Choose a cloud provider and a model id, or paste a model id, before creating a cloud profile.";
            return;
        }

        NewCloudProfileProvider = provider;
        NewCloudProfileSelectedModel = model;
        if (TryRetargetExpandedUnvalidatedCloudFamily(provider, model))
        {
            return;
        }

        SelectedCloudProvider = provider;
        SelectedCloudProfile = model;
        SelectedCloudVariant = BaseVariantDisplayName;
        AddIfMissing(CloudProfiles, model);
        AddIfMissing(NewCloudProfileModels, model);
        var created = EnsureCloudVariantEntry(BaseVariantDisplayName);
        SaveCurrentSelections();
        UpdateVariantEditingGate();
        RevealExpandedCloudProfile(provider, model, BaseVariantDisplayName);
        StatusMessage = created
            ? $"Created cloud default model profile for {provider} / {model}. Use Validate to endpoint to offer it in Quick Select."
            : $"Cloud default model profile for {provider} / {model} already exists. Select it below, then use Validate to endpoint if it is not yet offered in Quick Select.";
    }

    private bool CloudFamilyIdentityDraftDiffersFromSaved()
    {
        var item = ResolveActiveCloudEditorItem() ?? SelectedCloudVariantItem;
        if (item is null)
        {
            return false;
        }

        var draftProvider = FirstNonEmpty(
            CloudFamilyProviderDraft,
            FirstNonEmpty(item.Provider, SelectedCloudProvider));
        var draftModel = FirstNonEmpty(CloudFamilyModelIdPaste.Trim(), CloudFamilyModelIdDraft);
        if (string.IsNullOrWhiteSpace(draftModel))
        {
            draftModel = item.ModelName;
        }

        return !draftProvider.Equals(item.Provider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !draftModel.Equals(item.ModelName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnCloudFamilyProviderDraftChanged(string value)
    {
        if (_isValidatingCloudProfile || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var item = ResolveActiveCloudEditorItem() ?? SelectedCloudVariantItem;
        var profileProvider = item?.Provider ?? string.Empty;
        var profileModel = item?.ModelName ?? string.Empty;
        var models = GetCloudModelsForProvider(value);
        ReplaceCloudModelOptions(CloudFamilyModelIdChoices, models);
        if (value.Equals(profileProvider, StringComparison.OrdinalIgnoreCase))
        {
            AddIfMissing(CloudFamilyModelIdChoices, profileModel);
            if (string.IsNullOrWhiteSpace(CloudFamilyModelIdDraft)
                || !CloudFamilyModelIdChoices.Contains(CloudFamilyModelIdDraft))
            {
                CloudFamilyModelIdDraft = profileModel;
            }
        }
        else if (!CloudFamilyModelIdChoices.Contains(CloudFamilyModelIdDraft))
        {
            CloudFamilyModelIdDraft = CloudFamilyModelIdChoices.FirstOrDefault() ?? string.Empty;
        }
    }

    private bool TryCommitPendingCloudFamilyModelId()
    {
        var item = ResolveActiveCloudEditorItem() ?? SelectedCloudVariantItem;
        var fromProvider = item?.Provider ?? string.Empty;
        var fromModel = item?.ModelName ?? string.Empty;
        var toProvider = FirstNonEmpty(CloudFamilyProviderDraft, FirstNonEmpty(fromProvider, FirstNonEmpty(NewCloudProfileProvider, SelectedCloudProvider)));
        var toModel = FirstNonEmpty(CloudFamilyModelIdPaste.Trim(), CloudFamilyModelIdDraft);
        if (string.IsNullOrWhiteSpace(toModel)
            || (toModel.Equals(fromModel, StringComparison.OrdinalIgnoreCase)
                && toProvider.Equals(fromProvider, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return RetargetCloudFamilyModelId(fromProvider, fromModel, toProvider, toModel, announce: false);
    }

    private bool TryRetargetExpandedUnvalidatedCloudFamily(string provider, string toModel)
    {
        var item = ResolveActiveCloudEditorItem() ?? SelectedCloudVariantItem;
        if (item is null
            || string.IsNullOrWhiteSpace(item.Provider)
            || !item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
            || item.ModelName.Equals(toModel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        if (!CloudProfileFamilyRetarget.FamilyExists(profiles, item.Provider, item.ModelName))
        {
            return false;
        }

        if (CloudProfileFamilyRetarget.FamilyExists(profiles, provider, toModel)
            || CloudProfileFamilyRetarget.FamilyIsValidated(profiles, item.Provider, item.ModelName))
        {
            return false;
        }

        return RetargetCloudFamilyModelId(item.Provider, item.ModelName, item.Provider, toModel);
    }

    private bool RetargetCloudFamilyModelId(string fromProvider, string fromModel, string toProvider, string toModel, bool announce = true)
    {
        fromProvider = (fromProvider ?? string.Empty).Trim();
        fromModel = (fromModel ?? string.Empty).Trim();
        toProvider = (toProvider ?? string.Empty).Trim();
        toModel = (toModel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toProvider) || string.IsNullOrWhiteSpace(toModel))
        {
            StatusMessage = "Choose a cloud provider and a model id, then click Save profile.";
            return false;
        }

        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            StatusMessage = "No cloud profiles are stored yet.";
            return false;
        }

        if (!CloudProfileFamilyRetarget.TryRewriteProfiles(profiles, fromProvider, fromModel, toProvider, toModel, out var moved, out var error))
        {
            StatusMessage = error;
            return false;
        }

        if (_config.Root["RouteSlots"] is JsonArray slots)
        {
            CloudProfileFamilyRetarget.RewriteRouteSlots(slots, fromProvider, fromModel, toModel, toProvider);
        }

        foreach (var slot in QuickSelectSlots.Where(slot =>
                     slot.IsCloudRoute
                     && slot.CloudProvider.Equals(fromProvider, StringComparison.OrdinalIgnoreCase)
                     && slot.CloudModel.Equals(fromModel, StringComparison.OrdinalIgnoreCase)))
        {
            slot.CloudProvider = toProvider;
            slot.RetargetCloudModel(toModel);
        }

        SelectedCloudProvider = toProvider;
        SelectedCloudProfile = toModel;
        AddIfMissing(CloudProfiles, toModel);
        AddIfMissing(NewCloudProfileModels, toModel);
        CloudFamilyProviderDraft = toProvider;
        CloudFamilyModelIdDraft = toModel;
        CloudFamilyModelIdPaste = string.Empty;
        if (_mountedCloudProvider.Equals(fromProvider, StringComparison.OrdinalIgnoreCase)
            && _mountedCloudModel.Equals(fromModel, StringComparison.OrdinalIgnoreCase))
        {
            RememberMountedCloud(toProvider, toModel, FirstNonEmpty(_mountedCloudVariant, SelectedCloudVariant));
        }

        SaveCurrentSelections();
        RefreshCloudVariantTreeItems();
        RefreshCloudFamilyModelIdEditor(bindDraftToCurrent: true);
        RefreshProfileWarningSurfaces();
        UpdateRoutingPoolStatus();
        if (announce)
        {
            StatusMessage = moved == 1
                ? (fromProvider.Equals(toProvider, StringComparison.OrdinalIgnoreCase)
                    ? $"{toProvider} profile now uses {toModel}. Settings were kept. Run Validate to endpoint."
                    : $"{toProvider} / {toModel} now owns this profile (was {fromProvider} / {fromModel}). Settings were kept. Run Validate to endpoint.")
                : (fromProvider.Equals(toProvider, StringComparison.OrdinalIgnoreCase)
                    ? $"{toProvider} profile and {moved - 1} cop{(moved == 2 ? "y" : "ies")} now use {toModel}. Settings were kept. Run Validate to endpoint."
                    : $"{toProvider} / {toModel} now owns this profile family (was {fromProvider} / {fromModel}). Settings were kept. Run Validate to endpoint.");
        }

        return true;
    }

    private void RefreshCloudFamilyModelIdEditor(bool bindDraftToCurrent = false)
    {
        if (_isValidatingCloudProfile)
        {
            return;
        }

        var item = ResolveActiveCloudEditorItem() ?? SelectedCloudVariantItem;
        var provider = FirstNonEmpty(item?.Provider, SelectedCloudProvider);
        var current = FirstNonEmpty(item?.ModelName, SelectedCloudProfile);
        if (bindDraftToCurrent
            || string.IsNullOrWhiteSpace(CloudFamilyProviderDraft)
            || !CloudProviders.Contains(CloudFamilyProviderDraft))
        {
            CloudFamilyProviderDraft = provider;
        }

        var models = GetCloudModelsForProvider(CloudFamilyProviderDraft);
        ReplaceCloudModelOptions(CloudFamilyModelIdChoices, models);
        AddIfMissing(CloudFamilyModelIdChoices, current);
        if (bindDraftToCurrent || string.IsNullOrWhiteSpace(CloudFamilyModelIdDraft) || !CloudFamilyModelIdChoices.Contains(CloudFamilyModelIdDraft))
        {
            CloudFamilyModelIdDraft = current;
        }
    }

    [RelayCommand]
    private void CreateNewLocalModelProfile()
    {
        var model = FirstNonEmpty(NewLocalProfileModel, SelectedLocalProfile);
        if (string.IsNullOrWhiteSpace(model))
        {
            StatusMessage = "Choose a discovered local model before creating a local model profile.";
            return;
        }

        if (ResolveLocalModelPath(model) is null)
        {
            StatusMessage = "That name is not a local model in the model directory. Set the model folder in Servers, then choose a discovered local model.";
            return;
        }

        if (LocalHealthGuidance.IsVisionProjectorFile(model))
        {
            StatusMessage = LocalHealthGuidance.FormatVisionProjectorAsModelWarning(model);
            return;
        }

        NewLocalProfileModel = model;
        SelectedLocalProfile = model;
        SelectedLocalVariant = BaseVariantDisplayName;
        AddIfMissing(LocalProfiles, model);
        var created = EnsureLocalVariantEntry(BaseVariantDisplayName);
        SaveCurrentSelections();
        UpdateVariantEditingGate();
        RevealCreatedLocalVariant(BaseVariantDisplayName);
        StatusMessage = created
            ? $"Created local default model profile for {model}. Use Validate to endpoint to offer it in Quick Select."
            : $"Local default model profile for {model} already exists. Select it below, then use Validate to endpoint if it is not yet offered in Quick Select.";
    }

    [RelayCommand]
    private void ValidateCloudVariantProfile()
    {
        StatusMessage = "Cloud profile validation is not yet implemented because AI-FluxMux does not yet send these model profile controls to the provider.";
    }

    [RelayCommand]
    private void ValidateLocalVariantProfile()
    {
        if (!IsLocalVariantEditable)
        {
            StatusMessage = "Select an editable local derivative before validating it.";
            return;
        }

        if (ResolveSelectedLocalModelPath() is null)
        {
            StatusMessage = "Local profile validation failed: the selected local model is not present in the model directory.";
            return;
        }

        if (LocalVariantContext < 1024 || LocalVariantContext > DeepSeekHarnessSetup.MaxLocalContextWindow || LocalVariantTemperature is < 0 or > 2)
        {
            StatusMessage = "Local profile validation failed: context or temperature is outside the supported editor range.";
            return;
        }

        ApplyLocalVariantSettings(NormalizeVariantName(SelectedLocalVariant));
        var settings = ResolveLocalVariantObjectFromConfig(NormalizeVariantName(SelectedLocalVariant));
        settings["ProfileValidatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        SaveCurrentSelections();
        RefreshLocalVariantsForSelection();
        StatusMessage = "Local derivative structure validated and saved. Runtime launch validation is still required separately.";
    }

    [RelayCommand]
    private void OpenExternalLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusMessage = "No link available for this item.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            StatusMessage = "Opened the selected link.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open link: " + ex.Message;
        }
    }

    private void EvaluateFirstProfileBootstrapState()
    {
        RefreshSlotPresenceAndActions();

        IsFirstProfileBootstrapPending = !HasSelection1Slot || !IsSelection1TemplateChosen;
        ShowSelectionEditors = HasSelection1Slot && IsSelection1TemplateChosen;
        ShowBootstrapTemplateChooser = !ShowSelectionEditors;
        ShowBootstrapCreateFirstProfile = false;
        IsSelectionEditorsEnabled = ShowSelectionEditors;
        FirstProfileBootstrapText = "Choose Cloud or Local to start the first profile template for Selection 1.";

        UpdateVariantEditingGate();
    }

    private void RefreshSlotPresenceAndActions()
    {
        HasSelection1Slot = _config.Root["RouteSlots"] is JsonArray slots
            && slots.OfType<JsonObject>().Any(x => string.Equals(x["slotId"]?.ToString(), "slot-cloud-1", StringComparison.OrdinalIgnoreCase));

        if (_config.Root["RouteSlots"] is JsonArray existingSlots)
        {
            var selection1 = existingSlots.OfType<JsonObject>().FirstOrDefault(x => string.Equals(x["slotId"]?.ToString(), "slot-cloud-1", StringComparison.OrdinalIgnoreCase));
            IsSelection1TemplateChosen = !string.IsNullOrWhiteSpace(selection1?["routeType"]?.ToString());
        }

        ShowAddNewSlotButton = HasSelection1Slot && !IsSelection2Enabled;
        ShowDeleteSelection1SlotButton = HasSelection1Slot;
        ShowSelection2TemplateChooser = IsSelection2Enabled && !IsSelection2TemplateChosen;
        RefreshRouteSlotIndicators();
    }

    private void EnsureSelection2SlotExists(JsonArray slots)
    {
        var selection2 = slots.OfType<JsonObject>().FirstOrDefault(x => string.Equals(x["slotId"]?.ToString(), "slot-local-1", StringComparison.OrdinalIgnoreCase));
        if (selection2 is not null)
        {
            selection2["enabled"] = true;
            IsSelection2TemplateChosen = !string.IsNullOrWhiteSpace(selection2["routeType"]?.ToString());
            return;
        }

        selection2 = BuildRouteSlot(
            slotId: "slot-local-1",
            label: "Selection 2",
            routeType: string.Empty,
            provider: string.Empty,
            cloudModel: string.Empty,
            cloudVariant: string.Empty,
            localModel: string.Empty,
            localVariant: string.Empty,
            pinned: true);
        selection2["enabled"] = true;
        slots.Add(selection2);

        IsSelection2TemplateChosen = false;
        SelectedSelection2RouteType = string.Empty;
        SelectedSelection2CloudProvider = SelectedCloudProvider;
        SelectedSelection2CloudProfile = string.Empty;
        SelectedSelection2CloudVariant = BaseVariantDisplayName;
    }

    private void UpdateRequestRoutingModeText()
    {
        if (!RequestRoutingEnabled)
        {
            RequestRoutingModeText = string.Empty;
        }
        else
        {
            RequestRoutingModeText =
                "**Dynamic Model Routing.** **Instant Switching:** hop between an active local and a ready cloud in the same chat without losing context. "
                + "**Local-to-Local Delay:** switching between different local models reloads llama-server into VRAM — slower, and that chat's in-memory context is cleared. "
                + "**Smart Economy:** starts local, then offers the ready cloud if this turn would spill into RAM or cloud is requested. Override that anytime in **Cloud Usage Mode**.";
        }

        OnPropertyChanged(nameof(ShowRequestRoutingModeText));
    }

    private static bool ConfirmSlotDeletion(string slotLabel)
    {
        var message = "Delete " + slotLabel + "? This removes the slot from active favorites.";
        return ThemedDialog.Confirm("Delete slot", message, "Delete", "Keep slot");
    }

    private static bool ConfirmStopLocalForValidate(bool endpointTurnInFlight)
        => ThemedDialog.Confirm(
            LocalValidateLaunchPolicy.ConfirmStopTitle,
            LocalValidateLaunchPolicy.ConfirmStopMessage(endpointTurnInFlight),
            "Stop and validate",
            "Keep running");

    private static bool ConfirmProfileVariantDeletion(string profileLabel)
    {
        var message = "Delete this model profile variant (" + profileLabel
            + ")? The default model profile is kept.";
        return ThemedDialog.Confirm("Delete model profile variant", message, "Delete", "Keep variant");
    }

    private static bool ConfirmDefaultModelProfileDeletion(
        string modelLabel,
        int copyCount,
        bool fileMissing,
        bool isLocal)
    {
        var copies = copyCount <= 0
            ? "No editable model profile variants are attached."
            : copyCount == 1
                ? "This also removes 1 editable model profile variant."
                : $"This also removes {copyCount} editable model profile variants.";
        var disk = isLocal
            ? (fileMissing
                ? "The local model is already missing from the model folder. This only removes the model profile from AI-FluxMux."
                : "This does not delete the local model from your model folder. You can create a default model profile again later.")
            : "This does not change anything at the cloud provider. You can create a default model profile again later.";
        var running = isLocal
            ? " If this model is currently launched, AI-FluxMux will stop it first."
            : string.Empty;
        var message = "Remove this model's default model profile from AI-FluxMux ("
                      + modelLabel
                      + ")? "
                      + copies
                      + " "
                      + disk
                      + running;
        return ThemedDialog.Confirm("Remove model from AI-FluxMux", message, "Remove", "Keep");
    }

    private string BuildSelection2CandidateFingerprint()
    {
        if (!IsSelection2TemplateChosen)
        {
            return string.Empty;
        }

        var routeType = NormalizeSelectionRouteType(SelectedSelection2RouteType);
        if (routeType.Equals("Cloud", StringComparison.Ordinal))
        {
            return BuildSlotSettingsFingerprint(
                routeType: "cloud",
                provider: SelectedSelection2CloudProvider,
                cloudModel: SelectedSelection2CloudProfile,
                cloudVariant: SelectedSelection2CloudVariant,
                localModel: string.Empty,
                localVariant: string.Empty);
        }

        return BuildSlotSettingsFingerprint(
            routeType: "local",
            provider: string.Empty,
            cloudModel: string.Empty,
            cloudVariant: string.Empty,
            localModel: SelectedLocalProfile,
            localVariant: SelectedLocalVariant);
    }

    private static string BuildSlotSettingsFingerprint(JsonObject slot)
    {
        var routeType = (slot["routeType"]?.ToString() ?? string.Empty).Trim();
        var provider = (slot["provider"]?.ToString() ?? string.Empty).Trim();
        var cloudModel = (slot["cloudModel"]?.ToString() ?? string.Empty).Trim();
        var cloudVariant = (slot["cloudVariant"]?.ToString() ?? string.Empty).Trim();
        var localModel = (slot["localModel"]?.ToString() ?? string.Empty).Trim();
        var localVariant = (slot["localVariant"]?.ToString() ?? string.Empty).Trim();
        return BuildSlotSettingsFingerprint(routeType, provider, cloudModel, cloudVariant, localModel, localVariant);
    }

    private static string BuildSlotSettingsFingerprint(
        string routeType,
        string provider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant)
    {
        if (routeType.Equals("cloud", StringComparison.OrdinalIgnoreCase))
        {
            return "cloud|" +
                   (provider ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   (cloudModel ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   NormalizeVariantName(cloudVariant).ToLowerInvariant();
        }

        return "local|" +
               (localModel ?? string.Empty).Trim().ToLowerInvariant() + "|" +
               NormalizeVariantName(localVariant).ToLowerInvariant();
    }

    private void MarkMatchingDefaultProfilesValidated(
        string routeType,
        string provider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant)
    {
        var candidateFingerprint = BuildSlotSettingsFingerprint(routeType, provider, cloudModel, cloudVariant, localModel, localVariant);
        if (!EnsureDefaultProfileEndpointValidated(
            routeType,
            provider,
            cloudModel,
            localModel,
            out var createdNewProfile))
        {
            return;
        }

        if (_config.Root["RouteSlots"] is JsonArray slots)
        {
            foreach (var slot in slots.OfType<JsonObject>())
            {
                if (!ParseBool(slot["enabled"]?.ToString(), true))
                {
                    continue;
                }

                if (!BuildSlotSettingsFingerprint(slot).Equals(candidateFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                slot["validatedDefaultProfile"] = true;
                slot["endpointValidatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            }
        }

        UpdateVariantEditingGate();
        _configService.Save(_config);
        RefreshVariantTreeAfterValidationChange(
            isCloud: routeType.Equals("cloud", StringComparison.OrdinalIgnoreCase),
            structureChanged: createdNewProfile);
        RefreshSwitchBenchmarkProfiles();
    }

    private bool EnsureDefaultProfileEndpointValidated(
        string routeType,
        string provider,
        string cloudModel,
        string localModel,
        out bool createdNewProfile)
    {
        createdNewProfile = false;
        var isCloud = routeType.Equals("cloud", StringComparison.OrdinalIgnoreCase);
        var collectionName = isCloud ? "CloudProfiles" : "LocalProfiles";
        if (_config.Root[collectionName] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root[collectionName] = profiles;
        }

        if (isCloud && (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(cloudModel)))
        {
            return false;
        }

        if (!isCloud && string.IsNullOrWhiteSpace(localModel))
        {
            return false;
        }

        var targetKey = isCloud
            ? $"{provider.Trim()}::{cloudModel.Trim()}::{BaseVariantDisplayName}"
            : $"{localModel.Trim()}::{BaseVariantDisplayName}";
        var matchingKey = profiles.Select(entry => entry.Key)
            .FirstOrDefault(key => key.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            matchingKey = targetKey;
            profiles[matchingKey] = new JsonObject();
            createdNewProfile = true;
        }

        if (profiles[matchingKey] is not JsonObject profile)
        {
            return false;
        }

        profile["EndpointValidatedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        return true;
    }

    private void UpdateVariantEditingGate()
    {
        var hasProfile = _config.GetObjectKeys("CloudProfiles").Count > 0
            || _config.GetObjectKeys("LocalProfiles").Count > 0;
        IsVariantEditingEnabled = hasProfile;
        VariantEditingGateText = hasProfile
            ? "Model profiles are available. Validate a default or copy to endpoint before it appears in Quick Select."
            : "Create a local or cloud model profile, then use Validate to endpoint before it appears in Quick Select.";
    }

    private void PromoteSelection2IntoSelection1(JsonObject selection2Slot)
    {
        var routeType = NormalizeSelectionRouteType(selection2Slot["routeType"]?.ToString());
        if (routeType.Equals("Cloud", StringComparison.Ordinal))
        {
            SelectedSelection1RouteType = "Cloud";
            SelectedCloudProfile = FirstNonEmpty(selection2Slot["cloudModel"]?.ToString() ?? string.Empty, SelectedCloudProfile);
            SelectedCloudVariant = FirstNonEmpty(selection2Slot["cloudVariant"]?.ToString() ?? string.Empty, SelectedCloudVariant);
        }
        else
        {
            SelectedSelection1RouteType = "Local";
            SelectedSelection1LocalProfile = FirstNonEmpty(selection2Slot["localModel"]?.ToString() ?? string.Empty, SelectedSelection1LocalProfile);
            SelectedSelection1LocalVariant = FirstNonEmpty(selection2Slot["localVariant"]?.ToString() ?? string.Empty, SelectedSelection1LocalVariant);
        }

        EnsureSelection1SlotAssigned(
            routeType.Equals("Cloud", StringComparison.Ordinal) ? "cloud" : "local",
            routeType.Equals("Cloud", StringComparison.Ordinal) ? SelectedCloudProfile : SelectedSelection1LocalProfile,
            routeType.Equals("Cloud", StringComparison.Ordinal) ? SelectedCloudVariant : SelectedSelection1LocalVariant);
    }

    private void ApplyLocalDiscoveryResult(LocalEndpointDiscoveryResult result)
    {
        LocalServerDiscoveryText = result.Details;
        RefreshInstalledLocalServerOptions(result.InstalledServers);
        DiscoveredLocalServerOptions.Clear();

        foreach (var server in result.CompatibleServers)
        {
            var display = server.OwnerPid > 0
                ? $"localhost:{server.Port} ({server.OwnerName}, PID {server.OwnerPid})"
                : $"localhost:{server.Port} ({server.OwnerName})";

            DiscoveredLocalServerOptions.Add(new DiscoveredLocalServerOption
            {
                Port = server.Port,
                Display = display
            });
        }

        ShowDiscoveredLocalServerPicker = DiscoveredLocalServerOptions.Count > 0;
        SelectedDiscoveredLocalServer = DiscoveredLocalServerOptions
            .FirstOrDefault(x => x.Port == result.Port)
            ?? DiscoveredLocalServerOptions.FirstOrDefault();

        DiscoveredLocalServerPort = SelectedDiscoveredLocalServer?.Port ?? (result.Compatible ? result.Port : 0);
        CanAdoptDiscoveredLocalServer = (SelectedDiscoveredLocalServer is not null || DiscoveredLocalServerPort > 0)
                                      && DiscoveredLocalServerPort > 0;
    }

    private void RefreshInstalledLocalServerOptions(IReadOnlyList<InstalledLocalServerCandidate>? candidates = null)
    {
        candidates ??= _runtimeService.GetInstalledLocalServers();
        InstalledLocalServerOptions.Clear();

        foreach (var server in candidates)
        {
            InstalledLocalServerOptions.Add(new InstalledLocalServerOption
            {
                ExecutablePath = server.ExecutablePath,
                Display = $"{server.Name} - {server.ExecutablePath}"
            });
        }

        SelectedInstalledLocalServer = InstalledLocalServerOptions
            .FirstOrDefault(x => x.ExecutablePath.Equals(LocalServerExecutablePath, StringComparison.OrdinalIgnoreCase))
            ?? InstalledLocalServerOptions.FirstOrDefault();

        LocalServerInstallationText = InstalledLocalServerOptions.Count switch
        {
            0 => "No compatible local server installation detected.",
            1 => $"Detected compatible server: {InstalledLocalServerOptions[0].ExecutablePath}",
            _ => $"Detected {InstalledLocalServerOptions.Count} compatible server installations. Select which executable AI-FluxMux should manage."
        };
    }

    private void EnsureSelection1SlotAssigned(string routeType, string profileName, string variantName)
    {
        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            slots = new JsonArray();
            _config.Root["RouteSlots"] = slots;
        }

        var slotObjects = slots.OfType<JsonObject>().ToList();
        var selection1 = slotObjects.FirstOrDefault(x => string.Equals(x["slotId"]?.ToString(), "slot-cloud-1", StringComparison.OrdinalIgnoreCase));
        if (selection1 is null)
        {
            selection1 = BuildRouteSlot(
                slotId: "slot-cloud-1",
                label: "Selection 1",
                routeType: routeType,
                provider: string.Empty,
                cloudModel: string.Empty,
                cloudVariant: string.Empty,
                localModel: string.Empty,
                localVariant: string.Empty,
                pinned: true);
            slots.Add(selection1);
        }

        if (routeType.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            selection1["routeType"] = "local";
            selection1["provider"] = string.Empty;
            selection1["cloudModel"] = string.Empty;
            selection1["cloudVariant"] = string.Empty;
            selection1["localModel"] = profileName;
            selection1["localVariant"] = NormalizeVariantName(variantName);
        }
        else
        {
            selection1["routeType"] = "cloud";
            selection1["provider"] = SelectedCloudProvider;
            selection1["cloudModel"] = profileName;
            selection1["cloudVariant"] = NormalizeVariantName(variantName);
            selection1["localModel"] = string.Empty;
            selection1["localVariant"] = string.Empty;
        }

        var selection2 = slotObjects.FirstOrDefault(x => string.Equals(x["slotId"]?.ToString(), "slot-local-1", StringComparison.OrdinalIgnoreCase));
        if (selection2 is null)
        {
            selection2 = BuildRouteSlot(
                slotId: "slot-local-1",
                label: "Selection 2",
                routeType: "local",
                provider: string.Empty,
                cloudModel: string.Empty,
                cloudVariant: string.Empty,
                localModel: FirstNonEmpty(SelectedLocalProfile, profileName),
                localVariant: NormalizeVariantName(SelectedLocalVariant),
                pinned: true);
            slots.Add(selection2);
        }

        selection2["enabled"] = IsSelection2Enabled;

        _config.SetStringArray("ActiveRouteSlotIds", IsSelection2Enabled ? ["slot-cloud-1", "slot-local-1"] : ["slot-cloud-1"]);
        _config.SetInt("MaxVisibleRouteSlots", IsSelection2Enabled ? 2 : 1);
        _config.SetString("RouteSlotUiLayout", "dual-pane");
        _config.SetInt("RouteSlotSchemaVersion", RouteSlotSchemaVersion);
        HasSelection1Slot = true;
    }

    [RelayCommand]
    private void CaptureDependencyBaseline()
    {
        var snapshot = BuildCurrentDependencySnapshot().ToList();
        if (snapshot.Count == 0)
        {
            StatusMessage = "No environment snapshot available yet. Run Refresh first.";
            return;
        }

        _config.SetObjectArray("DependencyKnownGoodBaseline", snapshot);
        _config.SetString("DependencyKnownGoodBaselineCapturedAt", DateTime.UtcNow.ToString("o"));
        _configService.Save(_config);
        UpdateDependencyRiskPanels();
        StatusMessage = "Captured the current llama-server version and configured paths as the known-good snapshot.";
    }

    [RelayCommand]
    private void ExportDependencyBom()
    {
        var snapshot = BuildCurrentDependencySnapshot().ToList();
        if (snapshot.Count == 0)
        {
            StatusMessage = "No environment snapshot available yet. Run Refresh first.";
            return;
        }

        var configDir = Path.GetDirectoryName(_configService.ConfigPath) ?? Directory.GetCurrentDirectory();
        var workspaceRoot = Directory.GetParent(configDir)?.FullName ?? configDir;
        var logDir = Path.Combine(workspaceRoot, "Logs");
        Directory.CreateDirectory(logDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var filePath = Path.Combine(logDir, $"Dependency_BOM_{timestamp}.json");

        var payload = new JsonObject
        {
            ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
            ["activeProvider"] = SelectedCloudProvider,
            ["activeCloudModel"] = SelectedCloudProfile,
            ["activeLocalModel"] = SelectedLocalProfile,
            ["modelDirectory"] = LocalModelDirectory,
            ["orchestratorPort"] = (int)OrchestratorPort,
            ["hardwareSummary"] = DependencySummary,
            ["dependencyScope"] = DependencyScopeHelpText,
            ["pinPolicy"] = DependencyPinPolicyText,
            ["entries"] = new JsonArray(snapshot.ToArray())
        };

        File.WriteAllText(filePath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        StatusMessage = "Dependency BOM exported: " + filePath;
    }

    private void LoadFromConfig()
    {
        _isLoadingConfig = true;
        try
        {
        SelectedCloudProvider = FirstNonEmpty(_config.GetString("ActiveCloud", SelectedCloudProvider), SelectedCloudProvider);
        SelectedCloudProfile = FirstNonEmpty(_config.GetString("ActiveCloudModel", SelectedCloudProfile), SelectedCloudProfile);
        SelectedCloudVariant = FirstNonEmpty(_config.GetString("ActiveCloudVariant", SelectedCloudVariant), SelectedCloudVariant);
        SelectedLocalProfile = FirstNonEmpty(_config.GetString("ActiveLocalModel", SelectedLocalProfile), SelectedLocalProfile);
        SelectedLocalVariant = FirstNonEmpty(_config.GetString("ActiveLocalVariant", SelectedLocalVariant), SelectedLocalVariant);
        SelectedSelection1RouteType = NormalizeSelectionRouteType(_config.GetString("Selection1RouteType", SelectedSelection1RouteType));
        SelectedSelection1LocalProfile = FirstNonEmpty(_config.GetString("Selection1LocalModel", SelectedSelection1LocalProfile), SelectedSelection1LocalProfile);
        SelectedSelection1LocalVariant = FirstNonEmpty(_config.GetString("Selection1LocalVariant", SelectedSelection1LocalVariant), SelectedSelection1LocalVariant);
        IsSelection2TemplateChosen = _config.GetBool("Selection2TemplateChosen", IsSelection2TemplateChosen);
        var loadedSelection2RouteType = _config.GetString("Selection2RouteType", SelectedSelection2RouteType);
        SelectedSelection2RouteType = IsSelection2TemplateChosen
            ? NormalizeSelectionRouteType(loadedSelection2RouteType)
            : loadedSelection2RouteType;
        SelectedSelection2CloudProfile = FirstNonEmpty(_config.GetString("Selection2CloudModel", SelectedSelection2CloudProfile), SelectedSelection2CloudProfile);
        SelectedSelection2CloudProvider = FirstNonEmpty(_config.GetString("Selection2CloudProvider", SelectedSelection2CloudProvider), SelectedCloudProvider);
        SelectedSelection2CloudVariant = FirstNonEmpty(_config.GetString("Selection2CloudVariant", SelectedSelection2CloudVariant), SelectedSelection2CloudVariant);
        IsSelection2Enabled = _config.GetBool("Selection2Enabled", _config.GetStringArray("ActiveRouteSlotIds").Count > 1);
        LocalModelDirectory = FirstNonEmpty(_config.GetString("ModelDirectory", LocalModelDirectory), LocalModelDirectory);
        if (CustomCloudProviderRegistry.EnsureMigrated(_config.Root))
        {
            _configService.Save(_config);
        }

        try
        {
            if (File.Exists(_secretsPath))
            {
                var secrets = JsonNode.Parse(File.ReadAllText(_secretsPath)) as JsonObject ?? new JsonObject();
                if (CustomCloudProviderRegistry.EnsureSecretsMigrated(secrets))
                {
                    File.WriteAllText(_secretsPath, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch
        {
        }

        RefreshCloudProvidersList();
        RefreshCustomCompatEndpointFromStore();
        LocalServerExecutablePath = _config.GetString("LocalServerExecutablePath", LocalServerExecutablePath);
        UpdateFeedUrl = FirstNonEmpty(_config.GetString("UpdateFeedUrl", UpdateFeedUrl), UpdateFeedUrl);

        var port = _config.GetInt("OrchestratorPort", (int)OrchestratorPort);
        if (port < 1 || port > 65535)
        {
            port = 8080;
        }

        OrchestratorPort = port;
        SelectedEndpointApp = FirstNonEmpty(
            _config.GetString("EndpointApp", SelectedEndpointApp),
            "Cline");
        SyncEndpointAppToRuntime();
        var harnessWebPort = _config.GetInt("HarnessWebPort", (int)HarnessWebPort);
        if (harnessWebPort is >= 1 and <= 65535)
        {
            HarnessWebPort = harnessWebPort;
        }

        IdleTimeoutEnabled = _config.GetBool("IdleTimeoutEnabled", IdleTimeoutEnabled);
        GenerationSpeedTelemetryEnabled = _config.GetBool("GenerationSpeedTelemetryEnabled", GenerationSpeedTelemetryEnabled);
        _runtimeService.SetGenerationSpeedTelemetryEnabled(GenerationSpeedTelemetryEnabled);
        IdleTimeoutMinutes = IdleTimeoutPolicy.ClampMinutes(
            _config.GetInt("IdleTimeoutMinutes", IdleTimeoutPolicy.DefaultMinutes));
        RequestRoutingEnabled = _config.GetBool("RequestRoutingEnabled", RequestRoutingEnabled);
        InterventionPopupsEnabled = _config.GetBool("InterventionPopupsEnabled", RequestRoutingEnabled);
        _runtimeService.SetRequestRoutingTopology(RequestRoutingTopology.DualHot);
        UpdateRequestRoutingModeText();
        SelectedCloudRoutingCapacity = NormalizeCloudRoutingCapacityDisplay(
            _config.GetString("CloudRoutingCapacity", SelectedCloudRoutingCapacity));
        LocalExperimentalHardwareTuneEnabled = _config.GetBool("LocalExperimentalHardwareTuneEnabled", LocalExperimentalHardwareTuneEnabled);
        LocalVisionEnabled = _config.GetBool("LocalVisionEnabled", LocalVisionEnabled);
        LocalVisionProjectorPath = FirstNonEmpty(_config.GetString("LocalVisionProjectorPath", LocalVisionProjectorPath), LocalVisionProjectorPath);

        var maxImageEdge = _config.GetInt("LocalVisionMaxImageEdge", (int)LocalVisionMaxImageEdge);
        if (maxImageEdge < 256 || maxImageEdge > 4096)
        {
            maxImageEdge = 1344;
        }

        LocalVisionMaxImageEdge = maxImageEdge;
        SelectedSwitchBenchmarkOrder = NormalizeSwitchBenchmarkOrder(
            _config.GetString("SwitchBenchmarkOrder", SelectedSwitchBenchmarkOrder));
        var savedPriorityOrder = _config.GetStringArray("LocalPriorityOrder");
        if (savedPriorityOrder.Count > 0)
        {
            SetLocalPriorityOrder(savedPriorityOrder, persistToConfig: false);
        }
        var savedCloudPriorityOrder = _config.GetStringArray("CloudPriorityOrder");
        if (savedCloudPriorityOrder.Count > 0)
        {
            SetCloudPriorityOrder(savedCloudPriorityOrder, persistToConfig: false);
        }
        UpdateDependencyRiskPanels();
        EnsureRouteSlotConfigInitialized();
        ReconcileValidatedQuickSlotDefaults();

        HydrateProfileLists();

        if (string.IsNullOrWhiteSpace(SelectedCloudProvider))
        {
            SelectedCloudProvider = CloudProviders.FirstOrDefault() ?? "Gemini";
        }

        if (string.IsNullOrWhiteSpace(SelectedCloudProfile))
        {
            SelectedCloudProfile = CloudProfiles.FirstOrDefault() ?? "gemini-3.7-flash";
        }

        if (string.IsNullOrWhiteSpace(SelectedLocalProfile))
        {
            SelectedLocalProfile = LocalProfiles.FirstOrDefault() ?? "Local Balanced";
        }

        ApplyCloudTemplateDefaults();
        ApplyLocalTemplateDefaultsForSelection1();
        RefreshLocalProfilesFromModelDirectory();
        RefreshCloudProfilesForProvider();
        RefreshLocalVariantsForSelection(refreshEndpointStatus: false);
        EnsureSelection1LocalDefaults();
        EnsureSelection2CloudDefaults();
        EvaluateFirstProfileBootstrapState();
        RefreshVariantSettingsEditors();
        RefreshProfileEndpointStatusSurfaces();
        UpdateRoutingPoolStatus();
        SetCapabilitySummaries();
        ApplySavedWindowPreferences();
        UpdateLocalPrioritySummaryTexts();
        UpdateVariantEditingGate();
        RefreshCloudApiKeyFieldFromStore();
        StatusMessage = "Loaded configuration from " + _configService.ConfigPath;
        OnPropertyChanged(nameof(EndpointModelAddressText));
        OnPropertyChanged(nameof(HarnessServersSummaryText));
        RefreshClineSettingsStatus();
        TryAdoptHarnessFromPriorSession();
        RefreshHarnessProcessState();
        _ = RefreshHarnessWebUiStatusAsync();
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void ReconcileValidatedQuickSlotDefaults()
    {
        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            return;
        }

        var changed = false;
        foreach (var slot in slots.OfType<JsonObject>()
                     .Where(slot => ParseBool(slot["enabled"]?.ToString(), true))
                     .Where(slot => ParseBool(slot["validatedDefaultProfile"]?.ToString(), false)))
        {
            var routeType = slot["routeType"]?.ToString() ?? string.Empty;
            var validatedUtc = FirstNonEmpty(
                slot["endpointValidatedUtc"]?.ToString() ?? string.Empty,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            var isCloud = routeType.Equals("cloud", StringComparison.OrdinalIgnoreCase);
            var collectionName = isCloud ? "CloudProfiles" : "LocalProfiles";
            if (_config.Root[collectionName] is not JsonObject profiles)
            {
                profiles = new JsonObject();
                _config.Root[collectionName] = profiles;
            }

            var key = isCloud
                ? $"{(slot["provider"]?.ToString() ?? string.Empty).Trim()}::{(slot["cloudModel"]?.ToString() ?? string.Empty).Trim()}::{BaseVariantDisplayName}"
                : $"{(slot["localModel"]?.ToString() ?? string.Empty).Trim()}::{BaseVariantDisplayName}";
            if (key.StartsWith("::", StringComparison.Ordinal) || key.StartsWith(BaseVariantDisplayName, StringComparison.Ordinal))
            {
                continue;
            }

            var matchingKey = profiles.Select(entry => entry.Key)
                .FirstOrDefault(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(matchingKey))
            {
                matchingKey = key;
                profiles[matchingKey] = new JsonObject();
                changed = true;
            }

            if (profiles[matchingKey] is JsonObject profile
                && string.IsNullOrWhiteSpace(profile["EndpointValidatedUtc"]?.ToString()))
            {
                profile["EndpointValidatedUtc"] = validatedUtc;
                changed = true;
            }
        }

        if (changed)
        {
            _configService.Save(_config);
        }
    }

    private void EnsureRouteSlotConfigInitialized()
    {
        var version = _config.GetInt("RouteSlotSchemaVersion", 0);
        if (version >= RouteSlotSchemaVersion && _config.Root["RouteSlots"] is JsonArray existingSlots && existingSlots.Count > 0)
        {
            ApplySelectionsFromRouteSlots(existingSlots);
            return;
        }

        var slots = new JsonArray
        {
            BuildRouteSlot(
                slotId: "slot-cloud-1",
                label: "Selection 1",
                routeType: string.Empty,
                provider: string.Empty,
                cloudModel: string.Empty,
                cloudVariant: string.Empty,
                localModel: string.Empty,
                localVariant: string.Empty,
                pinned: true)
        };

        if (IsSelection2Enabled)
        {
            var selection2Slot = BuildRouteSlot(
                slotId: "slot-local-1",
                label: "Selection 2",
                routeType: string.Empty,
                provider: string.Empty,
                cloudModel: string.Empty,
                cloudVariant: string.Empty,
                localModel: string.Empty,
                localVariant: string.Empty,
                pinned: true);
            selection2Slot["enabled"] = true;
            slots.Add(selection2Slot);
        }

        var hasCloudProfiles = _config.GetObjectKeys("CloudProfiles").Count > 0;
        var hasLocalProfiles = _config.GetObjectKeys("LocalProfiles").Count > 0;
        if (hasCloudProfiles || hasLocalProfiles)
        {
            PreserveActiveVariantsThenPruneLegacyProfileObjects();
        }

        _config.Root["RouteSlots"] = slots;
        _config.SetStringArray("ActiveRouteSlotIds", []);
        _config.SetInt("MaxVisibleRouteSlots", IsSelection2Enabled ? 2 : 1);
        _config.SetString("RouteSlotUiLayout", "dual-pane");
        _config.SetInt("RouteSlotSchemaVersion", RouteSlotSchemaVersion);
        _configService.Save(_config);

        ApplySelectionsFromRouteSlots(slots);
        StatusMessage = "Initialized route-slot architecture with empty slot templates.";
    }

    private static JsonObject BuildRouteSlot(
        string slotId,
        string label,
        string routeType,
        string provider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant,
        bool pinned)
    {
        return new JsonObject
        {
            ["slotId"] = slotId,
            ["label"] = label,
            ["routeType"] = routeType,
            ["provider"] = provider,
            ["cloudModel"] = cloudModel,
            ["cloudVariant"] = cloudVariant,
            ["localModel"] = localModel,
            ["localVariant"] = localVariant,
            ["pinned"] = pinned,
            ["enabled"] = true,
            ["validatedDefaultProfile"] = false
        };
    }

    private void ApplySelectionsFromRouteSlots(JsonArray slots)
    {
        var slotObjects = slots.OfType<JsonObject>().ToList();
        var selection1Slot = slotObjects.FirstOrDefault(x =>
            string.Equals(x["slotId"]?.ToString(), "slot-cloud-1", StringComparison.OrdinalIgnoreCase));
        var selection2Slot = slotObjects.FirstOrDefault(x =>
            string.Equals(x["slotId"]?.ToString(), "slot-local-1", StringComparison.OrdinalIgnoreCase));

        if (selection1Slot is not null)
        {
            var routeType = NormalizeSelectionRouteType(selection1Slot["routeType"]?.ToString());
            SelectedSelection1RouteType = routeType;
            if (routeType.Equals("Cloud", StringComparison.Ordinal))
            {
                SelectedCloudProvider = FirstNonEmpty(selection1Slot["provider"]?.ToString() ?? string.Empty, SelectedCloudProvider);
                SelectedCloudProfile = FirstNonEmpty(selection1Slot["cloudModel"]?.ToString() ?? string.Empty, SelectedCloudProfile);
                SelectedCloudVariant = FirstNonEmpty(selection1Slot["cloudVariant"]?.ToString() ?? string.Empty, BaseVariantDisplayName);
            }
            else
            {
                SelectedSelection1LocalProfile = FirstNonEmpty(selection1Slot["localModel"]?.ToString() ?? string.Empty, SelectedSelection1LocalProfile);
                SelectedSelection1LocalVariant = FirstNonEmpty(selection1Slot["localVariant"]?.ToString() ?? string.Empty, BaseVariantDisplayName);
            }
        }

        if (selection2Slot is not null)
        {
            IsSelection2Enabled = ParseBool(selection2Slot["enabled"]?.ToString(), IsSelection2Enabled);
            var rawRouteType = selection2Slot["routeType"]?.ToString() ?? string.Empty;
            IsSelection2TemplateChosen = !string.IsNullOrWhiteSpace(rawRouteType);
            if (IsSelection2TemplateChosen)
            {
                var routeType = NormalizeSelectionRouteType(rawRouteType);
                SelectedSelection2RouteType = routeType;
                if (routeType.Equals("Cloud", StringComparison.Ordinal))
                {
                    SelectedSelection2CloudProvider = FirstNonEmpty(selection2Slot["provider"]?.ToString() ?? string.Empty, SelectedCloudProvider);
                    SelectedSelection2CloudProfile = FirstNonEmpty(selection2Slot["cloudModel"]?.ToString() ?? string.Empty, SelectedSelection2CloudProfile);
                    SelectedSelection2CloudVariant = FirstNonEmpty(selection2Slot["cloudVariant"]?.ToString() ?? string.Empty, SelectedSelection2CloudVariant);
                }
                else
                {
                    SelectedLocalProfile = FirstNonEmpty(selection2Slot["localModel"]?.ToString() ?? string.Empty, SelectedLocalProfile);
                    SelectedLocalVariant = FirstNonEmpty(selection2Slot["localVariant"]?.ToString() ?? string.Empty, BaseVariantDisplayName);
                }
            }
            else
            {
                SelectedSelection2RouteType = string.Empty;
            }
        }

        EnsureSelection1LocalDefaults();
        EnsureSelection2CloudDefaults();
        UpdateSelectionRouteVisibilityStates();
        UpdateActiveRouteSlotIdsForCurrentSlotEnablement();
        RefreshSlotPresenceAndActions();
    }

    private void PreserveActiveVariantsThenPruneLegacyProfileObjects()
    {
        // Preserve the full saved profile set. The advanced-profile tree is meant to show every
        // saved variant, not just the current selection, and pruning to a single active variant
        // makes the default profile appear as the only visible item in the tree.
        // Any legacy-only cleanup should be limited to truly invalid keys, not the whole variant set.
        if (_config.Root["CloudProfiles"] is not JsonObject cloudProfiles)
        {
            _config.Root["CloudProfiles"] = new JsonObject();
        }

        if (_config.Root["LocalProfiles"] is not JsonObject localProfiles)
        {
            _config.Root["LocalProfiles"] = new JsonObject();
        }
    }

    partial void OnCloudVariantTemperatureChanged(decimal value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantMaxTokensChanged(decimal value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantContextWindowChanged(string value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantReasoningModeChanged(string value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantResponseFormatChanged(string value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantOpenAiReasoningEffortChanged(string value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantCopilotReasoningEffortChanged(string value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantGeminiThinkingModeChanged(string value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantGeminiThinkingBudgetChanged(decimal value) => HandleCloudDetailedSettingsChanged();
    partial void OnCloudVariantAnthropicThinkingModeChanged(string value) => HandleCloudDetailedSettingsChanged();

    private void HandleCloudDetailedSettingsChanged()
    {
        if (_isHydratingCloudVariantForm)
        {
            return;
        }

        UpdateCloudIntentSummaryTexts();
        if (_isApplyingCloudPriorityWizard)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CloudVariantContextWindow)
            || string.IsNullOrWhiteSpace(CloudVariantOpenAiReasoningEffort))
        {
            return;
        }

        var previousOrder = CloudPriorityOrder.ToList();
        SyncCloudPriorityOrderFromDetailedSettings();
        var orderChanged = !previousOrder.SequenceEqual(CloudPriorityOrder);
        CloudIntentPresetInfoText = orderChanged
            ? "Changing the detailed settings also changed the guessed priority order."
            : "The detailed settings changed, but not enough to reorder your priorities.";
        RefreshProfileEndpointStatusSurfaces();
    }

    partial void OnLocalVariantContextChanged(decimal value)
    {
        HandleLocalDetailedSettingsChanged();
        OnPropertyChanged(nameof(ClineEndpointHintText));
        PublishHelpNoteValues();
    }
    partial void OnLocalVariantOverrideThreadsChanged(string value)
        => AcceptLocalComboChange("OverrideThreads", value, restored => LocalVariantOverrideThreads = restored);
    partial void OnLocalVariantThreadsBatchChanged(string value)
        => AcceptLocalComboChange("LocalThreadsBatch", value, restored => LocalVariantThreadsBatch = restored);
    partial void OnLocalVariantTemperatureChanged(decimal value) => HandleLocalDetailedSettingsChanged();
    partial void OnLocalVariantGpuOffloadModeChanged(string value)
        => AcceptLocalComboChange("LocalGpuOffloadMode", value, restored => LocalVariantGpuOffloadMode = restored);
    partial void OnLocalVariantMultiGpuModeChanged(string value)
        => AcceptLocalComboChange("LocalMultiGpuMode", value, restored => LocalVariantMultiGpuMode = restored);
    partial void OnLocalVariantSplitModeChanged(string value)
        => AcceptLocalComboChange("LocalSplitMode", value, restored => LocalVariantSplitMode = restored);
    partial void OnLocalVariantMainGpuChanged(string value)
        => AcceptLocalComboChange("LocalMainGpu", value, restored => LocalVariantMainGpu = restored);
    partial void OnLocalVariantTensorSplitChanged(string value) => HandleLocalDetailedSettingsChanged();
    partial void OnLocalVariantGpuLayersChanged(string value)
        => AcceptLocalComboChange("GpuLayers", value, restored => LocalVariantGpuLayers = restored);
    partial void OnLocalVariantFlashAttentionChanged(string value)
        => AcceptLocalComboChange("LocalFlashAttention", value, restored => LocalVariantFlashAttention = restored);
    partial void OnLocalVariantKvCacheTypeKChanged(string value)
        => AcceptLocalComboChange("LocalKvCacheTypeK", value, restored => LocalVariantKvCacheTypeK = restored);
    partial void OnLocalVariantKvCacheTypeVChanged(string value)
        => AcceptLocalComboChange("LocalKvCacheTypeV", value, restored => LocalVariantKvCacheTypeV = restored);
    partial void OnLocalVariantChatTemplateChanged(string value)
        => AcceptLocalComboChange("LocalChatTemplate", value, restored => LocalVariantChatTemplate = restored);
    partial void OnLocalVariantMultiUserModeChanged(string value)
        => AcceptLocalComboChange("LocalMultiUserMode", value, restored => LocalVariantMultiUserMode = restored);
    partial void OnLocalVariantUnbanTokensModeChanged(string value)
        => AcceptLocalComboChange("LocalUnbanTokensMode", value, restored => LocalVariantUnbanTokensMode = restored);
    partial void OnLocalVariantAutoCompressEnabledChanged(bool value) => HandleLocalDetailedSettingsChanged();
    partial void OnLocalVariantMaxTokensChanged(string value)
        => AcceptLocalComboChange("OverrideMaxTokens", value, restored => LocalVariantMaxTokens = restored);
    partial void OnLocalVariantBatchSizeChanged(string value)
        => AcceptLocalComboChange("LocalBatchSize", value, restored => LocalVariantBatchSize = restored);
    partial void OnLocalVariantUbatchSizeChanged(string value)
        => AcceptLocalComboChange("LocalUbatchSize", value, restored => LocalVariantUbatchSize = restored);
    partial void OnLocalVariantSpecTypeChanged(string value)
        => AcceptLocalComboChange("LocalSpecType", value, restored => LocalVariantSpecType = restored);
    partial void OnLocalVariantCacheReuseChanged(string value)
        => AcceptLocalComboChange("LocalCacheReuse", value, restored => LocalVariantCacheReuse = restored);
    partial void OnLocalVariantCacheRamChanged(string value)
        => AcceptLocalComboChange("LocalCacheRam", value, restored => LocalVariantCacheRam = restored);
    partial void OnLocalVariantFitChanged(string value)
    {
        AcceptLocalComboChange("LocalFit", value, restored => LocalVariantFit = restored);
        var enabled = (value ?? string.Empty).Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        if (LocalVariantFitEnabled != enabled)
        {
            LocalVariantFitEnabled = enabled;
        }
    }

    partial void OnLocalVariantFitEnabledChanged(bool value)
    {
        var text = value ? "Enabled" : "Disabled";
        if (!string.Equals(LocalVariantFit, text, StringComparison.OrdinalIgnoreCase))
        {
            LocalVariantFit = text;
        }
    }
    partial void OnLocalVariantSwaFullChanged(string value)
        => AcceptLocalComboChange("LocalSwaFull", value, restored => LocalVariantSwaFull = restored);
    partial void OnLocalVariantReasoningChanged(string value)
        => AcceptLocalComboChange("LocalReasoning", value, restored => LocalVariantReasoning = restored);
    partial void OnLocalVariantChatParserChanged(string value)
        => AcceptLocalComboChange("LocalChatParser", value, restored => LocalVariantChatParser = restored);
    partial void OnLocalVariantVisionEnabledChanged(string value)
    {
        if (_isHydratingLocalVariantForm || _preservingLocalVisionForm)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            LocalVariantVisionEnabled = _hydratedLocalVisionEnabled;
            return;
        }

        if (!IsLocalVariantEditable
            && !value.Equals(_hydratedLocalVisionEnabled, StringComparison.OrdinalIgnoreCase))
        {
            LocalVariantVisionEnabled = _hydratedLocalVisionEnabled;
            return;
        }

        if (IsLocalVariantEditable)
        {
            _hydratedLocalVisionEnabled = value;
        }

        RefreshLocalVisionHint();
        HandleLocalDetailedSettingsChanged();
    }
    partial void OnSelectedLocalVariantVisionProjectorChoiceChanged(LocalVisionProjectorChoice? value)
    {
        if (_isHydratingLocalVariantForm || _isRefreshingVisionProjectorChoices)
        {
            return;
        }

        LocalVariantVisionProjectorPath = value?.Path ?? string.Empty;
        RefreshLocalVisionHint();
    }
    partial void OnLocalVariantVisionProjectorPathChanged(string value)
    {
        if (_isHydratingLocalVariantForm || _preservingLocalVisionForm)
        {
            return;
        }

        if (IsLocalVariantEditable)
        {
            _hydratedLocalVisionProjectorPath = value ?? string.Empty;
        }

        HandleLocalDetailedSettingsChanged();
    }
    partial void OnLocalVariantVisionMaxImageEdgeChanged(decimal value)
    {
        RefreshImagesHeadroomNote();
        HandleLocalDetailedSettingsChanged();
    }

    private void HandleLocalDetailedSettingsChanged()
    {
        if (_isHydratingLocalVariantForm)
        {
            return;
        }

        UpdateLocalPrioritySummaryTexts();
        UpdateProfileFootprintIndicators();
        if (_isApplyingLocalPriorityWizard)
        {
            return;
        }

        var inferred = RankPrioritiesByScore(LocalPriorityNames, ComputeLocalPriorityScores());
        var orderChanged = inferred.Count > 0
            && !LocalPriorityOrder.SequenceEqual(inferred, StringComparer.OrdinalIgnoreCase);
        LocalQuickAutoTuneInfoText = orderChanged
            ? "These settings no longer match the priority list. Use the arrows or AutoTune if you want the list and settings aligned."
            : "The detailed settings changed. The priority list is unchanged until you move it or run AutoTune.";
        RefreshProfileEndpointStatusSurfaces();
    }

    private void AcceptLocalComboChange(string key, string? value, Action<string> restore)
    {
        if (_isHydratingLocalVariantForm)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            if (_acceptedLocalComboValues.TryGetValue(key, out var previous)
                && !string.IsNullOrWhiteSpace(previous))
            {
                restore(previous);
            }

            return;
        }

        _acceptedLocalComboValues[key] = value;
        HandleLocalDetailedSettingsChanged();
    }

    private void RestoreBlankLocalComboSelections()
    {
        void Restore(string key, string current, Action<string> set)
        {
            if (!string.IsNullOrWhiteSpace(current))
            {
                _acceptedLocalComboValues[key] = current;
                return;
            }

            if (_acceptedLocalComboValues.TryGetValue(key, out var previous)
                && !string.IsNullOrWhiteSpace(previous))
            {
                set(previous);
            }
        }

        Restore("OverrideThreads", LocalVariantOverrideThreads, value => LocalVariantOverrideThreads = value);
        Restore("LocalThreadsBatch", LocalVariantThreadsBatch, value => LocalVariantThreadsBatch = value);
        Restore("LocalGpuOffloadMode", LocalVariantGpuOffloadMode, value => LocalVariantGpuOffloadMode = value);
        Restore("GpuLayers", LocalVariantGpuLayers, value => LocalVariantGpuLayers = value);
        Restore("LocalFlashAttention", LocalVariantFlashAttention, value => LocalVariantFlashAttention = value);
        Restore("LocalKvCacheTypeK", LocalVariantKvCacheTypeK, value => LocalVariantKvCacheTypeK = value);
        Restore("LocalKvCacheTypeV", LocalVariantKvCacheTypeV, value => LocalVariantKvCacheTypeV = value);
        Restore("LocalChatTemplate", LocalVariantChatTemplate, value => LocalVariantChatTemplate = value);
        Restore("LocalMultiUserMode", LocalVariantMultiUserMode, value => LocalVariantMultiUserMode = value);
        Restore("LocalUnbanTokensMode", LocalVariantUnbanTokensMode, value => LocalVariantUnbanTokensMode = value);
        Restore("OverrideMaxTokens", LocalVariantMaxTokens, value => LocalVariantMaxTokens = value);
        Restore("LocalBatchSize", LocalVariantBatchSize, value => LocalVariantBatchSize = value);
        Restore("LocalUbatchSize", LocalVariantUbatchSize, value => LocalVariantUbatchSize = value);
        Restore("LocalSpecType", LocalVariantSpecType, value => LocalVariantSpecType = value);
        Restore("LocalCacheReuse", LocalVariantCacheReuse, value => LocalVariantCacheReuse = value);
        Restore("LocalCacheRam", LocalVariantCacheRam, value => LocalVariantCacheRam = value);
        Restore("LocalFit", LocalVariantFit, value => LocalVariantFit = value);
        Restore("LocalSwaFull", LocalVariantSwaFull, value => LocalVariantSwaFull = value);
        Restore("LocalReasoning", LocalVariantReasoning, value => LocalVariantReasoning = value);
        Restore("LocalChatParser", LocalVariantChatParser, value => LocalVariantChatParser = value);
    }

    private void RememberAcceptedLocalComboValues()
    {
        foreach (var pair in CaptureCurrentLocalDetailedSettings())
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                _acceptedLocalComboValues[pair.Key] = pair.Value;
            }
        }
    }

    private void UpdateRoutingPoolStatus()
    {
        var localGgufs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localLaunchStyles = new HashSet<string>(StringComparer.Ordinal);
        var localRequestStyles = new HashSet<string>(StringComparer.Ordinal);
        var localVariants = 0;
        if (_config.Root["LocalProfiles"] is JsonObject localProfiles)
        {
            foreach (var entry in localProfiles)
            {
                if (entry.Value is not JsonObject profile
                    || string.IsNullOrWhiteSpace(profile["EndpointValidatedUtc"]?.ToString()))
                {
                    continue;
                }

                var parts = (entry.Key ?? string.Empty).Split("::");
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                localVariants++;
                localGgufs.Add(parts[0].Trim());
                localLaunchStyles.Add(GetLocalLaunchCriticalFingerprint(profile));
                localRequestStyles.Add(
                    $"{ParseDouble(profile["LocalTemperature"]?.ToString(), 0.3):0.###}|{ParseInt(profile["OverrideMaxTokens"]?.ToString(), 2048)}");
            }
        }

        var cloudModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cloudSettingStyles = new HashSet<string>(StringComparer.Ordinal);
        var cloudVariants = 0;
        if (_config.Root["CloudProfiles"] is JsonObject cloudProfiles)
        {
            foreach (var entry in cloudProfiles)
            {
                if (entry.Value is not JsonObject profile
                    || string.IsNullOrWhiteSpace(profile["EndpointValidatedUtc"]?.ToString()))
                {
                    continue;
                }

                var parts = (entry.Key ?? string.Empty).Split("::");
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    continue;
                }

                cloudVariants++;
                cloudModels.Add($"{parts[0].Trim()}::{parts[1].Trim()}");
                cloudSettingStyles.Add(GetCloudSettingsFingerprint(profile));
            }
        }

        var overlays = _runtimeService.GetLocalRequestOverlays();
        var overlayStyles = overlays
            .Select(item => $"{item.Temperature:0.###}|{item.MaxTokens}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        var localHot = _runtimeService.IsManagedLocalAlive();
        var cloudHot = _runtimeService.IsCloudRouteActive();
        var hold = (SelectedCloudRoutingCapacity ?? string.Empty)
            .StartsWith("Hold", StringComparison.OrdinalIgnoreCase);
        var sameGgufRequestHops = localGgufs.Count == 1 && localLaunchStyles.Count == 1 && localRequestStyles.Count >= 2;
        var localPlusCloud = localGgufs.Count >= 1 && cloudModels.Count >= 1;
        var catalogHasUsableSplit = sameGgufRequestHops || localPlusCloud;

        var catalog =
            $"Validated catalog: {localVariants} local model profile(s) on {localGgufs.Count} local model(s) " +
            $"({localLaunchStyles.Count} launch-style(s), {localRequestStyles.Count} temperature/max-token style(s)); " +
            $"{cloudVariants} cloud model profile(s) on {cloudModels.Count} cloud model(s) " +
            $"({cloudSettingStyles.Count} distinct cloud setting(s)).";

        string verdict;
        if (!catalogHasUsableSplit)
        {
            if (localGgufs.Count > 1 && cloudModels.Count == 0)
            {
                verdict = "Several local models are validated, but only one llama process can be hot. That is a Launch switch, not per-request routing, until you also validate a cloud model or model profile variants of the same local model that differ in temperature/max tokens.";
            }
            else
            {
                verdict = "Routing is moot until you validate at least two usable strengths: a local profile plus a cloud model, or two model profile variants of the same local model with different temperature or max tokens.";
            }
        }
        else if (!RequestRoutingEnabled)
        {
            verdict = "The catalog has a usable split, but Route requests is off so Launch stays exclusive.";
        }
        else if (localHot && cloudHot && hold)
        {
            verdict = "Local and cloud are both launched, but Cloud Usage Mode is Hold so routing will not spend cloud. Switch to Low or Normal if you want that second strength used.";
        }
        else if (localHot && cloudHot)
        {
            var overlayNote = overlayStyles > 1
                ? $" Hot local also has {overlays.Count} attached model profile variant(s) with {overlayStyles} distinct temperature/max-token style(s)."
                : overlays.Count > 1
                    ? " Extra local model profile variant names are attached, but temperature and max tokens match, so local hops will not change requests."
                    : sameGgufRequestHops
                        ? " Launch another model profile variant of the same local model so the router can also hop local temperature/max tokens."
                        : string.Empty;
            verdict = "Hot pool can choose between local and cloud." + overlayNote;
        }
        else if (localHot && cloudModels.Count > 0)
        {
            verdict = overlayStyles > 1
                ? $"Local is hot and {overlayStyles} request styles are attached. Launch a validated cloud profile if you also want a ready cloud model."
                : "Local is hot. Launch a validated cloud profile so Route requests can use a ready cloud model, or Launch another model profile variant of the same local model with different temperature/max tokens.";
        }
        else if (cloudHot && localGgufs.Count > 0)
        {
            verdict = "Cloud is hot. Launch a validated local profile so Route requests can use llama-server.";
        }
        else if (localHot && overlayStyles > 1)
        {
            verdict = $"Only local is hot. The router can vary among {overlayStyles} local request styles; there is no hot cloud alternative.";
        }
        else if (localHot || cloudHot)
        {
            verdict = "Only the launched local model or the launched cloud model is hot. The catalog has other strengths, but they are not launched.";
        }
        else
        {
            verdict = "Nothing is hot yet. Launch the distinct validated profiles you want in the pool.";
        }

        RoutingPoolStatusText = catalog + " " + verdict;
        RefreshQuickSelectSlotActiveStates(ResolveActiveRouteIndicatorState());
    }

    private void HydrateProfileLists()
    {
        foreach (var key in _config.GetObjectKeys("CloudProfiles"))
        {
            var parts = key.Split("::");
            if (parts.Length < 2)
            {
                continue;
            }

            var provider = parts[0].Trim();
            var model = parts[1].Trim();
            var variant = parts.Length >= 3 ? parts[2].Trim() : "Variant 1";

            AddIfMissing(CloudProviders, provider);
            AddIfMissing(CloudProfiles, model);
            AddIfMissing(CloudVariants, variant);
        }

        foreach (var key in _config.GetObjectKeys("LocalProfiles"))
        {
            var parts = key.Split("::");
            if (parts.Length < 2)
            {
                continue;
            }

            var model = parts[0].Trim();
            var variant = parts[1].Trim();
            AddIfMissing(LocalProfiles, model);
            AddIfMissing(LocalVariants, variant);
        }

        if (!CloudProviders.Contains(SelectedCloudProvider))
        {
            AddIfMissing(CloudProviders, SelectedCloudProvider);
        }

        if (!CloudProfiles.Contains(SelectedCloudProfile))
        {
            AddIfMissing(CloudProfiles, SelectedCloudProfile);
        }

        if (!CloudVariants.Contains(SelectedCloudVariant))
        {
            AddIfMissing(CloudVariants, SelectedCloudVariant);
        }

        if (!LocalProfiles.Contains(SelectedLocalProfile))
        {
            AddIfMissing(LocalProfiles, SelectedLocalProfile);
        }

        if (!LocalVariants.Contains(SelectedLocalVariant))
        {
            AddIfMissing(LocalVariants, SelectedLocalVariant);
        }

        RefreshSwitchBenchmarkProfiles();
    }

    private void RefreshSwitchBenchmarkProfiles()
    {
        var selectedAKey = SelectedSwitchBenchmarkProfileA?.Key
            ?? _config.GetString("SwitchBenchmarkProfileA", string.Empty);
        var selectedBKey = SelectedSwitchBenchmarkProfileB?.Key
            ?? _config.GetString("SwitchBenchmarkProfileB", string.Empty);
        var options = new Dictionary<string, SwitchBenchmarkProfileOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _config.GetObjectKeys("CloudProfiles"))
        {
            var parts = key.Split("::");
            if (parts.Length < 2)
            {
                continue;
            }

            var provider = parts[0].Trim();
            var model = parts[1].Trim();
            var variant = parts.Length >= 3 ? parts[2].Trim() : BaseVariantDisplayName;
            if (!IsSavedProfileEndpointValidated("CloudProfiles", key))
            {
                continue;
            }
            AddSwitchBenchmarkProfileOption(options, "Cloud", provider, model, variant);
        }

        foreach (var key in _config.GetObjectKeys("LocalProfiles"))
        {
            var parts = key.Split("::");
            if (parts.Length < 2)
            {
                continue;
            }

            var model = parts[0].Trim();
            var variant = parts[1].Trim();
            if (!IsSavedProfileEndpointValidated("LocalProfiles", key))
            {
                continue;
            }
            AddSwitchBenchmarkProfileOption(options, "Local", string.Empty, model, variant);
        }

        SwitchBenchmarkProfiles.Clear();
        foreach (var option in options.Values
                     .OrderBy(option => option.RouteType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            SwitchBenchmarkProfiles.Add(option);
        }

        SelectedSwitchBenchmarkProfileA = SwitchBenchmarkProfiles
            .FirstOrDefault(option => option.Key.Equals(selectedAKey, StringComparison.OrdinalIgnoreCase))
            ?? SwitchBenchmarkProfiles.FirstOrDefault();
        SelectedSwitchBenchmarkProfileB = SwitchBenchmarkProfiles
            .FirstOrDefault(option => option.Key.Equals(selectedBKey, StringComparison.OrdinalIgnoreCase))
            ?? SwitchBenchmarkProfiles.FirstOrDefault(option => !ReferenceEquals(option, SelectedSwitchBenchmarkProfileA));
    }

    private bool IsSavedProfileEndpointValidated(string collectionName, string key)
    {
        return _config.Root[collectionName] is JsonObject profiles
               && profiles[key] is JsonObject profile
               && !string.IsNullOrWhiteSpace(profile["EndpointValidatedUtc"]?.ToString());
    }

    private bool SetLocalVariantEndpointValidated(string variantName, bool validated, string? warning = null)
        => SetLocalVariantEndpointValidated(SelectedLocalProfile, variantName, validated, warning);

    private bool SetLocalVariantEndpointValidated(string? model, string variantName, bool validated, string? warning = null)
    {
        return SetProfileEndpointValidated(
            "LocalProfiles",
            $"{(model ?? string.Empty).Trim()}::{ProfileConfigVariantName(variantName)}",
            validated,
            warning);
    }

    private void SetCloudVariantEndpointValidated(string variantName, bool validated, string? warning = null)
        => SetCloudVariantEndpointValidated(SelectedCloudProvider, SelectedCloudProfile, variantName, validated, warning);

    private void SetCloudVariantEndpointValidated(
        string? provider,
        string? model,
        string variantName,
        bool validated,
        string? warning = null)
    {
        SetProfileEndpointValidated(
            "CloudProfiles",
            $"{(provider ?? string.Empty).Trim()}::{(model ?? string.Empty).Trim()}::{ProfileConfigVariantName(variantName)}",
            validated,
            warning);
    }

    private void NoteLocalProfileConnectionFailure(string model, string variant, string reason)
    {
        SetProfileEndpointValidated(
            "LocalProfiles",
            $"{(model ?? string.Empty).Trim()}::{ProfileConfigVariantName(variant)}",
            validated: false,
            warning: reason);
        RefreshProfileWarningSurfaces();
    }

    private void NoteCloudProfileConnectionFailure(string provider, string model, string variant, string reason)
    {
        SetProfileEndpointValidated(
            "CloudProfiles",
            $"{(provider ?? string.Empty).Trim()}::{(model ?? string.Empty).Trim()}::{ProfileConfigVariantName(variant)}",
            validated: false,
            warning: reason);
        RefreshProfileWarningSurfaces();
    }

    private void RefreshProfileWarningSurfaces()
    {
        RefreshSwitchBenchmarkProfiles();
        RefreshQuickSelectVariantLists();
        RefreshProfileEndpointStatusSurfaces();
    }

    private void RefreshProfileEndpointStatusSurfaces()
    {
        foreach (var item in LocalVariantTreeItems)
        {
            var settings = GetLiveLocalProfileSettings(item.Name, item.ModelName) ?? new JsonObject();
            ApplyProfileEndpointStatus(item, isLocal: true, item.ModelName, settings);
        }

        foreach (var item in CloudVariantTreeItems)
        {
            var settings = GetLiveCloudProfileSettings(item.Name, item.Provider, item.ModelName) ?? new JsonObject();
            ApplyProfileEndpointStatus(item, isLocal: false, item.ModelName, settings);
        }
    }

    private void ApplyProfileEndpointStatus(
        CloudVariantTreeItemViewModel item,
        bool isLocal,
        string? model,
        JsonObject settings)
    {
        var warning = ResolveProfileWarning(isLocal, model, settings);
        var isValidatedOnDisk = !string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString());
        item.ApplyEndpointStatus(
            warning.Show,
            warning.Tooltip,
            isValidatedOnDisk,
            HasUnsavedEditsForTreeItem(item, isLocal));
    }

    private bool HasUnsavedEditsForTreeItem(CloudVariantTreeItemViewModel item, bool isLocal)
    {
        if (isLocal)
        {
            if (_isHydratingLocalVariantForm || _isValidatingLocalProfile || _isApplyingLocalPriorityWizard)
            {
                return false;
            }

            if (!item.ModelName.Equals(SelectedLocalProfile, StringComparison.OrdinalIgnoreCase)
                || !ProfileConfigVariantName(item.Name).Equals(ProfileConfigVariantName(SelectedLocalVariant), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (LocalVariantEditorMatchesBaselineFor(item.ModelName, item.Name))
            {
                return false;
            }

            return LocalProfileEditorDiffersFromSaved(
                GetLiveLocalProfileSettings(item.Name, item.ModelName),
                BuildLocalVariantSettingsObject());
        }

        if (_isHydratingCloudVariantForm || _isApplyingCloudPriorityWizard)
        {
            return false;
        }

        if (!item.Provider.Equals(SelectedCloudProvider, StringComparison.OrdinalIgnoreCase)
            || !item.ModelName.Equals(SelectedCloudProfile, StringComparison.OrdinalIgnoreCase)
            || !ProfileConfigVariantName(item.Name).Equals(ProfileConfigVariantName(SelectedCloudVariant), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ProfileSettingsDiffer(
            GetLiveCloudProfileSettings(item.Name, item.Provider, item.ModelName),
            BuildCloudVariantSettingsObject());
    }

    private (bool Show, string Tooltip) ResolveProfileWarning(bool isLocal, string? model, JsonObject? settings)
    {
        if (isLocal && IsLocalGgufMissing(model))
        {
            return (true, MissingLocalFileWarningTooltip);
        }

        if (isLocal && LocalHealthGuidance.IsVisionProjectorFile(model))
        {
            return (true, LocalHealthGuidance.FormatVisionProjectorAsModelWarning(model));
        }

        var stored = settings?["EndpointWarning"]?.ToString();
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return (true, CompactEndpointWarning(stored));
        }

        return (false, string.Empty);
    }

    private (bool Show, string Tooltip) ResolveProfileWarningForOption(
        bool isLocal,
        string? provider,
        string model,
        string variant)
    {
        var settings = isLocal
            ? GetLiveLocalProfileSettings(variant, model)
            : GetLiveCloudProfileSettings(variant, provider, model);
        return ResolveProfileWarning(isLocal, model, settings);
    }

    private static string CompactEndpointWarning(string? details)
        => LocalHealthGuidance.FormatProfilePanelWarning(details);

    private bool SetProfileEndpointValidated(string collectionName, string targetKey, bool validated, string? warning = null)
    {
        if (_config.Root[collectionName] is not JsonObject profiles)
        {
            return false;
        }

        var matchingKey = profiles.Select(entry => entry.Key)
            .FirstOrDefault(key => key.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingKey) || profiles[matchingKey] is not JsonObject profile)
        {
            return false;
        }

        ProfileEndpointValidationStamp.Apply(profile, validated, warning);

        _configService.Save(_config);
        UpdateRoutingPoolStatus();
        return validated;
    }

    private static bool ProfileSettingsDiffer(JsonObject? saved, JsonObject pending)
    {
        if (saved is null)
        {
            return true;
        }

        foreach (var property in pending)
        {
            var savedValue = saved[property.Key]?.ToString() ?? string.Empty;
            var pendingValue = property.Value?.ToString() ?? string.Empty;
            if (!savedValue.Equals(pendingValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void CaptureLocalVariantEditorBaseline()
    {
        _localVariantEditorBaselineModel = SelectedLocalProfile ?? string.Empty;
        _localVariantEditorBaselineVariant = ProfileConfigVariantName(SelectedLocalVariant);
        _localVariantEditorBaselineJson = SerializeJsonObject(BuildLocalVariantSettingsObject());
    }

    private bool LocalVariantEditorMatchesBaselineFor(string model, string variant)
    {
        return !string.IsNullOrWhiteSpace(_localVariantEditorBaselineJson)
            && model.Equals(_localVariantEditorBaselineModel, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(variant).Equals(_localVariantEditorBaselineVariant, StringComparison.OrdinalIgnoreCase)
            && LocalVariantEditorMatchesBaseline(BuildLocalVariantSettingsObject());
    }

    private bool LocalVariantEditorMatchesBaseline(JsonObject pending)
        => SerializeJsonObject(pending).Equals(_localVariantEditorBaselineJson, StringComparison.Ordinal);

    private void SyncRuntimeLocalProfileMetadataFromDisk(string model, string variant)
    {
        if (string.IsNullOrWhiteSpace(model)
            || _config.Root["LocalProfiles"] is not JsonObject memoryProfiles)
        {
            return;
        }

        var fresh = _configService.Load();
        if (fresh.Root["LocalProfiles"] is not JsonObject diskProfiles)
        {
            return;
        }

        var normalizedVariant = ProfileConfigVariantName(variant);
        var diskKey = diskProfiles.Select(entry => entry.Key)
            .FirstOrDefault(key =>
            {
                var parts = key.Split("::", 2, StringSplitOptions.TrimEntries);
                return parts.Length >= 2
                    && parts[0].Equals(model.Trim(), StringComparison.OrdinalIgnoreCase)
                    && parts[1].Equals(normalizedVariant, StringComparison.OrdinalIgnoreCase);
            });
        if (string.IsNullOrWhiteSpace(diskKey) || diskProfiles[diskKey] is not JsonObject diskProfile)
        {
            return;
        }

        var memoryKey = memoryProfiles.Select(entry => entry.Key)
            .FirstOrDefault(key => key.Equals(diskKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(memoryKey) || memoryProfiles[memoryKey] is not JsonObject memoryProfile)
        {
            memoryProfiles[diskKey] = diskProfile.DeepClone();
            return;
        }

        foreach (var metadataKey in RuntimeLocalProfileMetadataKeys)
        {
            if (diskProfile[metadataKey] is null)
            {
                memoryProfile.Remove(metadataKey);
                continue;
            }

            memoryProfile[metadataKey] = diskProfile[metadataKey]?.DeepClone();
        }
    }

    private void RefreshLocalProfilePresentationAfterRuntimeChange(string? model = null, string? variant = null)
    {
        model = FirstNonEmpty(model, SelectedLocalProfile);
        variant = ProfileConfigVariantName(FirstNonEmpty(variant, SelectedLocalVariant));
        if (!string.IsNullOrWhiteSpace(model))
        {
            SyncRuntimeLocalProfileMetadataFromDisk(model, variant);
        }

        UpdateLocalLaunchTelemetrySummary();
        UpdateProfileFootprintIndicators();
        RefreshLocalVariantTreeItemSummariesInPlace();
        RefreshProfileEndpointStatusSurfaces();
    }

    private static bool LocalProfileEditorDiffersFromSaved(JsonObject? saved, JsonObject pending)
    {
        if (saved is null)
        {
            return true;
        }

        foreach (var property in pending)
        {
            if (!LocalProfileEditorValuesEqual(property.Key, saved[property.Key], property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LocalProfileEditorValuesEqual(string key, JsonNode? savedNode, JsonNode? pendingNode)
    {
        if (key.Equals("PriorityOrder", StringComparison.OrdinalIgnoreCase))
        {
            var savedOrder = ReadPriorityOrderFromNode(savedNode);
            var pendingOrder = ReadPriorityOrderFromNode(pendingNode);
            return savedOrder.SequenceEqual(pendingOrder, StringComparer.OrdinalIgnoreCase);
        }

        var savedValue = NormalizeLocalProfileEditorValue(key, savedNode);
        var pendingValue = NormalizeLocalProfileEditorValue(key, pendingNode);
        return savedValue.Equals(pendingValue, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ReadPriorityOrderFromNode(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array.Select(entry => entry?.ToString()?.Trim())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry!)
            .ToList();
    }

    private static string NormalizeLocalProfileEditorValue(string key, JsonNode? node)
    {
        var value = (node?.ToString() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (key.Equals("GpuLayers", StringComparison.OrdinalIgnoreCase)
            && value.Equals("fit", StringComparison.OrdinalIgnoreCase))
        {
            return "Auto";
        }

        if (key.Equals("AutoCompressEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBool(value, true).ToString(CultureInfo.InvariantCulture);
        }

        if (key.Equals("LocalTemperature", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDouble(value, 0.3).ToString("0.########", CultureInfo.InvariantCulture);
        }

        if (key.Equals("OverrideContext", StringComparison.OrdinalIgnoreCase)
            || key.Equals("LocalVisionMaxImageEdge", StringComparison.OrdinalIgnoreCase))
        {
            return ParseInt(value, 0).ToString(CultureInfo.InvariantCulture);
        }

        return value;
    }

    private void RefreshQuickSelectVariantLists()
    {
        RefreshValidatedQuickSelectProfiles();
        RefreshAvailableQuickSelectProfiles();
        RefreshQuickSelectSlotMetadata();
    }

    private void RefreshAvailableQuickSelectProfiles()
    {
        foreach (var slot in QuickSelectSlots)
        {
            slot.NotifyValidatedProfilesRefreshed();
        }
    }

    internal ValidatedQuickSelectProfileOption? FindValidatedQuickSelectProfile(
        string routeType,
        string provider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant)
    {
        if (routeType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return ValidatedQuickSelectProfiles.FirstOrDefault(option =>
                option.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
                && option.Provider.Equals(provider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && option.Model.Equals(cloudModel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && ProfileConfigVariantName(option.Variant).Equals(ProfileConfigVariantName(cloudVariant), StringComparison.OrdinalIgnoreCase));
        }

        if (routeType.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return ValidatedQuickSelectProfiles.FirstOrDefault(option =>
                option.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase)
                && option.Model.Equals(localModel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && ProfileConfigVariantName(option.Variant).Equals(ProfileConfigVariantName(localVariant), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private void RefreshValidatedQuickSelectProfiles()
    {
        var options = new Dictionary<string, ValidatedQuickSelectProfileOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _config.GetObjectKeys("CloudProfiles"))
        {
            var parts = key.Split("::");
            if (parts.Length < 3 || !IsSavedProfileEndpointValidated("CloudProfiles", key))
            {
                continue;
            }

            var provider = parts[0].Trim();
            var model = parts[1].Trim();
            var variant = parts[2].Trim();
            var isDefault = variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
            var warning = ResolveProfileWarningForOption(isLocal: false, provider, model, variant);
            var logo = CorporateLogoCatalog.Resolve(provider, model);
            var option = new ValidatedQuickSelectProfileOption
            {
                RouteType = "Cloud",
                Provider = provider,
                Model = model,
                Variant = variant,
                DisplayName = isDefault
                    ? $"Cloud | {provider} | {model} (default)"
                    : $"Cloud | {provider} | {model}: {variant}",
                LogoAssetKey = logo?.AssetKey,
                LogoCompanyName = logo?.CompanyName ?? string.Empty,
                ShowMissingFileWarning = warning.Show,
                MissingFileWarningTooltip = warning.Show ? warning.Tooltip : DefaultEndpointWarningTooltip
            };
            options.TryAdd(option.Key, option);
        }

        foreach (var key in _config.GetObjectKeys("LocalProfiles"))
        {
            var parts = key.Split("::", 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !IsSavedProfileEndpointValidated("LocalProfiles", key))
            {
                continue;
            }

            var model = parts[0];
            var variant = parts[1];
            var isDefault = variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
            var warning = ResolveProfileWarningForOption(isLocal: true, provider: string.Empty, model, variant);
            var logo = CorporateLogoCatalog.Resolve(provider: string.Empty, model);
            var settings = GetLiveLocalProfileSettings(variant, model);
            var images = (settings?["LocalVisionEnabled"]?.ToString() ?? string.Empty)
                .Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                ? " \u00b7 images"
                : string.Empty;
            var option = new ValidatedQuickSelectProfileOption
            {
                RouteType = "Local",
                Model = model,
                Variant = variant,
                ShowMissingFileWarning = warning.Show,
                MissingFileWarningTooltip = warning.Show ? warning.Tooltip : MissingLocalFileWarningTooltip,
                LogoAssetKey = logo?.AssetKey,
                LogoCompanyName = logo?.CompanyName ?? string.Empty,
                DisplayName = isDefault
                    ? $"Local | (default) \u00b7 {model}{images}"
                    : $"Local | {variant} \u00b7 {model}{images}"
            };
            options.TryAdd(option.Key, option);
        }

        ValidatedQuickSelectProfiles.Clear();
        foreach (var option in options.Values
                     .OrderBy(option => option.RouteType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            ValidatedQuickSelectProfiles.Add(option);
        }
    }

    private void AddSwitchBenchmarkProfileOption(
        IDictionary<string, SwitchBenchmarkProfileOption> options,
        string routeType,
        string provider,
        string model,
        string variant)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(variant))
        {
            return;
        }

        var isDefault = variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        var displayName = routeType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
            ? isDefault ? $"Cloud | {provider} | {model} (default)" : $"Cloud | {provider} | {model}: {variant}"
            : isDefault ? $"Local | {model} (default)" : $"Local | {model}: {variant}";
        var isLocal = routeType.Equals("Local", StringComparison.OrdinalIgnoreCase);
        var warning = ResolveProfileWarningForOption(isLocal, provider, model, variant);
        var logo = CorporateLogoCatalog.Resolve(isLocal ? string.Empty : provider, model);
        var option = new SwitchBenchmarkProfileOption
        {
            RouteType = routeType,
            Provider = provider,
            Model = model,
            Variant = variant,
            DisplayName = displayName,
            LogoAssetKey = logo?.AssetKey,
            LogoCompanyName = logo?.CompanyName ?? string.Empty,
            ShowMissingFileWarning = warning.Show,
            MissingFileWarningTooltip = warning.Show
                ? warning.Tooltip
                : (isLocal ? MissingLocalFileWarningTooltip : DefaultEndpointWarningTooltip)
        };
        options.TryAdd(option.Key, option);
    }

    private void SaveCurrentSelections()
    {
        SyncPrimaryRouteSlotsFromCurrentSelections();
        _config.SetString("ActiveCloud", SelectedCloudProvider);
        _config.SetString("ActiveCloudModel", SelectedCloudProfile);
        _config.SetString("ActiveCloudVariant", SelectedCloudVariant);
        _config.SetString("ActiveLocalModel", SelectedLocalProfile);
        _config.SetString("ActiveLocalVariant", SelectedLocalVariant);
        _config.SetString("Selection1RouteType", NormalizeSelectionRouteType(SelectedSelection1RouteType));
        _config.SetBool("Selection1TemplateChosen", IsSelection1TemplateChosen);
        _config.SetString("Selection1LocalModel", SelectedSelection1LocalProfile);
        _config.SetString("Selection1LocalVariant", SelectedSelection1LocalVariant);
        _config.SetString("Selection2RouteType", IsSelection2TemplateChosen ? NormalizeSelectionRouteType(SelectedSelection2RouteType) : string.Empty);
        _config.SetBool("Selection2TemplateChosen", IsSelection2TemplateChosen);
        _config.SetString("Selection2CloudProvider", SelectedSelection2CloudProvider);
        _config.SetString("Selection2CloudModel", SelectedSelection2CloudProfile);
        _config.SetString("Selection2CloudVariant", SelectedSelection2CloudVariant);
        _config.SetBool("Selection2Enabled", IsSelection2Enabled);
        _config.SetString("ModelDirectory", LocalModelDirectory);
        _config.SetString("LocalServerExecutablePath", LocalServerExecutablePath);
        _config.SetString("UpdateFeedUrl", UpdateFeedUrl?.Trim() ?? string.Empty);
        _config.SetInt("OrchestratorPort", (int)OrchestratorPort);
        _config.SetString("EndpointApp", SelectedEndpointApp?.Trim() ?? string.Empty);
        _config.SetInt("HarnessWebPort", (int)HarnessWebPort);
        _config.SetBool("IdleTimeoutEnabled", IdleTimeoutEnabled);
        _config.SetBool("GenerationSpeedTelemetryEnabled", GenerationSpeedTelemetryEnabled);
        _config.SetInt("IdleTimeoutMinutes", (int)IdleTimeoutMinutes);
        _config.SetBool("RequestRoutingEnabled", RequestRoutingEnabled);
        _config.SetBool("InterventionPopupsEnabled", InterventionPopupsEnabled);
        _config.SetString("RequestRoutingTopology", RequestRoutingTopology.DualHot);
        _config.SetString("CloudRoutingCapacity", SelectedCloudRoutingCapacity);
        _config.SetBool("LocalExperimentalHardwareTuneEnabled", LocalExperimentalHardwareTuneEnabled);
        _config.SetBool("LocalVisionEnabled", LocalVisionEnabled);
        _config.SetString("LocalVisionProjectorPath", LocalVisionProjectorPath);
        _config.SetInt("LocalVisionMaxImageEdge", (int)LocalVisionMaxImageEdge);
        _config.SetString("SwitchBenchmarkOrder", SelectedSwitchBenchmarkOrder);
        _config.SetString("SwitchBenchmarkProfileA", SelectedSwitchBenchmarkProfileA?.Key ?? string.Empty);
        _config.SetString("SwitchBenchmarkProfileB", SelectedSwitchBenchmarkProfileB?.Key ?? string.Empty);
        _config.SetStringArray("CloudPriorityOrder", CloudPriorityOrder);
        _config.SetStringArray("LocalPriorityOrder", LocalPriorityOrder);
        _configService.Save(_config);
        StatusMessage = "Saved configuration to " + _configService.ConfigPath;
    }

    private static string NormalizeCloudRoutingCapacityDisplay(string? value)
    {
        var text = FirstNonEmpty(value ?? string.Empty, "Normal");
        if (text.StartsWith("Hold", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Local only", StringComparison.OrdinalIgnoreCase))
        {
            return "Hold (local only)";
        }

        if (text.StartsWith("Low", StringComparison.OrdinalIgnoreCase)
            || text.Contains("prefer local", StringComparison.OrdinalIgnoreCase))
        {
            return "Low (prefer local)";
        }

        return "Normal";
    }

    private static string NormalizeSwitchBenchmarkOrder(string? value)
    {
        var input = FirstNonEmpty(value ?? string.Empty, string.Empty);
        if (string.Equals(input, "Profile B -> Profile A -> Profile B", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "Secondary -> Primary -> Secondary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "2\u21921\u21922", StringComparison.Ordinal) ||
            string.Equals(input, "2->1->2", StringComparison.OrdinalIgnoreCase))
        {
            return "Profile B -> Profile A -> Profile B";
        }

        return "Profile A -> Profile B -> Profile A";
    }

    private static string NormalizeSelectionRouteType(string? value)
    {
        var input = FirstNonEmpty(value ?? string.Empty, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        if (input.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        if (input.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloud";
        }

        return string.Empty;
    }

    private (string Model, string Variant) ResolveActiveLocalTarget()
    {
        if (IsSelection1LocalRouteSelected)
        {
            return (
                FirstNonEmpty(SelectedSelection1LocalProfile, SelectedLocalProfile),
                FirstNonEmpty(SelectedSelection1LocalVariant, FirstNonEmpty(SelectedLocalVariant, BaseVariantDisplayName)));
        }

        if (IsSelection2LocalRouteSelected)
        {
            return (
                FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile),
                FirstNonEmpty(SelectedLocalVariant, FirstNonEmpty(SelectedSelection1LocalVariant, BaseVariantDisplayName)));
        }

        return (
            FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile),
            FirstNonEmpty(SelectedLocalVariant, FirstNonEmpty(SelectedSelection1LocalVariant, BaseVariantDisplayName)));
    }

    private void UpdateSelectionRouteVisibilityStates()
    {
        var selection1Type = NormalizeSelectionRouteType(SelectedSelection1RouteType);
        IsSelection1CloudRouteSelected = selection1Type.Equals("Cloud", StringComparison.Ordinal);
        IsSelection1LocalRouteSelected = selection1Type.Equals("Local", StringComparison.Ordinal);

        var selection2Type = NormalizeSelectionRouteType(SelectedSelection2RouteType);
        IsSelection2CloudRouteSelected = IsSelection2TemplateChosen && selection2Type.Equals("Cloud", StringComparison.Ordinal);
        IsSelection2LocalRouteSelected = IsSelection2TemplateChosen && selection2Type.Equals("Local", StringComparison.Ordinal);
        IsAnyCloudRouteSelected = IsSelection1CloudRouteSelected || IsSelection2CloudRouteSelected;
        IsAnyLocalRouteSelected = IsSelection1LocalRouteSelected || IsSelection2LocalRouteSelected;
    }

    private static IBrush ResolveRouteIndicatorBrush(RouteIndicatorState state)
    {
        return state switch
        {
            RouteIndicatorState.Live => Brushes.LimeGreen,
            RouteIndicatorState.Amber => Brushes.DarkOrange,
            RouteIndicatorState.Fault => Brushes.Crimson,
            _ => Brushes.DarkGray
        };
    }

    private static double ResolveRouteIndicatorOpacity(RouteIndicatorState state)
    {
        return state == RouteIndicatorState.Off ? 0.25 : 1.0;
    }

    private static bool IsRouteNotStartedResult(RuntimeActionResult result)
    {
        return result.Status.Contains("not started", StringComparison.OrdinalIgnoreCase)
            || result.Details.Contains("not started", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeRouteIndicatorState(RouteIndicatorState state)
    {
        return state switch
        {
            RouteIndicatorState.Live => "Green: the mounted profile is connected to the endpoint.",
            RouteIndicatorState.Amber => "Amber: the server has mounted this profile and is standing by, but it is not connected to the endpoint yet.",
            RouteIndicatorState.Fault => "Red: a fault is preventing operation (configuration, subscription, network, or endpoint failure).",
            _ => "Gray: no profile is mounted."
        };
    }

    private RouteIndicatorState ResolveActiveRouteIndicatorState()
    {
        if (_mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            if (LocalRouteIndicatorState is RouteIndicatorState.Fault
                || CloudRouteIndicatorState is RouteIndicatorState.Fault)
            {
                return RouteIndicatorState.Fault;
            }

            if (LocalRouteIndicatorState is RouteIndicatorState.Live
                || CloudRouteIndicatorState is RouteIndicatorState.Live)
            {
                return RouteIndicatorState.Live;
            }

            if (LocalRouteIndicatorState is RouteIndicatorState.Amber
                || CloudRouteIndicatorState is RouteIndicatorState.Amber)
            {
                return RouteIndicatorState.Amber;
            }
        }

        if (_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return CloudRouteIndicatorState;
        }

        if (_mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return LocalRouteIndicatorState;
        }

        if (_activeQuickSelectSlot is { IsCloudRoute: true })
        {
            return CloudRouteIndicatorState;
        }

        if (_activeQuickSelectSlot is { IsLocalRoute: true })
        {
            return LocalRouteIndicatorState;
        }

        if (LocalRouteIndicatorState is not RouteIndicatorState.Off)
        {
            return LocalRouteIndicatorState;
        }

        if (CloudRouteIndicatorState is not RouteIndicatorState.Off)
        {
            return CloudRouteIndicatorState;
        }

        return RouteIndicatorState.Off;
    }

    private string ResolveActiveRouteStatusLabel(RouteIndicatorState state)
    {
        if (!string.IsNullOrWhiteSpace(_mountedRouteType))
        {
            return FormatMountedRouteProfileName();
        }

        if (_activeQuickSelectSlot is { } slot)
        {
            return FormatActiveRouteProfileName(slot);
        }

        if (state is RouteIndicatorState.Off)
        {
            return "Idle";
        }

        if (LocalRouteIndicatorState is not RouteIndicatorState.Off)
        {
            return FormatRouteProfileStatusName("Local", string.Empty, SelectedLocalProfile, SelectedLocalVariant);
        }

        if (CloudRouteIndicatorState is not RouteIndicatorState.Off)
        {
            return FormatRouteProfileStatusName("Cloud", SelectedCloudProvider, SelectedCloudProfile, SelectedCloudVariant);
        }

        return "Idle";
    }

    private string FormatMountedRouteProfileName()
    {
        var lastServed = FormatLastServedLabel();
        var showLastServed = !string.IsNullOrWhiteSpace(lastServed) && !LastServedMatchesMountedRoute();
        if (_mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            var hot = "Routing | "
                + FormatRouteProfileStatusName("Local", string.Empty, _runningLocalModel, _runningLocalVariant)
                + " \u00b7 "
                + FormatRouteProfileStatusName("Cloud", _mountedCloudProvider, _mountedCloudModel, _mountedCloudVariant);
            return showLastServed ? lastServed + " — " + hot : hot;
        }

        if (_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            var name = FormatRouteProfileStatusName("Cloud", _mountedCloudProvider, _mountedCloudModel, _mountedCloudVariant);
            return showLastServed ? lastServed + " — " + name : name;
        }

        if (_mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            var name = FormatRouteProfileStatusName("Local", string.Empty, _runningLocalModel, _runningLocalVariant);
            return showLastServed ? lastServed + " — " + name : name;
        }

        return string.IsNullOrWhiteSpace(lastServed) ? "Idle" : lastServed;
    }

    private bool LastServedMatchesMountedRoute()
    {
        if (!RequestRoutingEnabled)
        {
            return true;
        }

        var snapshot = _runtimeService.ReadLastServedRoute();
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Kind))
        {
            return true;
        }

        if (snapshot.Kind.Equals("cloud", StringComparison.OrdinalIgnoreCase))
        {
            if (!_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
                && !_mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return snapshot.Model.Equals(_mountedCloudModel, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(snapshot.Provider)
                    || snapshot.Provider.Equals(_mountedCloudProvider, StringComparison.OrdinalIgnoreCase));
        }

        if (!_mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase)
            && !_mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return snapshot.Model.Equals(_runningLocalModel, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatLastServedLabel()
    {
        if (!RequestRoutingEnabled)
        {
            return string.Empty;
        }

        var snapshot = _runtimeService.ReadLastServedRoute();
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Kind))
        {
            return string.Empty;
        }

        var kind = snapshot.Kind.Equals("cloud", StringComparison.OrdinalIgnoreCase) ? "Cloud" : "Local";
        var model = string.IsNullOrWhiteSpace(snapshot.Model) ? snapshot.Provider : snapshot.Model;
        return "Last served: " + kind + " | " + FirstNonEmpty(model, "model");
    }

    private static string FormatRouteProfileStatusName(string routeType, string? provider, string? model, string? variant)
    {
        var variantName = ProfileConfigVariantName(variant);
        var isDefault = string.IsNullOrWhiteSpace(variantName)
            || variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        if (routeType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return isDefault
                ? $"Cloud | {FirstNonEmpty(provider, "provider")} | {FirstNonEmpty(model, "model")} (default)"
                : $"Cloud | {FirstNonEmpty(provider, "provider")} | {FirstNonEmpty(model, "model")}: {variantName}";
        }

        return isDefault
            ? $"Local | {FirstNonEmpty(model, "model")} (default)"
            : $"Local | {FirstNonEmpty(model, "model")}: {variantName}";
    }

    private static string FormatActiveRouteProfileName(RouteSlotViewModel slot)
    {
        if (slot.SelectedValidatedProfile is { } profile
            && !string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return profile.DisplayName;
        }

        if (slot.IsCloudRoute)
        {
            return FormatRouteProfileStatusName("Cloud", slot.CloudProvider, slot.CloudModel, slot.CloudVariant);
        }

        if (slot.IsLocalRoute)
        {
            return FormatRouteProfileStatusName("Local", string.Empty, slot.LocalModel, slot.LocalVariant);
        }

        return slot.DisplayName;
    }

    private void RememberMountedLocal(string? model, string? variant)
    {
        _runningLocalModel = model ?? string.Empty;
        _runningLocalVariant = variant ?? string.Empty;
        if (!RequestRoutingEnabled)
        {
            _mountedRouteType = "Local";
            _mountedCloudProvider = string.Empty;
            _mountedCloudModel = string.Empty;
            _mountedCloudVariant = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(_mountedCloudProvider))
        {
            _mountedRouteType = "Local";
        }
        else
        {
            _mountedRouteType = "Route";
        }

        BindActiveQuickSelectSlotToMountedRoute();
        _mountedRouteGeneration++;
        RefreshRouteSlotIndicators();
        TrySyncEndpointSettingsForMountedRoute();
    }

    private void RememberMountedCloud(string provider, string model, string variant)
    {
        _mountedCloudProvider = provider ?? string.Empty;
        _mountedCloudModel = model ?? string.Empty;
        _mountedCloudVariant = variant ?? string.Empty;
        if (!RequestRoutingEnabled)
        {
            _mountedRouteType = "Cloud";
            _runningLocalModel = string.Empty;
            _runningLocalVariant = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(_runningLocalModel))
        {
            _mountedRouteType = "Cloud";
        }
        else
        {
            _mountedRouteType = "Route";
        }

        BindActiveQuickSelectSlotToMountedRoute();
        _mountedRouteGeneration++;
        RefreshRouteSlotIndicators();
        TrySyncEndpointSettingsForMountedRoute();
    }

    private void ClearMountedRoute()
    {
        StopManagedHarnessWebForHygiene(
            "Harness web stopped because the local model was unloaded. Close old Harness browser tabs if any remain open.");
        _mountedRouteType = string.Empty;
        _mountedCloudProvider = string.Empty;
        _mountedCloudModel = string.Empty;
        _mountedCloudVariant = string.Empty;
        _runningLocalModel = string.Empty;
        _runningLocalVariant = string.Empty;
        _activeQuickSelectSlot = null;
        ResetActiveQuickSelectRuntimePresentation();
        _mountedRouteGeneration++;
        CancelActiveEndpointHealthMonitor();
        RefreshRouteSlotIndicators();
        RefreshHarnessYamlSurfaces();
        _clineSyncedContextFingerprint = string.Empty;
        _endpointSyncedFingerprint = string.Empty;
    }

    private void BindActiveQuickSelectSlotToPreferredRoute(bool preferCloud)
    {
        if (preferCloud)
        {
            var cloudSlot = FindMountedCloudQuickSelectSlot();
            if (cloudSlot is not null)
            {
                _activeQuickSelectSlot = cloudSlot;
                return;
            }
        }

        var localSlot = FindMountedLocalQuickSelectSlot();
        if (localSlot is not null)
        {
            _activeQuickSelectSlot = localSlot;
            return;
        }

        BindActiveQuickSelectSlotToMountedRoute();
    }

    private RouteSlotViewModel? FindMountedCloudQuickSelectSlot()
    {
        var exact = QuickSelectSlots.FirstOrDefault(slot =>
            slot.IsCloudRoute
            && slot.CloudProvider.Equals(_mountedCloudProvider, StringComparison.OrdinalIgnoreCase)
            && slot.CloudModel.Equals(_mountedCloudModel, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(slot.CloudVariant).Equals(ProfileConfigVariantName(_mountedCloudVariant), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return QuickSelectSlots.FirstOrDefault(slot =>
            slot.IsCloudRoute
            && slot.CloudProvider.Equals(_mountedCloudProvider, StringComparison.OrdinalIgnoreCase)
            && slot.CloudModel.Equals(_mountedCloudModel, StringComparison.OrdinalIgnoreCase))
            ?? QuickSelectSlots.FirstOrDefault(slot => slot.IsCloudRoute);
    }

    private RouteSlotViewModel? FindMountedLocalQuickSelectSlot()
    {
        return QuickSelectSlots.FirstOrDefault(slot =>
            slot.IsLocalRoute
            && slot.LocalModel.Equals(_runningLocalModel, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(slot.LocalVariant).Equals(ProfileConfigVariantName(_runningLocalVariant), StringComparison.OrdinalIgnoreCase));
    }

    private void BindActiveQuickSelectSlotToMountedRoute()
    {
        if (PreferApprovedCloudRoute())
        {
            var cloudSlot = FindMountedCloudQuickSelectSlot();
            if (cloudSlot is not null)
            {
                _activeQuickSelectSlot = cloudSlot;
                return;
            }
        }

        if (_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            _activeQuickSelectSlot = FindMountedCloudQuickSelectSlot();
            return;
        }

        // Local and dual-hot Route both highlight the loaded local Quick Select row (local stays first).
        _activeQuickSelectSlot = FindMountedLocalQuickSelectSlot();
    }

    private void RefreshRouteSlotIndicators()
    {
        var state = ResolveActiveRouteIndicatorState();
        ActiveRouteStatusLabel = ResolveActiveRouteStatusLabel(state);
        ActiveRouteStatusBrush = ResolveRouteIndicatorBrush(state);
        ActiveRouteStatusOpacity = ResolveRouteIndicatorOpacity(state);
        ActiveRouteStatusTooltip = ActiveRouteStatusLabel + " — " + DescribeRouteIndicatorState(state);
        IsModelActive = LocalRouteIndicatorState is RouteIndicatorState.Live or RouteIndicatorState.Amber
            || CloudRouteIndicatorState is RouteIndicatorState.Live or RouteIndicatorState.Amber;
        RefreshQuickSelectSlotActiveStates(state);
    }

    private void RefreshQuickSelectSlotActiveStates(RouteIndicatorState activeState)
    {
        var highlightActiveSlot = activeState is RouteIndicatorState.Live
            or RouteIndicatorState.Amber
            or RouteIndicatorState.Fault;
        foreach (var slot in QuickSelectSlots)
        {
            var isActive = highlightActiveSlot && ReferenceEquals(slot, _activeQuickSelectSlot);
            if (isActive)
            {
                slot.ApplyActiveRuntimeState(
                    isActive: true,
                    activeState,
                    BuildActiveQuickSelectTelemetry(slot, activeState));
                continue;
            }

            var standbyState = ResolveStandbyHotState(slot);
            var isStandby = standbyState is RouteIndicatorState.Live
                or RouteIndicatorState.Amber
                or RouteIndicatorState.Fault;
            slot.ApplyActiveRuntimeState(
                isActive: false,
                isStandby ? standbyState : RouteIndicatorState.Off,
                isStandby ? BuildStandbyQuickSelectTelemetry(slot, standbyState) : string.Empty,
                isStandby: isStandby);
        }

        SyncQuickSelectLoadingPulseTimer();

        if (highlightActiveSlot && _activeQuickSelectSlot is not null)
        {
            _activeQuickSelectInFlightKnown = _runtimeService.InFlightChatCount > 0;
            if (_runtimeService.InFlightChatCount == 0)
            {
                RefreshQuickSelectSlotFacts(_activeQuickSelectSlot);
            }
        }
    }

    private void SyncQuickSelectLoadingPulseTimer()
    {
        var anyLoading = QuickSelectSlots.Any(slot => slot.IsLoadingRuntimeSlot);
        if (!anyLoading)
        {
            StopQuickSelectLoadingPulseTimer();
            if (QuickSelectLoadingPulseOn)
            {
                QuickSelectLoadingPulseOn = false;
            }

            ApplyQuickSelectLoadingPulse(false);
            return;
        }

        if (_quickSelectLoadingPulseTimer is not null)
        {
            ApplyQuickSelectLoadingPulse(QuickSelectLoadingPulseOn);
            return;
        }

        QuickSelectLoadingPulseOn = true;
        ApplyQuickSelectLoadingPulse(true);
        _quickSelectLoadingPulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _quickSelectLoadingPulseTimer.Tick += OnQuickSelectLoadingPulseTick;
        _quickSelectLoadingPulseTimer.Start();
    }

    private void OnQuickSelectLoadingPulseTick(object? sender, EventArgs e)
    {
        if (!QuickSelectSlots.Any(slot => slot.IsLoadingRuntimeSlot))
        {
            StopQuickSelectLoadingPulseTimer();
            QuickSelectLoadingPulseOn = false;
            ApplyQuickSelectLoadingPulse(false);
            return;
        }

        QuickSelectLoadingPulseOn = !QuickSelectLoadingPulseOn;
        ApplyQuickSelectLoadingPulse(QuickSelectLoadingPulseOn);
    }

    private void StopQuickSelectLoadingPulseTimer()
    {
        if (_quickSelectLoadingPulseTimer is null)
        {
            return;
        }

        _quickSelectLoadingPulseTimer.Tick -= OnQuickSelectLoadingPulseTick;
        _quickSelectLoadingPulseTimer.Stop();
        _quickSelectLoadingPulseTimer = null;
    }

    private void ApplyQuickSelectLoadingPulse(bool on)
    {
        foreach (var slot in QuickSelectSlots)
        {
            slot.LoadingPulseOn = on && slot.IsLoadingRuntimeSlot;
        }
    }

    private RouteIndicatorState ResolveStandbyHotState(RouteSlotViewModel slot)
    {
        if (CloudSlotIsRuntimeHot(slot))
        {
            return CloudRouteIndicatorState is RouteIndicatorState.Off
                ? RouteIndicatorState.Live
                : CloudRouteIndicatorState;
        }

        // One llama-server only. A second local slot is never "loaded / standing by".
        // Local standby is only when the answering slot is cloud (dual-hot).
        if (_activeQuickSelectSlot is { IsLocalRoute: true })
        {
            return RouteIndicatorState.Off;
        }

        if (LocalSlotIsRuntimeHot(slot))
        {
            return LocalRouteIndicatorState is RouteIndicatorState.Off
                ? RouteIndicatorState.Live
                : LocalRouteIndicatorState;
        }

        return RouteIndicatorState.Off;
    }

    private bool CloudSlotIsRuntimeHot(RouteSlotViewModel slot)
    {
        if (!_runtimeService.IsCloudRouteActive())
        {
            return false;
        }

        var (provider, model) = _runtimeService.GetActiveCloudIdentity();
        if (CloudSlotMatchesIdentity(slot, provider, model))
        {
            return true;
        }

        return CloudSlotMatchesIdentity(slot, _mountedCloudProvider, _mountedCloudModel)
            && !string.IsNullOrWhiteSpace(_mountedCloudModel);
    }

    private bool LocalSlotIsRuntimeHot(RouteSlotViewModel slot)
    {
        if (!_runtimeService.IsManagedLocalAlive())
        {
            return false;
        }

        var identity = _runtimeService.GetManagedLocalIdentity();
        if (LocalSlotMatchesIdentity(slot, identity.Model, identity.Variant))
        {
            return true;
        }

        return LocalSlotMatchesIdentity(slot, _runningLocalModel, _runningLocalVariant)
            && !string.IsNullOrWhiteSpace(_runningLocalModel);
    }

    private static bool CloudSlotMatchesIdentity(RouteSlotViewModel slot, string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (!slot.IsCloudRoute
            && slot.SelectedValidatedProfile?.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var slotProvider = FirstNonEmpty(slot.CloudProvider, slot.SelectedValidatedProfile?.Provider);
        var slotModel = FirstNonEmpty(slot.CloudModel, slot.SelectedValidatedProfile?.Model);
        return slotProvider.Equals(provider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && slotModel.Equals(model, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LocalSlotMatchesIdentity(RouteSlotViewModel slot, string? model, string? variant)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (!slot.IsLocalRoute
            && slot.SelectedValidatedProfile?.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var slotModel = FirstNonEmpty(slot.LocalModel, slot.SelectedValidatedProfile?.Model);
        var slotVariant = FirstNonEmpty(slot.LocalVariant, slot.SelectedValidatedProfile?.Variant);
        if (!slotModel.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NormalizeVariantName(slotVariant).Equals(NormalizeVariantName(variant), StringComparison.OrdinalIgnoreCase);
    }

    private string BuildStandbyQuickSelectTelemetry(RouteSlotViewModel slot, RouteIndicatorState state)
    {
        return state switch
        {
            RouteIndicatorState.Amber when slot.IsLocalRoute =>
                "llama-server is starting — then standing by.",
            RouteIndicatorState.Amber =>
                "Connecting cloud — then standing by.",
            RouteIndicatorState.Fault =>
                "Route fault — run Health or Restart. This slot is still the launched standby.",
            RouteIndicatorState.Live when slot.IsLocalRoute =>
                "llama-server is loaded and standing by.",
            RouteIndicatorState.Live when slot.IsCloudRoute =>
                "Cloud is ready and standing by.",
            _ => string.Empty
        };
    }

    private string BuildActiveQuickSelectTelemetry(
        RouteSlotViewModel slot,
        RouteIndicatorState state)
    {
        return state switch
        {
            RouteIndicatorState.Amber when slot.IsLocalRoute =>
                BuildActiveLocalStartingTelemetry(slot),
            RouteIndicatorState.Amber =>
                "Connecting cloud route…",
            RouteIndicatorState.Fault => "Route fault — run Health or Restart",
            RouteIndicatorState.Live when slot.IsCloudRoute => BuildActiveCloudQuickSelectTelemetry(slot),
            RouteIndicatorState.Live when slot.IsLocalRoute => BuildActiveLocalQuickSelectTelemetry(slot),
            _ => string.Empty
        };
    }

    private string BuildActiveLocalStartingTelemetry(RouteSlotViewModel slot)
    {
        var estimateMs = _runtimeService.GetLocalLaunchEstimateMs(slot.LocalModel, slot.LocalVariant);
        return estimateMs > 0
            ? $"Starting llama-server \u00b7 cold load ~{FormatFriendlyDurationMs(estimateMs)}"
            : "Starting llama-server…";
    }

    private string BuildActiveCloudQuickSelectTelemetry(RouteSlotViewModel slot)
    {
        var block = TryGetCloudBreakerForSlot(slot);
        return block is null ? string.Empty : FormatCloudBreakerActionable(block);
    }

    private string BuildActiveLocalQuickSelectTelemetry(RouteSlotViewModel slot)
    {
        if (_runtimeService.InFlightChatCount > 0)
        {
            return "Generating reply — wait before Stop or Restart";
        }

        var settings = GetLiveLocalProfileSettings(slot.LocalVariant, slot.LocalModel);
        var parts = new List<string>();

        var cloudBreaker = TryGetCloudBreakerForSlot(slot);
        if (cloudBreaker is not null)
        {
            parts.Add(FormatCloudBreakerActionable(cloudBreaker));
        }

        var idleStopMinutes = _runtimeService.GetLocalIdleStopMinutesRemaining();
        if (idleStopMinutes is int remaining)
        {
            var whenText = remaining <= 0
                ? "under 1 min"
                : remaining == 1
                    ? "1 min"
                    : remaining.ToString(CultureInfo.InvariantCulture) + " min";
            parts.Add($"Idle stop in {whenText} — send a message or change Idle timeout in Diagnostics");
        }

        var vramAction = BuildActiveSlotVramActionable(slot, settings);
        if (!string.IsNullOrWhiteSpace(vramAction))
        {
            parts.Add(vramAction);
        }

        return string.Join(" \u00b7 ", parts);
    }

    private string? BuildActiveSlotVramActionable(RouteSlotViewModel slot, JsonObject? settings)
    {
        if (settings is null || !TryReadCachedGpuFree(out var freeGiB, out var totalGiB))
        {
            return null;
        }

        var measuredGiB = ParseDouble(settings["MeasuredVramGiB"]?.ToString(), 0d);
        if (measuredGiB > 0 && measuredGiB > freeGiB - LocalVramPressureAdvisor.HeadroomGiB)
        {
            return $"VRAM tight — {freeGiB.ToString("0.0", CultureInfo.InvariantCulture)} GiB free — reduce Context or turn Images off";
        }

        var modelPath = ResolveLocalModelPathForSlot(slot.LocalModel);
        if (modelPath is null)
        {
            return null;
        }

        var fileGiB = new FileInfo(modelPath).Length / 1024d / 1024d / 1024d;
        var imagesOn = (settings["LocalVisionEnabled"]?.ToString() ?? string.Empty)
            .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        var estimatedGiB = LocalVramFootprintEstimate.EstimateGiB(settings, fileGiB, imagesOn);
        var pressure = LocalVramPressureAdvisor.Assess(
            estimatedGiB,
            freeGiB,
            totalGiB,
            settings["LocalGpuOffloadMode"]?.ToString() ?? string.Empty,
            imagesOn,
            profileCurrentlyLoaded: slot.IsLocalRoute
                && IsLocalProfileCurrentlyLoaded(slot.LocalModel, slot.LocalVariant),
            measuredGiB: measuredGiB);
        if (!pressure.ShowWarning)
        {
            return null;
        }

        var variantHint = string.Empty;
        var suggestion = _runtimeService.FindValidatedVramAlternative(
            slot.LocalModel,
            slot.LocalVariant,
            freeGiB,
            measuredGiB > 0 ? measuredGiB : estimatedGiB,
            fileGiB);
        variantHint = LocalVramVariantAdvisor.FormatSuggestion(suggestion);

        return $"VRAM tight — {freeGiB.ToString("0.0", CultureInfo.InvariantCulture)} GiB free — reduce Context or turn Images off{variantHint}";
    }

    private bool TryReadCachedGpuFree(out double freeGiB, out double totalGiB)
    {
        freeGiB = 0;
        totalGiB = 0;
        if (GpuVramGauge.DetailText.Equals("NVIDIA readout unavailable", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(GpuVramGauge.DetailText))
        {
            return false;
        }

        var parts = GpuVramGauge.DetailText.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!double.TryParse(parts[0].Replace(" GiB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var usedGiB)
            || !double.TryParse(parts[1].Replace(" GiB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out totalGiB)
            || totalGiB <= 0)
        {
            return false;
        }

        freeGiB = Math.Max(0, totalGiB - usedGiB);
        return true;
    }

    private string? ResolveLocalModelPathForSlot(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(LocalModelDirectory))
        {
            return null;
        }

        var path = Path.Combine(LocalModelDirectory, model);
        return File.Exists(path) ? path : null;
    }

    private IReadOnlyList<string> BuildQuickSelectSuitabilityHints(
        RouteSlotViewModel slot,
        JsonObject? settings,
        bool isLocal)
    {
        var hints = new List<string>();
        if (settings is null)
        {
            return hints;
        }

        if (isLocal)
        {
            var measuredGiB = ParseDouble(settings["MeasuredVramGiB"]?.ToString(), 0d);
            if (measuredGiB > 0)
            {
                hints.Add($"measured VRAM {measuredGiB.ToString("F1", CultureInfo.InvariantCulture)} GiB");
            }

            var estimateMs = _runtimeService.GetLocalLaunchEstimateMs(slot.LocalModel, slot.LocalVariant);
            if (estimateMs > 0)
            {
                hints.Add($"cold load ~{FormatFriendlyDurationMs(estimateMs)}");
            }

            var fastestMs = _runtimeService.GetLocalLaunchFastestMs(slot.LocalModel, slot.LocalVariant);
            if (fastestMs > 0 && fastestMs < estimateMs)
            {
                hints.Add($"warm load ~{FormatFriendlyDurationMs(fastestMs)}");
            }
        }

        if (ReferenceEquals(slot, _activeQuickSelectSlot)
            && _runtimeService.InFlightChatCount == 0
            && !GpuVramGauge.DetailText.Equals("NVIDIA readout unavailable", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GpuVramGauge.DetailText))
        {
            hints.Add($"GPU VRAM {GpuVramGauge.DetailText}");
        }

        return hints;
    }

    private async Task ApplyRequestRoutingPreferenceAsync(bool enabled)
    {
        _runtimeService.SetRequestRoutingTopology(RequestRoutingTopology.DualHot);
        var result = await _runtimeService.ApplyRequestRoutingAsync(enabled);
        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        StatusMessage = result.Status;
        if (!enabled
            && _mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            _mountedRouteType = !string.IsNullOrWhiteSpace(_runningLocalModel) ? "Local" : "Cloud";
        }

        RefreshRouteSlotIndicators();
        UpdateRoutingPoolStatus();
        UpdateRequestRoutingModeText();
        if (!string.IsNullOrWhiteSpace(_mountedRouteType))
        {
            StartMountedEndpointHealthMonitor();
        }
    }

    private void EnsureSelection1LocalDefaults()
    {
        if (!LocalProfiles.Contains(SelectedSelection1LocalProfile))
        {
            var alternativeProfile = LocalProfiles
                .FirstOrDefault(x => !x.Equals(SelectedLocalProfile, StringComparison.OrdinalIgnoreCase));
            SelectedSelection1LocalProfile = FirstNonEmpty(alternativeProfile ?? string.Empty, SelectedLocalProfile);
        }

        if (!LocalVariants.Contains(SelectedSelection1LocalVariant))
        {
            var alternativeVariant = LocalVariants
                .FirstOrDefault(x => !x.Equals(SelectedLocalVariant, StringComparison.OrdinalIgnoreCase));
            SelectedSelection1LocalVariant = FirstNonEmpty(alternativeVariant ?? string.Empty, SelectedLocalVariant);
        }
    }

    private void EnsureSelection2CloudDefaults()
    {
        if (!Selection2CloudProfiles.Contains(SelectedSelection2CloudProfile))
        {
            var alternative = Selection2CloudProfiles.FirstOrDefault();
            SelectedSelection2CloudProfile = FirstNonEmpty(alternative ?? string.Empty, SelectedCloudProfile);
        }

        if (!CloudVariants.Contains(SelectedSelection2CloudVariant))
        {
            var fallback = CloudVariants.FirstOrDefault();
            SelectedSelection2CloudVariant = FirstNonEmpty(fallback ?? string.Empty, SelectedCloudVariant);
        }
    }

    private void ApplyCloudTemplateDefaults()
    {
        if (CloudProviders.Count == 0)
        {
            RefreshCloudProvidersList();
        }

        if (string.IsNullOrWhiteSpace(SelectedCloudProvider))
        {
            SelectedCloudProvider = CloudProviders.FirstOrDefault() ?? "Gemini";
        }

        RefreshCloudProfilesForProvider();

        if (string.IsNullOrWhiteSpace(SelectedCloudProfile))
        {
            SelectedCloudProfile = CloudProfiles.FirstOrDefault() ?? string.Empty;
        }

        RefreshCloudVariantsForSelection();
        SelectedCloudVariant = FirstNonEmpty(SelectedCloudVariant, BaseVariantDisplayName);
        EnsureCloudVariantEntry(SelectedCloudVariant);
        EnsureSelection2CloudDefaults();
    }

    private void ApplyLocalTemplateDefaultsForSelection1()
    {
        var selected = FirstNonEmpty(
            SelectedSelection1LocalProfile,
            FirstNonEmpty(SelectedLocalProfile, LocalProfiles.FirstOrDefault() ?? string.Empty));

        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = "Local Balanced";
        }

        AddIfMissing(LocalProfiles, selected);
        SelectedSelection1LocalProfile = selected;
        SelectedSelection1LocalVariant = BaseVariantDisplayName;
        AddIfMissing(LocalVariants, BaseVariantDisplayName);
        EnsureLocalVariantEntry(SelectedSelection1LocalVariant);

        if (string.IsNullOrWhiteSpace(SelectedLocalProfile))
        {
            SelectedLocalProfile = selected;
        }

        if (string.IsNullOrWhiteSpace(SelectedLocalVariant))
        {
            SelectedLocalVariant = BaseVariantDisplayName;
        }

        TryAutoDiscoverLocalModelDirectory();
    }

    private void ApplyLocalTemplateDefaultsForSelection2()
    {
        var selected = FirstNonEmpty(SelectedLocalProfile, LocalProfiles.FirstOrDefault() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = "Local Balanced";
        }

        AddIfMissing(LocalProfiles, selected);
        SelectedLocalProfile = selected;
        SelectedLocalVariant = FirstNonEmpty(SelectedLocalVariant, BaseVariantDisplayName);
        AddIfMissing(LocalVariants, SelectedLocalVariant);
        EnsureLocalVariantEntry(SelectedLocalVariant);

        TryAutoDiscoverLocalModelDirectory();
    }

    private bool TryAutoDiscoverLocalModelDirectory()
    {
        if (!string.IsNullOrWhiteSpace(LocalModelDirectory) && Directory.Exists(LocalModelDirectory))
        {
            return false;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            @"C:\\models",
            Path.Combine(userProfile, "models"),
            Path.Combine(userProfile, "Downloads", "models"),
            Path.Combine(userProfile, ".cache", "lm-studio", "models")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            var hasModelFiles = false;
            try
            {
                hasModelFiles = Directory.EnumerateFiles(candidate, "*.gguf", SearchOption.AllDirectories).Any();
            }
            catch
            {
                hasModelFiles = false;
            }

            if (!hasModelFiles)
            {
                continue;
            }

            LocalModelDirectory = candidate;
            return true;
        }

        return false;
    }

    private bool ValidateSwitchBenchmarkSelections(out string validationError)
    {
        validationError = string.Empty;
        if (SelectedSwitchBenchmarkProfileA is null || SelectedSwitchBenchmarkProfileB is null)
        {
            validationError = "Choose both Profile A and Profile B from the endpoint-validated Model Profiles catalogue before running the switch benchmark.";
            return false;
        }

        if (SelectedSwitchBenchmarkProfileA.Key.Equals(SelectedSwitchBenchmarkProfileB.Key, StringComparison.OrdinalIgnoreCase))
        {
            validationError = "Profile A and Profile B must be different endpoint-validated profiles or variants.";
            return false;
        }

        foreach (var profile in new[] { SelectedSwitchBenchmarkProfileA, SelectedSwitchBenchmarkProfileB })
        {
            if (profile.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase) && ResolveLocalModelPath(profile.Model) is null)
            {
                validationError = $"Local model for '{profile.DisplayName}' was not found in the configured model directory.";
                return false;
            }
        }

        return true;
    }

    private void SyncPrimaryRouteSlotsFromCurrentSelections()
    {
        if (QuickSelectSlots.Count > 0)
        {
            PersistQuickSelectSlots(saveDocument: false);
            return;
        }

        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            RefreshRouteSlotIndicators();
            return;
        }

        foreach (var node in slots.OfType<JsonObject>())
        {
            var slotId = node["slotId"]?.ToString() ?? string.Empty;
            if (slotId.Equals("slot-cloud-1", StringComparison.OrdinalIgnoreCase))
            {
                node["enabled"] = true;
                var routeType = NormalizeSelectionRouteType(SelectedSelection1RouteType);
                if (routeType.Equals("Local", StringComparison.Ordinal))
                {
                    node["routeType"] = "local";
                    node["provider"] = string.Empty;
                    node["cloudModel"] = string.Empty;
                    node["cloudVariant"] = string.Empty;
                    node["localModel"] = SelectedSelection1LocalProfile;
                    node["localVariant"] = NormalizeVariantName(SelectedSelection1LocalVariant);
                }
                else
                {
                    node["routeType"] = "cloud";
                    node["provider"] = SelectedCloudProvider;
                    node["cloudModel"] = SelectedCloudProfile;
                    node["cloudVariant"] = NormalizeVariantName(SelectedCloudVariant);
                    node["localModel"] = string.Empty;
                    node["localVariant"] = string.Empty;
                }
            }
            else if (slotId.Equals("slot-local-1", StringComparison.OrdinalIgnoreCase))
            {
                node["enabled"] = IsSelection2Enabled;
                if (!IsSelection2TemplateChosen)
                {
                    node["routeType"] = string.Empty;
                    node["provider"] = string.Empty;
                    node["cloudModel"] = string.Empty;
                    node["cloudVariant"] = string.Empty;
                    node["localModel"] = string.Empty;
                    node["localVariant"] = string.Empty;
                }
                else
                {
                    var routeType = NormalizeSelectionRouteType(SelectedSelection2RouteType);
                    if (routeType.Equals("Cloud", StringComparison.Ordinal))
                    {
                        node["routeType"] = "cloud";
                        node["provider"] = SelectedSelection2CloudProvider;
                        node["cloudModel"] = SelectedSelection2CloudProfile;
                        node["cloudVariant"] = NormalizeVariantName(SelectedSelection2CloudVariant);
                        node["localModel"] = string.Empty;
                        node["localVariant"] = string.Empty;
                    }
                    else
                    {
                        node["routeType"] = "local";
                        node["localModel"] = SelectedLocalProfile;
                        node["localVariant"] = NormalizeVariantName(SelectedLocalVariant);
                        node["provider"] = string.Empty;
                        node["cloudModel"] = string.Empty;
                        node["cloudVariant"] = string.Empty;
                    }
                }
            }
        }

        UpdateActiveRouteSlotIdsForCurrentSlotEnablement();
        RefreshRouteSlotIndicators();

    }

    private void InitializeQuickSelectSlots()
    {
        QuickSelectSlots.Clear();
        if (_config.Root["RouteSlots"] is JsonArray slots)
        {
            foreach (var node in slots.OfType<JsonObject>()
                         .Where(node => ParseBool(node["enabled"]?.ToString(), true))
                         .Take(QuickSelectSlotLimit))
            {
                var slot = new RouteSlotViewModel(this, node["slotId"]?.ToString() ?? string.Empty, QuickSelectSlots.Count + 1);
                slot.Hydrate(
                    NormalizeSelectionRouteType(node["routeType"]?.ToString()),
                    node["provider"]?.ToString() ?? string.Empty,
                    node["cloudModel"]?.ToString() ?? string.Empty,
                    FirstNonEmpty(node["cloudVariant"]?.ToString() ?? string.Empty, BaseVariantDisplayName),
                    node["localModel"]?.ToString() ?? string.Empty,
                    FirstNonEmpty(node["localVariant"]?.ToString() ?? string.Empty, BaseVariantDisplayName),
                    ParseBool(node["validatedDefaultProfile"]?.ToString(), false),
                    node["endpointValidatedUtc"]?.ToString() ?? string.Empty,
                    node["validatedProfileKey"]?.ToString() ?? string.Empty);
                QuickSelectSlots.Add(slot);
            }
        }

        RefreshQuickSelectSlotMetadata();
    }

    private void RestoreQuickSelectSlotSelections()
    {
        var recovered = false;
        if (QuickSelectSlots.Count > 0 && !QuickSelectSlots[0].HasSavedAssignment)
        {
            RecoverSlotFromLegacySelection(QuickSelectSlots[0], SelectedSelection1RouteType, SelectedCloudProvider, SelectedCloudProfile, SelectedCloudVariant, SelectedSelection1LocalProfile, SelectedSelection1LocalVariant);
            recovered = QuickSelectSlots[0].HasSavedAssignment || recovered;
        }

        if (QuickSelectSlots.Count > 1 && !QuickSelectSlots[1].HasSavedAssignment && IsSelection2Enabled && IsSelection2TemplateChosen)
        {
            RecoverSlotFromLegacySelection(QuickSelectSlots[1], SelectedSelection2RouteType, SelectedSelection2CloudProvider, SelectedSelection2CloudProfile, SelectedSelection2CloudVariant, SelectedLocalProfile, SelectedLocalVariant);
            recovered = QuickSelectSlots[1].HasSavedAssignment || recovered;
        }

        foreach (var slot in QuickSelectSlots)
        {
            slot.RefreshValidatedProfileOptions();
        }

        if (recovered)
        {
            PersistQuickSelectSlots(saveDocument: true);
        }

        Dispatcher.UIThread.Post(() =>
        {
            RefreshAvailableQuickSelectProfiles();
            foreach (var slot in QuickSelectSlots)
            {
                slot.RefreshValidatedProfileOptions();
            }

            OfferNewestUnusedValidatedProfileOnEmptySlot();
        }, DispatcherPriority.Loaded);
    }

    private void RecoverSlotFromLegacySelection(
        RouteSlotViewModel slot,
        string routeType,
        string cloudProvider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant)
    {
        var normalized = NormalizeSelectionRouteType(routeType);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var match = FindValidatedQuickSelectProfile(
            normalized,
            cloudProvider,
            cloudModel,
            cloudVariant,
            localModel,
            localVariant);
        if (match is null)
        {
            return;
        }

        slot.Hydrate(
            match.RouteType,
            match.Provider,
            match.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ? match.Model : string.Empty,
            match.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ? match.Variant : BaseVariantDisplayName,
            match.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase) ? match.Model : string.Empty,
            match.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase) ? match.Variant : BaseVariantDisplayName,
            isDefaultProfileValidated: true,
            endpointValidatedUtc: slot.EndpointValidatedUtc,
            validatedProfileKey: match.Key);
    }

    [RelayCommand]
    internal void AddQuickSelectSlot()
    {
        if (!CanAddQuickSelectSlots)
        {
            StatusMessage = DescribeQuickSelectSlotAddBlock();
            return;
        }

        var slot = new RouteSlotViewModel(this, string.Empty, QuickSelectSlots.Count + 1);
        slot.Hydrate(
            string.Empty,
            string.Empty,
            string.Empty,
            BaseVariantDisplayName,
            string.Empty,
            BaseVariantDisplayName,
            isDefaultProfileValidated: false,
            endpointValidatedUtc: string.Empty);
        QuickSelectSlots.Add(slot);
        RefreshQuickSelectSlotMetadata();
        RefreshAvailableQuickSelectProfiles();
        PersistQuickSelectSlots(saveDocument: true);
        StatusMessage = $"Added Selection {slot.SlotNumber}.";
    }

    private string DescribeQuickSelectSlotAddBlock()
    {
        var unsaved = QuickSelectSlots.FirstOrDefault(slot => slot.HasUnsavedPopulatedAssignment);
        if (unsaved is not null)
        {
            return $"Add New Slot function is suspended until current incomplete slot definition is completed: click Save on Selection {unsaved.SlotNumber} first. That row has a profile chosen in the dropdown but is not saved yet.";
        }

        if (QuickSelectSlots.Count >= QuickSelectSlotLimit)
        {
            return $"Quick Select is full ({QuickSelectSlotLimit} slots). Delete a row before adding another.";
        }

        return "Add New Slot is not available right now.";
    }

    internal void DeleteQuickSelectSlot(RouteSlotViewModel slot)
    {
        if (!QuickSelectSlots.Contains(slot) || !ConfirmSlotDeletion($"Selection {slot.SlotNumber}"))
        {
            return;
        }

        var deletedNumber = slot.SlotNumber;
        if (ReferenceEquals(slot, _activeQuickSelectSlot))
        {
            _activeQuickSelectSlot = null;
            ResetActiveQuickSelectRuntimePresentation();
        }

        QuickSelectSlots.Remove(slot);
        BindActiveQuickSelectSlotToMountedRoute();
        RefreshRouteSlotIndicators();
        RefreshQuickSelectSlotMetadata();
        PersistQuickSelectSlots(saveDocument: true);
        StatusMessage = $"Deleted Selection {deletedNumber}.";
    }

    internal string? DescribeQuickSelectConflict(RouteSlotViewModel slot, ValidatedQuickSelectProfileOption profile)
    {
        foreach (var other in QuickSelectSlots)
        {
            if (ReferenceEquals(other, slot) || !other.HasSavedAssignment)
            {
                continue;
            }

            if (SameQuickSelectIdentity(other, profile))
            {
                return $"Selection {other.SlotNumber} already uses {profile.DisplayName}.";
            }
        }

        return null;
    }

    internal IReadOnlyList<ValidatedQuickSelectProfileOption> GetAvailableValidatedProfilesForSlot(RouteSlotViewModel slot)
    {
        var available = new List<ValidatedQuickSelectProfileOption>();
        foreach (var option in ValidatedQuickSelectProfiles)
        {
            if (IsProfileOfferedInQuickSelectSlot(slot, option))
            {
                available.Add(option);
            }
        }

        return available
            .OrderBy(option => IsValidatedProfileAssignedToAnySlot(option) ? 1 : 0)
            .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsValidatedProfileAssignedToAnySlot(ValidatedQuickSelectProfileOption option)
    {
        if (option.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return IsCloudProfileInQuickSelect(option.Provider, option.Model, option.Variant);
        }

        return IsLocalProfileInQuickSelect(option.Model, option.Variant);
    }

    private bool IsProfileOfferedInQuickSelectSlot(RouteSlotViewModel slot, ValidatedQuickSelectProfileOption option)
    {
        if (slot.SelectedValidatedProfile is { } selected
            && selected.Key.Equals(option.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (slot.HasSavedAssignment && SameQuickSelectIdentity(slot, option))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(DescribeQuickSelectConflict(slot, option));
    }

    private static bool SameQuickSelectIdentity(RouteSlotViewModel slot, ValidatedQuickSelectProfileOption profile)
        => profile.MatchesSavedAssignment(
            slot.RouteType,
            slot.CloudProvider,
            slot.CloudModel,
            slot.CloudVariant,
            slot.LocalModel,
            slot.LocalVariant);

    private string DescribeValidatedProfileQuickSelectFollowThrough(RouteSlotViewModel? offeredSlot)
    {
        if (offeredSlot is not null)
        {
            return "Add New Slot stays paused until you click Save on Selection " + offeredSlot.SlotNumber + ". That empty row already has this profile chosen.";
        }

        var unsaved = QuickSelectSlots.FirstOrDefault(slot => slot.HasUnsavedPopulatedAssignment);
        if (unsaved is not null)
        {
            return "Add New Slot stays paused until you click Save on Selection " + unsaved.SlotNumber + ". That row has a profile chosen in the dropdown but is not saved yet.";
        }

        if (QuickSelectSlots.Count >= QuickSelectSlotLimit)
        {
            return "This profile is listed in the Quick Select dropdown. Quick Select is full — delete a row before adding another.";
        }

        return "This profile is listed in the Quick Select dropdown. Click Add New Slot, choose it, then Save.";
    }

    private RouteSlotViewModel? OfferValidatedProfileOnEmptyQuickSelectSlot(
        string routeType,
        string provider,
        string model,
        string variant)
    {
        var option = FindValidatedQuickSelectProfile(
            routeType,
            provider,
            model,
            variant,
            model,
            variant);
        if (option is null)
        {
            return null;
        }

        var empty = QuickSelectSlots.FirstOrDefault(slot =>
            !slot.HasSavedAssignment && !slot.HasUnsavedPopulatedAssignment);
        if (empty is null)
        {
            return null;
        }

        empty.SelectDraftProfile(option);
        RefreshQuickSelectSlotMetadata();
        return empty;
    }

    private RouteSlotViewModel? OfferNewestUnusedValidatedProfileOnEmptySlot()
    {
        var empty = QuickSelectSlots.FirstOrDefault(slot =>
            !slot.HasSavedAssignment && !slot.HasUnsavedPopulatedAssignment);
        if (empty is null)
        {
            return null;
        }

        ValidatedQuickSelectProfileOption? newest = null;
        var newestUtc = DateTimeOffset.MinValue;
        foreach (var option in ValidatedQuickSelectProfiles)
        {
            if (IsValidatedProfileAssignedToAnySlot(option))
            {
                continue;
            }

            var settings = option.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
                ? GetLiveCloudProfileSettings(option.Variant, option.Provider, option.Model)
                : GetLiveLocalProfileSettings(option.Variant, option.Model);
            var utcText = settings?["EndpointValidatedUtc"]?.ToString();
            if (!DateTimeOffset.TryParse(utcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc)
                || utc < newestUtc)
            {
                continue;
            }

            newestUtc = utc;
            newest = option;
        }

        if (newest is null)
        {
            return null;
        }

        empty.SelectDraftProfile(newest);
        RefreshQuickSelectSlotMetadata();
        return empty;
    }

    private bool IsCloudProfileInQuickSelect(string? provider, string? model, string? variant)
    {
        return QuickSelectSlots.Any(slot =>
            slot.HasSavedAssignment
            && slot.IsCloudRoute
            && slot.CloudProvider.Equals(provider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && slot.CloudModel.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(slot.CloudVariant).Equals(ProfileConfigVariantName(variant), StringComparison.OrdinalIgnoreCase));
    }

    private bool IsLocalProfileInQuickSelect(string? model, string? variant)
    {
        return QuickSelectSlots.Any(slot =>
            slot.HasSavedAssignment
            && slot.IsLocalRoute
            && slot.LocalModel.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(slot.LocalVariant).Equals(ProfileConfigVariantName(variant), StringComparison.OrdinalIgnoreCase));
    }

    internal void OnQuickSelectSlotDraftChanged(RouteSlotViewModel slot)
    {
        if (!QuickSelectSlots.Contains(slot))
        {
            return;
        }

        RefreshQuickSelectSlotMetadata();
        if (slot.SelectedValidatedProfile is { } draft)
        {
            var conflict = DescribeQuickSelectConflict(slot, draft);
            if (!string.IsNullOrWhiteSpace(conflict))
            {
                StatusMessage = conflict;
            }
        }

        if (ReferenceEquals(slot, _activeQuickSelectSlot) && !slot.HasUnsavedPopulatedAssignment)
        {
            BindActiveQuickSelectSlotToMountedRoute();
            RefreshRouteSlotIndicators();
        }
    }

    internal void SaveQuickSelectSlot(RouteSlotViewModel slot)
    {
        if (!QuickSelectSlots.Contains(slot))
        {
            return;
        }

        if (slot.SelectedValidatedProfile is null)
        {
            StatusMessage = $"Selection {slot.SlotNumber}: choose a validated profile before saving.";
            return;
        }

        var conflict = DescribeQuickSelectConflict(slot, slot.SelectedValidatedProfile);
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            StatusMessage = conflict + " Save was not applied.";
            return;
        }

        if (!slot.TryCommitSelectedProfile())
        {
            StatusMessage = $"Selection {slot.SlotNumber}: could not save that profile assignment.";
            return;
        }

        MirrorQuickSelectSlotsToLegacySelections();
        PersistQuickSelectSlots(saveDocument: true);
        RefreshQuickSelectSlotMetadata();
        RefreshAvailableQuickSelectProfiles();
        StatusMessage = $"Selection {slot.SlotNumber} saved: {slot.SelectedValidatedProfile.DisplayName}.";
    }

    internal void OnQuickSelectSlotChanged(RouteSlotViewModel slot)
    {
        if (!QuickSelectSlots.Contains(slot))
        {
            return;
        }

        if (ReferenceEquals(slot, _activeQuickSelectSlot))
        {
            BindActiveQuickSelectSlotToMountedRoute();
            RefreshRouteSlotIndicators();
        }

        MirrorQuickSelectSlotsToLegacySelections();
        PersistQuickSelectSlots(saveDocument: true);
    }

    internal async Task LaunchQuickSelectSlotAsync(RouteSlotViewModel slot)
    {
        if (!slot.HasSavedAssignment)
        {
            StatusMessage = $"Selection {slot.SlotNumber}: choose a validated profile and click Save before launching.";
            return;
        }

        if (slot.HasUnsavedPopulatedAssignment)
        {
            StatusMessage = $"Selection {slot.SlotNumber}: save the slot assignment before launching.";
            return;
        }

        RefreshQuickSelectSlotFacts(slot);
        var launchBlock = DescribeQuickSelectLaunchBlock(slot);
        if (!string.IsNullOrWhiteSpace(launchBlock))
        {
            StatusMessage = launchBlock;
            return;
        }

        SetActiveQuickSelectSlot(slot);
        if (slot.IsCloudRoute)
        {
            await LaunchCloudRouteAsync($"Selection {slot.SlotNumber}", slot.CloudProvider, slot.CloudModel, slot.CloudVariant);
        }
        else if (slot.IsLocalRoute)
        {
            await LaunchLocalRouteAsync($"Selection {slot.SlotNumber}", slot.LocalModel, slot.LocalVariant);
        }


        if ((slot.IsCloudRoute && CloudRouteIndicatorState == RouteIndicatorState.Live)
            || (slot.IsLocalRoute && LocalRouteIndicatorState == RouteIndicatorState.Live))
        {
            StartMountedEndpointHealthMonitor();
        }
    }

    internal async Task RestartQuickSelectSlotAsync(RouteSlotViewModel slot)
    {
        if (!slot.HasSavedAssignment)
        {
            StatusMessage = $"Selection {slot.SlotNumber}: choose a validated profile and click Save before launching.";
            return;
        }

        if (slot.HasUnsavedPopulatedAssignment)
        {
            StatusMessage = $"Selection {slot.SlotNumber}: save the slot assignment before launching.";
            return;
        }

        RefreshQuickSelectSlotFacts(slot);
        var launchBlock = DescribeQuickSelectLaunchBlock(slot);
        if (!string.IsNullOrWhiteSpace(launchBlock))
        {
            StatusMessage = launchBlock;
            return;
        }

        SetActiveQuickSelectSlot(slot);
        if (slot.IsCloudRoute)
        {
            await RestartCloudRouteAsync($"Selection {slot.SlotNumber}", slot.CloudProvider, slot.CloudModel);
        }
        else if (slot.IsLocalRoute)
        {
            await RestartLocalRouteAsync($"Selection {slot.SlotNumber}", slot.LocalModel, slot.LocalVariant);
        }

        if ((slot.IsCloudRoute && CloudRouteIndicatorState == RouteIndicatorState.Live)
            || (slot.IsLocalRoute && LocalRouteIndicatorState == RouteIndicatorState.Live))
        {
            StartMountedEndpointHealthMonitor();
        }
    }

    internal async Task CheckQuickSelectSlotAsync(RouteSlotViewModel slot)
    {
        if (!ReferenceEquals(slot, _activeQuickSelectSlot))
        {
            StatusMessage = $"Selection {slot.SlotNumber} is not the active runtime slot. Launch it before validating endpoint communication.";
            return;
        }

        CancelActiveEndpointHealthMonitor();

        var endpointValidated = false;
        if (slot.IsCloudRoute)
        {
            endpointValidated = await CheckCloudRouteHealthAsync(
                $"Selection {slot.SlotNumber}",
                slot.CloudProvider,
                slot.CloudModel,
                slot.CloudVariant);
        }
        else if (slot.IsLocalRoute)
        {
            endpointValidated = await CheckLocalRouteHealthAsync(
                $"Selection {slot.SlotNumber}",
                slot.LocalModel,
                slot.LocalVariant);
        }

        if (endpointValidated)
        {
            if (!MarkQuickSelectSlotValidated(slot))
            {
                StatusMessage = $"Selection {slot.SlotNumber}: endpoint responded, but its Model Profiles default could not be saved.";
                return;
            }

            StatusMessage = $"Selection {slot.SlotNumber}: endpoint responded.";
            StartMountedEndpointHealthMonitor();
        }
    }

    private bool MarkQuickSelectSlotValidated(RouteSlotViewModel slot)
    {
        if (!EnsureDefaultProfileEndpointValidated(
                slot.IsCloudRoute ? "cloud" : "local",
                slot.CloudProvider,
                slot.CloudModel,
                slot.LocalModel,
                out var createdNewProfile))
        {
            return false;
        }

        slot.MarkEndpointValidated(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        PersistQuickSelectSlots(saveDocument: true);
        UpdateVariantEditingGate();
        RefreshVariantTreeAfterValidationChange(slot.IsCloudRoute, createdNewProfile);
        RefreshSwitchBenchmarkProfiles();
        return true;
    }

    internal async Task StopQuickSelectSlotAsync(RouteSlotViewModel slot)
    {
        if (!ReferenceEquals(slot, _activeQuickSelectSlot))
        {
            StatusMessage = $"Selection {slot.SlotNumber} is not the active runtime slot.";
            return;
        }

        if (slot.IsCloudRoute)
        {
            await StopCloudRouteAsync($"Selection {slot.SlotNumber}");
        }
        else if (slot.IsLocalRoute)
        {
            await StopLocalRouteAsync($"Selection {slot.SlotNumber}");
        }

        if (RequestRoutingEnabled
            && ((slot.IsCloudRoute && !string.IsNullOrWhiteSpace(_runningLocalModel))
                || (slot.IsLocalRoute && !string.IsNullOrWhiteSpace(_mountedCloudProvider))))
        {
            _mountedRouteType = slot.IsCloudRoute ? "Local" : "Cloud";
            BindActiveQuickSelectSlotToMountedRoute();
            RefreshRouteSlotIndicators();
            return;
        }

        _activeQuickSelectSlot = null;
        ResetActiveQuickSelectRuntimePresentation();
        if (slot.IsCloudRoute
            ? _mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
            : _mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            ClearMountedRoute();
        }

        CancelActiveEndpointHealthMonitor();
        RefreshRouteSlotIndicators();
    }

    private void SetActiveQuickSelectSlot(RouteSlotViewModel slot)
    {
        CancelLocalRuntimeMonitors();
        CancelActiveEndpointHealthMonitor();
        _activeQuickSelectSlot = slot;

        var keepLocalLive = slot.IsLocalRoute
            && !string.IsNullOrWhiteSpace(_runningLocalModel)
            && _runningLocalModel.Equals(slot.LocalModel, StringComparison.OrdinalIgnoreCase)
            && NormalizeVariantName(_runningLocalVariant).Equals(NormalizeVariantName(slot.LocalVariant), StringComparison.OrdinalIgnoreCase);
        if (!keepLocalLive && !RequestRoutingEnabled)
        {
            LocalRouteIndicatorState = RouteIndicatorState.Off;
        }

        if (!slot.IsCloudRoute && !RequestRoutingEnabled)
        {
            CloudRouteIndicatorState = RouteIndicatorState.Off;
        }

        if (slot.IsCloudRoute)
        {
            RememberMountedCloud(slot.CloudProvider, slot.CloudModel, slot.CloudVariant);
        }
        else if (slot.IsLocalRoute)
        {
            RememberMountedLocal(slot.LocalModel, slot.LocalVariant);
        }

        RefreshRouteSlotIndicators();
    }

    private void StartMountedEndpointHealthMonitor()
    {
        CancelActiveEndpointHealthMonitor();
        if (!_mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase)
            && !_mountedRouteType.Equals("Local", StringComparison.OrdinalIgnoreCase)
            && !_mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isCloud = _mountedRouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
        var isRoute = _mountedRouteType.Equals("Route", StringComparison.OrdinalIgnoreCase);
        var provider = _mountedCloudProvider;
        var cloudModel = _mountedCloudModel;
        var generation = _mountedRouteGeneration;
        _activeEndpointHealthCts = new CancellationTokenSource();
        var cts = _activeEndpointHealthCts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            var localFailures = 0;
            var cloudFailures = 0;
            const int faultAfterFailures = 3;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    if (isRoute)
                    {
                        var localHealth = await _runtimeService.CheckLocalHealthAsync((int)OrchestratorPort, token);
                        var cloudHealth = await _runtimeService.CheckCloudEndpointLivenessAsync(provider, cloudModel, token);
                        var localDecision = DecidePolledLocalHealth(localHealth, localFailures, faultAfterFailures);
                        localFailures = localDecision.ConsecutiveFailures;
                        RouteIndicatorState? cloudState = null;
                        if (cloudHealth.IsSuccess)
                        {
                            cloudFailures = 0;
                            cloudState = RouteIndicatorState.Live;
                        }
                        else if (++cloudFailures >= faultAfterFailures)
                        {
                            cloudState = RouteIndicatorState.Fault;
                        }

                        SafeUiInvoke(() =>
                        {
                            if (generation != _mountedRouteGeneration || token.IsCancellationRequested)
                            {
                                return;
                            }

                            ApplyPolledLocalHealthDecision(localDecision);
                            if (cloudState is { } state)
                            {
                                CloudRouteIndicatorState = state;
                            }

                            RefreshRouteSlotIndicators();
                        });
                        continue;
                    }

                    var health = isCloud
                        ? await _runtimeService.CheckCloudEndpointLivenessAsync(provider, cloudModel, token)
                        : await _runtimeService.CheckLocalHealthAsync((int)OrchestratorPort, token);

                    if (isCloud)
                    {
                        RouteIndicatorState? cloudState = null;
                        string? reportStatus = null;
                        string? reportDetails = null;
                        var clearReport = false;
                        if (health.IsSuccess)
                        {
                            cloudFailures = 0;
                            cloudState = RouteIndicatorState.Live;
                            clearReport = true;
                        }
                        else if (++cloudFailures >= faultAfterFailures)
                        {
                            cloudState = RouteIndicatorState.Fault;
                            reportStatus = health.Status;
                            reportDetails = health.Details;
                        }

                        SafeUiInvoke(() =>
                        {
                            if (generation != _mountedRouteGeneration || token.IsCancellationRequested)
                            {
                                return;
                            }

                            if (cloudState is { } state)
                            {
                                CloudRouteIndicatorState = state;
                            }

                            if (clearReport)
                            {
                                ClearHealthReport();
                            }
                            else if (!string.IsNullOrWhiteSpace(reportStatus))
                            {
                                PublishHealthReport(reportStatus, reportDetails ?? string.Empty, entity: "cloud model");
                            }

                            RefreshRouteSlotIndicators();
                        });
                    }
                    else
                    {
                        var localDecision = DecidePolledLocalHealth(health, localFailures, faultAfterFailures);
                        localFailures = localDecision.ConsecutiveFailures;
                        SafeUiInvoke(() =>
                        {
                            if (generation != _mountedRouteGeneration || token.IsCancellationRequested)
                            {
                                return;
                            }

                            ApplyPolledLocalHealthDecision(localDecision);
                            RefreshRouteSlotIndicators();
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }, token);
    }

    private readonly record struct PolledLocalHealthDecision(
        int ConsecutiveFailures,
        RouteIndicatorState? IndicatorState,
        bool ClearReport,
        string? ReportStatus,
        string? ReportDetails);

    private PolledLocalHealthDecision DecidePolledLocalHealth(
        RuntimeActionResult health,
        int consecutiveFailures,
        int faultAfterFailures)
    {
        var attention = health.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase);
        var inFlight = _runtimeService.InFlightChatCount > 0;
        if (health.IsSuccess)
        {
            return new PolledLocalHealthDecision(0, RouteIndicatorState.Live, ClearReport: true, null, null);
        }

        if (attention)
        {
            return new PolledLocalHealthDecision(
                0,
                RouteIndicatorState.Amber,
                ClearReport: false,
                health.Status,
                health.Details);
        }

        // Client app is mid-reply: keep green rather than flashing red on a busy probe.
        if (inFlight)
        {
            return new PolledLocalHealthDecision(0, RouteIndicatorState.Live, ClearReport: false, null, null);
        }

        var nextFailures = consecutiveFailures + 1;
        if (nextFailures < faultAfterFailures)
        {
            return new PolledLocalHealthDecision(nextFailures, null, ClearReport: false, null, null);
        }

        return new PolledLocalHealthDecision(
            nextFailures,
            RouteIndicatorState.Fault,
            ClearReport: false,
            health.Status,
            health.Details);
    }

    private void ApplyPolledLocalHealthDecision(PolledLocalHealthDecision decision)
    {
        if (decision.IndicatorState is { } state)
        {
            LocalRouteIndicatorState = state;
        }

        if (decision.ClearReport)
        {
            ClearHealthReport();
        }
        else if (!string.IsNullOrWhiteSpace(decision.ReportStatus))
        {
            PublishHealthReport(decision.ReportStatus, decision.ReportDetails ?? string.Empty, entity: "llama-server");
        }
    }

    private void CancelActiveEndpointHealthMonitor()
    {
        try
        {
            _activeEndpointHealthCts?.Cancel();
        }
        catch
        {
        }

        _activeEndpointHealthCts?.Dispose();
        _activeEndpointHealthCts = null;
    }

    internal IReadOnlyList<string> GetCloudModelsForSlot(string provider, string currentModel)
    {
        var models = GetCloudModelsForProvider(provider);
        if (models.Count == 0 && !string.IsNullOrWhiteSpace(currentModel))
        {
            models.Add(currentModel);
        }

        return models;
    }

    internal IReadOnlyList<string> GetCloudVariantsForSlot(string provider, string model)
    {
        var variants = _config.GetObjectKeys("CloudProfiles")
            .Where(key =>
            {
                var parts = key.Split("::");
                return parts.Length >= 3
                    && parts[0].Trim().Equals(provider.Trim(), StringComparison.OrdinalIgnoreCase)
                    && parts[1].Trim().Equals(model.Trim(), StringComparison.OrdinalIgnoreCase)
                    && IsSavedProfileEndpointValidated("CloudProfiles", key);
            })
            .Select(key => key.Split("::")[2].Trim())
            .Where(variant => !string.IsNullOrWhiteSpace(variant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(variant => variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(variant => variant, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return variants;
    }

    internal IReadOnlyList<string> GetLocalVariantsForSlot(string model)
    {
        var variants = _config.GetObjectKeys("LocalProfiles")
            .Where(key =>
            {
                var parts = key.Split("::");
                return parts.Length >= 2
                    && parts[0].Trim().Equals(model.Trim(), StringComparison.OrdinalIgnoreCase)
                    && IsSavedProfileEndpointValidated("LocalProfiles", key);
            })
            .Select(key =>
            {
                var parts = key.Split("::", 2, StringSplitOptions.TrimEntries);
                return parts.Length >= 2 ? parts[1] : string.Empty;
            })
            .Where(variant => !string.IsNullOrWhiteSpace(variant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(variant => variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(variant => variant, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return variants;
    }

    private void RefreshQuickSelectSlotMetadata()
    {
        OnPropertyChanged(nameof(CanAddQuickSelectSlots));
        OnPropertyChanged(nameof(ShowQuickSelectSlotAddButton));
        OnPropertyChanged(nameof(ShowQuickSelectSlotAddBlockedText));
        OnPropertyChanged(nameof(QuickSelectSlotAddBlockedText));
        OnPropertyChanged(nameof(QuickSelectSlotAddButtonTooltip));
        for (var index = 0; index < QuickSelectSlots.Count; index++)
        {
            var slot = QuickSelectSlots[index];
            slot.SlotId = index switch
            {
                0 => "slot-cloud-1",
                1 => "slot-local-1",
                _ => $"route-slot-{index + 1}"
            };
            slot.RefreshSlotNumber(index + 1);
            if (slot.AvailableValidatedProfiles.Count == 0 && ValidatedQuickSelectProfiles.Count > 0)
            {
                slot.NotifyValidatedProfilesRefreshed();
            }

            RefreshQuickSelectSlotFacts(slot);
        }
    }

    internal void RefreshQuickSelectSlotFacts(RouteSlotViewModel slot)
    {
        var routeType = slot.HasUnsavedPopulatedAssignment
            ? slot.SelectedValidatedProfile?.RouteType ?? slot.RouteType
            : slot.RouteType;
        var isCloud = routeType.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
        var isLocal = routeType.Equals("Local", StringComparison.OrdinalIgnoreCase);
        if (!isCloud && !isLocal)
        {
            slot.AttributeSummary = string.Empty;
            slot.ProfileSyncNote = string.Empty;
            slot.IsEndpointTrusted = false;
            return;
        }

        if (isCloud)
        {
            var provider = slot.HasUnsavedPopulatedAssignment ? slot.SelectedValidatedProfile!.Provider : slot.CloudProvider;
            var model = slot.HasUnsavedPopulatedAssignment ? slot.SelectedValidatedProfile!.Model : slot.CloudModel;
            var variant = slot.HasUnsavedPopulatedAssignment ? slot.SelectedValidatedProfile!.Variant : slot.CloudVariant;
            var settings = GetLiveCloudProfileSettings(variant, provider, model);
            slot.AttributeSummary = LocalProfileAttributeSummary.FormatCloudSuitability(
                settings,
                BuildQuickSelectSuitabilityHints(slot, settings, isLocal: false));
            var warning = ResolveProfileWarning(isLocal: false, model, settings);
            slot.IsEndpointTrusted = settings is not null && !warning.Show;
            slot.ProfileSyncNote = settings is null
                ? "This shortcut is not in Model Profiles."
                : string.Empty;
            return;
        }

        var localModel = slot.HasUnsavedPopulatedAssignment ? slot.SelectedValidatedProfile!.Model : slot.LocalModel;
        var localVariant = slot.HasUnsavedPopulatedAssignment ? slot.SelectedValidatedProfile!.Variant : slot.LocalVariant;
        var localSettings = GetLiveLocalProfileSettings(localVariant, localModel);
        slot.AttributeSummary = LocalProfileAttributeSummary.FormatLocalSuitability(
            localSettings,
            BuildQuickSelectSuitabilityHints(slot, localSettings, isLocal: true));
        var localWarning = ResolveProfileWarning(isLocal: true, localModel, localSettings);
        slot.IsEndpointTrusted = localSettings is not null && !localWarning.Show;
        slot.ProfileSyncNote = localSettings is null
            ? "This shortcut is not in Model Profiles."
            : string.Empty;
    }

    private string DescribeQuickSelectLaunchBlock(RouteSlotViewModel slot)
    {
        if (slot.IsEndpointTrusted)
        {
            return string.Empty;
        }

        if (slot.IsLocalRoute && IsLocalGgufMissing(slot.LocalModel))
        {
            return $"Selection {slot.SlotNumber}: the local model is not in the model folder. Restore the local model, then Validate to endpoint on Model Profiles.";
        }

        return $"Selection {slot.SlotNumber}: settings changed since the last successful Validate, or the last Validate failed. Run Validate to endpoint on Model Profiles before Launch.";
    }

    private void PersistQuickSelectSlots(bool saveDocument)
    {
        var slots = new JsonArray();
        foreach (var slot in QuickSelectSlots)
        {
            var routeType = NormalizeSelectionRouteType(slot.RouteType);
            var persistedSlot = BuildRouteSlot(
                slot.SlotId,
                $"Selection {slot.SlotNumber}",
                routeType.ToLowerInvariant(),
                slot.IsCloudRoute ? slot.CloudProvider : string.Empty,
                slot.IsCloudRoute ? slot.CloudModel : string.Empty,
                slot.IsCloudRoute ? FirstNonEmpty(slot.CloudVariant, BaseVariantDisplayName) : string.Empty,
                slot.IsLocalRoute ? slot.LocalModel : string.Empty,
                slot.IsLocalRoute ? FirstNonEmpty(slot.LocalVariant, BaseVariantDisplayName) : string.Empty,
                pinned: true);
            if (slot.IsDefaultProfileValidated)
            {
                persistedSlot["validatedDefaultProfile"] = true;
                persistedSlot["endpointValidatedUtc"] = slot.EndpointValidatedUtc;
            }

            if (slot.HasSavedAssignment)
            {
                persistedSlot["validatedProfileKey"] = slot.SavedAssignmentKey;
            }

            slots.Add(persistedSlot);
        }

        _config.Root["RouteSlots"] = slots;
        _config.SetStringArray("ActiveRouteSlotIds", QuickSelectSlots.Select(slot => slot.SlotId));
        _config.SetInt("MaxVisibleRouteSlots", QuickSelectSlots.Count);
        _config.SetInt("QuickSelectSlotLimit", QuickSelectSlotLimit);
        MirrorQuickSelectSlotsToLegacySelections();
        RefreshRouteSlotIndicators();
        if (saveDocument)
        {
            _configService.Save(_config);
        }
    }

    private void MirrorQuickSelectSlotsToLegacySelections()
    {
        var previousLoadingState = _isLoadingConfig;
        _isLoadingConfig = true;
        try
        {
            var first = QuickSelectSlots.ElementAtOrDefault(0);
            if (first is not null)
            {
                SelectedSelection1RouteType = first.RouteType;
                IsSelection1TemplateChosen = first.HasSavedAssignment;
                SelectedCloudProvider = first.CloudProvider;
                SelectedCloudProfile = first.CloudModel;
                SelectedCloudVariant = first.CloudVariant;
                SelectedSelection1LocalProfile = first.LocalModel;
                SelectedSelection1LocalVariant = first.LocalVariant;
            }

            var second = QuickSelectSlots.ElementAtOrDefault(1);
            IsSelection2Enabled = second is not null;
            IsSelection2TemplateChosen = second?.HasSavedAssignment == true;
            if (second is not null)
            {
                SelectedSelection2RouteType = second.RouteType;
                SelectedSelection2CloudProvider = second.CloudProvider;
                SelectedSelection2CloudProfile = second.CloudModel;
                SelectedSelection2CloudVariant = second.CloudVariant;
                SelectedLocalProfile = second.LocalModel;
                SelectedLocalVariant = second.LocalVariant;
            }
        }
        finally
        {
            _isLoadingConfig = previousLoadingState;
        }

        UpdateSelectionRouteVisibilityStates();
    }

    private void UpdateActiveRouteSlotIdsForCurrentSlotEnablement()
    {
        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            return;
        }

        var activeIds = slots
            .OfType<JsonObject>()
            .Where(x => ParseBool(x["enabled"]?.ToString(), true))
            .Where(x => !string.IsNullOrWhiteSpace(x["routeType"]?.ToString()))
            .Select(x => x["slotId"]?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();

        var ordered = activeIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(IsSelection2Enabled ? 2 : 1)
            .ToArray();

        _config.SetStringArray("ActiveRouteSlotIds", ordered);
        _config.SetInt("MaxVisibleRouteSlots", IsSelection2Enabled ? 2 : 1);
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static double ParseDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static DateTime ParseDate(string? value, DateTime fallback)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    partial void OnSelectedCloudProviderChanged(string value)
    {
        RefreshCloudProfilesForProvider();
        EnsureSelection2CloudDefaults();
        LoadCloudVariantSettingsEditor();
        UpdateVariantBaseTargets();
        SetCapabilitySummaries();
        UpdateCloudModelCatalogStatus();
        RefreshProfileTemplateSources();
        RefreshCloudFamilyModelIdEditor(bindDraftToCurrent: true);
    }

    partial void OnNewCloudProfileProviderChanged(string value)
    {
        RefreshNewCloudProfileModelOptions();
    }

    partial void OnSelectedCloudProfileChanged(string value)
    {
        RefreshCloudVariantsForSelection(rebuildTree: false);
        EnsureSelection2CloudDefaults();
        LoadCloudVariantSettingsEditor();
        UpdateVariantBaseTargets();
        UpdateVariantEditability();
        SetCapabilitySummaries();
        UpdateProfileFootprintIndicators();
        RefreshProfileTemplateSources();
        RefreshCloudFamilyModelIdEditor(bindDraftToCurrent: true);
    }

    partial void OnSelectedLocalProfileChanged(string value)
    {
        RefreshLocalVariantsForSelection(rebuildTree: false);
        EnsureSelection1LocalDefaults();
        if (!_isValidatingLocalProfile)
        {
            LoadLocalVariantSettingsEditor();
            RefreshProfileEndpointStatusSurfaces();
        }
        UpdateVariantBaseTargets();
        UpdateVariantEditability();
        SetCapabilitySummaries();
        UpdateProfileFootprintIndicators();
        RefreshProfileTemplateSources();
        RefreshLocalVisionHint();
    }

    partial void OnSelectedCloudVariantChanged(string value)
    {
        EnsureSelection2CloudDefaults();
        LoadCloudVariantSettingsEditor();
        UpdateVariantEditability();
        CloudVariantDraftName = IsCloudVariantEditable ? RenameDraftFromProfileName(value) : string.Empty;
        UpdateProfileFootprintIndicators();
        var matchingItem = CloudVariantTreeItems.FirstOrDefault(item =>
            item.Provider.Equals(SelectedCloudProvider, StringComparison.OrdinalIgnoreCase)
            && item.ModelName.Equals(SelectedCloudProfile, StringComparison.OrdinalIgnoreCase)
            && item.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (matchingItem is not null && !ReferenceEquals(SelectedCloudVariantItem, matchingItem))
        {
            SelectedCloudVariantItem = matchingItem;
        }
    }

    partial void OnSelectedCloudVariantItemChanged(CloudVariantTreeItemViewModel? value)
    {
        if (value is null)
        {
            RestoreExpandedVariantSelection(
                CloudVariantTreeItems,
                () => SelectedCloudVariantItem,
                item => SelectedCloudVariantItem = item);
            if (SelectedCloudVariantItem is null)
            {
                SyncVariantTreeSelectionState(CloudVariantTreeItems, null);
                SyncAdvancedProfileEditorVisibility();
                UpdateVariantEditability();
            }

            return;
        }

        if (!_isSynchronizingAdvancedProfileSelection)
        {
            _isSynchronizingAdvancedProfileSelection = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(value.Provider)
                    && !string.Equals(SelectedCloudProvider, value.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedCloudProvider = value.Provider;
                }

                if (!string.Equals(SelectedCloudProfile, value.ModelName, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedCloudProfile = value.ModelName;
                }

                if (!string.Equals(SelectedCloudVariant, value.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedCloudVariant = value.Name;
                }

                SelectedCloudVariantItem = CloudVariantTreeItems.FirstOrDefault(item =>
                    item.Provider.Equals(value.Provider, StringComparison.OrdinalIgnoreCase)
                    && item.ModelName.Equals(value.ModelName, StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isSynchronizingAdvancedProfileSelection = false;
            }
        }

        SyncVariantTreeSelectionState(CloudVariantTreeItems, SelectedCloudVariantItem);
        SyncAdvancedProfileEditorVisibility();
        UpdateVariantEditability();
        RefreshCloudFamilyModelIdEditor(bindDraftToCurrent: true);
        OpenSelectedProfileIfClosed(SelectedCloudVariantItem);
        if (SelectedCloudVariantItem is { IsEditable: true })
        {
            CloudVariantDraftName = RenameDraftFromProfileName(SelectedCloudVariantItem.Name);
        }
    }

    partial void OnSelectedLocalVariantChanged(string value)
    {
        EnsureSelection1LocalDefaults();
        if (!_isValidatingLocalProfile)
        {
            LoadLocalVariantSettingsEditor();
            RefreshProfileEndpointStatusSurfaces();
        }
        UpdateVariantEditability();
        UpdateLocalLaunchTelemetrySummary();
        LocalVariantDraftName = IsLocalVariantEditable ? RenameDraftFromProfileName(value) : string.Empty;
        UpdateProfileFootprintIndicators();
        var matchingItem = LocalVariantTreeItems.FirstOrDefault(item =>
            item.ModelName.Equals(SelectedLocalProfile, StringComparison.OrdinalIgnoreCase)
            && item.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (matchingItem is not null && !ReferenceEquals(SelectedLocalVariantItem, matchingItem))
        {
            SelectedLocalVariantItem = matchingItem;
        }
    }

    partial void OnSelectedLocalVariantItemChanged(CloudVariantTreeItemViewModel? value)
    {
        if (value is null)
        {
            RestoreExpandedVariantSelection(
                LocalVariantTreeItems,
                () => SelectedLocalVariantItem,
                item => SelectedLocalVariantItem = item);
            if (SelectedLocalVariantItem is null)
            {
                SyncVariantTreeSelectionState(LocalVariantTreeItems, null);
                SyncAdvancedProfileEditorVisibility();
                UpdateVariantEditability();
            }

            return;
        }

        if (!_isSynchronizingAdvancedProfileSelection)
        {
            _isSynchronizingAdvancedProfileSelection = true;
            try
            {
                if (!string.Equals(SelectedLocalProfile, value.ModelName, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedLocalProfile = value.ModelName;
                }

                if (!string.Equals(SelectedLocalVariant, value.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedLocalVariant = value.Name;
                }

                SelectedLocalVariantItem = LocalVariantTreeItems.FirstOrDefault(item =>
                    item.ModelName.Equals(value.ModelName, StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isSynchronizingAdvancedProfileSelection = false;
            }
        }

        SyncVariantTreeSelectionState(LocalVariantTreeItems, SelectedLocalVariantItem);
        SyncAdvancedProfileEditorVisibility();
        UpdateVariantEditability();
        OpenSelectedProfileIfClosed(SelectedLocalVariantItem);
        if (SelectedLocalVariantItem is { IsEditable: true })
        {
            LocalVariantDraftName = RenameDraftFromProfileName(SelectedLocalVariantItem.Name);
        }
    }

    partial void OnSelectedProfileTemplateRouteTypeChanged(string value)
    {
        IsCloudProfileTemplateSelected = value.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
        IsLocalProfileTemplateSelected = value.Equals("Local", StringComparison.OrdinalIgnoreCase);
        ProfileTemplateGuidanceText = IsCloudProfileTemplateSelected
            ? "Choose a cloud trunk or branch to load as the starting point. Cloud request controls remain unavailable until provider translation is implemented."
            : IsLocalProfileTemplateSelected
                ? "Choose a local trunk or branch to copy, then adjust the applicable fields before adding the model profile variant."
                : "Choose Cloud or Local to begin a model profile variant.";
        RefreshProfileTemplateSources();
        SelectedProfileTemplateSource = null;
        IsProfileTemplateSourceSelected = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            SyncAdvancedProfileEditorVisibility();
        }
        else
        {
            IsCloudProfileEditorVisible = IsCloudProfileTemplateSelected && CloudVariantTreeItems.Count > 0;
            IsLocalProfileEditorVisible = IsLocalProfileTemplateSelected && LocalVariantTreeItems.Count > 0;
        }

        UpdateVariantEditability();
    }

    partial void OnSelectedProfileTemplateSourceChanged(ProfileTemplateSourceViewModel? value)
    {
        IsProfileTemplateSourceSelected = value is not null;
        IsCloudProfileEditorVisible = value?.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) == true;
        IsLocalProfileEditorVisible = value?.RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase) == true;
        UpdateVariantEditability();
        if (value is null)
        {
            return;
        }

        if (value.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            SelectedCloudProvider = value.Provider;
            SelectedCloudProfile = value.Model;
            SelectedCloudVariant = value.Variant;
        }
        else
        {
            SelectedLocalProfile = value.Model;
            SelectedLocalVariant = value.Variant;
        }
    }

    partial void OnNewProfileVariantNameChanged(string value)
    {
        if (IsCloudProfileTemplateSelected)
        {
            CloudVariantDraftName = value;
        }
        else if (IsLocalProfileTemplateSelected)
        {
            LocalVariantDraftName = value;
        }
    }

    partial void OnSelectedSelection1RouteTypeChanged(string value)
    {
        SelectedSelection1RouteType = NormalizeSelectionRouteType(value);
        UpdateSelectionRouteVisibilityStates();
        if (string.IsNullOrWhiteSpace(SelectedSelection1RouteType))
        {
            IsSelection1TemplateChosen = false;
            EvaluateFirstProfileBootstrapState();
            return;
        }

        if (IsFirstProfileBootstrapPending)
        {
            IsSelection1TemplateChosen = true;
            EvaluateFirstProfileBootstrapState();
        }
        EnsureSelection1LocalDefaults();
    }

    partial void OnSelectedSelection1LocalProfileChanged(string value) => EnsureSelection1LocalDefaults();

    partial void OnSelectedSelection1LocalVariantChanged(string value) => EnsureSelection1LocalDefaults();

    partial void OnSelectedSelection2RouteTypeChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            IsSelection2TemplateChosen = false;
            UpdateSelectionRouteVisibilityStates();
            return;
        }

        SelectedSelection2RouteType = NormalizeSelectionRouteType(value);
        IsSelection2TemplateChosen = true;
        EnsureSelection2CloudDefaults();
        UpdateSelectionRouteVisibilityStates();
    }

    partial void OnSelectedSelection2CloudProfileChanged(string value) => EnsureSelection2CloudDefaults();

    partial void OnSelectedSelection2CloudProviderChanged(string value)
    {
        RefreshSelection2CloudProfilesForProvider();
        EnsureSelection2CloudDefaults();
    }

    partial void OnSelectedSelection2CloudVariantChanged(string value) => EnsureSelection2CloudDefaults();

    partial void OnSelectedDiscoveredLocalServerChanged(DiscoveredLocalServerOption? value)
    {
        DiscoveredLocalServerPort = value?.Port ?? 0;
        CanAdoptDiscoveredLocalServer = value is not null;
    }

    partial void OnSelectedInstalledLocalServerChanged(InstalledLocalServerOption? value)
    {
        LocalServerExecutablePath = value?.ExecutablePath ?? string.Empty;
        _config.SetString("LocalServerExecutablePath", LocalServerExecutablePath);
        _configService.Save(_config);
    }

    partial void OnIsSelection2EnabledChanged(bool value)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        SyncPrimaryRouteSlotsFromCurrentSelections();
        _config.SetBool("Selection2Enabled", value);
        _configService.Save(_config);
        RefreshSlotPresenceAndActions();
    }

    partial void OnLocalModelDirectoryChanged(string value)
    {
        _ggufPathCache = null;
        _ggufPathCacheDirectory = null;
        RefreshLocalProfilesFromModelDirectory();
        if (!_isLoadingConfig)
        {
            RefreshLocalVariantTreeItems();
        }
    }

    private void SetCapabilitySummaries()
    {
        CloudSummary = LocalProfileAttributeSummary.FormatCloud(
            GetLiveCloudProfileSettings(SelectedCloudVariant, SelectedCloudProvider, SelectedCloudProfile));
        LocalSummary = LocalProfileAttributeSummary.FormatLocal(
            GetLiveLocalProfileSettings(SelectedLocalVariant, SelectedLocalProfile));
        UpdateLocalLaunchTelemetrySummary();
    }

    private void UpdateLocalLaunchTelemetrySummary()
    {
        LocalLaunchTelemetryText = _runtimeService.GetLocalLaunchTelemetrySummary(SelectedLocalProfile, SelectedLocalVariant);
        LocalLaunchTelemetryDetailText = _runtimeService.GetLocalLaunchTelemetryDetailText(SelectedLocalProfile, SelectedLocalVariant);
    }

    private void StartLocalLaunchCountdownMonitor(
        string model,
        string variant,
        bool includeLoadEstimate = true,
        bool dualHotAlongsideLocal = false)
    {
        StopLocalLaunchCountdownMonitor();

        var estimateMs = includeLoadEstimate ? _runtimeService.GetLocalLaunchEstimateMs(model, variant) : 0;
        var target = string.IsNullOrWhiteSpace(variant) ? model : $"{model} / {variant}";
        _localLaunchPhase = dualHotAlongsideLocal
            ? "AI-FluxMux is adding cloud beside the loaded local"
            : "AI-FluxMux is preparing to switch";
        _localLaunchPhaseVersion++;
        _localLaunchCountdownCts = new CancellationTokenSource();
        var cts = _localLaunchCountdownCts;
        var launchLabel = dualHotAlongsideLocal
            ? $"AI-FluxMux is adding {target} for hot routing (local stays first)... elapsed 0s"
            : !includeLoadEstimate
            ? $"AI-FluxMux is switching to {target}... elapsed 0s"
            : estimateMs <= 0
                ? $"AI-FluxMux is preparing llama-server for {target}... elapsed 0s"
                : $"llama-server is loading {target}... cold-load wait {FormatTelemetryDuration(estimateMs)} | estimated time left {FormatTelemetryDuration(estimateMs)}";
        _localLaunchCountdownText = launchLabel;
        RefreshCompositeStatusMessage();
        _ = Task.Run(async () =>
        {
            var phaseStopwatch = Stopwatch.StartNew();
            var observedPhaseVersion = _localLaunchPhaseVersion;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (observedPhaseVersion != _localLaunchPhaseVersion)
                    {
                        observedPhaseVersion = _localLaunchPhaseVersion;
                        phaseStopwatch.Restart();
                    }

                    var phase = _localLaunchPhase;
                    var elapsedMs = (int)Math.Min(int.MaxValue, phaseStopwatch.ElapsedMilliseconds);
                    var remainingMs = estimateMs - elapsedMs;
                    SafeUiInvoke(() =>
                    {
                        if (!phase.Equals("Loading model", StringComparison.OrdinalIgnoreCase))
                        {
                            StatusMessageBrush = Brushes.SteelBlue;
                            _localLaunchCountdownText = $"{phase}: {target} | elapsed {FormatTelemetryDuration(elapsedMs)}";
                        }
                        else if (estimateMs <= 0)
                        {
                            StatusMessageBrush = Brushes.SteelBlue;
                            _localLaunchCountdownText = $"llama-server is loading {target}... elapsed {FormatTelemetryDuration(elapsedMs)}";
                        }
                        else if (remainingMs >= 0)
                        {
                            StatusMessageBrush = Brushes.ForestGreen;
                            _localLaunchCountdownText = $"llama-server is loading {target}... cold-load wait {FormatTelemetryDuration(estimateMs)} | estimated time left {FormatTelemetryDuration(remainingMs)}";
                        }
                        else
                        {
                            StatusMessageBrush = Brushes.DarkOrange;
                            _localLaunchCountdownText = $"llama-server is loading {target}... cold-load wait {FormatTelemetryDuration(estimateMs)} | taking longer than a cold load by {FormatTelemetryDuration(Math.Abs(remainingMs))}";
                        }

                        RefreshCompositeStatusMessage();
                    });

                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private void UpdateLocalLaunchPhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return;
        }

        var normalized = NormalizeLaunchPhase(phase);
        if (normalized.Equals(_localLaunchPhase, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _localLaunchPhase = normalized;
        _localLaunchPhaseVersion++;
        RefreshCompositeStatusMessage();
    }

    private static string NormalizeLaunchPhase(string phase)
    {
        var text = (phase ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return text;
        }

        if (text.Equals("Loading model", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("llama-server is loading", StringComparison.OrdinalIgnoreCase))
        {
            return "Loading model";
        }

        if (text.Contains("testing the public endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return "AI-FluxMux is testing the public endpoint";
        }

        if (text.Contains("stopping llama-server", StringComparison.OrdinalIgnoreCase))
        {
            return "AI-FluxMux is stopping llama-server";
        }

        if (text.Contains("checking the previous llama-server", StringComparison.OrdinalIgnoreCase))
        {
            return "AI-FluxMux is checking the previous llama-server";
        }

        if (text.Contains("starting llama-server", StringComparison.OrdinalIgnoreCase))
        {
            return "AI-FluxMux is starting llama-server";
        }

        return text;
    }

    private void StopLocalLaunchCountdownMonitor()
    {
        void StopCore()
        {
            try
            {
                _localLaunchCountdownCts?.Cancel();
            }
            catch
            {
            }

            _localLaunchCountdownCts = null;
            _localLaunchCountdownText = string.Empty;
            RefreshCompositeStatusMessage();
            StatusMessageBrush = Brushes.Black;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                StopCore();
                return;
            }

            Dispatcher.UIThread.Post(StopCore);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshCompositeStatusMessage()
    {
        if (_benchmarkStatusActive)
        {
            var liveProgress = _benchmarkProgressText;
            if (!string.IsNullOrWhiteSpace(_localLaunchCountdownText))
            {
                liveProgress += " | " + _localLaunchCountdownText;
            }

            StatusMessage = liveProgress;
            SwitchBenchmarkResultText = "Switch benchmark running...\n\n" + liveProgress;
            SwitchBenchmarkSummaryText = "Current phase: " + liveProgress;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_localLaunchCountdownText))
        {
            StatusMessage = _localLaunchCountdownText;
        }
    }

    private static string FormatTelemetryDuration(int milliseconds)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(milliseconds / 1000.0));
        return seconds + "s";
    }

    private void RefreshCloudProfilesForProvider()
    {
        var provider = SelectedCloudProvider ?? string.Empty;
        var modelEntries = GetCloudModelsForProvider(provider);

        ReplaceCloudModelOptions(CloudProfiles, modelEntries);

        if (!CloudProfiles.Contains(SelectedCloudProfile))
        {
            SelectedCloudProfile = CloudProfiles.FirstOrDefault() ?? SelectedCloudProfile;
        }

        RefreshCloudVariantsForSelection();
        RefreshNewCloudProfileModelOptions();
    }

    private void RefreshNewCloudProfileModelOptions()
    {
        var provider = FirstNonEmpty(NewCloudProfileProvider, SelectedCloudProvider);
        if (string.IsNullOrWhiteSpace(NewCloudProfileProvider))
        {
            NewCloudProfileProvider = provider;
        }

        var modelEntries = GetCloudModelsForProvider(provider);
        ReplaceCloudModelOptions(NewCloudProfileModels, modelEntries);
        if (!string.IsNullOrWhiteSpace(NewCloudProfileSelectedModel)
            && !NewCloudProfileModels.Contains(NewCloudProfileSelectedModel)
            && string.IsNullOrWhiteSpace(NewCloudProfileCustomModelId))
        {
            AddIfMissing(NewCloudProfileModels, NewCloudProfileSelectedModel);
        }

        if (NewCloudProfileModels.Count > 0
            && (string.IsNullOrWhiteSpace(NewCloudProfileSelectedModel)
                || !NewCloudProfileModels.Contains(NewCloudProfileSelectedModel)))
        {
            NewCloudProfileSelectedModel = NewCloudProfileModels[0];
        }
    }

    private void RefreshSelection2CloudProfilesForProvider()
    {
        var modelEntries = GetCloudModelsForProvider(SelectedSelection2CloudProvider ?? string.Empty);
        ReplaceCloudModelOptions(Selection2CloudProfiles, modelEntries);

        if (!Selection2CloudProfiles.Contains(SelectedSelection2CloudProfile))
        {
            SelectedSelection2CloudProfile = Selection2CloudProfiles.FirstOrDefault() ?? SelectedSelection2CloudProfile;
        }
    }

    private List<string> GetCloudModelsForProvider(string provider)
    {
        var seeds = GetSeedCloudModelsForProvider(provider).ToList();
        var profileModels = _config.GetObjectKeys("CloudProfiles")
            .Select(key => key.Split("::"))
            .Where(parts => parts.Length >= 2)
            .Where(parts => string.Equals(parts[0].Trim(), provider, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var catalogModels = CloudModelCatalogFilter.Filter(provider, GetCachedCloudCatalogModels(provider)).ToList();

        var modelEntries = new List<string>();
        foreach (var id in seeds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!modelEntries.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                modelEntries.Add(id);
            }
        }

        foreach (var id in profileModels.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!modelEntries.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                modelEntries.Add(id);
            }
        }

        foreach (var id in catalogModels.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!modelEntries.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                modelEntries.Add(id);
            }
        }

        if (modelEntries.Count == 0)
        {
            modelEntries.Add(provider.Equals(SelectedSelection2CloudProvider, StringComparison.OrdinalIgnoreCase)
                ? SelectedSelection2CloudProfile
                : SelectedCloudProfile);
        }

        return modelEntries;
    }

    private static string AppendCloudValidateFailureGuidance(string provider, string details)
    {
        var normalized = (details ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Refresh Models lists ids the provider reports; only Validate to endpoint confirms chat works on AI-FluxMux.";
        }

        if (normalized.Contains("did not accept", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("does not currently expose", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("model_not_supported", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("not yet callable", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(provider, "Copilot GitHub", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("Refresh Models can list ids", StringComparison.OrdinalIgnoreCase))
            {
                return normalized
                    + " Refresh Models can list ids before Copilot's chat route accepts them — try gpt-4.1 or a claude-sonnet id, click Save profile, then Validate again.";
            }

            if (!normalized.Contains("Refresh Models can list ids", StringComparison.OrdinalIgnoreCase))
            {
                return normalized
                    + " Refresh Models lists ids the provider reports; only Validate to endpoint confirms chat works on AI-FluxMux.";
            }
        }

        return normalized;
    }

    private IEnumerable<string> GetCachedCloudCatalogModels(string provider)
    {
        if (_config.Root["CloudModelCatalog"] is not JsonObject catalog)
        {
            return [];
        }

        var match = catalog
            .FirstOrDefault(entry => entry.Key.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (match.Value is not JsonArray models)
        {
            return [];
        }

        return models
            .Select(node => node?.ToString() ?? string.Empty)
            .Where(id => !string.IsNullOrWhiteSpace(id));
    }

    private void SaveCloudModelCatalog(string provider, IReadOnlyList<string> models)
    {
        if (_config.Root["CloudModelCatalog"] is not JsonObject catalog)
        {
            catalog = new JsonObject();
            _config.Root["CloudModelCatalog"] = catalog;
        }

        foreach (var existing in catalog
                     .Where(entry => entry.Key.Equals(provider, StringComparison.OrdinalIgnoreCase))
                     .Select(entry => entry.Key)
                     .ToList())
        {
            catalog.Remove(existing);
        }

        var array = new JsonArray();
        foreach (var id in models.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            array.Add(id);
        }

        catalog[provider] = array;
        _configService.Save(_config);
    }

    private void StampDiscoveredCloudProfileTraits(
        string provider,
        IReadOnlyDictionary<string, CloudCatalogVision.Traits> traitsByModel)
    {
        if (traitsByModel.Count == 0 || _config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return;
        }

        var changed = false;
        foreach (var entry in profiles)
        {
            if (entry.Value is not JsonObject settings)
            {
                continue;
            }

            var parts = (entry.Key ?? string.Empty).Split("::", 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2
                || !parts[0].Equals(provider, StringComparison.OrdinalIgnoreCase)
                || !traitsByModel.TryGetValue(parts[1], out var traits))
            {
                continue;
            }

            if (traits.Vision.HasValue && string.IsNullOrWhiteSpace(settings["CloudVisionEnabled"]?.ToString()))
            {
                settings["CloudVisionEnabled"] = traits.Vision.Value ? "Enabled" : "Disabled";
                changed = true;
            }

            var context = settings["CloudContextWindow"]?.ToString();
            if (traits.ContextTokens is > 0
                && (string.IsNullOrWhiteSpace(context) || context.Equals("Auto", StringComparison.OrdinalIgnoreCase)))
            {
                settings["CloudContextWindow"] = traits.ContextTokens.Value.ToString(CultureInfo.InvariantCulture);
                changed = true;
            }

            if (traits.MaxTokens is > 0
                && (string.IsNullOrWhiteSpace(settings["CloudMaxTokens"]?.ToString())
                    || settings["CloudMaxTokens"]?.ToString() == "2048"))
            {
                settings["CloudMaxTokens"] = traits.MaxTokens.Value;
                changed = true;
            }

            if (traits.Reasoning == true
                && string.IsNullOrWhiteSpace(settings["CloudReasoningMode"]?.ToString()))
            {
                settings["CloudReasoningMode"] = "Balanced";
                changed = true;
            }
        }

        if (changed)
        {
            _configService.Save(_config);
        }
    }

    private static void ReplaceCloudModelOptions(ObservableCollection<string> target, IEnumerable<string> models)
    {
        target.Clear();
        foreach (var model in models)
        {
            AddIfMissing(target, model);
        }
    }

    private static IEnumerable<string> GetSeedCloudModelsForProvider(string provider)
    {
        if (string.Equals(provider, "Copilot GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "gpt-5.3-codex",
                "gpt-5",
                "gpt-4.1",
                "claude-3-7-sonnet-latest",
                "claude-3-5-sonnet-latest"
            ];
        }

        if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "gemini-3.7-flash",
                "gemini-3.6-flash",
                "gemini-3.5-flash",
                "gemini-3.5-flash-lite",
                "gemini-3.1-flash-lite",
                "gemini-3.1-pro-preview",
                "gemini-3-flash-preview",
                "gemini-2.5-pro",
                "gemini-2.5-flash",
                "gemini-2.5-flash-lite"
            ];
        }

        if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "claude-3-7-sonnet-latest",
                "claude-3-5-sonnet-latest",
                "claude-3-5-haiku-latest"
            ];
        }

        if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "gpt-5",
                "gpt-4.1",
                "gpt-4o",
                "o4-mini"
            ];
        }

        return [];
    }

    private void RefreshCloudVariantsForSelection(bool rebuildTree = true)
    {
        var provider = SelectedCloudProvider ?? string.Empty;
        var model = SelectedCloudProfile ?? string.Empty;
        var variantEntries = _config.GetObjectKeys("CloudProfiles")
            .Select(key => key.Split("::"))
            .Where(parts => parts.Length >= 2)
            .Where(parts => string.Equals(parts[0].Trim(), provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[1].Trim(), model, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts.Length >= 3 ? parts[2].Trim() : "Variant 1")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CloudVariants.Clear();
        AddIfMissing(CloudVariants, BaseVariantDisplayName);
        foreach (var variant in variantEntries)
        {
            AddIfMissing(CloudVariants, variant);
        }

        if (!CloudVariants.Contains(SelectedCloudVariant) && !_isSynchronizingAdvancedProfileSelection)
        {
            SelectedCloudVariant = CloudVariants.FirstOrDefault() ?? BaseVariantDisplayName;
        }

        if (rebuildTree)
        {
            RefreshCloudVariantTreeItems();
        }
    }

    private void RefreshCloudVariantTreeItems()
    {
        var expandedKeys = CaptureExpandedVariantKeys(CloudVariantTreeItems);
        var previousSelectedKey = SelectedCloudVariantItem is { } selectedCloudItem
            ? GetVariantTreeItemKey(selectedCloudItem)
            : string.Empty;
        CloudVariantTreeItems.Clear();
        if (_config.Root["CloudProfiles"] is JsonObject profiles)
        {
            foreach (var entry in profiles.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!CloudProfileFamilyRetarget.TryParseKey(entry.Key, out var provider, out var model, out var variant)
                    || entry.Value is not JsonObject settings)
                {
                    continue;
                }
                var designatedDefault = FindCloudModelDefaultVariantName(provider, model);
                var isBase = variant.Equals(designatedDefault, StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(designatedDefault) && IsModelDefaultProfile(settings, variant));
                if (!isBase)
                {
                    ClearModelDefaultMetadata(settings);
                }
                var warning = ResolveProfileWarning(isLocal: false, model, settings);
                CloudVariantTreeItems.Add(new CloudVariantTreeItemViewModel(
                    model,
                    variant,
                    isBase,
                    BuildCloudVariantListSummary(settings, isBase),
                    provider,
                    isUnlocked: IsProfileDefaultUnlocked(settings),
                    warningTooltip: warning.Tooltip,
                    isValidated: !warning.Show && !string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString()),
                    editorHost: this,
                    configKey: entry.Key));
            }
        }

        RestoreVariantTreeAfterRefresh(
            CloudVariantTreeItems,
            expandedKeys,
            previousSelectedKey,
            item => item.Provider.Equals(SelectedCloudProvider, StringComparison.OrdinalIgnoreCase)
                && item.ModelName.Equals(SelectedCloudProfile, StringComparison.OrdinalIgnoreCase)
                && item.Name.Equals(SelectedCloudVariant, StringComparison.OrdinalIgnoreCase),
            item => SelectedCloudVariantItem = item);
        RefreshCloudFamilyModelIdEditor();
        RefreshSwitchBenchmarkProfiles();
        RefreshProfileTemplateSources();
        SyncAdvancedProfileEditorVisibility();
        RefreshQuickSelectVariantLists();
        UpdateVariantEditingGate();
        RefreshProfileEndpointStatusSurfaces();
    }

    private string? FindCloudVariantWithMatchingSettings(JsonObject candidate, string? excludedVariant = null)
    {
        var candidateFingerprint = ProfileSettingsFingerprint.Cloud(candidate);
        foreach (var variant in CloudVariants)
        {
            if (ProfileConfigVariantName(variant).Equals(ProfileConfigVariantName(excludedVariant), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existing = ResolveCloudVariantObjectFromConfig(variant);
            if (ProfileSettingsFingerprint.Cloud(existing).Equals(candidateFingerprint, StringComparison.Ordinal))
            {
                return variant;
            }
        }

        return null;
    }

    private string? FindLocalVariantWithMatchingSettings(JsonObject candidate, string? excludedVariant = null)
    {
        var model = (SelectedLocalProfile ?? string.Empty).Trim();
        var candidateFingerprint = ProfileSettingsFingerprint.Local(candidate);
        if (_config.Root["LocalProfiles"] is not JsonObject profiles || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::", 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2
                || !parts[0].Equals(model, StringComparison.OrdinalIgnoreCase)
                || entry.Value is not JsonObject existing)
            {
                continue;
            }

            if (ProfileConfigVariantName(parts[1]).Equals(ProfileConfigVariantName(excludedVariant), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ProfileSettingsFingerprint.Local(existing).Equals(candidateFingerprint, StringComparison.Ordinal))
            {
                return parts[1];
            }
        }

        return null;
    }

    private static string GetLocalLaunchCriticalFingerprint(JsonObject settings)
    {
        return LocalLaunchFingerprint.From(settings);
    }

    private bool RejectDuplicateProfileSettings(string? duplicateName)
    {
        if (string.IsNullOrWhiteSpace(duplicateName))
        {
            return false;
        }

        StatusMessage = DuplicateProfileSettingsSaveFailedMessage;
        ThemedDialog.Warn("Save failed", DuplicateProfileSettingsSaveFailedMessage);
        return true;
    }

    private static string GetCloudSettingsFingerprint(JsonObject settings)
        => ProfileSettingsFingerprint.Cloud(settings);

    private static string FormatCloudVariantDisplayName(string variantName) =>
        variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            ? "the base defaults"
            : $"model profile variant '{variantName}'";

    private static string BuildCloudVariantListSummary(JsonObject settings, bool isBase)
    {
        var context = settings["CloudContextWindow"]?.ToString() ?? "Auto";
        var temperature = FormatProfileTemperature(settings["CloudTemperature"]?.ToString(), "0.7");
        var maxTokens = settings["CloudMaxTokens"]?.ToString() ?? "2048";
        var reasoning = settings["CloudReasoningMode"]?.ToString() ?? "Balanced";
        if (isBase)
        {
            context = DisplayResolvedSetting(context, "Provider default");
            reasoning = DisplayResolvedSetting(settings["CloudReasoningMode"]?.ToString() ?? "Balanced", "Balanced");
        }

        var validation = string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString())
            ? "not yet offered in Quick Select"
            : "offered in Quick Select";
        return $"context {context} \u00b7 temp {temperature} \u00b7 max {maxTokens} \u00b7 {reasoning} \u00b7 {validation}";
    }

    private double EstimateLocalVariantFootprintGiB(JsonObject settings, string modelName)
    {
        var modelPath = ResolveLocalModelPath(modelName);
        double fileGiB = 0;
        if (modelPath is not null)
        {
            try
            {
                fileGiB = new FileInfo(modelPath).Length / 1024d / 1024d / 1024d;
            }
            catch
            {
                fileGiB = 0;
            }
        }

        var imagesOn = (settings["LocalVisionEnabled"]?.ToString() ?? string.Empty)
            .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        return LocalVramFootprintEstimate.EstimateGiB(settings, fileGiB, imagesOn);
    }

    private void UpdateCloudModelCatalogStatus()
    {
        CloudModelCatalogStatusText = "Refresh Models asks the selected provider for chat-oriented ids using the stored API key. That dropdown is not proof a profile will validate — run Validate to endpoint before Quick Select.";
    }

    private void RefreshLocalVariantsForSelection(bool rebuildTree = true, bool refreshEndpointStatus = true)
    {
        var model = SelectedLocalProfile ?? string.Empty;
        var variantEntries = _config.GetObjectKeys("LocalProfiles")
            .Select(key => key.Split("::", 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 2)
            .Where(parts => string.Equals(parts[0], model, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LocalVariants.Clear();
        AddIfMissing(LocalVariants, BaseVariantDisplayName);
        foreach (var variant in variantEntries)
        {
            AddIfMissing(LocalVariants, variant);
        }

        if (!LocalVariants.Contains(SelectedLocalVariant) && !_isSynchronizingAdvancedProfileSelection)
        {
            SelectedLocalVariant = LocalVariants.FirstOrDefault() ?? BaseVariantDisplayName;
        }

        if (rebuildTree)
        {
            RefreshLocalVariantTreeItems(refreshEndpointStatus);
        }
        else if (refreshEndpointStatus)
        {
            RefreshProfileEndpointStatusSurfaces();
        }
    }

    private void RefreshLocalVariantTreeItems(bool refreshEndpointStatus = true)
    {
        var expandedKeys = CaptureExpandedVariantKeys(LocalVariantTreeItems);
        var previousSelectedKey = SelectedLocalVariantItem is { } selectedLocalItem
            ? GetVariantTreeItemKey(selectedLocalItem)
            : string.Empty;
        LocalVariantTreeItems.Clear();
        if (_config.Root["LocalProfiles"] is JsonObject profiles)
        {
            foreach (var entry in profiles.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var parts = entry.Key.Split("::", 2, StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || entry.Value is not JsonObject settings)
                {
                    continue;
                }

                var model = parts[0];
                var variant = parts[1];
                var designatedDefault = FindLocalModelDefaultVariantName(model);
                var isBase = variant.Equals(designatedDefault, StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(designatedDefault) && IsModelDefaultProfile(settings, variant));
                if (!isBase)
                {
                    ClearModelDefaultMetadata(settings);
                }
                var warning = ResolveProfileWarning(isLocal: true, model, settings);
                LocalVariantTreeItems.Add(new CloudVariantTreeItemViewModel(
                    model,
                    variant,
                    isBase,
                    BuildLocalVariantListSummary(model, variant, settings, isBase),
                    isMissingLocalFile: IsLocalGgufMissing(model),
                    isUnlocked: IsProfileDefaultUnlocked(settings),
                    warningTooltip: warning.Tooltip,
                    isValidated: !warning.Show && !string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString()),
                    editorHost: this));
            }
        }

        RestoreVariantTreeAfterRefresh(
            LocalVariantTreeItems,
            expandedKeys,
            previousSelectedKey,
            item => item.ModelName.Equals(SelectedLocalProfile, StringComparison.OrdinalIgnoreCase)
                && item.Name.Equals(SelectedLocalVariant, StringComparison.OrdinalIgnoreCase),
            item => SelectedLocalVariantItem = item);
        RefreshSwitchBenchmarkProfiles();
        RefreshProfileTemplateSources();
        SyncAdvancedProfileEditorVisibility();
        RefreshQuickSelectVariantLists();
        UpdateVariantEditingGate();
        if (refreshEndpointStatus)
        {
            RefreshProfileEndpointStatusSurfaces();
        }
    }

    private HashSet<string> GetValidatedQuickSlotModelKeys(bool isCloud)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            return result;
        }

        foreach (var slot in slots.OfType<JsonObject>()
                     .Where(slot => ParseBool(slot["enabled"]?.ToString(), true))
                     .Where(slot => ParseBool(slot["validatedDefaultProfile"]?.ToString(), false)))
        {
            var routeType = slot["routeType"]?.ToString() ?? string.Empty;
            if (isCloud != routeType.Equals("cloud", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = isCloud
                ? $"{(slot["provider"]?.ToString() ?? string.Empty).Trim()}::{(slot["cloudModel"]?.ToString() ?? string.Empty).Trim()}"
                : (slot["localModel"]?.ToString() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(key) && !key.EndsWith("::", StringComparison.Ordinal))
            {
                result.Add(key);
            }
        }

        return result;
    }

    private string BuildLocalVariantListSummary(string modelName, string variantName, JsonObject settings, bool isBase)
    {
        var context = settings["OverrideContext"]?.ToString() ?? string.Empty;
        var temperature = FormatProfileTemperature(settings["LocalTemperature"]?.ToString(), "0.3");
        var offload = settings["LocalGpuOffloadMode"]?.ToString() ?? "CPU only";
        var layers = settings["GpuLayers"]?.ToString() ?? string.Empty;
        if (isBase)
        {
            context = DisplayResolvedSetting(context, context);
            offload = DisplayResolvedSetting(offload, offload);
            if (layers.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                && offload.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase))
            {
                offload += " (fit)";
            }
        }

        var validation = string.IsNullOrWhiteSpace(settings["EndpointValidatedUtc"]?.ToString())
            ? "not yet offered in Quick Select"
            : "offered in Quick Select";
        var missing = IsLocalGgufMissing(modelName) ? "FILE MISSING \u00b7 " : string.Empty;
        var vramFootprint = EstimateLocalVariantFootprintGiB(settings, modelName);
        return $"{missing}context {context} \u00b7 temp {temperature} \u00b7 {offload} \u00b7 ~{vramFootprint:F1} GiB \u00b7 {validation}";
    }

    private void RefreshLocalVariantTreeItemSummary(string variantName)
    {
        var item = LocalVariantTreeItems.FirstOrDefault(candidate =>
            candidate.ModelName.Equals(SelectedLocalProfile, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.Equals(variantName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.SettingsSummary = BuildLocalVariantListSummary(
            SelectedLocalProfile,
            variantName,
            ResolveLocalVariantObjectFromConfig(variantName),
            item.IsBase);
    }

    private void RefreshCloudVariantTreeItemSummary(string variantName)
    {
        var item = CloudVariantTreeItems.FirstOrDefault(candidate =>
            candidate.Provider.Equals(SelectedCloudProvider, StringComparison.OrdinalIgnoreCase)
            && candidate.ModelName.Equals(SelectedCloudProfile, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.Equals(variantName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.SettingsSummary = BuildCloudVariantListSummary(
            ResolveCloudVariantObjectFromConfig(variantName),
            item.IsBase);
    }

    private void RefreshLocalVariantTreeItemSummariesInPlace()
    {
        foreach (var item in LocalVariantTreeItems)
        {
            var settings = GetLiveLocalProfileSettings(item.Name, item.ModelName) ?? new JsonObject();
            item.SettingsSummary = BuildLocalVariantListSummary(item.ModelName, item.Name, settings, item.IsBase);
        }
    }

    private void RefreshCloudVariantTreeItemSummariesInPlace()
    {
        foreach (var item in CloudVariantTreeItems)
        {
            var settings = GetLiveCloudProfileSettings(item.Name, item.Provider, item.ModelName) ?? new JsonObject();
            item.SettingsSummary = BuildCloudVariantListSummary(settings, item.IsBase);
        }
    }

    private void RefreshVariantTreeAfterValidationChange(bool isCloud, bool structureChanged)
    {
        _variantTreeRestoreEpoch++;
        if (structureChanged)
        {
            if (isCloud)
            {
                RefreshCloudVariantTreeItems();
            }
            else
            {
                RefreshLocalVariantTreeItems();
            }

            return;
        }

        if (isCloud)
        {
            RefreshCloudVariantTreeItemSummariesInPlace();
        }
        else
        {
            RefreshLocalVariantTreeItemSummariesInPlace();
        }
    }

    private static void SyncVariantTreeSelectionState(ICollection<CloudVariantTreeItemViewModel> items, CloudVariantTreeItemViewModel? selectedItem)
    {
        foreach (var item in items)
        {
            item.IsSelected = ReferenceEquals(item, selectedItem);
        }
    }

    private static CloudVariantTreeItemViewModel? ResolveActiveEditorItem(
        IEnumerable<CloudVariantTreeItemViewModel> items,
        CloudVariantTreeItemViewModel? selected)
        => VariantTreeSelection.ResolveActive(items, selected);

    private CloudVariantTreeItemViewModel? ResolveActiveLocalEditorItem()
        => ResolveActiveEditorItem(LocalVariantTreeItems, SelectedLocalVariantItem);

    private CloudVariantTreeItemViewModel? ResolveActiveCloudEditorItem()
        => ResolveActiveEditorItem(CloudVariantTreeItems, SelectedCloudVariantItem);

    private void AdoptExpandedLocalEditorSelection()
    {
        var active = ResolveActiveLocalEditorItem();
        if (active is not null && !ReferenceEquals(SelectedLocalVariantItem, active))
        {
            SelectedLocalVariantItem = active;
            return;
        }

        UpdateVariantEditability();
    }

    private void AdoptExpandedCloudEditorSelection()
    {
        var active = ResolveActiveCloudEditorItem();
        if (active is not null && !ReferenceEquals(SelectedCloudVariantItem, active))
        {
            SelectedCloudVariantItem = active;
            return;
        }

        UpdateVariantEditability();
    }

    private void QueueReassertExpandedTreeSelection(
        IList<CloudVariantTreeItemViewModel> items,
        Action<CloudVariantTreeItemViewModel> assign)
    {
        var epoch = _variantTreeRestoreEpoch;
        void Reassert()
        {
            if (epoch != _variantTreeRestoreEpoch)
            {
                return;
            }

            if (HoldPinnedCreatedVariant(items, assign))
            {
                return;
            }

            var current = ReferenceEquals(items, LocalVariantTreeItems)
                ? SelectedLocalVariantItem
                : SelectedCloudVariantItem;
            var expanded = VariantTreeSelection.ResolveExpanded(items, current);
            if (expanded is null)
            {
                UpdateVariantEditability();
                return;
            }

            if (!ReferenceEquals(current, expanded))
            {
                assign(expanded);
            }

            VariantTreeSelection.ExpandOnly(items, expanded);
            SyncVariantTreeSelectionState(items, expanded);
            UpdateVariantEditability();
        }

        Dispatcher.UIThread.Post(Reassert, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Reassert, DispatcherPriority.Render);
    }

    private void OpenSelectedProfileIfClosed(CloudVariantTreeItemViewModel? selected)
    {
        if (selected is null
            || selected.IsExpanded
            || _isSynchronizingAdvancedProfileSelection
            || _suppressVariantExpanderSelection
            || _isValidatingLocalProfile
            || _isValidatingCloudProfile
            || _holdProfileLayout)
        {
            return;
        }

        OnAdvancedProfileExpanderExpanded(selected);
    }

    private void QueueAdoptExpandedSelectionIfStolen(
        IList<CloudVariantTreeItemViewModel> items,
        CloudVariantTreeItemViewModel? selected,
        Action<CloudVariantTreeItemViewModel> assign)
    {
        var expanded = items.FirstOrDefault(item => item.IsExpanded);
        if (!VariantTreeSelection.ShouldAdoptStolenSelection(selected, expanded))
        {
            return;
        }

        if (_isSynchronizingAdvancedProfileSelection || _suppressVariantExpanderSelection)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isSynchronizingAdvancedProfileSelection || _suppressVariantExpanderSelection)
            {
                return;
            }

            if (HoldPinnedCreatedVariant(items, assign))
            {
                return;
            }

            var stillExpanded = items.FirstOrDefault(item => item.IsExpanded);
            var current = ReferenceEquals(items, LocalVariantTreeItems)
                ? SelectedLocalVariantItem
                : SelectedCloudVariantItem;
            if (!VariantTreeSelection.ShouldAdoptStolenSelection(current, stillExpanded)
                || stillExpanded is null)
            {
                return;
            }

            assign(stillExpanded);
        }, DispatcherPriority.Input);
    }

    private static string GetVariantTreeItemKey(CloudVariantTreeItemViewModel item)
        => !string.IsNullOrWhiteSpace(item.ConfigKey)
            ? item.ConfigKey
            : string.IsNullOrWhiteSpace(item.Provider)
                ? item.ModelName + "::" + item.Name
                : item.Provider + "::" + item.ModelName + "::" + item.Name;

    private static HashSet<string> CaptureExpandedVariantKeys(IEnumerable<CloudVariantTreeItemViewModel> items)
        => items
            .Where(item => item.IsExpanded)
            .Select(GetVariantTreeItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void RestoreVariantTreeAfterRefresh(
        IList<CloudVariantTreeItemViewModel> items,
        HashSet<string> expandedKeys,
        string previousSelectedKey,
        Func<CloudVariantTreeItemViewModel, bool> matchesCurrentProfile,
        Action<CloudVariantTreeItemViewModel?> assignSelectedItem)
    {
        ++_variantTreeRestoreEpoch;
        var intended = items.FirstOrDefault(matchesCurrentProfile)
            ?? items.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(previousSelectedKey)
                && GetVariantTreeItemKey(item).Equals(previousSelectedKey, StringComparison.OrdinalIgnoreCase));

        ApplyVariantTreeSelectionAndExpansion(items, intended, expandedKeys, assignSelectedItem);
        QueueReassertExpandedTreeSelection(items, item => assignSelectedItem(item));
    }

    private void ApplyVariantTreeSelectionAndExpansion(
        IList<CloudVariantTreeItemViewModel> items,
        CloudVariantTreeItemViewModel? selectedItem,
        HashSet<string> expandedKeys,
        Action<CloudVariantTreeItemViewModel?> assignSelectedItem)
    {
        _suppressVariantExpanderSelection = true;
        _isSynchronizingAdvancedProfileSelection = true;
        try
        {
            assignSelectedItem(selectedItem);
            SyncVariantTreeSelectionState(items, selectedItem);
            var expandKey = selectedItem is not null
                ? GetVariantTreeItemKey(selectedItem)
                : expandedKeys.Count == 1
                    ? expandedKeys.First()
                    : null;
            foreach (var item in items)
            {
                item.IsExpanded = expandKey is not null
                    && GetVariantTreeItemKey(item).Equals(expandKey, StringComparison.OrdinalIgnoreCase);
            }

            UpdateVariantEditability();
        }
        finally
        {
            _isSynchronizingAdvancedProfileSelection = false;
            _suppressVariantExpanderSelection = false;
        }
    }

    private void ReleaseCloudProfileEditor()
        => ReleaseProfileEditor(
            CloudVariantTreeItems,
            () => SelectedCloudVariantItem = null);

    private void ReleaseLocalProfileEditor()
        => ReleaseProfileEditor(
            LocalVariantTreeItems,
            () => SelectedLocalVariantItem = null);

    private void ReleaseProfileEditor(
        IList<CloudVariantTreeItemViewModel> items,
        Action clearSelected)
    {
        var wasSuppress = _suppressVariantExpanderSelection;
        var wasSync = _isSynchronizingAdvancedProfileSelection;
        _suppressVariantExpanderSelection = true;
        _isSynchronizingAdvancedProfileSelection = true;
        try
        {
            VariantTreeSelection.CollapseAll(items);
            foreach (var item in items)
            {
                item.IsSelected = false;
            }

            clearSelected();
        }
        finally
        {
            _isSynchronizingAdvancedProfileSelection = wasSync;
            _suppressVariantExpanderSelection = wasSuppress;
        }
    }

    internal void OnAdvancedProfileExpanderExpanded(CloudVariantTreeItemViewModel item)
    {
        if (_suppressVariantExpanderSelection)
        {
            return;
        }

        if (!_isValidatingLocalProfile && !_isValidatingCloudProfile)
        {
            _holdProfileLayout = false;
        }

        if (HoldPinnedCreatedVariant(item))
        {
            return;
        }

        var items = ResolveVariantTree(item);
        if (items is null)
        {
            return;
        }

        var key = GetVariantTreeItemKey(item);
        var live = items.FirstOrDefault(candidate =>
                       GetVariantTreeItemKey(candidate).Equals(key, StringComparison.OrdinalIgnoreCase))
                   ?? item;

        _suppressVariantExpanderSelection = true;
        _isSynchronizingAdvancedProfileSelection = true;
        try
        {
            if (ReferenceEquals(items, LocalVariantTreeItems))
            {
                ReleaseCloudProfileEditor();
            }
            else
            {
                ReleaseLocalProfileEditor();
            }

            foreach (var other in items)
            {
                if (!ReferenceEquals(other, live) && other.IsExpanded)
                {
                    other.IsExpanded = false;
                }
            }

            live.IsExpanded = true;
        }
        finally
        {
            _isSynchronizingAdvancedProfileSelection = false;
            _suppressVariantExpanderSelection = false;
        }

        if (ReferenceEquals(items, CloudVariantTreeItems))
        {
            SelectedCloudVariantItem = live;
        }
        else
        {
            SelectedLocalVariantItem = live;
        }

        UpdateVariantEditability();
    }

    private ObservableCollection<CloudVariantTreeItemViewModel>? ResolveVariantTree(CloudVariantTreeItemViewModel item)
    {
        var key = GetVariantTreeItemKey(item);
        if (CloudVariantTreeItems.Contains(item)
            || CloudVariantTreeItems.Any(candidate =>
                GetVariantTreeItemKey(candidate).Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            return CloudVariantTreeItems;
        }

        if (LocalVariantTreeItems.Contains(item)
            || LocalVariantTreeItems.Any(candidate =>
                GetVariantTreeItemKey(candidate).Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            return LocalVariantTreeItems;
        }

        return string.IsNullOrWhiteSpace(item.Provider) ? LocalVariantTreeItems : CloudVariantTreeItems;
    }

    private void RestoreExpandedVariantSelection(
        IList<CloudVariantTreeItemViewModel> items,
        Func<CloudVariantTreeItemViewModel?> getSelected,
        Action<CloudVariantTreeItemViewModel> assignSelectedItem)
    {
        if (_isSynchronizingAdvancedProfileSelection)
        {
            return;
        }

        var expanded = items.FirstOrDefault(item => item.IsExpanded);
        if (expanded is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isSynchronizingAdvancedProfileSelection || getSelected() is not null)
            {
                return;
            }

            if (!items.Contains(expanded) || !expanded.IsExpanded)
            {
                return;
            }

            assignSelectedItem(expanded);
        });
    }

    private static void ExpandVariantTreeItem(IEnumerable<CloudVariantTreeItemViewModel> items, string variantName)
    {
        var matchingItem = items.FirstOrDefault(item => item.Name.Equals(variantName, StringComparison.OrdinalIgnoreCase));
        if (matchingItem is not null)
        {
            matchingItem.IsExpanded = true;
        }
    }

    private void RevealCreatedLocalVariant(string variantName)
    {
        _variantTreeRestoreEpoch++;
        var item = LocalVariantTreeItems.FirstOrDefault(candidate =>
            candidate.ModelName.Equals(SelectedLocalProfile, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.Equals(variantName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        ReleaseCloudProfileEditor();
        PinCreatedVariant(item);
        ApplyVariantTreeSelectionAndExpansion(
            LocalVariantTreeItems,
            item,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GetVariantTreeItemKey(item) },
            selected => SelectedLocalVariantItem = selected);
        ProfileExpanderRevealRequested?.Invoke(item, true);
        QueueForceVariantTreeReveal(
            LocalVariantTreeItems,
            GetVariantTreeItemKey(item),
            selected => SelectedLocalVariantItem = selected);
    }

    private void RevealExpandedCloudProfile(string provider, string model, string variantName)
    {
        var normalizedVariant = ProfileConfigVariantName(variantName);
        var item = CloudVariantTreeItems.FirstOrDefault(candidate =>
            candidate.Provider.Equals(provider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && candidate.ModelName.Equals(model ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.Equals(normalizedVariant, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        ReleaseLocalProfileEditor();
        _variantTreeRestoreEpoch++;
        PinCreatedVariant(item);
        ApplyVariantTreeSelectionAndExpansion(
            CloudVariantTreeItems,
            item,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GetVariantTreeItemKey(item) },
            selected => SelectedCloudVariantItem = selected);
        QueueForceVariantTreeReveal(
            CloudVariantTreeItems,
            GetVariantTreeItemKey(item),
            selected => SelectedCloudVariantItem = selected);
    }

    private void PinCreatedVariant(CloudVariantTreeItemViewModel item)
    {
        _pinnedCreatedVariantKey = GetVariantTreeItemKey(item);
        var pinnedKey = _pinnedCreatedVariantKey;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(500).ConfigureAwait(true);
            if (string.Equals(_pinnedCreatedVariantKey, pinnedKey, StringComparison.OrdinalIgnoreCase))
            {
                _pinnedCreatedVariantKey = null;
            }
        }, DispatcherPriority.Background);
    }

    private bool HoldPinnedCreatedVariant(CloudVariantTreeItemViewModel item)
    {
        var items = ResolveVariantTree(item);
        if (items is null)
        {
            return false;
        }

        return HoldPinnedCreatedVariant(items, live =>
        {
            if (ReferenceEquals(items, CloudVariantTreeItems))
            {
                SelectedCloudVariantItem = live;
            }
            else
            {
                SelectedLocalVariantItem = live;
            }
        });
    }

    private bool HoldPinnedCreatedVariant(
        IList<CloudVariantTreeItemViewModel> items,
        Action<CloudVariantTreeItemViewModel> assign)
    {
        if (string.IsNullOrWhiteSpace(_pinnedCreatedVariantKey))
        {
            return false;
        }

        var pinned = items.FirstOrDefault(candidate =>
            GetVariantTreeItemKey(candidate).Equals(_pinnedCreatedVariantKey, StringComparison.OrdinalIgnoreCase));
        if (pinned is null)
        {
            return false;
        }

        ApplyVariantTreeSelectionAndExpansion(
            items,
            pinned,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _pinnedCreatedVariantKey },
            selected =>
            {
                if (selected is not null)
                {
                    assign(selected);
                }
            });
        return true;
    }

    private void QueueForceVariantTreeReveal(
        IList<CloudVariantTreeItemViewModel> items,
        string itemKey,
        Action<CloudVariantTreeItemViewModel?> assign)
    {
        var epoch = _variantTreeRestoreEpoch;
        void Reassert()
        {
            if (epoch != _variantTreeRestoreEpoch)
            {
                return;
            }

            var live = items.FirstOrDefault(item =>
                GetVariantTreeItemKey(item).Equals(itemKey, StringComparison.OrdinalIgnoreCase));
            if (live is null)
            {
                return;
            }

            ApplyVariantTreeSelectionAndExpansion(
                items,
                live,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { itemKey },
                assign);
            ProfileExpanderRevealRequested?.Invoke(live, false);
        }

        Dispatcher.UIThread.Post(Reassert, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Reassert, DispatcherPriority.Render);
        Dispatcher.UIThread.Post(Reassert, DispatcherPriority.Input);
    }

    private void SyncAdvancedProfileEditorVisibility()
    {
        IsCloudProfileEditorVisible = false;
        IsLocalProfileEditorVisible = false;
    }

    private void RefreshProfileTemplateSources()
    {
        if (!IsCloudProfileTemplateSelected && !IsLocalProfileTemplateSelected)
        {
            ProfileTemplateSources.Clear();
            SelectedProfileTemplateSource = null;
            SyncAdvancedProfileEditorVisibility();
            return;
        }

        var selectedRoute = IsCloudProfileTemplateSelected ? "Cloud" : "Local";
        var selectedVariant = IsCloudProfileTemplateSelected ? SelectedCloudVariant : SelectedLocalVariant;
        ProfileTemplateSources.Clear();
        var variants = IsCloudProfileTemplateSelected ? CloudVariants : LocalVariants;
        foreach (var variant in variants)
        {
            ProfileTemplateSources.Add(new ProfileTemplateSourceViewModel(
                selectedRoute,
                IsCloudProfileTemplateSelected ? SelectedCloudProvider : string.Empty,
                IsCloudProfileTemplateSelected ? SelectedCloudProfile : SelectedLocalProfile,
                variant));
        }

        SelectedProfileTemplateSource = ProfileTemplateSources
            .FirstOrDefault(source => source.Variant.Equals(selectedVariant, StringComparison.OrdinalIgnoreCase))
            ?? ProfileTemplateSources.FirstOrDefault();
    }

    private void RefreshLocalProfilesFromModelDirectory()
    {
        var discovered = new List<string>();
        if (!string.IsNullOrWhiteSpace(LocalModelDirectory) && Directory.Exists(LocalModelDirectory))
        {
            try
            {
                discovered = EnumerateGgufFilesCached()
                    .Select(Path.GetFileName)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !LocalHealthGuidance.IsVisionProjectorFile(x))
                    .Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                discovered = [];
            }
        }

        if (discovered.Count == 0)
        {
            LocalProfiles.Clear();
            AddIfMissing(LocalProfiles, "Local Balanced");
            AddIfMissing(LocalProfiles, "Local Throughput");
            AddIfMissing(LocalProfiles, "Local Max Quality");
            LocalModelDiscoveryText = "No local models discovered in the selected directory yet. Showing fallback local presets.";
            RefreshLocalVisionHint();
            return;
        }

        LocalProfiles.Clear();
        foreach (var modelName in discovered)
        {
            AddIfMissing(LocalProfiles, modelName);
        }

        if (!LocalProfiles.Contains(SelectedLocalProfile))
        {
            SelectedLocalProfile = LocalProfiles.FirstOrDefault() ?? SelectedLocalProfile;
        }

        if (!LocalProfiles.Contains(SelectedSelection1LocalProfile))
        {
            SelectedSelection1LocalProfile = LocalProfiles.FirstOrDefault() ?? SelectedSelection1LocalProfile;
        }

        LocalModelDiscoveryText = $"Discovered {discovered.Count} local model(s) from directory.";
        RefreshLocalVisionHint();
        if (string.IsNullOrWhiteSpace(NewLocalProfileModel) || !LocalProfiles.Contains(NewLocalProfileModel))
        {
            NewLocalProfileModel = LocalProfiles.FirstOrDefault() ?? NewLocalProfileModel;
        }
    }

    private bool EnsureCloudVariantEntry(string variantName)
    {
        var normalized = ProfileConfigVariantName(variantName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root["CloudProfiles"] = profiles;
        }

        RemoveEmptyCloudProfileKeys(SelectedCloudProvider, SelectedCloudProfile);

        var provider = (SelectedCloudProvider ?? string.Empty).Trim();
        var model = (SelectedCloudProfile ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            StatusMessage = "Choose a cloud provider and a model id before saving this profile.";
            return false;
        }

        var targetKey = $"{provider}::{model}::{normalized}";
        var existingKey = profiles
            .Select(x => x.Key)
            .FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            RefreshCloudVariantsForSelection();
            return false;
        }

        if (normalized.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(FindCloudModelDefaultVariantName(SelectedCloudProvider, SelectedCloudProfile)))
        {
            RefreshCloudVariantsForSelection();
            return false;
        }

        if (normalized.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            SetCloudPriorityOrder(CloudPriorityNames, persistToConfig: false);
            ApplyCloudPriorityOrderToDetailedSettings(isAutoTune: false, announce: false);
        }

        var created = BuildCloudVariantSettingsObject().DeepClone() as JsonObject ?? BuildCloudVariantSettingsObject();
        if (normalized.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            StampModelDefaultMetadata(created, unlocked: false);
        }
        else
        {
            ClearModelDefaultMetadata(created);
        }

        profiles[targetKey] = created;
        SelectedCloudVariant = normalized;
        CloudVariantDraftName = RenameDraftFromProfileName(normalized);
        RefreshCloudVariantsForSelection();
        LoadCloudVariantSettingsEditor();
        return true;
    }

    private bool TryDeleteCloudVariantEntry(string variantName)
    {
        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var targetKey = $"{(SelectedCloudProvider ?? string.Empty).Trim()}::{(SelectedCloudProfile ?? string.Empty).Trim()}::{NormalizeVariantName(variantName)}";
        var matchingKey = profiles
            .Select(x => x.Key)
            .FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            return false;
        }

        profiles.Remove(matchingKey);
        return true;
    }

    private bool TryRenameCloudVariantEntry(string currentVariantName, string newVariantName)
    {
        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var currentName = NormalizeVariantName(currentVariantName);
        var nextName = NormalizeVariantName(newVariantName);
        if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(nextName))
        {
            return false;
        }

        if (currentName.Equals(nextName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentKey = $"{(SelectedCloudProvider ?? string.Empty).Trim()}::{(SelectedCloudProfile ?? string.Empty).Trim()}::{currentName}";
        var nextKey = $"{(SelectedCloudProvider ?? string.Empty).Trim()}::{(SelectedCloudProfile ?? string.Empty).Trim()}::{nextName}";
        var matchingCurrentKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(currentKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingCurrentKey))
        {
            return false;
        }

        var matchingNextKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(nextKey, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matchingNextKey))
        {
            StatusMessage = "Cloud model profile variant already exists.";
            return false;
        }

        var payload = (profiles[matchingCurrentKey] as JsonObject)?.DeepClone();
        profiles.Remove(matchingCurrentKey);
        profiles[nextKey] = payload ?? new JsonObject();
        return true;
    }

    private bool EnsureLocalVariantEntry(string variantName)
    {
        var normalized = ProfileConfigVariantName(variantName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root["LocalProfiles"] = profiles;
        }

        RemoveEmptyLocalProfileKeys(SelectedLocalProfile);

        var targetKey = $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{normalized}";
        var existingKey = profiles
            .Select(x => x.Key)
            .FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            RefreshLocalVariantsForSelection();
            return false;
        }

        if (normalized.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(FindLocalModelDefaultVariantName(SelectedLocalProfile)))
        {
            RefreshLocalVariantsForSelection();
            return false;
        }

        var created = _runtimeService.MaterializeLocalProfileSettings(
            BuildLocalVariantSettingsObject(),
            FirstNonEmpty(SelectedLocalProfile, string.Empty),
            LocalModelDirectory,
            normalized);
        if (normalized.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            StampModelDefaultMetadata(created, unlocked: false);
        }
        else
        {
            ClearModelDefaultMetadata(created);
        }

        profiles[targetKey] = created;
        SelectedLocalVariant = normalized;
        LocalVariantDraftName = RenameDraftFromProfileName(normalized);
        RefreshLocalVariantsForSelection();
        LoadLocalVariantSettingsEditor();
        return true;
    }

    private void RefreshVariantSettingsEditors()
    {
        UpdateVariantBaseTargets();
        UpdateVariantEditability();
        LoadCloudVariantSettingsEditor();
        LoadLocalVariantSettingsEditor();
    }

    private void UpdateVariantBaseTargets()
    {
        CloudVariantBaseTarget = "Model profile family: " + SelectedCloudProvider + " / " + SelectedCloudProfile;
        LocalVariantBaseTarget = "Model profile family: " + SelectedLocalProfile;
        CloudVariantSelectorPrefix = SelectedCloudProvider + " / " + SelectedCloudProfile + " ::";
        LocalVariantSelectorPrefix = SelectedLocalProfile + " ::";
        UpdateProfileFootprintIndicators();
    }

    private void UpdateProfileFootprintIndicators()
    {
        CloudBaseFootprintText = $"Cloud model at {SelectedCloudProvider}: {SelectedCloudProfile}.";
        CloudVariantFootprintText = $"Variant: {SelectedCloudVariant}. Token use, wait time, and cost follow this company's settings and your account.";

        var modelPath = ResolveSelectedLocalModelPath();
        if (modelPath is null)
        {
            LocalBaseFootprintText = "Base model footprint unavailable: select a local model discovered in the global model directory.";
            LocalVariantFootprintText = "Model profile VRAM estimate unavailable until a local model is selected.";
            LocalVariantPerformanceEstimateText = "Throughput and startup estimates unavailable until a local model is selected.";
            LocalVariantVramWarningText = string.Empty;
            RefreshImagesHeadroomNote();
            return;
        }

        var fileGiB = new FileInfo(modelPath).Length / 1024d / 1024d / 1024d;
        LocalBaseFootprintText = $"Base model footprint: {fileGiB:F1} GiB local model. Full GPU offload needs roughly this much VRAM plus runtime and KV cache overhead.";

        var offloadFraction = LocalVariantGpuOffloadMode switch
        {
            "CPU only" => 0d,
            "GPU only" => 1d,
            "GPU + CPU" => 0.6d,
            _ => LocalVramFootprintEstimate.OffloadFractionFromLayers(LocalVariantGpuLayers)
        };
        var configuredContextTokens = Math.Max(1024d, (double)LocalVariantContext);
        var kvBytesPerToken = ResolveKvBytesPerToken(LocalVariantKvCacheTypeK, LocalVariantKvCacheTypeV);
        var weightsGiB = fileGiB * offloadFraction;
        var kvGiB = configuredContextTokens * kvBytesPerToken / 1024d / 1024d / 1024d;
        var runtimeGiB = offloadFraction > 0 ? LocalVramFootprintEstimate.RuntimeOverheadGiB : 0d;
        var imagesOn = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        var projectorGiB = imagesOn ? LocalVramFootprintEstimate.ProjectorGiB : 0d;
        var settings = BuildLocalVariantSettingsObject();
        var estimatedGiB = LocalVramFootprintEstimate.EstimateGiB(settings, fileGiB, imagesOn);
        var advice = CurrentLocalHardwareAdvice();
        var contextDenom = Math.Max(4096d, advice.ContextSafeDefault);
        var contextPressure = Math.Clamp(configuredContextTokens / contextDenom, 0.125d, 4d);
        var flashFactor = LocalVariantFlashAttention == "Enabled" ? 1.12d : LocalVariantFlashAttention == "Disabled" ? 0.88d : 1d;
        var throughputIndex = Math.Clamp((0.45d + offloadFraction * 0.85d) * flashFactor / Math.Sqrt(contextPressure), 0.2d, 1.5d);
        var gpuGb = advice.GpuTotalGb;
        var startupPressure = gpuGb > 0
            ? estimatedGiB >= gpuGb * 0.85 || fileGiB >= gpuGb * 0.70
                ? "high"
                : estimatedGiB >= gpuGb * 0.55 || fileGiB >= gpuGb * 0.45
                    ? "moderate"
                    : "low"
            : fileGiB >= 12d ? "high" : fileGiB >= 6d ? "moderate" : "low";
        var measuredGiB = ParseDouble(GetLiveLocalProfileSettings(SelectedLocalVariant, SelectedLocalProfile)?["MeasuredVramGiB"]?.ToString(), 0d);
        DateTimeOffset? measuredUtc = null;
        if (DateTimeOffset.TryParse(
                GetLiveLocalProfileSettings(SelectedLocalVariant, SelectedLocalProfile)?["MeasuredVramUpdatedUtc"]?.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedMeasuredUtc))
        {
            measuredUtc = parsedMeasuredUtc;
        }

        var measuredFootnote = LocalVramPressureAdvisor.FormatMeasuredFootnote(measuredGiB, measuredUtc);
        var measuredSuffix = string.IsNullOrWhiteSpace(measuredFootnote) ? string.Empty : "  |  " + measuredFootnote;
        LocalVramPressureAssessment vramPressure = default;
        if (LocalGpuVramSample.TryRead(out _, out var sampledTotalGiB, out var sampledFreeGiB))
        {
            var currentlyLoaded = IsSelectedLocalProfileCurrentlyLoaded();
            vramPressure = LocalVramPressureAdvisor.Assess(
                estimatedGiB,
                sampledFreeGiB,
                sampledTotalGiB,
                LocalVariantGpuOffloadMode,
                imagesOn,
                profileCurrentlyLoaded: currentlyLoaded,
                measuredGiB: measuredGiB,
                reclaimableGiB: currentlyLoaded ? 0 : EstimateReclaimableManagedLocalVramGiB());
        }

        LocalVariantFootprintText = $"Estimated VRAM  {estimatedGiB:F1} GiB  |  weights {weightsGiB:F1} GiB  +  KV {kvGiB:F1} GiB  +  runtime {runtimeGiB:F1} GiB{(projectorGiB > 0 ? $"  +  mmproj {projectorGiB:F1} GiB" : string.Empty)}  |  {configuredContextTokens:N0} configured tokens{measuredSuffix}";
        var liveVramSuffix = string.Empty;
        if (LocalGpuVramSample.TryRead(out _, out sampledTotalGiB, out sampledFreeGiB))
        {
            liveVramSuffix = IsSelectedLocalProfileCurrentlyLoaded()
                ? $"  |  GPU in use now {sampledTotalGiB - sampledFreeGiB:F1} / {sampledTotalGiB:F1} GiB"
                : $"  |  GPU free now {sampledFreeGiB:F1} / {sampledTotalGiB:F1} GiB";
        }

        LocalVariantVramWarningText = vramPressure.ShowWarning ? vramPressure.Message : string.Empty;
        RefreshImagesHeadroomNote();
        LocalVariantPerformanceEstimateText = $"Relative throughput  {throughputIndex * 100:F0}%  |  startup pressure {startupPressure}  |  flash attention {LocalVariantFlashAttention}{liveVramSuffix}{FormatValidateSpeedSuffix()}";
    }

    private string FormatValidateSpeedSuffix()
    {
        var summary = _runtimeService.GetLocalValidateSpeedSummary(SelectedLocalProfile, SelectedLocalVariant);
        return string.IsNullOrWhiteSpace(summary) ? string.Empty : "  |  " + summary;
    }

    private bool IsSelectedLocalProfileCurrentlyLoaded()
        => IsLocalProfileCurrentlyLoaded(SelectedLocalProfile, SelectedLocalVariant);

    private bool IsLocalProfileCurrentlyLoaded(string? model, string? variant)
    {
        model = (model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        variant = ProfileConfigVariantName(variant);
        if (_runtimeService.IsManagedLocalProfileLive(model, variant))
        {
            return true;
        }

        if (_runtimeService.IsManagedLocalAlive())
        {
            var (managedModel, managedVariant) = _runtimeService.GetManagedLocalIdentity();
            if (managedModel.Equals(model, StringComparison.OrdinalIgnoreCase)
                && (ProfileConfigVariantName(managedVariant).Equals(variant, StringComparison.OrdinalIgnoreCase)
                    || LocalRouteIndicatorState is RouteIndicatorState.Live or RouteIndicatorState.Amber))
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(_runningLocalModel))
        {
            return false;
        }

        if (!_runningLocalModel.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ProfileConfigVariantName(_runningLocalVariant)
                .Equals(variant, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LocalRouteIndicatorState is RouteIndicatorState.Live or RouteIndicatorState.Amber;
    }

    private double EstimateReclaimableManagedLocalVramGiB()
    {
        if (!_runtimeService.IsManagedLocalAlive())
        {
            return 0;
        }

        var (model, variant) = _runtimeService.GetManagedLocalIdentity();
        var settings = GetLiveLocalProfileSettings(variant, model);
        var measured = ParseDouble(settings?["MeasuredVramGiB"]?.ToString(), 0d);
        if (measured > 0)
        {
            return measured;
        }

        var modelPath = ResolveLocalModelPath(model);
        if (modelPath is null || settings is null)
        {
            return 0;
        }

        var fileGiB = new FileInfo(modelPath).Length / 1024d / 1024d / 1024d;
        var imagesOn = (settings["LocalVisionEnabled"]?.ToString() ?? string.Empty)
            .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        return LocalVramFootprintEstimate.EstimateGiB(settings, fileGiB, imagesOn);
    }

    private string? ResolveSelectedLocalModelPath() => ResolveLocalModelPath(SelectedLocalProfile);

    private string? ResolveLocalModelPath(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || !Directory.Exists(LocalModelDirectory))
        {
            return null;
        }

        try
        {
            return EnumerateGgufFilesCached()
                .FirstOrDefault(path => Path.GetFileName(path).Equals(modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<string> EnumerateGgufFilesCached()
    {
        var directory = LocalModelDirectory ?? string.Empty;
        if (_ggufPathCache is not null
            && string.Equals(_ggufPathCacheDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            return _ggufPathCache;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _ggufPathCache = [];
            _ggufPathCacheDirectory = directory;
            return _ggufPathCache;
        }

        _ggufPathCache = Directory.EnumerateFiles(directory, "*.gguf", SearchOption.AllDirectories).ToArray();
        _ggufPathCacheDirectory = directory;
        return _ggufPathCache;
    }

    private static double EstimateAutoOffloadFraction(string layers)
    {
        if (!int.TryParse(layers, out var parsedLayers))
        {
            return 0.75d;
        }

        return Math.Clamp(parsedLayers / 80d, 0d, 1d);
    }

    private static double ResolveKvBytesPerToken(string keyType, string valueType)
    {
        static double Resolve(string type) => type.ToLowerInvariant() switch
        {
            "q4_1" => 64 * 1024d,
            "q5_1" => 80 * 1024d,
            "q6_k" => 96 * 1024d,
            "q8_0" => 128 * 1024d,
            _ => 192 * 1024d
        };

        return Resolve(keyType) + Resolve(valueType);
    }

    private void UpdateVariantEditability()
    {
        var activeCloud = ResolveActiveEditorItem(CloudVariantTreeItems, SelectedCloudVariantItem);
        var activeLocal = ResolveActiveEditorItem(LocalVariantTreeItems, SelectedLocalVariantItem);
        IsCloudVariantEditable = activeCloud is null
            ? !string.Equals(SelectedCloudVariant, BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            : activeCloud.IsEditable;
        IsLocalVariantEditable = activeLocal is null
            ? !string.Equals(SelectedLocalVariant, BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            : activeLocal.IsEditable;
        CanValidateActiveCloudProfile = activeCloud is not null;
        CanDeleteActiveCloudProfile = activeCloud is not null
            && (activeCloud.IsEditable || string.IsNullOrWhiteSpace(activeCloud.ModelName));
        CanValidateActiveLocalProfile = activeLocal is not null;
        CanEditLocalVariantSettings = IsLocalVariantEditable
            || (IsLocalProfileEditorVisible && SelectedProfileTemplateSource is not null);
        ShowCloudCreateProfileTip = SelectedCloudVariantItem is { IsBase: true, IsUnlocked: false };
        ShowLocalCreateProfileTip = SelectedLocalVariantItem is { IsBase: true, IsUnlocked: false };
        if (_isValidatingLocalProfile)
        {
            return;
        }

        RefreshAutoChoiceListsThenRebindEditors();
    }

    private void RefreshAutoChoiceListsThenRebindEditors()
    {
        _isHydratingCloudVariantForm = true;
        _isHydratingLocalVariantForm = true;
        try
        {
            OnPropertyChanged(nameof(LocalGpuLayersChoices));
            OnPropertyChanged(nameof(CloudContextWindowChoices));
            OnPropertyChanged(nameof(CloudReasoningEffortChoices));
            AlignLockedChoiceLabelsWithEditability();
            OnPropertyChanged(nameof(CloudVariantContextWindow));
            OnPropertyChanged(nameof(CloudVariantOpenAiReasoningEffort));
            OnPropertyChanged(nameof(CloudVariantCopilotReasoningEffort));
            OnPropertyChanged(nameof(LocalVariantGpuLayers));
        }
        finally
        {
            _isHydratingCloudVariantForm = false;
            _isHydratingLocalVariantForm = false;
        }

        if (!_isValidatingLocalProfile)
        {
            CaptureLocalVariantEditorBaseline();
        }

        EnqueueRestoreHydratedLocalVision();
    }

    private void EnqueueRestoreHydratedLocalVision()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_preservingLocalVisionForm)
            {
                return;
            }

            if (IsLocalVariantEditable)
            {
                return;
            }

            if (LocalVariantVisionEnabled.Equals(_hydratedLocalVisionEnabled, StringComparison.OrdinalIgnoreCase)
                && string.Equals(LocalVariantVisionProjectorPath ?? string.Empty, _hydratedLocalVisionProjectorPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isHydratingLocalVariantForm = true;
            try
            {
                LocalVariantVisionEnabled = _hydratedLocalVisionEnabled;
                LocalVariantVisionProjectorPath = _hydratedLocalVisionProjectorPath ?? string.Empty;
                RefreshLocalVisionHint();
            }
            finally
            {
                _isHydratingLocalVariantForm = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private void AlignLockedChoiceLabelsWithEditability()
    {
        CloudVariantContextWindow = AlignAutoChoice(CloudVariantContextWindow, IsCloudVariantEditable, "Provider default");
        CloudVariantOpenAiReasoningEffort = AlignAutoChoice(CloudVariantOpenAiReasoningEffort, IsCloudVariantEditable, "Provider default");
        CloudVariantCopilotReasoningEffort = AlignAutoChoice(CloudVariantCopilotReasoningEffort, IsCloudVariantEditable, "Provider default");
        LocalVariantGpuLayers = AlignAutoChoice(LocalVariantGpuLayers, IsLocalVariantEditable, "fit");
    }

    private static string AlignAutoChoice(string? current, bool editable, string lockedDisplay)
    {
        var value = (current ?? string.Empty).Trim();
        if (editable)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Equals(lockedDisplay, StringComparison.OrdinalIgnoreCase))
            {
                return "Auto";
            }

            return value;
        }

        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return lockedDisplay;
        }

        return value;
    }

    private CloudVariantTreeItemViewModel? ActivateLocalAdvancedProfile(CloudVariantTreeItemViewModel? profile)
    {
        if (!_holdProfileLayout)
        {
            ReleaseCloudProfileEditor();
        }
        if (profile is not null)
        {
            SelectedLocalVariantItem = profile;
            if (!string.Equals(SelectedLocalProfile, profile.ModelName, StringComparison.OrdinalIgnoreCase))
            {
                SelectedLocalProfile = profile.ModelName;
            }

            if (!string.Equals(SelectedLocalVariant, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                SelectedLocalVariant = profile.Name;
            }

            var live = LocalVariantTreeItems.FirstOrDefault(item =>
                item.ModelName.Equals(profile.ModelName, StringComparison.OrdinalIgnoreCase)
                && item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (live is not null && !ReferenceEquals(SelectedLocalVariantItem, live))
            {
                SelectedLocalVariantItem = live;
            }
        }

        UpdateVariantEditability();
        return SelectedLocalVariantItem;
    }

    private bool IsEditableLocalCopy(CloudVariantTreeItemViewModel? item)
    {
        if (item is not null)
        {
            return item.IsEditable;
        }

        return IsLocalVariantEditable;
    }

    private static string BuildLocalTuneRemediation(IReadOnlyList<string> notes)
    {
        var blob = string.Join(" ", notes);
        var steps = new List<string>();
        if (blob.Contains("could not store", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add("Keep this editable model profile variant selected and try AutoTune again.");
        }

        if (blob.Contains("did not finish loading", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("Reload did not succeed", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add("If the model never becomes ready: confirm the local model is in the model directory, turn Fit VRAM on, or lower Context, then try AutoTune again.");
        }

        if (blob.Contains("short reply", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("did not complete", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add("If the model loads but a short reply fails: try GPU + CPU or fewer GPU layers, then try AutoTune again.");
        }

        if (steps.Count == 0)
        {
            steps.Add("Check the diagnostics window for the last error, then try AutoTune again after a successful Launch of this profile.");
        }

        return "What to try: " + string.Join(" ", steps);
    }

    private static string BuildLocalTuneResultSummary(long winnerMs, long? settingsPassMs, bool extraTrialsRan)
    {
        var testNow = " Those launch settings were stored so the speed test could reload the model. Max tokens, temperature, reasoning, and chat format you already set were left alone.";
        if (!extraTrialsRan)
        {
            return "No extra speed trials ran beyond the usual AutoTune settings. Those settings have been applied." + testNow;
        }

        if (settingsPassMs is null)
        {
            return "The extra speed trials could not be compared with the usual AutoTune settings, because that mix did not complete a timed reply. The fastest successful mix has been applied." + testNow;
        }

        if (winnerMs >= settingsPassMs.Value)
        {
            return "The extra speed trials did not improve response time compared with the usual AutoTune settings. Those settings have been applied." + testNow;
        }

        var gain = settingsPassMs.Value - winnerMs;
        return "About " + FormatFriendlyDurationMs(gain) +
               " faster than the usual AutoTune settings. That mix has been applied." + testNow;
    }

    private void LoadCloudVariantSettingsEditor()
    {
        var settings = ResolveCloudVariantObjectFromConfig(NormalizeVariantName(SelectedCloudVariant));
        LoadCloudVariantSettingsForm(settings);
        LoadCloudPriorityOrder(settings);
        CloudVariantSettingsJson = SerializeJsonObject(settings);
        CloudIntentPresetInfoText = "AutoTune has not been run yet.";
        UpdateCloudIntentSummaryTexts();
    }

    private void LoadLocalVariantSettingsEditor()
    {
        var settings = ResolveLocalVariantObjectFromConfig(NormalizeVariantName(SelectedLocalVariant));
        LoadLocalVariantSettingsForm(settings);
        LoadLocalPriorityOrder(settings);
        LocalVariantSettingsJson = SerializeJsonObject(settings);
        if (!_localAutoTuneRunning)
        {
            LocalQuickAutoTuneInfoText = "AutoTune has not been run yet.";
        }
        UpdateLocalPrioritySummaryTexts();
        UpdateLocalLaunchTelemetrySummary();
        CaptureLocalVariantEditorBaseline();
    }

    private void LoadCloudPriorityOrder(JsonObject settings)
    {
        var savedOrder = ReadStringArray(settings, "PriorityOrder");
        if (savedOrder.Count > 0)
            SetCloudPriorityOrder(savedOrder, persistToConfig: false);
        else
            InferCloudIntentPresetFromDetailedSettings();
    }

    private void LoadLocalPriorityOrder(JsonObject settings)
    {
        var savedOrder = ReadStringArray(settings, "PriorityOrder");
        if (savedOrder.Count > 0)
            SetLocalPriorityOrder(savedOrder, persistToConfig: false);
        else
            SyncLocalPriorityOrderFromDetailedSettings(requireClearLead: false);
    }

    private static JsonArray BuildStringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject settings, string key) =>
        settings[key] is JsonArray array
            ? array.Select(node => node?.ToString()?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList()
            : [];

    private void UpdateRouteLiveIndicators(RouteIndicatorState? cloudState = null, RouteIndicatorState? localState = null)
    {
        if (cloudState.HasValue)
        {
            CloudRouteIndicatorState = cloudState.Value;
        }

        if (localState.HasValue)
        {
            LocalRouteIndicatorState = localState.Value;
        }
    }

    private void ApplyCloudVariantSettings(string variantName, bool reloadEditor = true)
    {
        var normalized = ProfileConfigVariantName(variantName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            StatusMessage = "Enter a cloud model profile variant name first.";
            return;
        }

        var provider = (SelectedCloudProvider ?? string.Empty).Trim();
        var model = (SelectedCloudProfile ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            StatusMessage = "Choose a cloud provider and a model id before saving this profile.";
            return;
        }

        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root["CloudProfiles"] = profiles;
        }

        var settings = ResolveCloudVariantObjectFromConfig(normalized);
        settings["CloudTemperature"] = CloudVariantTemperature;
        settings["CloudMaxTokens"] = (int)CloudVariantMaxTokens;
        settings["CloudContextWindow"] = PersistSettingChoice(CloudVariantContextWindow, "Provider default");
        settings["CloudReasoningMode"] = CloudVariantReasoningMode;
        settings["CloudResponseFormat"] = CloudVariantResponseFormat;
        settings["OpenAiReasoningEffort"] = PersistSettingChoice(CloudVariantOpenAiReasoningEffort, "Provider default");
        settings["CopilotReasoningEffort"] = PersistSettingChoice(CloudVariantCopilotReasoningEffort, "Provider default");
        settings["GeminiThinkingMode"] = CloudVariantGeminiThinkingMode;
        settings["GeminiThinkingBudget"] = (int)CloudVariantGeminiThinkingBudget;
        settings["AnthropicThinkingMode"] = CloudVariantAnthropicThinkingMode;
        settings["PriorityOrder"] = BuildStringArray(CloudPriorityOrder);

        var targetKey = $"{provider}::{model}::{normalized}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        profiles[matchingKey ?? targetKey] = settings;

        RefreshCloudVariantsForSelection(rebuildTree: false);
        RefreshCloudVariantTreeItemSummary(normalized);
        SelectedCloudVariant = normalized;
        if (reloadEditor)
        {
            LoadCloudVariantSettingsEditor();
        }
    }

    private void ApplyLocalVariantSettings(string variantName, bool reloadEditor = true)
        => ApplyLocalVariantSettings(SelectedLocalProfile, variantName, reloadEditor);

    private void ApplyLocalVariantSettings(string? modelName, string variantName, bool reloadEditor = true)
    {
        var normalized = ProfileConfigVariantName(variantName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            StatusMessage = "Enter a local model profile variant name first.";
            return;
        }

        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root["LocalProfiles"] = profiles;
        }

        var model = (modelName ?? string.Empty).Trim();
        var targetKey = $"{model}::{normalized}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        JsonObject settings;
        if (!string.IsNullOrWhiteSpace(matchingKey) && profiles[matchingKey] is JsonObject live)
        {
            settings = live;
        }
        else
        {
            settings = ResolveLocalVariantObjectFromConfig(model, normalized);
            matchingKey = targetKey;
            profiles[matchingKey] = settings;
        }

        WriteLocalVariantSettingsFields(settings);
        StripDefaultMetadataIfCopy(settings, normalized);

        RefreshLocalVariantsForSelection(rebuildTree: false);
        RefreshLocalVariantTreeItemSummary(normalized);
        SelectedLocalVariant = normalized;
        if (reloadEditor)
        {
            LoadLocalVariantSettingsEditor();
        }
    }

    private bool TryPersistCurrentLocalVariantSettings()
    {
        if (!IsLocalVariantEditable)
        {
            return false;
        }

        var normalized = NormalizeVariantName(SelectedLocalVariant);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root["LocalProfiles"] = profiles;
        }

        var targetKey = $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{normalized}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        JsonObject settings;
        if (!string.IsNullOrWhiteSpace(matchingKey) && profiles[matchingKey] is JsonObject live)
        {
            settings = live;
        }
        else
        {
            settings = ResolveLocalVariantObjectFromConfig(normalized);
            matchingKey = targetKey;
            profiles[matchingKey] = settings;
        }

        WriteLocalVariantSettingsFields(settings);
        StripDefaultMetadataIfCopy(settings, normalized);
        _configService.Save(_config);
        return true;
    }

    private void PersistLocalAutoTuneFormSettings()
    {
        var variant = NormalizeVariantName(SelectedLocalVariant);
        if (string.IsNullOrWhiteSpace(variant) || !IsLocalVariantEditable)
        {
            return;
        }

        ApplyLocalVariantSettings(variant, reloadEditor: false);
        _configService.Save(_config);
    }

    private void ReassertLocalVariantEditor(CloudVariantTreeItemViewModel? profile)
    {
        var live = profile is null
            ? SelectedLocalVariantItem
            : LocalVariantTreeItems.FirstOrDefault(item =>
                  item.ModelName.Equals(profile.ModelName, StringComparison.OrdinalIgnoreCase)
                  && item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
              ?? profile;
        if (live is null)
        {
            UpdateVariantEditability();
            return;
        }

        ReleaseCloudProfileEditor();
        if (VariantTreeSelection.ShouldReassert(SelectedLocalVariantItem, live))
        {
            VariantTreeSelection.ExpandOnly(LocalVariantTreeItems, live);
            if (!ReferenceEquals(SelectedLocalVariantItem, live))
            {
                SelectedLocalVariantItem = live;
                return;
            }
        }

        SyncVariantTreeSelectionState(LocalVariantTreeItems, live);
        UpdateVariantEditability();
    }

    private void StripDefaultMetadataIfCopy(JsonObject settings, string variantName, string? modelName = null)
    {
        var designated = FindLocalModelDefaultVariantName(FirstNonEmpty(modelName, SelectedLocalProfile));
        if (variantName.Equals(designated, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(designated)
                && variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ClearModelDefaultMetadata(settings);
    }

    private JsonObject? CloneSavedLocalVariantSettings()
    {
        var normalized = NormalizeVariantName(SelectedLocalVariant);
        if (string.IsNullOrWhiteSpace(normalized) || !IsLocalVariantEditable)
        {
            return null;
        }

        return ResolveLocalVariantObjectFromConfig(normalized).DeepClone() as JsonObject;
    }

    private void RestoreSavedLocalVariantSettings(JsonObject? snapshot)
    {
        if (snapshot is null || !IsLocalVariantEditable)
        {
            return;
        }

        var normalized = NormalizeVariantName(SelectedLocalVariant);
        if (string.IsNullOrWhiteSpace(normalized) || _config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return;
        }

        var targetKey = $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{normalized}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        profiles[matchingKey ?? targetKey] = snapshot.DeepClone();
        _configService.Save(_config);
    }

    private void WriteLocalVariantSettingsFields(JsonObject settings)
    {
        settings["OverrideContext"] = ((int)LocalVariantContext).ToString();
        settings["OverrideThreads"] = LocalVariantOverrideThreads;
        settings["LocalThreadsBatch"] = LocalVariantThreadsBatch;
        settings["LocalTemperature"] = LocalVariantTemperature.ToString(CultureInfo.InvariantCulture);
        settings["LocalGpuOffloadMode"] = LocalVariantGpuOffloadMode;
        settings["LocalMultiGpuMode"] = LocalVariantMultiGpuMode;
        settings["LocalSplitMode"] = LocalVariantSplitMode;
        settings["LocalMainGpu"] = LocalVariantMainGpu;
        settings["LocalTensorSplit"] = LocalVariantTensorSplit;
        settings["GpuLayers"] = PersistSettingChoice(LocalVariantGpuLayers, "fit");
        settings["LocalFlashAttention"] = LocalVariantFlashAttention;
        settings["LocalKvCacheTypeK"] = LocalVariantKvCacheTypeK;
        settings["LocalKvCacheTypeV"] = LocalVariantKvCacheTypeV;
        settings["LocalChatTemplate"] = LocalVariantChatTemplate = SanitizeLocalChatTemplate(LocalVariantChatTemplate);
        settings["LocalMultiUserMode"] = LocalVariantMultiUserMode;
        settings["LocalUnbanTokensMode"] = LocalVariantUnbanTokensMode;
        settings["AutoCompressEnabled"] = LocalVariantAutoCompressEnabled;
        settings["OverrideMaxTokens"] = LocalVariantMaxTokens;
        settings["LocalBatchSize"] = LocalVariantBatchSize;
        settings["LocalUbatchSize"] = LocalVariantUbatchSize;
        settings["LocalSpecType"] = FluxMuxRuntimeService.NormalizeLlamaSpecType(LocalVariantSpecType);
        settings["LocalCacheReuse"] = LocalVariantCacheReuse;
        settings["LocalCacheRam"] = LocalVariantCacheRam;
        settings["LocalFit"] = LocalVariantFit;
        settings["LocalSwaFull"] = LocalVariantSwaFull;
        settings["LocalReasoning"] = LocalVariantReasoning;
        settings["LocalChatParser"] = LocalVariantChatParser;
        settings["LocalVisionEnabled"] = LocalVariantVisionEnabled;
        settings["LocalVisionProjectorPath"] = LocalVariantVisionProjectorPath;
        settings["LocalVisionMaxImageEdge"] = ((int)LocalVariantVisionMaxImageEdge).ToString(CultureInfo.InvariantCulture);
        settings["PriorityOrder"] = BuildStringArray(LocalPriorityOrder);
    }

    private void PersistLocalProfileSnapshot(string model, string variantName, JsonObject snapshot)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            _config.Root["LocalProfiles"] = profiles;
        }

        var normalized = ProfileConfigVariantName(variantName);
        var targetKey = $"{(model ?? string.Empty).Trim()}::{normalized}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        JsonObject live;
        if (!string.IsNullOrWhiteSpace(matchingKey) && profiles[matchingKey] is JsonObject existing)
        {
            live = existing;
        }
        else
        {
            live = new JsonObject();
            matchingKey = targetKey;
            profiles[matchingKey] = live;
        }

        foreach (var property in snapshot)
        {
            live[property.Key] = property.Value?.DeepClone();
        }

        StripDefaultMetadataIfCopy(live, normalized);
        _configService.Save(_config);
    }

    private void RestoreLocalVariantForm(JsonObject snapshot)
    {
        LoadLocalVariantSettingsForm(snapshot);
        LoadLocalPriorityOrder(snapshot);
        RememberLocalVisionForm(
            snapshot["LocalVisionEnabled"]?.ToString() ?? LocalVariantVisionEnabled,
            snapshot["LocalVisionProjectorPath"]?.ToString() ?? string.Empty,
            ParseInt(snapshot["LocalVisionMaxImageEdge"]?.ToString(), (int)LocalVariantVisionMaxImageEdge));
    }

    private void LoadCloudVariantSettingsForm(JsonObject settings)
    {
        _isHydratingCloudVariantForm = true;
        try
        {
        CloudVariantTemperature = (decimal)ParseDouble(settings["CloudTemperature"]?.ToString() ?? string.Empty, 0.7);
        CloudVariantMaxTokens = ParseInt(settings["CloudMaxTokens"]?.ToString() ?? string.Empty, 2048);
        var isDefaultCloud = string.Equals(SelectedCloudVariant, BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        CloudVariantContextWindow = MapAutoForEditor(
            settings["CloudContextWindow"]?.ToString(),
            "Auto",
            isDefaultCloud,
            "Provider default");
        CloudVariantReasoningMode = FirstNonEmpty(settings["CloudReasoningMode"]?.ToString() ?? string.Empty, "Balanced");
        CloudVariantResponseFormat = FirstNonEmpty(settings["CloudResponseFormat"]?.ToString() ?? string.Empty, "Text");
        CloudVariantOpenAiReasoningEffort = MapAutoForEditor(
            settings["OpenAiReasoningEffort"]?.ToString(),
            "Auto",
            isDefaultCloud,
            "Provider default");
        CloudVariantCopilotReasoningEffort = MapAutoForEditor(
            settings["CopilotReasoningEffort"]?.ToString(),
            "Auto",
            isDefaultCloud,
            "Provider default");
        CloudVariantGeminiThinkingMode = FirstNonEmpty(settings["GeminiThinkingMode"]?.ToString() ?? string.Empty, "Balanced");
        CloudVariantGeminiThinkingBudget = ParseInt(settings["GeminiThinkingBudget"]?.ToString() ?? string.Empty, 0);
        CloudVariantAnthropicThinkingMode = FirstNonEmpty(settings["AnthropicThinkingMode"]?.ToString() ?? string.Empty, "Standard");
        }
        finally
        {
            _isHydratingCloudVariantForm = false;
        }
        UpdateCloudIntentSummaryTexts();
    }

    [RelayCommand]
    private void MoveCloudPriorityUp() => MoveSelectedCloudPriority(-1);

    [RelayCommand]
    private void MoveCloudPriorityDown() => MoveSelectedCloudPriority(1);

    [RelayCommand]
    private void ApplyCloudIntentPreset()
    {
        ApplyCloudPriorityOrderToDetailedSettings(isAutoTune: true);
    }

    private void ApplyCloudPriorityOrderToDetailedSettings(bool isAutoTune, bool announce = true)
    {
        var preset = BuildCloudPriorityPresetSelection(CloudPriorityOrder);
        var reconciled = ReconcileCloudSettingsWithPriorityOrder(ResolveCloudPriorityOrder(CloudPriorityOrder), preset.Settings);
        ApplyCapturedCloudSettings(reconciled);

        if (announce)
        {
            CloudIntentPresetInfoText = isAutoTune
                ? "AutoTune filled spare capacity using known-good settings that still follow your priority list."
                : "Updated detailed settings to match your new priority order.";
        }

        CloudPriorityMinimalEffectNote = string.Empty;
        UpdateCloudIntentSummaryTexts();
    }

    private void ApplyCapturedCloudSettings(CloudIntentPresetSettings settings)
    {
        _isApplyingCloudPriorityWizard = true;
        try
        {
            CloudVariantContextWindow = settings.ContextWindow;
            CloudVariantTemperature = settings.Temperature;
            CloudVariantMaxTokens = settings.MaxTokens;
            CloudVariantReasoningMode = settings.ReasoningMode;
            CloudVariantResponseFormat = settings.ResponseFormat;
            CloudVariantOpenAiReasoningEffort = settings.OpenAiReasoningEffort;
            CloudVariantCopilotReasoningEffort = settings.CopilotReasoningEffort;
            CloudVariantGeminiThinkingMode = settings.GeminiThinkingMode;
            CloudVariantGeminiThinkingBudget = settings.GeminiThinkingBudget;
            CloudVariantAnthropicThinkingMode = settings.AnthropicThinkingMode;
        }
        finally
        {
            _isApplyingCloudPriorityWizard = false;
            RefreshProfileEndpointStatusSurfaces();
        }
    }

    [RelayCommand]
    private void InferCloudIntentPresetFromDetailedSettings()
    {
        SyncCloudPriorityOrderFromDetailedSettings(requireClearLead: false);
        CloudIntentPresetInfoText = "Guessed priority order from the current cloud settings.";
        CloudPriorityMinimalEffectNote = string.Empty;
        UpdateCloudIntentSummaryTexts();
    }

    private void MoveSelectedCloudPriority(int delta)
    {
        if (string.IsNullOrWhiteSpace(SelectedCloudPriority))
        {
            return;
        }

        var currentIndex = CloudPriorityOrder.IndexOf(SelectedCloudPriority);
        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= CloudPriorityOrder.Count)
        {
            return;
        }

        var beforeOrder = CloudPriorityOrder.ToList();
        var beforeOutcome = BuildCloudPriorityPresetSelection(beforeOrder).PresetName;
        var firstLabel = $"{currentIndex + 1}. {CloudPriorityOrder[currentIndex]}";
        var secondLabel = $"{targetIndex + 1}. {CloudPriorityOrder[targetIndex]}";

        CloudPriorityOrder.Move(currentIndex, targetIndex);
        SelectedCloudPriority = CloudPriorityOrder[targetIndex];
        var afterOutcome = BuildCloudPriorityPresetSelection(CloudPriorityOrder).PresetName;
        CloudPriorityMinimalEffectNote = string.Equals(beforeOutcome, afterOutcome, StringComparison.OrdinalIgnoreCase)
            ? $"No effective gains/losses compared with previous order: swapping {firstLabel} and {secondLabel} currently leaves the chosen preset unchanged."
            : string.Empty;
        ApplyCloudPriorityOrderToDetailedSettings(isAutoTune: false);
    }

    private void SetCloudPriorityOrder(IEnumerable<string> priorityOrder, bool persistToConfig)
    {
        var resolved = ResolveCloudPriorityOrder(priorityOrder).ToList();
        if (!resolved.SequenceEqual(CloudPriorityOrder))
        {
            CloudPriorityOrder.Clear();
            foreach (var priority in resolved)
                CloudPriorityOrder.Add(priority);
        }

        if (!CloudPriorityOrder.Contains(SelectedCloudPriority))
            SelectedCloudPriority = CloudPriorityOrder.FirstOrDefault() ?? "Stability";
        if (persistToConfig)
        {
            _config.SetStringArray("CloudPriorityOrder", CloudPriorityOrder);
        }

        CloudPriorityMinimalEffectNote = string.Empty;

        UpdateCloudIntentSummaryTexts();
    }

    private void SyncCloudPriorityOrderFromDetailedSettings(bool requireClearLead = true)
    {
        var scores = ComputeCloudPriorityScores();
        var inferred = RankPrioritiesByScore(CloudPriorityNames, scores);
        if (inferred.Count == 0)
        {
            return;
        }

        if (!requireClearLead || ShouldAdoptInferredPriorityOrder(CloudPriorityOrder, inferred, scores, margin: 3))
        {
            SetCloudPriorityOrder(inferred, persistToConfig: false);
        }
    }

    public void ReorderCloudPriority(string draggedPriority, string targetPriority)
    {
        if (string.IsNullOrWhiteSpace(draggedPriority) || string.IsNullOrWhiteSpace(targetPriority) || draggedPriority == targetPriority)
        {
            return;
        }

        var fromIndex = CloudPriorityOrder.IndexOf(draggedPriority);
        var toIndex = CloudPriorityOrder.IndexOf(targetPriority);
        if (fromIndex < 0 || toIndex < 0)
        {
            return;
        }

        CloudPriorityOrder.Move(fromIndex, toIndex);
        SelectedCloudPriority = draggedPriority;
        ApplyCloudPriorityOrderToDetailedSettings(isAutoTune: false);
    }

    private Dictionary<string, int> ComputeCloudPriorityScores()
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stability"] = 0,
            ["Speed"] = 0,
            ["Reasoning depth"] = 0,
            ["Context length"] = 0,
            ["Reply length"] = 0,
            ["Token Cost Economy"] = 0
        };

        if (CloudVariantTemperature <= 0.25m)
        {
            // Low temp signals precision/careful output — Stability and Reasoning depth, not cost economy.
            scores["Stability"] += 2;
            scores["Reasoning depth"] += 1;
        }
        else if (CloudVariantTemperature <= 0.45m)
        {
            scores["Stability"] += 2;
        }
        else if (CloudVariantTemperature >= 0.85m)
        {
            scores["Speed"] += 3;
        }
        else
        {
            scores["Stability"] += 1;
        }

        var ctxInt = ParseInt(CloudVariantContextWindow, 0);
        if (!string.Equals(CloudVariantContextWindow, "Auto", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(CloudVariantContextWindow, "Provider default", StringComparison.OrdinalIgnoreCase))
        {
            if (ctxInt >= 98304)
            {
                scores["Context length"] += 16;
                scores["Speed"] -= 4;
                scores["Token Cost Economy"] -= 2;
            }
            else if (ctxInt >= 65536)
            {
                scores["Context length"] += 12;
                scores["Speed"] -= 2;
            }
            else if (ctxInt >= 32768)
            {
                scores["Context length"] += 6;
            }
            else if (ctxInt >= 16384)
            {
                scores["Context length"] += 2;
            }
            else
            {
                scores["Speed"] += 2;
                scores["Token Cost Economy"] += 1;
            }
        }

        if (string.Equals(CloudVariantReasoningMode, "Deep", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CloudVariantOpenAiReasoningEffort, "High", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CloudVariantCopilotReasoningEffort, "High", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CloudVariantAnthropicThinkingMode, "Extended", StringComparison.OrdinalIgnoreCase))
        {
            scores["Reasoning depth"] += 4;
        }

        // Medium effort is a deliberate stability/quality balance signal.
        if (string.Equals(CloudVariantOpenAiReasoningEffort, "Medium", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CloudVariantCopilotReasoningEffort, "Medium", StringComparison.OrdinalIgnoreCase))
        {
            scores["Stability"] += 1;
        }

        if (string.Equals(CloudVariantReasoningMode, "Fast", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CloudVariantOpenAiReasoningEffort, "Low", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CloudVariantCopilotReasoningEffort, "Low", StringComparison.OrdinalIgnoreCase))
        {
            scores["Speed"] += 3;
        }

        if (CloudVariantMaxTokens >= 4096)
        {
            scores["Reply length"] += 8;
        }
        else if (CloudVariantMaxTokens >= 2048)
        {
            scores["Reply length"] += 3;
        }
        else if (CloudVariantMaxTokens <= 768 || string.Equals(CloudVariantResponseFormat, "Json", StringComparison.OrdinalIgnoreCase))
        {
            scores["Token Cost Economy"] += 4;
        }
        else if (CloudVariantMaxTokens <= 1024)
        {
            scores["Speed"] += 1;
            scores["Token Cost Economy"] += 2;
        }

        if (string.Equals(CloudVariantResponseFormat, "Json", StringComparison.OrdinalIgnoreCase)
            && CloudVariantMaxTokens > 768)
        {
            scores["Token Cost Economy"] += 2;
        }

        if (string.Equals(CloudVariantGeminiThinkingMode, "On", StringComparison.OrdinalIgnoreCase)
            || CloudVariantGeminiThinkingBudget >= 1024)
        {
            scores["Reasoning depth"] += 2;
        }

        return scores;
    }

    private CloudPriorityPreset BuildCloudPriorityPresetSelection(IEnumerable<string> priorityOrder)
    {
        var resolved = ResolveCloudPriorityOrder(priorityOrder);
        var settings = CalculateCloudSettingsFromPriorityOrder(resolved);
        var primary = resolved[0];
        var name = primary switch
        {
            "Speed" => "Cloud Fast Draft",
            "Reasoning depth" => "Cloud Reasoning depth",
            "Context length" => "Cloud Context length",
            "Reply length" => "Cloud Reply length",
            "Token Cost Economy" => "Cloud Token Cost Economy",
            _ => "Cloud Stable"
        };
        return new CloudPriorityPreset(name, settings, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0);
    }

    private static CloudIntentPresetSettings CalculateCloudSettingsFromPriorityOrder(IReadOnlyList<string> resolved)
    {
        int Rank(string name)
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                if (resolved[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return resolved.Count;
        }

        var speed = Rank("Speed");
        var reasoning = Rank("Reasoning depth");
        var longContext = Rank("Context length");
        var reply = Rank("Reply length");
        var cost = Rank("Token Cost Economy");
        var stability = Rank("Stability");

        var contextWindow = longContext == 0
            ? "131072"
            : longContext == 1 && speed != 0
                ? "65536"
                : longContext <= 2
                    ? "32768"
                    : "Auto";

        var temperature = speed == 0 ? 0.9m
            : reasoning == 0 ? 0.35m
            : cost == 0 ? 0.2m
            : stability == 0 ? 0.7m
            : 0.45m;
        if (reasoning == 1 && speed != 0)
            temperature = Math.Min(temperature, 0.4m);

        var maxTokens = reply == 0 ? 8192m
            : reply == 1 ? 4096m
            : reply == 2 ? 2048m
            : 1024m;
        if (cost == 0 && reply > 0)
        {
            maxTokens = Math.Min(maxTokens, 2048m);
        }

        var reasoningMode = reasoning == 0 ? "Deep" : speed == 0 ? "Fast" : "Balanced";
        var effort = reasoning == 0 ? "High" : speed == 0 || cost == 0 ? "Low" : reasoning == 1 ? "Medium" : "Auto";
        var responseFormat = cost == 0 ? "Json" : "Text";
        var geminiMode = reasoning == 0 ? "On" : speed == 0 ? "Off" : "Balanced";
        var geminiBudget = reasoning == 0 ? 2048m : longContext == 0 ? 1024m : 0m;
        var anthropic = reasoning == 0 ? "Extended" : "Standard";

        return new CloudIntentPresetSettings(
            contextWindow,
            temperature,
            maxTokens,
            reasoningMode,
            responseFormat,
            effort,
            effort,
            geminiMode,
            geminiBudget,
            anthropic);
    }

    private CloudIntentPresetSettings ReconcileCloudSettingsWithPriorityOrder(
        IReadOnlyList<string> resolved,
        CloudIntentPresetSettings canonical)
    {
        int Rank(string name)
        {
            var index = IndexOfPriority(resolved, name);
            return index >= 0 ? index : resolved.Count;
        }

        var speed = Rank("Speed");
        var reasoning = Rank("Reasoning depth");
        var longContext = Rank("Context length");
        var reply = Rank("Reply length");
        var cost = Rank("Token Cost Economy");

        var contextWindow = CloudVariantContextWindow;
        var ctxInt = ParseInt(contextWindow, 0);
        if (longContext == 0 && (contextWindow.Equals("Auto", StringComparison.OrdinalIgnoreCase) || ctxInt < 65536))
            contextWindow = canonical.ContextWindow;
        else if (longContext <= 2 && contextWindow.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            contextWindow = "32768";
        else if (speed == 0 && ctxInt >= 98304)
            contextWindow = canonical.ContextWindow;

        var temperature = CloudVariantTemperature;
        if (reasoning == 0 && temperature > 0.5m)
            temperature = canonical.Temperature;
        else if (speed == 0 && reasoning > 0 && temperature <= 0.35m)
            temperature = canonical.Temperature;

        var maxTokens = CloudVariantMaxTokens;
        if (reply == 0 && maxTokens < canonical.MaxTokens)
        {
            maxTokens = canonical.MaxTokens;
        }

        var reasoningMode = CloudVariantReasoningMode;
        if (reasoning == 0)
            reasoningMode = "Deep";
        else if (speed == 0 && string.Equals(reasoningMode, "Deep", StringComparison.OrdinalIgnoreCase))
            reasoningMode = "Fast";

        var responseFormat = cost == 0 ? "Json" : string.Equals(CloudVariantResponseFormat, "Json", StringComparison.OrdinalIgnoreCase) && speed == 0
            ? "Text"
            : CloudVariantResponseFormat;

        var effort = CloudVariantOpenAiReasoningEffort ?? "Auto";
        if (reasoning == 0)
            effort = "High";
        else if (speed == 0 || cost == 0)
            effort = string.Equals(effort, "High", StringComparison.OrdinalIgnoreCase) ? "Low" : effort;

        var geminiMode = CloudVariantGeminiThinkingMode;
        var geminiBudget = CloudVariantGeminiThinkingBudget;
        if (reasoning == 0)
        {
            geminiMode = "On";
            if (geminiBudget < 1024)
                geminiBudget = canonical.GeminiThinkingBudget;
        }
        else if (speed == 0 && string.Equals(geminiMode, "On", StringComparison.OrdinalIgnoreCase))
        {
            geminiMode = "Off";
            geminiBudget = 0;
        }

        var anthropic = reasoning == 0 ? "Extended" : CloudVariantAnthropicThinkingMode;

        return new CloudIntentPresetSettings(
            contextWindow,
            temperature,
            maxTokens,
            reasoningMode,
            responseFormat,
            effort,
            effort,
            geminiMode,
            geminiBudget,
            anthropic);
    }

    private void UpdateCloudIntentSummaryTexts()
    {
        CloudVariantSettingsJson = SerializeJsonObject(BuildCloudVariantSettingsObject());
    }

    private JsonObject BuildCloudVariantSettingsObject()
    {
        return new JsonObject
        {
            ["CloudTemperature"] = CloudVariantTemperature,
            ["CloudMaxTokens"] = (int)CloudVariantMaxTokens,
            ["CloudContextWindow"] = PersistSettingChoice(CloudVariantContextWindow, "Provider default"),
            ["CloudReasoningMode"] = CloudVariantReasoningMode,
            ["CloudResponseFormat"] = CloudVariantResponseFormat,
            ["OpenAiReasoningEffort"] = PersistSettingChoice(CloudVariantOpenAiReasoningEffort, "Provider default"),
            ["CopilotReasoningEffort"] = PersistSettingChoice(CloudVariantCopilotReasoningEffort, "Provider default"),
            ["GeminiThinkingMode"] = CloudVariantGeminiThinkingMode,
            ["GeminiThinkingBudget"] = (int)CloudVariantGeminiThinkingBudget,
            ["AnthropicThinkingMode"] = CloudVariantAnthropicThinkingMode,
            ["PriorityOrder"] = BuildStringArray(CloudPriorityOrder)
        };
    }

    private void LoadLocalVariantSettingsForm(JsonObject settings)
    {
        _isHydratingLocalVariantForm = true;
        try
        {
        var advice = CurrentLocalHardwareAdvice();
        LocalVariantContext = ParseInt(settings["OverrideContext"]?.ToString() ?? string.Empty, advice.ContextSafeDefault);
        LocalVariantOverrideThreads = CoerceLocalOption(settings["OverrideThreads"]?.ToString(), LocalThreadOptions, DefaultLocalThreadCount());
        LocalVariantThreadsBatch = CoerceLocalOption(settings["LocalThreadsBatch"]?.ToString(), LocalThreadBatchOptions, DefaultLocalBatchThreadCount());
        LocalVariantTemperature = (decimal)ParseDouble(settings["LocalTemperature"]?.ToString() ?? string.Empty, 0.3);
        LocalVariantGpuOffloadMode = CoerceLocalOption(settings["LocalGpuOffloadMode"]?.ToString(), LocalGpuOffloadModeOptions, advice.OffloadMode);
        LocalVariantMultiGpuMode = CoerceLocalOption(settings["LocalMultiGpuMode"]?.ToString(), LocalMultiGpuModeOptions, "Auto");
        LocalVariantSplitMode = CoerceLocalOption(settings["LocalSplitMode"]?.ToString(), LocalSplitModeOptions, "Auto");
        LocalVariantMainGpu = CoerceLocalOption(settings["LocalMainGpu"]?.ToString(), LocalMainGpuOptions, "Auto");
        LocalVariantTensorSplit = string.IsNullOrWhiteSpace(settings["LocalTensorSplit"]?.ToString())
            ? "Auto"
            : settings["LocalTensorSplit"]!.ToString();
        var isDefaultLocal = string.Equals(SelectedLocalVariant, BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        LocalVariantGpuLayers = isDefaultLocal
            ? CoerceDefaultGpuLayers(settings["GpuLayers"]?.ToString(), advice)
            : CoerceLocalOptionKeepAuto(settings["GpuLayers"]?.ToString(), LocalGpuLayersOptions, advice.GpuLayers);
        var flashFallback = advice.FlashAttention.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? (advice.NvidiaAvailable ? "Enabled" : "Disabled")
            : advice.FlashAttention;
        LocalVariantFlashAttention = CoerceOnOff(settings["LocalFlashAttention"]?.ToString(), flashFallback);
        LocalVariantKvCacheTypeK = CoerceLocalOption(settings["LocalKvCacheTypeK"]?.ToString(), LocalKvCacheOptions, "q8_0");
        LocalVariantKvCacheTypeV = CoerceLocalOption(settings["LocalKvCacheTypeV"]?.ToString(), LocalKvCacheOptions, "q8_0");
        LocalVariantChatTemplate = CoerceLocalChatTemplate(settings["LocalChatTemplate"]?.ToString());
        LocalVariantMultiUserMode = CoerceOnOff(settings["LocalMultiUserMode"]?.ToString(), "Disabled");
        LocalVariantUnbanTokensMode = CoerceOnOff(settings["LocalUnbanTokensMode"]?.ToString(), LocalVariantChatTemplate.Equals("qwen", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled");
        LocalVariantAutoCompressEnabled = ParseBool(settings["AutoCompressEnabled"]?.ToString() ?? string.Empty, true);
        LocalVariantFit = CoerceOnOff(settings["LocalFit"]?.ToString(), advice.PreferFitEnabled ? "Enabled" : "Disabled");
        LocalVariantFit = LocalOnOffOptions.FirstOrDefault(option =>
            option.Equals(LocalVariantFit, StringComparison.OrdinalIgnoreCase)) ?? LocalVariantFit;
        LocalVariantFitEnabled = LocalVariantFit.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        LocalVariantMaxTokens = CoerceLocalOption(settings["OverrideMaxTokens"]?.ToString(), LocalMaxTokenOptions, advice.MaxTokens);
        LocalVariantMaxTokens = LocalMaxTokenOptions.FirstOrDefault(option =>
            option.Equals(LocalVariantMaxTokens, StringComparison.OrdinalIgnoreCase)) ?? LocalVariantMaxTokens;
        LocalVariantBatchSize = CoerceLocalOption(settings["LocalBatchSize"]?.ToString(), LocalBatchSizeOptions, advice.BatchSize);
        LocalVariantUbatchSize = CoerceLocalOption(settings["LocalUbatchSize"]?.ToString(), LocalUbatchSizeOptions, advice.UbatchSize);
        var specFallback = InferSpecTypeFromProfile();
        if (string.IsNullOrWhiteSpace(specFallback) || specFallback.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            specFallback = advice.SpecType;
        }

        LocalVariantSpecType = CoerceLocalOption(
            FluxMuxRuntimeService.NormalizeLlamaSpecType(settings["LocalSpecType"]?.ToString()),
            LocalSpecTypeOptions,
            specFallback);
        LocalVariantCacheReuse = CoerceLocalOption(settings["LocalCacheReuse"]?.ToString(), LocalCacheReuseOptions, "256");
        LocalVariantCacheRam = CoerceLocalOption(NormalizeCacheRamOption(settings["LocalCacheRam"]?.ToString() ?? string.Empty), LocalCacheRamOptions, advice.CacheRam);
        LocalVariantSwaFull = CoerceOnOff(settings["LocalSwaFull"]?.ToString(), advice.EnableSwaFull ? "Enabled" : "Disabled");
        LocalVariantReasoning = CoerceLocalOption(settings["LocalReasoning"]?.ToString(), LocalReasoningOptions, "Off");
        LocalVariantChatParser = CoerceLocalOption(settings["LocalChatParser"]?.ToString(), LocalChatParserOptions, "Jinja");
        var visionRec = CurrentLocalVisionRecommendation();
        var savedVision = CoerceOnOff(settings["LocalVisionEnabled"]?.ToString(), "Disabled");
        LocalVariantVisionEnabled = LocalOnOffOptions.FirstOrDefault(option =>
            option.Equals(savedVision, StringComparison.OrdinalIgnoreCase)) ?? savedVision;
        var savedProjector = settings["LocalVisionProjectorPath"]?.ToString() ?? string.Empty;
        LocalVariantVisionProjectorPath = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            ? FirstNonEmpty(savedProjector, visionRec.ProjectorPath)
            : savedProjector;
        LocalVariantVisionMaxImageEdge = ParseInt(settings["LocalVisionMaxImageEdge"]?.ToString() ?? string.Empty, 1344);
        _hydratedLocalVisionEnabled = LocalVariantVisionEnabled;
        _hydratedLocalVisionProjectorPath = LocalVariantVisionProjectorPath;
        RefreshLocalVisionHint();
        }
        finally
        {
            _isHydratingLocalVariantForm = false;
            RememberAcceptedLocalComboValues();
        }
        UpdateLocalPrioritySummaryTexts();
        EnqueueRestoreHydratedLocalVision();
    }

    [RelayCommand]
    private void MoveLocalPriorityUp()
    {
        MoveSelectedLocalPriority(-1);
    }

    [RelayCommand]
    private void MoveLocalPriorityDown()
    {
        MoveSelectedLocalPriority(1);
    }

    [RelayCommand]
    private async Task ApplyLocalPriorityWizard(CloudVariantTreeItemViewModel? profile)
    {
        if (_localAutoTuneRunning)
        {
            return;
        }

        var active = ActivateLocalAdvancedProfile(profile);
        if (!IsEditableLocalCopy(active))
        {
            LocalQuickAutoTuneInfoText =
                "This is the locked default. Unlock it to edit here, or Make New Variant for a copy, then run AutoTune.";
            ReportTuneProgress(
                "AutoTune did not change anything. It only runs on an editable model profile variant.");
            return;
        }

        _localAutoTuneRunning = true;
        var savedSnapshot = CloneSavedLocalVariantSettings();
        var keepResults = false;
        try
        {
            var profileLabel = active?.DisplayName ?? FirstNonEmpty(SelectedLocalVariant, BaseVariantDisplayName);
            ReportTuneProgress(LocalExperimentalHardwareTuneEnabled
                ? "AutoTune started on " + profileLabel + " (settings pass plus speed test)."
                : "AutoTune started on " + profileLabel + " (settings pass only).");
            LocalQuickAutoTuneInfoText = LocalExperimentalHardwareTuneEnabled
                ? "Updating settings, then timing short replies. Progress is in the diagnostics window."
                : "Updating settings from your priority list.";
            await FlushUiAsync();

            var preset = BuildLocalPriorityPresetSelection(LocalPriorityOrder);
            ApplyCanonicalLocalPrioritySettings(preset.Settings);

            LocalPriorityMinimalEffectNote = string.Empty;
            UpdateLocalPrioritySummaryTexts();
            keepResults = true;

            if (!LocalExperimentalHardwareTuneEnabled)
            {
                LocalQuickAutoTuneInfoText =
                    "AutoTune filled leftover GPU and context room from your priority list. Images, max tokens, Context, temperature, reasoning, and chat format you already set were left alone. **Save profile** to keep the new launch settings, or **Revert changes** to discard.";
                ReportTuneProgress("AutoTune finished the settings pass. **Save profile** to keep the launch settings, or **Revert changes** to discard. No speed test ran because the experimental box is unticked.");
                return;
            }

            await RunLocalMeasuredAutoTuneAsync();
        }
        finally
        {
            if (keepResults)
            {
                if (LocalExperimentalHardwareTuneEnabled)
                {
                    PersistLocalAutoTuneFormSettings();
                }
            }
            else
            {
                RestoreSavedLocalVariantSettings(savedSnapshot);
                LoadLocalVariantSettingsEditor();
            }

            _localAutoTuneRunning = false;
            ReassertLocalVariantEditor(active);
            UpdateProfileFootprintIndicators();
        }
    }

    [RelayCommand]
    private void InferLocalPrioritiesFromDetailedSettings()
    {
        SyncLocalPriorityOrderFromDetailedSettings(requireClearLead: false);
        LocalPriorityMinimalEffectNote = string.Empty;
        var orderText = string.Join(" > ", LocalPriorityOrder.Select((v, i) => $"{i + 1}. {v}"));
        LocalQuickAutoTuneInfoText =
            "Guessed priority order from the current settings: " + orderText +
            ". Larger conversation windows rank Context length higher; quicker-reply options rank Speed higher.";
        UpdateLocalPrioritySummaryTexts();
    }

    [RelayCommand]
    private void RunLocalQuickAutoTune()
    {
        var preset = BuildLocalPriorityPresetSelection(LocalPriorityOrder);
        ApplyCanonicalLocalPrioritySettings(preset.Settings);
        LocalQuickAutoTuneInfoText = "Applied a few extra launch tweaks that still match your priority order. Max tokens, temperature, reasoning, and chat format you already set were left alone. **Save profile** to keep the launch settings, or **Revert changes** to discard.";
        LocalPriorityMinimalEffectNote = string.Empty;
        UpdateLocalPrioritySummaryTexts();
    }

    private async Task RunLocalMeasuredAutoTuneAsync()
    {
        var active = SelectedLocalVariantItem;
        if (!IsEditableLocalCopy(active))
        {
            LocalQuickAutoTuneInfoText =
                "This is the locked default. Unlock it to edit here, or Make New Variant for a copy, then run AutoTune.";
            ReportTuneProgress("Speed test did not run. AutoTune only runs on an editable model profile variant.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cancellationToken = timeout.Token;

        if (LocalRouteIndicatorState is RouteIndicatorState.Off or RouteIndicatorState.Fault)
        {
            LocalQuickAutoTuneInfoText = "Launching this profile, then timing short replies. Progress is in the diagnostics window.";
            ReportTuneProgress("This profile is not running yet. Launching it for the speed test...");
            await FlushUiAsync();
            if (!await RestartLocalForMeasuredAutoTuneAsync(cancellationToken))
            {
                var remediation = BuildLocalTuneRemediation(["the model did not finish loading"]);
                LocalQuickAutoTuneInfoText = "AutoTune could not start this profile, so the speed test did not run. " + remediation;
                ReportTuneProgress("Could not start this profile. " + remediation);
                return;
            }
        }

        var trials = BuildLocalMeasuredAutoTuneTrials();
        Dictionary<string, string>? winner = null;
        var winnerMs = long.MaxValue;
        long? settingsPassMs = null;
        var notes = new List<string>();
        ReportTuneProgress(
            "Speed test starting on " + (SelectedLocalVariantItem?.DisplayName ?? SelectedLocalVariant) +
            ": " + trials.Count + " timed trial(s). The model may reload.");
        LocalQuickAutoTuneInfoText = "Timing short replies. Progress is in the diagnostics window.";
        await FlushUiAsync();

        try
        {
            var trialNumber = 0;
            foreach (var trial in trials)
            {
                trialNumber++;
                ReportTuneProgress("Trial " + trialNumber + " of " + trials.Count + ": " + trial.Label + ". " + trial.SettingUnderTest);
                LocalQuickAutoTuneInfoText =
                    "Trial " + trialNumber + " of " + trials.Count + ": " + trial.Label + ".";
                await FlushUiAsync();
                _isApplyingLocalPriorityWizard = true;
                try
                {
                    ApplyLocalPriorityPresetToDetailedSettings(trial.Settings);
                }
                finally
                {
                    _isApplyingLocalPriorityWizard = false;
                }

                if (!TryPersistCurrentLocalVariantSettings())
                {
                    notes.Add(trial.Label + ": could not store settings for reload.");
                    ReportTuneProgress("Trial " + trialNumber + " stopped: could not store settings for reload.");
                    break;
                }

                ReportTuneProgress("Trial " + trialNumber + ": reloading the local model.");
                LocalQuickAutoTuneInfoText = "Trial " + trialNumber + " of " + trials.Count + ": reloading the model. Progress is in the diagnostics window.";
                await FlushUiAsync();
                if (!await RestartLocalForMeasuredAutoTuneAsync(cancellationToken))
                {
                    notes.Add(trial.Label + ": the model did not finish loading after reload.");
                    ReportTuneProgress("Trial " + trialNumber + " failed: the model did not finish loading after reload.");
                    continue;
                }

                ReportTuneProgress("Trial " + trialNumber + ": sending a short test prompt.");
                LocalQuickAutoTuneInfoText = "Trial " + trialNumber + " of " + trials.Count + ": waiting for a short test reply.";
                await FlushUiAsync();
                var timing = await TimeShortLocalReplyWithProgressAsync(
                    "Trial " + trialNumber + ":",
                    cancellationToken);
                if (!timing.IsSuccess)
                {
                    notes.Add(trial.Label + ": the short reply did not complete.");
                    ReportTuneProgress("Trial " + trialNumber + " failed: " + timing.Details);
                    continue;
                }

                notes.Add(trial.Label + " took about " + FormatFriendlyDurationMs(timing.LatencyMs));
                ReportTuneProgress("Trial " + trialNumber + " finished in about " + FormatFriendlyDurationMs(timing.LatencyMs) + ".");
                if (trial.Label.Equals("usual AutoTune settings", StringComparison.OrdinalIgnoreCase))
                {
                    settingsPassMs = timing.LatencyMs;
                }

                if (timing.LatencyMs < winnerMs)
                {
                    winnerMs = timing.LatencyMs;
                    winner = new Dictionary<string, string>(trial.Settings, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (OperationCanceledException)
        {
            var remediation = BuildLocalTuneRemediation(["timed out"]);
            LocalQuickAutoTuneInfoText = "The speed test stopped because it took too long. Settings so far have been applied. " + remediation;
            ReportTuneProgress("Speed test stopped because it took too long. " + remediation);
            return;
        }

        if (winner is not null)
        {
            _isApplyingLocalPriorityWizard = true;
            try
            {
                ApplyLocalPriorityPresetToDetailedSettings(winner);
            }
            finally
            {
                _isApplyingLocalPriorityWizard = false;
                RefreshProfileEndpointStatusSurfaces();
            }

            var summary = BuildLocalTuneResultSummary(winnerMs, settingsPassMs, extraTrialsRan: trials.Count > 1);
            LocalQuickAutoTuneInfoText = summary;
            ReportTuneProgress(summary);
        }
        else
        {
            var remediation = BuildLocalTuneRemediation(notes);
            LocalQuickAutoTuneInfoText =
                "Settings were applied, but no timed reply succeeded. " + string.Join(" ", notes) + " " + remediation;
            ReportTuneProgress("Speed test finished without a successful short reply. " + string.Join(" ", notes) + " " + remediation);
        }

        UpdateLocalPrioritySummaryTexts();
    }

    private List<LocalMeasuredTuneTrial> BuildLocalMeasuredAutoTuneTrials()
    {
        var baseline = CaptureLocalDetailedSettings();
        var trials = new List<LocalMeasuredTuneTrial>
        {
            new(
                "usual AutoTune settings",
                new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase),
                "No extra change: times the usual AutoTune mix already in the form.")
        };

        var order = ResolveLocalPriorityOrder(LocalPriorityOrder);
        var speedRank = IndexOfPriority(order, "Speed");
        var contextRank = IndexOfPriority(order, "Context length");
        var thinkingRank = IndexOfPriority(order, "Fidelity");

        if (speedRank <= 1
            && contextRank > 0)
        {
            var largerBatch = ScaleWizardBatchUp(CurrentLocalHardwareAdvice().BatchSize);
            if (!string.Equals(baseline.GetValueOrDefault("LocalBatchSize"), largerBatch, StringComparison.OrdinalIgnoreCase))
            {
                var extra = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase)
                {
                    ["LocalBatchSize"] = largerBatch,
                    ["LocalUbatchSize"] = DeriveUbatch(largerBatch)
                };

                trials.Add(new(
                    "larger Batch size",
                    extra,
                    "Testing Batch size " + extra["LocalBatchSize"] +
                    " (prompt processed in larger chunks), was " + (baseline.GetValueOrDefault("LocalBatchSize") ?? "unset") +
                    ". Micro-batch " + extra.GetValueOrDefault("LocalUbatchSize") + "."));
            }
        }

        if (thinkingRank > 0
            && !string.Equals(baseline.GetValueOrDefault("LocalSpecType"), "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            var currentSpec = FluxMuxRuntimeService.NormalizeLlamaSpecType(
                baseline.GetValueOrDefault("LocalSpecType") ?? InferSpecTypeFromProfile());
            if (string.IsNullOrWhiteSpace(currentSpec)
                || currentSpec.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                currentSpec = InferSpecTypeFromProfile();
            }

            var alternateSpec = currentSpec.StartsWith("ngram", StringComparison.OrdinalIgnoreCase)
                ? "draft-mtp"
                : "ngram-simple";
            if (!alternateSpec.Equals(currentSpec, StringComparison.OrdinalIgnoreCase))
            {
                var extra = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase)
                {
                    ["LocalSpecType"] = alternateSpec
                };
                trials.Add(new(
                    "other Speculative method",
                    extra,
                    "Testing Speculative " + alternateSpec + " instead of " + currentSpec +
                    " (the guess-ahead / draft method)."));
            }
        }

        return trials.Take(3).ToList();
    }

    private Dictionary<string, string> CaptureLocalDetailedSettings()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverrideContext"] = ((int)LocalVariantContext).ToString(CultureInfo.InvariantCulture),
            ["OverrideThreads"] = LocalVariantOverrideThreads,
            ["LocalThreadsBatch"] = LocalVariantThreadsBatch,
            ["LocalTemperature"] = LocalVariantTemperature.ToString(CultureInfo.InvariantCulture),
            ["LocalGpuOffloadMode"] = LocalVariantGpuOffloadMode,
            ["LocalMultiGpuMode"] = LocalVariantMultiGpuMode,
            ["LocalSplitMode"] = LocalVariantSplitMode,
            ["LocalMainGpu"] = LocalVariantMainGpu,
            ["LocalTensorSplit"] = LocalVariantTensorSplit,
            ["GpuLayers"] = PersistSettingChoice(LocalVariantGpuLayers, "fit"),
            ["LocalFlashAttention"] = LocalVariantFlashAttention,
            ["LocalKvCacheTypeK"] = LocalVariantKvCacheTypeK,
            ["LocalKvCacheTypeV"] = LocalVariantKvCacheTypeV,
            ["LocalChatTemplate"] = SanitizeLocalChatTemplate(LocalVariantChatTemplate),
            ["LocalMultiUserMode"] = LocalVariantMultiUserMode,
            ["LocalUnbanTokensMode"] = LocalVariantUnbanTokensMode,
            ["AutoCompressEnabled"] = LocalVariantAutoCompressEnabled.ToString(),
            ["OverrideMaxTokens"] = LocalVariantMaxTokens,
            ["LocalBatchSize"] = LocalVariantBatchSize,
            ["LocalUbatchSize"] = LocalVariantUbatchSize,
            ["LocalSpecType"] = LocalVariantSpecType,
            ["LocalCacheReuse"] = LocalVariantCacheReuse,
            ["LocalCacheRam"] = LocalVariantCacheRam,
            ["LocalFit"] = LocalVariantFit,
            ["LocalSwaFull"] = LocalVariantSwaFull,
            ["LocalReasoning"] = LocalVariantReasoning,
            ["LocalChatParser"] = LocalVariantChatParser,
            ["LocalVisionEnabled"] = LocalVariantVisionEnabled,
            ["LocalVisionProjectorPath"] = LocalVariantVisionProjectorPath,
            ["LocalVisionMaxImageEdge"] = ((int)LocalVariantVisionMaxImageEdge).ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task<bool> RestartLocalForMeasuredAutoTuneAsync(CancellationToken cancellationToken)
    {
        var model = FirstNonEmpty(SelectedLocalProfile, SelectedSelection1LocalProfile);
        var variant = FirstNonEmpty(SelectedLocalVariant, FirstNonEmpty(SelectedSelection1LocalVariant, BaseVariantDisplayName));
        ReportTuneProgress("Reloading the local model with the trial settings...");
        await FlushUiAsync();
        var vision = GetLocalVisionLaunchSettings(model, variant);
        var result = await _runtimeService.RestartLocalAsync(
            model,
            variant,
            LocalModelDirectory,
            (int)OrchestratorPort,
            vision.Enabled,
            vision.ProjectorPath,
            vision.MaxImageEdge,
            cancellationToken,
            progressReporter: ReportTuneProgress);
        _lastHealthDetail = result.Details;
        ConnectionHealthText = result.Details;
        LocalRouteIndicatorState = result.IsSuccess ? RouteIndicatorState.Live : RouteIndicatorState.Fault;
        if (!result.IsSuccess)
        {
            ReportTuneProgress("Reload did not succeed: " + result.Status + ". " + result.Details);
            return false;
        }

        if (result.IsLocalRouteReady
            || result.Details.Contains("llama-server is ready", StringComparison.OrdinalIgnoreCase))
        {
            ReportTuneProgress("Local model finished loading in a new process. " + result.Details);
            return true;
        }

        ReportTuneProgress("Reload requested. Waiting for the local model to become ready...");
        return await WaitForLocalEndpointReadyAsync(cancellationToken);
    }

    private async Task<bool> WaitForLocalEndpointReadyAsync(CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var health = await _runtimeService.CheckLocalHealthAsync((int)OrchestratorPort, cancellationToken);
            if (health.Details.Contains("responding", StringComparison.OrdinalIgnoreCase)
                || health.Status.Contains("health check passed", StringComparison.OrdinalIgnoreCase)
                || health.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase))
            {
                LocalRouteIndicatorState = RouteIndicatorState.Live;
                ReportTuneProgress("Local model is ready after about " + FormatFriendlyDurationMs((long)(DateTime.UtcNow - startedUtc).TotalMilliseconds) + ".");
                return true;
            }

            var elapsedSeconds = Math.Max(0, (int)(DateTime.UtcNow - startedUtc).TotalSeconds);
            if (attempt == 0 || attempt % 5 == 0)
            {
                ReportTuneProgress("Waiting for the local model to finish loading (" + elapsedSeconds + "s). " + health.Status);
                await FlushUiAsync();
            }

            await Task.Delay(2000, cancellationToken);
        }

        ReportTuneProgress("Timed out waiting for the local model to become ready.");
        return false;
    }

    private static string FormatFriendlyDurationMs(long milliseconds)
    {
        if (milliseconds < 1000)
        {
            return milliseconds + " milliseconds";
        }

        var seconds = milliseconds / 1000.0;
        return seconds < 10
            ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + " seconds"
            : ((int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + " seconds";
    }

    private sealed record LocalMeasuredTuneTrial(string Label, Dictionary<string, string> Settings, string SettingUnderTest);

    private void MoveSelectedLocalPriority(int delta)
    {
        if (string.IsNullOrWhiteSpace(SelectedLocalPriority))
        {
            return;
        }

        var currentIndex = LocalPriorityOrder.IndexOf(SelectedLocalPriority);
        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= LocalPriorityOrder.Count)
        {
            return;
        }

        var beforeOrder = LocalPriorityOrder.ToList();
        var beforeOutcome = BuildLocalPriorityPresetSelection(beforeOrder).PresetName;
        var firstLabel = $"{currentIndex + 1}. {LocalPriorityOrder[currentIndex]}";
        var secondLabel = $"{targetIndex + 1}. {LocalPriorityOrder[targetIndex]}";

        LocalPriorityOrder.Move(currentIndex, targetIndex);
        SelectedLocalPriority = LocalPriorityOrder[targetIndex];
        var afterOutcome = BuildLocalPriorityPresetSelection(LocalPriorityOrder).PresetName;
        LocalPriorityMinimalEffectNote = string.Equals(beforeOutcome, afterOutcome, StringComparison.OrdinalIgnoreCase)
            ? $"No effective gains/losses compared with previous order: swapping {firstLabel} and {secondLabel} currently leaves the chosen preset unchanged."
            : string.Empty;
        ApplyLocalPriorityOrderToDetailedSettings();
    }

    private void SetLocalPriorityOrder(IEnumerable<string> priorityOrder, bool persistToConfig)
    {
        var resolved = ResolveLocalPriorityOrder(priorityOrder).ToList();
        if (!resolved.SequenceEqual(LocalPriorityOrder))
        {
            LocalPriorityOrder.Clear();
            foreach (var priority in resolved)
                LocalPriorityOrder.Add(priority);
        }

        if (!LocalPriorityOrder.Contains(SelectedLocalPriority))
            SelectedLocalPriority = LocalPriorityOrder.FirstOrDefault() ?? "Stability";
        if (persistToConfig)
        {
            _config.SetStringArray("LocalPriorityOrder", LocalPriorityOrder);
        }

        LocalPriorityMinimalEffectNote = string.Empty;

        UpdateLocalPrioritySummaryTexts();
    }

    private void SyncLocalPriorityOrderFromDetailedSettings(bool requireClearLead = true)
    {
        var scores = ComputeLocalPriorityScores();
        var inferred = RankPrioritiesByScore(LocalPriorityNames, scores);
        if (inferred.Count == 0)
        {
            return;
        }

        if (!requireClearLead || ShouldAdoptInferredPriorityOrder(LocalPriorityOrder, inferred, scores, margin: 3))
        {
            SetLocalPriorityOrder(inferred, persistToConfig: false);
        }
    }

    public void ReorderLocalPriority(string draggedPriority, string targetPriority)
    {
        if (string.IsNullOrWhiteSpace(draggedPriority) || string.IsNullOrWhiteSpace(targetPriority) || draggedPriority == targetPriority)
        {
            return;
        }

        var fromIndex = LocalPriorityOrder.IndexOf(draggedPriority);
        var toIndex = LocalPriorityOrder.IndexOf(targetPriority);
        if (fromIndex < 0 || toIndex < 0)
        {
            return;
        }

        LocalPriorityOrder.Move(fromIndex, toIndex);
        SelectedLocalPriority = draggedPriority;
        ApplyLocalPriorityOrderToDetailedSettings();
    }

    private void ApplyLocalPriorityOrderToDetailedSettings()
    {
        var preset = BuildLocalPriorityPresetSelection(LocalPriorityOrder);
        ApplyCanonicalLocalPrioritySettings(preset.Settings);
        LocalQuickAutoTuneInfoText = "Updated detailed settings to match your new priority order.";
        LocalPriorityMinimalEffectNote = string.Empty;
        UpdateLocalPrioritySummaryTexts();
        UpdateProfileFootprintIndicators();
    }

    private void ApplyCanonicalLocalPrioritySettings(IReadOnlyDictionary<string, string> canonical)
    {
        var current = CaptureCurrentLocalDetailedSettings();
        var settings = new Dictionary<string, string>(canonical, StringComparer.OrdinalIgnoreCase)
        {
            ["LocalVisionEnabled"] = LocalVariantVisionEnabled,
            ["LocalVisionProjectorPath"] = LocalVariantVisionProjectorPath,
            ["LocalVisionMaxImageEdge"] = ((int)LocalVariantVisionMaxImageEdge).ToString(CultureInfo.InvariantCulture)
        };
        ResolveWizardAutoSettings(settings);
        settings["LocalKvCacheTypeK"] = LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum(
            settings.GetValueOrDefault("LocalKvCacheTypeK"));
        settings["LocalKvCacheTypeV"] = LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum(
            settings.GetValueOrDefault("LocalKvCacheTypeV", settings["LocalKvCacheTypeK"]));
        var imagesOn = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        LocalPrioritySettingsCalculator.PreserveHandTunedReplySettings(
            settings,
            current,
            imagesOn ? CurrentLocalHardwareAdvice().ContextPriorityFirst : 0);
        _isApplyingLocalPriorityWizard = true;
        try
        {
            ApplyLocalPriorityPresetToDetailedSettings(settings);
        }
        finally
        {
            _isApplyingLocalPriorityWizard = false;
            RefreshProfileEndpointStatusSurfaces();
            UpdateProfileFootprintIndicators();
        }
    }

    private IReadOnlyList<string> BuildInferredPriorityOrderFromDetailedSettings()
    {
        var scores = ComputeLocalPriorityScores();
        return RankPrioritiesByScore(LocalPriorityNames, scores);
    }

    private static IReadOnlyList<string> RankPrioritiesByScore(IReadOnlyList<string> baseOrder, IReadOnlyDictionary<string, int> scores) =>
        baseOrder
            .OrderByDescending(priority => scores.TryGetValue(priority, out var score) ? score : 0)
            .ThenBy(priority => Array.IndexOf(baseOrder.ToArray(), priority))
            .ToList();

    private static bool ShouldAdoptInferredPriorityOrder(
        IReadOnlyList<string> current,
        IReadOnlyList<string> inferred,
        IReadOnlyDictionary<string, int> scores,
        int margin)
    {
        if (inferred.Count == 0 || current.SequenceEqual(inferred, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        int Score(string name) => scores.TryGetValue(name, out var value) ? value : 0;

        if (!inferred[0].Equals(current[0], StringComparison.OrdinalIgnoreCase))
        {
            return Score(inferred[0]) >= Score(current[0]) + margin;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var inferredIndex = IndexOfPriority(inferred, current[i]);
            if (inferredIndex < 0 || inferredIndex == i)
            {
                continue;
            }

            var displaced = current[i];
            var occupant = inferred[i];
            if (Score(occupant) >= Score(displaced) + margin)
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfPriority(IReadOnlyList<string> order, string priority)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Equals(priority, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private Dictionary<string, int> ComputeLocalPriorityScores()
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stability"] = 0,
            ["Speed"] = 0,
            ["Fidelity"] = 0,
            ["Context length"] = 0,
            ["Reply length"] = 0,
            ["Reasoning depth"] = 0
        };

        var ctx = (int)LocalVariantContext;
        var advice = CurrentLocalHardwareAdvice();
        var first = Math.Max(4096, advice.ContextPriorityFirst);
        var ratio = ctx / (double)first;
        if (ratio >= 0.90)
        {
            scores["Context length"] += 18;
            scores["Speed"] -= 8;
        }
        else if (ratio >= 0.67)
        {
            scores["Context length"] += 14;
            scores["Speed"] -= 4;
        }
        else if (ratio >= 0.45)
        {
            scores["Context length"] += 10;
            scores["Speed"] -= 2;
        }
        else if (ratio >= 0.28)
        {
            scores["Context length"] += 6;
        }
        else if (ratio <= 0.20)
        {
            scores["Speed"] += 5;
            scores["Stability"] += 1;
        }
        else
        {
            scores["Speed"] += 1;
        }

        if (LocalVariantTemperature <= 0.2m)
        {
            scores["Fidelity"] += 4;
            scores["Stability"] += 1;
        }
        else if (LocalVariantTemperature <= 0.3m)
        {
            scores["Fidelity"] += 2;
            scores["Stability"] += 1;
        }
        else
        {
            scores["Speed"] += 2;
        }

        switch (LocalVariantGpuOffloadMode)
        {
            case "GPU only":
                scores["Speed"] += 4;
                break;
            case "GPU + CPU":
                scores["Stability"] += 2;
                scores["Context length"] += 1;
                break;
            case "CPU only":
                scores["Speed"] -= 6;
                scores["Stability"] += 4;
                scores["Context length"] += 3;
                break;
        }

        var threadTarget = Math.Clamp(Environment.ProcessorCount / 2, 4, 32);
        if (int.TryParse(LocalVariantOverrideThreads, out var configuredThreads))
        {
            var distance = Math.Abs(configuredThreads - threadTarget);
            if (distance <= 2)
            {
                scores["Speed"] += 1;
                scores["Stability"] += 1;
            }
            else if (configuredThreads > threadTarget)
            {
                scores["Stability"] -= 1;
            }
            else
            {
                scores["Stability"] += 1;
            }
        }

        var batchThreadTarget = Math.Max(1, threadTarget / 2);
        if (int.TryParse(LocalVariantThreadsBatch, out var configuredBatchThreads) &&
            Math.Abs(configuredBatchThreads - batchThreadTarget) <= 2)
        {
            scores["Speed"] += 1;
        }

        static int KvTypeScore(string t) => (t ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "q4_0" or "q4_1" => 0,
            "q5_0" or "q5_1" or "q6_k" => 1,
            "q8_0" or "q8_1" or "q8_k" => 2,
            "f16" or "f32" or "bf16" => 3,
            _ => 2
        };
        var kvAvg = (KvTypeScore(LocalVariantKvCacheTypeK) + KvTypeScore(LocalVariantKvCacheTypeV)) / 2.0;
        if (kvAvg >= 2.5)
        {
            scores["Fidelity"] += 3;
            scores["Stability"] += 1;
        }
        else if (kvAvg >= 1.5)
        {
            scores["Stability"] += 2;
            scores["Fidelity"] += 1;
        }
        else if (kvAvg >= 0.5)
        {
            scores["Context length"] += 3;
        }
        else
        {
            scores["Context length"] += 4;
        }

        if (LocalVariantFlashAttention == "Enabled")
        {
            scores["Context length"] += 1;
            scores["Speed"] += 1;
        }
        else if (LocalVariantFlashAttention == "Disabled")
        {
            scores["Stability"] += 1;
        }

        if (int.TryParse(LocalVariantGpuLayers, out var configuredGpuLayers))
        {
            if (configuredGpuLayers >= 999)
                scores["Speed"] += 2;
            else if (configuredGpuLayers <= 20)
                scores["Context length"] += 2;
        }

        if (LocalVariantMultiUserMode == "Enabled")
        {
            scores["Stability"] += 1;
            scores["Speed"] -= 1;
        }

        if (LocalVariantUnbanTokensMode == "Enabled" &&
            LocalVariantChatTemplate.Equals("qwen", StringComparison.OrdinalIgnoreCase))
        {
            scores["Fidelity"] += 1;
            scores["Stability"] += 1;
        }

        if (LocalVariantSpecType.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            scores["Fidelity"] += 3;
        }
        else
        {
            scores["Speed"] += 1;
        }

        if (int.TryParse(LocalVariantCacheReuse, out var cacheReuse) && cacheReuse >= 256)
            scores["Context length"] += 2;

        if (LocalVariantCacheRam.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)
            || LocalVariantCacheRam.Equals(advice.CacheRam, StringComparison.OrdinalIgnoreCase))
            scores["Context length"] += 3;
        else if (LocalVariantCacheRam.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            scores["Stability"] += 1;
            scores["Speed"] -= 1;
        }

        if (LocalVariantFit == "Enabled")
            scores["Stability"] += 3;
        else
        {
            scores["Context length"] += 2;
            scores["Fidelity"] += 1;
        }

        if (LocalVariantSwaFull == "Enabled")
            scores["Context length"] += 3;

        if (LocalVariantReasoning == "On")
        {
            scores["Reasoning depth"] += 6;
            scores["Speed"] -= 2;
        }
        else
        {
            scores["Stability"] += 1;
        }

        if (int.TryParse(LocalVariantMaxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTok))
        {
            if (maxTok >= 16384)
            {
                scores["Reply length"] += 8;
            }
            else if (maxTok >= 8192)
            {
                scores["Reply length"] += 5;
            }
            else if (maxTok >= 4096)
            {
                scores["Reply length"] += 2;
            }
        }

        if (int.TryParse(LocalVariantBatchSize, out var batchSize))
        {
            if (batchSize >= 2048)
                scores["Speed"] += 2;
            else if (batchSize <= 512)
            {
                scores["Context length"] += 2;
                scores["Stability"] += 1;
            }
        }

        if (LocalVariantChatParser.Equals("Skip parsing", StringComparison.OrdinalIgnoreCase) ||
            LocalVariantChatParser.Equals("No Jinja", StringComparison.OrdinalIgnoreCase))
        {
            scores["Stability"] += 2;
            scores["Reasoning depth"] -= 1;
        }

        return scores;
    }

    private LocalPriorityPreset BuildLocalPriorityPresetSelection(IEnumerable<string> priorityOrder)
    {
        var resolved = ResolveLocalPriorityOrder(priorityOrder);
        var settings = CalculateLocalSettingsFromPriorityOrder(resolved);
        ResolveWizardAutoSettings(settings);
        var primary = resolved[0];
        var name = primary switch
        {
            "Speed" => "Local Speed",
            "Fidelity" => "Local Fidelity",
            "Reasoning depth" => "Local Reasoning depth",
            "Context length" => "Local Context length",
            "Reply length" => "Local Reply length",
            _ => "Local Stability"
        };
        var gains = primary switch
        {
            "Speed" => "Quicker tokens; more risk of sloppy answers and a smaller window.",
            "Fidelity" => "Closer recall of the prompt and earlier turns; speculative speed yields. Reasoning stays off unless Reasoning depth is first.",
            "Reasoning depth" => "Hidden reasoning before the answer. Often little help for local coding turns; moderately useful for architectural strategy talk. Slower, uses VRAM in the context window, and consumes reply budget.",
            "Context length" => "More of the prompt and chat stay in view. Uses more VRAM. A shorter rank means background or requirements are forgotten sooner. Does not set how long one answer can be.",
            "Reply length" => "Longer single answers. A short rank raises the risk of truncated code or other output. Context window stays with Context length.",
            _ => "Fewer failed launches and GPU stalls; speed and window yield if they fight VRAM."
        };
        return new LocalPriorityPreset(name, settings, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0, gains, "Lower ranks still contribute cost-free helpers.");
    }

    private Dictionary<string, string> CalculateLocalSettingsFromPriorityOrder(IReadOnlyList<string> resolved)
    {
        var advice = CurrentLocalHardwareAdvice();
        var specType = InferSpecTypeFromProfile();
        if (string.IsNullOrWhiteSpace(specType) || specType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            specType = advice.SpecType;
        }

        return LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            FluxMuxRuntimeService.RecommendStoredChatTemplate(SelectedLocalProfile),
            specType,
            LocalVariantSwaFull,
            LocalVariantAutoCompressEnabled,
            LocalVariantVisionEnabled,
            LocalVariantVisionProjectorPath,
            ((int)LocalVariantVisionMaxImageEdge).ToString(CultureInfo.InvariantCulture),
            DefaultLocalThreadCount(),
            DefaultLocalBatchThreadCount(),
            LocalVariantMaxTokens,
            ((int)LocalVariantContext).ToString(CultureInfo.InvariantCulture));
    }

    private Dictionary<string, string> ReconcileLocalSettingsWithPriorityOrder(
        IReadOnlyList<string> resolved,
        IReadOnlyDictionary<string, string> canonical)
    {
        var current = CaptureCurrentLocalDetailedSettings();
        var merged = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        int Rank(string name)
        {
            var index = IndexOfPriority(resolved, name);
            return index >= 0 ? index : resolved.Count;
        }
        var speed = Rank("Speed");
        var fidelity = Rank("Fidelity");
        var thinking = Rank("Reasoning depth");
        var context = Rank("Context length");
        var stability = Rank("Stability");
        var advice = CurrentLocalHardwareAdvice();

        static int KvFidelity(string t) => t switch
        {
            "f16" or "f32" or "bf16" => 3,
            "q8_0" or "q8_1" or "q8_k" => 2,
            "q6_k" => 1,
            "q5_0" or "q5_1" => 1,
            _ => 0
        };

        void Take(string key)
        {
            if (canonical.TryGetValue(key, out var value))
                merged[key] = value;
        }

        merged["LocalFlashAttention"] = advice.FlashAttention.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? current.GetValueOrDefault("LocalFlashAttention", advice.FlashAttention)
            : advice.FlashAttention;
        merged["LocalSwaFull"] = advice.EnableSwaFull ? "Enabled" : current.GetValueOrDefault("LocalSwaFull", "Disabled");
        Take("OverrideThreads");
        Take("LocalThreadsBatch");

        var reuse = ParseInt(current.GetValueOrDefault("LocalCacheReuse"), 0);
        if (reuse < 256)
            merged["LocalCacheReuse"] = "256";

        var template = current.GetValueOrDefault("LocalChatTemplate", canonical["LocalChatTemplate"]);
        merged["LocalChatTemplate"] = SanitizeLocalChatTemplate(
            string.IsNullOrWhiteSpace(template) ? canonical["LocalChatTemplate"] : template);
        if (merged["LocalChatTemplate"].Equals("qwen", StringComparison.OrdinalIgnoreCase) &&
            !current.GetValueOrDefault("LocalUnbanTokensMode", "").Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            merged["LocalUnbanTokensMode"] = "Enabled";
        }

        merged["AutoCompressEnabled"] = current.GetValueOrDefault("AutoCompressEnabled", canonical["AutoCompressEnabled"]);

        var currentCtx = ParseInt(current.GetValueOrDefault("OverrideContext"), 0);
        var wizardCtx = ParseInt(canonical.GetValueOrDefault("OverrideContext"), 0);
        var imagesOn = current.GetValueOrDefault("LocalVisionEnabled", "")
            .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        if (imagesOn && currentCtx > advice.ContextPriorityFirst)
            Take("OverrideContext");
        else if (currentCtx <= 0 || wizardCtx > currentCtx)
            Take("OverrideContext");

        var gpu = current.GetValueOrDefault("LocalGpuOffloadMode", "");
        if (context == 0)
        {
            merged["LocalGpuOffloadMode"] = advice.OffloadModeAtMaxContext;
            gpu = advice.OffloadModeAtMaxContext;
            Take("GpuLayers");
            if (gpu.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase))
            {
                merged["GpuLayers"] = "Auto";
            }
        }
        else if (gpu.Equals("CPU only", StringComparison.OrdinalIgnoreCase))
            Take("LocalGpuOffloadMode");
        gpu = merged.GetValueOrDefault("LocalGpuOffloadMode", gpu);
        if (context != 0
            && gpu.Equals("GPU only", StringComparison.OrdinalIgnoreCase)
            && !advice.OffloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase))
        {
            Take("LocalGpuOffloadMode");
            Take("GpuLayers");
            gpu = merged.GetValueOrDefault("LocalGpuOffloadMode", gpu);
        }
        if (gpu.Equals("GPU only", StringComparison.OrdinalIgnoreCase))
            merged["GpuLayers"] = "999";
        else if (gpu.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase)
                 && (advice.GpuLayers.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                     || ParseInt(current.GetValueOrDefault("GpuLayers"), 0) <= 0))
        {
            merged["GpuLayers"] = advice.GpuLayers;
        }

        if (speed == 0 || context == 0)
            merged["LocalMultiUserMode"] = "Disabled";

        var kvK = current.GetValueOrDefault("LocalKvCacheTypeK", LocalPrioritySettingsCalculator.MinimumKvCacheType);
        var kvV = current.GetValueOrDefault("LocalKvCacheTypeV", kvK);
        if (fidelity == 0 && KvFidelity(kvK) < 2)
        {
            Take("LocalKvCacheTypeK");
            kvK = merged["LocalKvCacheTypeK"];
        }
        else if (stability == 0 && KvFidelity(kvK) < 1)
        {
            Take("LocalKvCacheTypeK");
            kvK = merged["LocalKvCacheTypeK"];
        }

        // Priorities wizard never keeps KV below q8_0 — fidelity loss outweighs Context gain.
        kvK = LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum(kvK);
        kvV = LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum(kvV);
        merged["LocalKvCacheTypeK"] = kvK;
        if (!kvV.Equals(kvK, StringComparison.OrdinalIgnoreCase) || (fidelity == 0 && KvFidelity(kvV) < 2))
            merged["LocalKvCacheTypeV"] = kvK;
        else
            merged["LocalKvCacheTypeV"] = kvV;

        var temp = (decimal)ParseDouble(current.GetValueOrDefault("LocalTemperature"), (double)LocalVariantTemperature);
        if (fidelity == 0 && temp > 0.35m)
            Take("LocalTemperature");
        else if (speed == 0 && fidelity > 0 && temp <= 0.2m)
            Take("LocalTemperature");

        if (fidelity == 0)
            merged["LocalSpecType"] = "Disabled";
        else if (current.GetValueOrDefault("LocalSpecType", "").Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            Take("LocalSpecType");

        merged["LocalReasoning"] = thinking == 0 ? "On" : "Off";

        var maxTok = ParseInt(current.GetValueOrDefault("OverrideMaxTokens"), 2048);
        var wizardTok = ParseInt(canonical.GetValueOrDefault("OverrideMaxTokens"), 2048);
        if (maxTok < wizardTok)
        {
            Take("OverrideMaxTokens");
        }

        var fit = current.GetValueOrDefault("LocalFit", "Enabled");
        if (string.IsNullOrWhiteSpace(fit)
            || fit.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            merged["LocalFit"] = advice.PreferFitEnabled ? "Enabled" : "Disabled";
        }
        else
        {
            merged["LocalFit"] = fit;
        }

        var cacheRam = current.GetValueOrDefault("LocalCacheRam", advice.CacheRam);
        if (context == 0)
        {
            Take("LocalCacheRam");
        }
        else if (cacheRam.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            merged["LocalCacheRam"] = advice.CacheRam;
        }

        var batch = ParseInt(current.GetValueOrDefault("LocalBatchSize"), ParseInt(advice.BatchSize, 512));
        if (context == 0 && batch > ParseInt(advice.BatchSize, batch))
        {
            Take("LocalBatchSize");
            Take("LocalUbatchSize");
        }
        else if (speed == 0 && context > 0 && batch < ParseInt(advice.BatchSize, 512))
        {
            Take("LocalBatchSize");
            Take("LocalUbatchSize");
        }

        var parser = current.GetValueOrDefault("LocalChatParser", "Jinja");
        if (stability != 0 &&
            (parser.Equals("Skip parsing", StringComparison.OrdinalIgnoreCase) ||
             parser.Equals("No Jinja", StringComparison.OrdinalIgnoreCase)))
        {
            merged["LocalChatParser"] = "Jinja";
        }

        return merged;
    }

    private Dictionary<string, string> CaptureCurrentLocalDetailedSettings()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverrideContext"] = ((int)LocalVariantContext).ToString(),
            ["OverrideThreads"] = LocalVariantOverrideThreads,
            ["LocalThreadsBatch"] = LocalVariantThreadsBatch,
            ["LocalTemperature"] = LocalVariantTemperature.ToString(CultureInfo.InvariantCulture),
            ["LocalGpuOffloadMode"] = LocalVariantGpuOffloadMode,
            ["LocalMultiGpuMode"] = LocalVariantMultiGpuMode,
            ["LocalSplitMode"] = LocalVariantSplitMode,
            ["LocalMainGpu"] = LocalVariantMainGpu,
            ["LocalTensorSplit"] = LocalVariantTensorSplit,
            ["GpuLayers"] = PersistSettingChoice(LocalVariantGpuLayers, "fit"),
            ["LocalFlashAttention"] = LocalVariantFlashAttention,
            ["LocalKvCacheTypeK"] = LocalVariantKvCacheTypeK,
            ["LocalKvCacheTypeV"] = LocalVariantKvCacheTypeV,
            ["LocalChatTemplate"] = SanitizeLocalChatTemplate(LocalVariantChatTemplate),
            ["LocalMultiUserMode"] = LocalVariantMultiUserMode,
            ["LocalUnbanTokensMode"] = LocalVariantUnbanTokensMode,
            ["AutoCompressEnabled"] = LocalVariantAutoCompressEnabled.ToString(),
            ["OverrideMaxTokens"] = LocalVariantMaxTokens,
            ["LocalBatchSize"] = LocalVariantBatchSize,
            ["LocalUbatchSize"] = LocalVariantUbatchSize,
            ["LocalSpecType"] = LocalVariantSpecType,
            ["LocalCacheReuse"] = LocalVariantCacheReuse,
            ["LocalCacheRam"] = LocalVariantCacheRam,
            ["LocalFit"] = LocalVariantFit,
            ["LocalSwaFull"] = LocalVariantSwaFull,
            ["LocalReasoning"] = LocalVariantReasoning,
            ["LocalChatParser"] = LocalVariantChatParser,
            ["LocalVisionEnabled"] = LocalVariantVisionEnabled,
            ["LocalVisionProjectorPath"] = LocalVariantVisionProjectorPath,
            ["LocalVisionMaxImageEdge"] = ((int)LocalVariantVisionMaxImageEdge).ToString(CultureInfo.InvariantCulture)
        };
    }

    private void ResolveWizardAutoSettings(IDictionary<string, string> settings)
    {
        var threadTarget = Math.Clamp(Environment.ProcessorCount / 2, 4, 32);
        settings["OverrideThreads"] = ResolveNearestOption(threadTarget, [4, 6, 8, 10, 12, 16, 20, 24, 32]).ToString();
        settings["LocalThreadsBatch"] = ResolveNearestOption(Math.Max(1, threadTarget / 2), [1, 2, 4, 8, 12, 16, 24, 32]).ToString();
        settings["LocalChatTemplate"] = SanitizeLocalChatTemplate(
            settings.TryGetValue("LocalChatTemplate", out var template) ? template : null);

        var advice = CurrentLocalHardwareAdvice();
        if (settings["LocalGpuOffloadMode"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            settings["LocalGpuOffloadMode"] = advice.OffloadMode;
        }

        if (settings["GpuLayers"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            settings["GpuLayers"] = settings["LocalGpuOffloadMode"] switch
            {
                "CPU only" => "0",
                "GPU only" => "999",
                _ => advice.GpuLayers
            };
        }

        if (settings["LocalKvCacheTypeK"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
            settings["LocalKvCacheTypeK"] = "f16";
        if (settings["LocalKvCacheTypeV"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
            settings["LocalKvCacheTypeV"] = "f16";
        if (settings["LocalFlashAttention"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
            settings["LocalFlashAttention"] = advice.FlashAttention;
        if (settings["LocalMultiUserMode"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
            settings["LocalMultiUserMode"] = "Disabled";
        if (settings["LocalUnbanTokensMode"].Equals("Auto", StringComparison.OrdinalIgnoreCase))
            settings["LocalUnbanTokensMode"] = settings["LocalChatTemplate"].Equals("qwen", StringComparison.OrdinalIgnoreCase)
                ? "Enabled"
                : "Disabled";

        SetIfMissingOrAuto(settings, "OverrideMaxTokens", advice.MaxTokens);
        SetIfMissingOrAuto(settings, "LocalBatchSize", advice.BatchSize);
        SetIfMissingOrAuto(settings, "LocalUbatchSize", advice.UbatchSize);
        var inferredSpec = InferSpecTypeFromProfile();
        SetIfMissingOrAuto(
            settings,
            "LocalSpecType",
            string.IsNullOrWhiteSpace(inferredSpec) || inferredSpec.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? advice.SpecType
                : inferredSpec);
        SetIfMissingOrAuto(settings, "LocalCacheReuse", "256");
        SetIfMissingOrAuto(settings, "LocalCacheRam", advice.CacheRam);
        SetIfMissingOrAuto(settings, "LocalFit", advice.PreferFitEnabled ? "Enabled" : "Disabled");
        SetIfMissingOrAuto(settings, "LocalSwaFull", advice.EnableSwaFull ? "Enabled" : "Disabled");
        SetIfMissingOrAuto(settings, "LocalReasoning", "Off");
        SetIfMissingOrAuto(settings, "LocalChatParser", "Jinja");
        SetIfMissingOrAuto(settings, "LocalMultiGpuMode", "Auto");
        SetIfMissingOrAuto(settings, "LocalSplitMode", "Auto");
        SetIfMissingOrAuto(settings, "LocalMainGpu", "Auto");
        SetIfMissingOrAuto(settings, "LocalTensorSplit", "Auto");
    }

    private static void SetIfMissingOrAuto(IDictionary<string, string> settings, string key, string value)
    {
        if (!settings.TryGetValue(key, out var current) || string.IsNullOrWhiteSpace(current) || current.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            settings[key] = value;
        }
    }

    private static int ResolveNearestOption(int target, IReadOnlyList<int> options) =>
        options.OrderBy(value => Math.Abs(value - target)).ThenBy(value => value).First();

    private void ApplyLocalPriorityPresetToDetailedSettings(IReadOnlyDictionary<string, string> settings)
    {
        LocalVariantContext = ParseInt(settings["OverrideContext"], (int)LocalVariantContext);
        LocalVariantOverrideThreads = settings["OverrideThreads"];
        LocalVariantThreadsBatch = settings["LocalThreadsBatch"];
        LocalVariantTemperature = (decimal)ParseDouble(settings["LocalTemperature"], (double)LocalVariantTemperature);
        LocalVariantGpuOffloadMode = settings["LocalGpuOffloadMode"];
        LocalVariantMultiGpuMode = settings.GetValueOrDefault("LocalMultiGpuMode", LocalVariantMultiGpuMode);
        LocalVariantSplitMode = settings.GetValueOrDefault("LocalSplitMode", LocalVariantSplitMode);
        LocalVariantMainGpu = settings.GetValueOrDefault("LocalMainGpu", LocalVariantMainGpu);
        LocalVariantTensorSplit = settings.GetValueOrDefault("LocalTensorSplit", LocalVariantTensorSplit);
        LocalVariantGpuLayers = settings["GpuLayers"];
        LocalVariantFlashAttention = settings["LocalFlashAttention"];
        LocalVariantKvCacheTypeK = settings["LocalKvCacheTypeK"];
        LocalVariantKvCacheTypeV = settings["LocalKvCacheTypeV"];
        LocalVariantChatTemplate = SanitizeLocalChatTemplate(settings["LocalChatTemplate"]);
        LocalVariantMultiUserMode = settings["LocalMultiUserMode"];
        LocalVariantUnbanTokensMode = settings["LocalUnbanTokensMode"];
        LocalVariantAutoCompressEnabled = ParseBool(settings["AutoCompressEnabled"], LocalVariantAutoCompressEnabled);
        LocalVariantMaxTokens = settings.GetValueOrDefault("OverrideMaxTokens", LocalVariantMaxTokens);
        LocalVariantBatchSize = settings.GetValueOrDefault("LocalBatchSize", LocalVariantBatchSize);
        LocalVariantUbatchSize = settings.GetValueOrDefault("LocalUbatchSize", LocalVariantUbatchSize);
        LocalVariantSpecType = CoerceLocalOption(
            FluxMuxRuntimeService.NormalizeLlamaSpecType(settings.GetValueOrDefault("LocalSpecType", LocalVariantSpecType)),
            LocalSpecTypeOptions,
            InferSpecTypeFromProfile());
        LocalVariantCacheReuse = settings.GetValueOrDefault("LocalCacheReuse", LocalVariantCacheReuse);
        LocalVariantCacheRam = settings.GetValueOrDefault("LocalCacheRam", LocalVariantCacheRam);
        LocalVariantFit = settings.GetValueOrDefault("LocalFit", LocalVariantFit);
        LocalVariantSwaFull = settings.GetValueOrDefault("LocalSwaFull", LocalVariantSwaFull);
        LocalVariantReasoning = settings.GetValueOrDefault("LocalReasoning", LocalVariantReasoning);
        LocalVariantChatParser = settings.GetValueOrDefault("LocalChatParser", LocalVariantChatParser);
        RefreshLocalVisionHint();
        UpdateLocalPrioritySummaryTexts();
    }

    private void UpdateLocalPrioritySummaryTexts()
    {
        LocalVariantSettingsJson = SerializeJsonObject(BuildLocalVariantSettingsObject());
    }

    private JsonObject BuildLocalVariantSettingsObject()
    {
        return new JsonObject
        {
            ["OverrideContext"] = ((int)LocalVariantContext).ToString(),
            ["OverrideThreads"] = LocalVariantOverrideThreads,
            ["LocalThreadsBatch"] = LocalVariantThreadsBatch,
            ["LocalTemperature"] = LocalVariantTemperature.ToString(CultureInfo.InvariantCulture),
            ["LocalGpuOffloadMode"] = LocalVariantGpuOffloadMode,
            ["LocalMultiGpuMode"] = LocalVariantMultiGpuMode,
            ["LocalSplitMode"] = LocalVariantSplitMode,
            ["LocalMainGpu"] = LocalVariantMainGpu,
            ["LocalTensorSplit"] = LocalVariantTensorSplit,
            ["GpuLayers"] = PersistSettingChoice(LocalVariantGpuLayers, "fit"),
            ["LocalFlashAttention"] = LocalVariantFlashAttention,
            ["LocalKvCacheTypeK"] = LocalVariantKvCacheTypeK,
            ["LocalKvCacheTypeV"] = LocalVariantKvCacheTypeV,
            ["LocalChatTemplate"] = SanitizeLocalChatTemplate(LocalVariantChatTemplate),
            ["LocalMultiUserMode"] = LocalVariantMultiUserMode,
            ["LocalUnbanTokensMode"] = LocalVariantUnbanTokensMode,
            ["AutoCompressEnabled"] = LocalVariantAutoCompressEnabled,
            ["OverrideMaxTokens"] = LocalVariantMaxTokens,
            ["LocalBatchSize"] = LocalVariantBatchSize,
            ["LocalUbatchSize"] = LocalVariantUbatchSize,
            ["LocalSpecType"] = LocalVariantSpecType,
            ["LocalCacheReuse"] = LocalVariantCacheReuse,
            ["LocalCacheRam"] = LocalVariantCacheRam,
            ["LocalFit"] = LocalVariantFit,
            ["LocalSwaFull"] = LocalVariantSwaFull,
            ["LocalReasoning"] = LocalVariantReasoning,
            ["LocalChatParser"] = LocalVariantChatParser,
            ["LocalVisionEnabled"] = LocalVariantVisionEnabled,
            ["LocalVisionProjectorPath"] = LocalVariantVisionProjectorPath,
            ["LocalVisionMaxImageEdge"] = ((int)LocalVariantVisionMaxImageEdge).ToString(),
            ["PriorityOrder"] = BuildStringArray(LocalPriorityOrder)
        };
    }

    private Dictionary<string, string> BuildLocalSettingsSnapshot()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctx"] = ((int)LocalVariantContext).ToString(),
            ["threads"] = LocalVariantOverrideThreads,
            ["batch"] = LocalVariantThreadsBatch,
            ["temp"] = LocalVariantTemperature.ToString(CultureInfo.InvariantCulture),
            ["offload"] = LocalVariantGpuOffloadMode,
            ["kv"] = LocalVariantKvCacheTypeK + "/" + LocalVariantKvCacheTypeV,
            ["flash"] = LocalVariantFlashAttention,
            ["template"] = LocalVariantChatTemplate,
            ["slots"] = LocalVariantMultiUserMode,
            ["unban"] = LocalVariantUnbanTokensMode,
            ["maxtok"] = LocalVariantMaxTokens,
            ["b"] = LocalVariantBatchSize,
            ["ub"] = LocalVariantUbatchSize,
            ["spec"] = LocalVariantSpecType,
            ["reuse"] = LocalVariantCacheReuse,
            ["cram"] = LocalVariantCacheRam,
            ["fit"] = LocalVariantFit,
            ["swa"] = LocalVariantSwaFull,
            ["reason"] = LocalVariantReasoning,
            ["parser"] = LocalVariantChatParser
        };
    }

    private static string BuildChangeSummary(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var changes = before.Keys
            .Where(key => after.TryGetValue(key, out var next) && !string.Equals(before[key], next, StringComparison.OrdinalIgnoreCase))
            .Select(key => key + " " + before[key] + " -> " + after[key])
            .ToList();
        return changes.Count > 0 ? string.Join("; ", changes) : "no material change";
    }


    private static IReadOnlyList<string> ResolveLocalPriorityOrder(IEnumerable<string> priorityOrder)
    {
        var resolved = priorityOrder
            .Select(LocalPrioritySettingsCalculator.CanonicalizeGoalName)
            .Where(value => LocalPriorityNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in LocalPriorityNames)
        {
            if (!resolved.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(fallback);
            }
        }

        return resolved;
    }

    private static IReadOnlyList<string> ResolveCloudPriorityOrder(IEnumerable<string> priorityOrder)
    {
        var resolved = priorityOrder
            .Select(LocalPrioritySettingsCalculator.CanonicalizeGoalName)
            .Where(value => CloudPriorityNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in CloudPriorityNames)
        {
            if (!resolved.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(fallback);
            }
        }

        return resolved;
    }

    private FluxMuxRuntimeService.LocalHardwareLaunchAdvice CurrentLocalHardwareAdvice()
        => _runtimeService.GetLocalHardwareLaunchAdvice(
            SelectedLocalProfile,
            LocalModelDirectory,
            LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase),
            LocalVariantVisionProjectorPath,
            (int)LocalVariantVisionMaxImageEdge);

    private FluxMuxRuntimeService.LocalVisionRecommendation CurrentLocalVisionRecommendation()
    {
        var modelPath = ResolveSelectedLocalModelPath() ?? SelectedLocalProfile;
        return _runtimeService.RecommendVisionForModel(modelPath ?? string.Empty, LocalModelDirectory);
    }

    private (bool Enabled, string ProjectorPath, int MaxImageEdge) GetLocalVisionLaunchSettings(string? model, string? variant)
    {
        var isCurrentEditor = (model ?? string.Empty).Equals(SelectedLocalProfile ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && NormalizeVariantName(variant).Equals(NormalizeVariantName(SelectedLocalVariant), StringComparison.OrdinalIgnoreCase);

        bool enabled;
        string projector;
        int maxEdge;
        if (isCurrentEditor)
        {
            enabled = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
            projector = (LocalVariantVisionProjectorPath ?? string.Empty).Trim();
            maxEdge = (int)LocalVariantVisionMaxImageEdge;
        }
        else
        {
            var settings = GetLiveLocalProfileSettings(variant ?? string.Empty, model) ?? new JsonObject();
            enabled = (settings["LocalVisionEnabled"]?.ToString() ?? string.Empty)
                .Equals("Enabled", StringComparison.OrdinalIgnoreCase);
            projector = (settings["LocalVisionProjectorPath"]?.ToString() ?? string.Empty).Trim();
            maxEdge = ParseInt(settings["LocalVisionMaxImageEdge"]?.ToString(), 1344);
        }

        if (enabled && string.IsNullOrWhiteSpace(projector))
        {
            var modelPath = ResolveLocalModelPath(model ?? string.Empty) ?? model ?? string.Empty;
            projector = _runtimeService.RecommendVisionForModel(modelPath, LocalModelDirectory).ProjectorPath;
        }

        if (maxEdge < 256)
        {
            maxEdge = 1344;
        }

        return (enabled, projector, Math.Clamp(maxEdge, 256, 4096));
    }

    private void RememberLocalVisionForm(string enabled, string? projectorPath, decimal maxImageEdge)
    {
        _hydratedLocalVisionEnabled = enabled;
        _hydratedLocalVisionProjectorPath = projectorPath ?? string.Empty;
        if (_isHydratingLocalVariantForm)
        {
            return;
        }

        if (LocalVariantVisionEnabled.Equals(enabled, StringComparison.OrdinalIgnoreCase)
            && string.Equals(LocalVariantVisionProjectorPath ?? string.Empty, projectorPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && LocalVariantVisionMaxImageEdge == maxImageEdge)
        {
            return;
        }

        _isHydratingLocalVariantForm = true;
        try
        {
            LocalVariantVisionEnabled = enabled;
            LocalVariantVisionProjectorPath = projectorPath ?? string.Empty;
            LocalVariantVisionMaxImageEdge = maxImageEdge;
            RefreshLocalVisionHint();
        }
        finally
        {
            _isHydratingLocalVariantForm = false;
        }
    }

    private void RefreshImagesHeadroomNote()
    {
        var imagesOn = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        var edge = (int)LocalVariantVisionMaxImageEdge;
        var projectorGiB = LocalVramFootprintEstimate.ProjectorGiB;
        try
        {
            var path = (LocalVariantVisionProjectorPath ?? string.Empty).Trim();
            if (imagesOn && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                projectorGiB = Math.Max(
                    LocalVramFootprintEstimate.ProjectorGiB,
                    new FileInfo(path).Length / 1024d / 1024d / 1024d);
            }
        }
        catch
        {
        }

        var reserveGiB = imagesOn
            ? LocalVramFootprintEstimate.VisionReserveGiB(projectorGiB, edge)
            : 0;
        LocalVariantImagesHeadroomNote = LocalVramFootprintEstimate.FormatImagesHeadroomNote(
            imagesOn,
            reserveGiB,
            edge,
            CurrentLocalHardwareAdvice().GpuTotalGb);
    }

    private void RefreshLocalVisionHint()
    {
        RefreshLocalVisionProjectorChoices();
        RefreshImagesHeadroomNote();
        var rec = CurrentLocalVisionRecommendation();
        var on = LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        if (!on)
        {
            LocalVariantVisionHintText = rec.Enabled
                ? "Images are off on this profile, so picture turns cannot use it while it is loaded. A matching projector file is available if you turn Images on and Launch again (uses more graphics memory)."
                : rec.Hint;
            return;
        }

        if (string.IsNullOrWhiteSpace(LocalVariantVisionProjectorPath))
        {
            LocalVariantVisionHintText = LocalVariantVisionProjectorChoices.Count > 1
                ? "Images are on. Pick the matching projector from the list (only files that look like they belong to this local model are shown)."
                : rec.Hint;
            return;
        }

        LocalVariantVisionHintText = "This profile loads images and uses extra graphics memory. Make a text-only copy if you do not need pictures.";
    }

    private void RefreshLocalVisionProjectorChoices()
    {
        var modelPath = ResolveSelectedLocalModelPath() ?? string.Empty;
        var matches = _runtimeService.ListRelevantVisionProjectors(modelPath, LocalModelDirectory);
        var none = new LocalVisionProjectorChoice { Path = string.Empty, Label = "(none)" };
        var choices = new List<LocalVisionProjectorChoice> { none };
        var modelDir = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
            {
                modelDir = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? string.Empty;
            }
        }
        catch
        {
        }

        foreach (var path in matches)
        {
            var name = Path.GetFileName(path);
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var sameFolder = !string.IsNullOrWhiteSpace(modelDir)
                             && dir.Equals(modelDir, StringComparison.OrdinalIgnoreCase);
            choices.Add(new LocalVisionProjectorChoice
            {
                Path = path,
                Label = sameFolder ? name + " (same folder)" : name
            });
        }

        var current = LocalVariantVisionProjectorPath?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current)
            && choices.TrueForAll(choice => !choice.Path.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new LocalVisionProjectorChoice
            {
                Path = current,
                Label = Path.GetFileName(current) + " (chosen file)"
            });
        }

        _isRefreshingVisionProjectorChoices = true;
        try
        {
            LocalVariantVisionProjectorChoices.Clear();
            foreach (var choice in choices)
            {
                LocalVariantVisionProjectorChoices.Add(choice);
            }

            SelectedLocalVariantVisionProjectorChoice = choices.FirstOrDefault(choice =>
                choice.Path.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? none;

            if (string.IsNullOrWhiteSpace(current)
                && matches.Count == 1
                && LocalVariantVisionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                && IsLocalVariantEditable)
            {
                LocalVariantVisionProjectorPath = matches[0];
                SelectedLocalVariantVisionProjectorChoice = choices.FirstOrDefault(choice =>
                    choice.Path.Equals(matches[0], StringComparison.OrdinalIgnoreCase)) ?? none;
            }
        }
        finally
        {
            _isRefreshingVisionProjectorChoices = false;
        }
    }

    private static string SnapWizardBatchDown(string value)
        => LocalPrioritySettingsCalculator.SnapWizardBatchDown(value);

    private static string ScaleWizardBatchUp(string value)
        => LocalPrioritySettingsCalculator.ScaleWizardBatchUp(value);

    private static string DeriveUbatch(string batch)
        => LocalPrioritySettingsCalculator.DeriveUbatch(batch);

    private string SanitizeLocalChatTemplate(string? configured)
        => FluxMuxRuntimeService.SanitizeStoredChatTemplate(configured, SelectedLocalProfile);

    private string CoerceLocalChatTemplate(string? value)
    {
        var sanitized = SanitizeLocalChatTemplate(value);
        if (sanitized.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "Auto";
        }

        return CoerceLocalOption(sanitized, LocalChatTemplateOptions, "Auto");
    }

    private string InferSpecTypeFromProfile()
    {
        var lowered = (SelectedLocalProfile ?? string.Empty).ToLowerInvariant();
        return lowered.Contains("qwen3") ? "draft-mtp" : "ngram-simple";
    }

    private static string DefaultLocalThreadCount() =>
        ResolveNearestOption(Math.Clamp(Environment.ProcessorCount / 2, 4, 32), [4, 6, 8, 10, 12, 16, 20, 24, 32]).ToString();

    private static string DefaultLocalBatchThreadCount() =>
        ResolveNearestOption(Math.Max(1, Math.Clamp(Environment.ProcessorCount / 2, 4, 32) / 2), [1, 2, 4, 8, 12, 16, 24, 32]).ToString();

    private static string CoerceOnOff(string? value, string fallback)
    {
        if (string.Equals(value, "Enabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Enabled";
        }

        if (string.Equals(value, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Disabled";
        }

        return fallback;
    }

    private static string CoerceLocalOptionKeepAuto(string? value, IEnumerable<string> options, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(fallback) ? "Auto" : fallback;
        }

        var match = options.FirstOrDefault(option => option.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? fallback;
    }

    private static string CoerceLocalOption(string? value, IEnumerable<string> options, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        var match = options.FirstOrDefault(option => option.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        if (options is ObservableCollection<string> live
            && live.All(option => !option.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            live.Add(trimmed);
        }

        return trimmed;
    }

    private static IReadOnlyList<string> BuildSettingChoices(
        IEnumerable<string> options,
        bool includeAuto,
        string autoDisplayReplacement)
    {
        var choices = options.ToList();
        if (!includeAuto)
        {
            choices.RemoveAll(option => option.Equals("Auto", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(autoDisplayReplacement)
            && !choices.Contains(autoDisplayReplacement, StringComparer.OrdinalIgnoreCase))
        {
            choices.Insert(0, autoDisplayReplacement);
        }

        return choices;
    }

    private static string MapAutoForEditor(string? value, string fallback, bool isDefaultProfile, string defaultDisplay)
    {
        var resolved = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (isDefaultProfile && resolved.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return defaultDisplay;
        }

        return resolved;
    }

    private static string PersistSettingChoice(string? value, string displayReplacement, string storedValue = "Auto")
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals(displayReplacement, StringComparison.OrdinalIgnoreCase)
            || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return storedValue;
        }

        return value;
    }

    private static string DisplayResolvedSetting(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return value;
    }

    private static string FormatProfileTemperature(string? value, string fallback)
    {
        var parsed = ParseDouble(value, ParseDouble(fallback, 0.3));
        return parsed.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string CoerceDefaultGpuLayers(string? value, FluxMuxRuntimeService.LocalHardwareLaunchAdvice advice)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !value.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("fit", StringComparison.OrdinalIgnoreCase))
        {
            return CoerceLocalOption(value, ["0", "20", "40", "70", "999"], advice.GpuLayers);
        }

        if (advice.GpuLayers.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            || advice.OffloadMode.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase))
        {
            return "fit";
        }

        return CoerceLocalOption(advice.GpuLayers, ["0", "20", "40", "70", "999"], "999");
    }

    private sealed record LocalPriorityPreset(
        string PresetName,
        Dictionary<string, string> Settings,
        Dictionary<string, int> Scores,
        int CompositeScore,
        string Gains,
        string Tradeoffs);

    private sealed record CloudPriorityPreset(
        string PresetName,
        CloudIntentPresetSettings Settings,
        Dictionary<string, int> Scores,
        int CompositeScore = 0);

    private sealed record CloudIntentPresetSettings(
        string ContextWindow,
        decimal Temperature,
        decimal MaxTokens,
        string ReasoningMode,
        string ResponseFormat,
        string OpenAiReasoningEffort,
        string CopilotReasoningEffort,
        string GeminiThinkingMode,
        decimal GeminiThinkingBudget,
        string AnthropicThinkingMode);

    private JsonObject ResolveCloudVariantObjectFromConfig(string variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName))
        {
            return new JsonObject();
        }

        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return new JsonObject();
        }

        var provider = (SelectedCloudProvider ?? string.Empty).Trim();
        var model = (SelectedCloudProfile ?? string.Empty).Trim();
        var variant = ProfileConfigVariantName(variantName);
        if (variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            variant = FindCloudModelDefaultVariantName(provider, model) ?? variant;
        }

        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::");
            if (parts.Length < 3)
            {
                continue;
            }

            if (!string.Equals(parts[0].Trim(), provider, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[1].Trim(), model, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[2].Trim(), variant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (entry.Value as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        }

        return new JsonObject();
    }

    private JsonObject ResolveLocalVariantObjectFromConfig(string variantName)
        => ResolveLocalVariantObjectFromConfig(SelectedLocalProfile, variantName);

    private JsonObject ResolveLocalVariantObjectFromConfig(string? modelName, string variantName)
    {
        if (string.IsNullOrWhiteSpace(variantName))
        {
            return new JsonObject();
        }

        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return new JsonObject();
        }

        var model = (modelName ?? string.Empty).Trim();
        var variant = ProfileConfigVariantName(variantName);
        if (variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            variant = FindLocalModelDefaultVariantName(model) ?? variant;
        }

        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::");
            if (parts.Length < 2)
            {
                continue;
            }

            if (!string.Equals(parts[0].Trim(), model, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[1].Trim(), variant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (entry.Value as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        }

        return new JsonObject();
    }

    private static string SerializeJsonObject(JsonObject obj)
    {
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildSettingsSnapshot(string json, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "no settings";
        }

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return "invalid json";
        }

        if (parsed is null)
        {
            return "invalid json";
        }

        var values = new List<string>();
        foreach (var key in keys)
        {
            if (!parsed.TryGetPropertyValue(key, out var node) || node is null)
            {
                continue;
            }

            var text = node.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            values.Add(key + "=" + text);
        }

        return values.Count > 0 ? string.Join(", ", values) : "defaults/unspecified";
    }

    private static bool IsModelDefaultProfile(JsonObject settings, string variantName)
    {
        return variantName.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            || ParseBool(settings[ProfileIsModelDefaultKey]?.ToString(), false);
    }

    private static bool IsProfileDefaultUnlocked(JsonObject settings)
    {
        if (settings[ProfileDefaultUnlockedKey] is JsonValue value)
        {
            if (value.TryGetValue(out bool flag))
            {
                return flag;
            }

            if (value.TryGetValue(out string? text))
            {
                return ParseBool(text, false);
            }
        }

        return ParseBool(settings[ProfileDefaultUnlockedKey]?.ToString(), false);
    }

    private static void StampModelDefaultMetadata(JsonObject settings, bool unlocked)
    {
        settings[ProfileIsModelDefaultKey] = true;
        settings[ProfileDefaultUnlockedKey] = unlocked ? "true" : "false";
    }

    private static void ClearModelDefaultMetadata(JsonObject settings)
    {
        settings.Remove(ProfileIsModelDefaultKey);
        settings.Remove(ProfileDefaultUnlockedKey);
    }

    private string? FindLocalModelDefaultVariantName(string? modelName)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return null;
        }

        var model = (modelName ?? string.Empty).Trim();
        string? namedDefault = null;
        string? builtinDefault = null;
        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::");
            if (parts.Length < 2
                || !parts[0].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
                || entry.Value is not JsonObject settings)
            {
                continue;
            }

            var variant = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(variant))
            {
                continue;
            }

            if (ParseBool(settings[ProfileIsModelDefaultKey]?.ToString(), false))
            {
                namedDefault = variant;
            }
            else if (variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                builtinDefault = variant;
            }
        }

        return builtinDefault ?? namedDefault;
    }

    private string? FindCloudModelDefaultVariantName(string? provider, string? modelName)
    {
        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return null;
        }

        var providerName = (provider ?? string.Empty).Trim();
        var model = (modelName ?? string.Empty).Trim();
        string? namedDefault = null;
        string? builtinDefault = null;
        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::");
            if (parts.Length < 3
                || !parts[0].Trim().Equals(providerName, StringComparison.OrdinalIgnoreCase)
                || !parts[1].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
                || entry.Value is not JsonObject settings)
            {
                continue;
            }

            var variant = parts[2].Trim();
            if (string.IsNullOrWhiteSpace(variant))
            {
                continue;
            }

            if (ParseBool(settings[ProfileIsModelDefaultKey]?.ToString(), false))
            {
                namedDefault = variant;
            }
            else if (variant.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                builtinDefault = variant;
            }
        }

        return builtinDefault ?? namedDefault;
    }

    private JsonObject? GetLiveLocalProfileSettings(string variantName, string? modelName = null)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return null;
        }

        var model = FirstNonEmpty(modelName, SelectedLocalProfile);
        var targetKey = $"{model.Trim()}::{ProfileConfigVariantName(variantName)}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(matchingKey) ? null : profiles[matchingKey] as JsonObject;
    }

    private JsonObject? GetLiveCloudProfileSettings(string variantName, string? provider = null, string? modelName = null)
    {
        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return null;
        }

        var targetKey = $"{FirstNonEmpty(provider, SelectedCloudProvider).Trim()}::{FirstNonEmpty(modelName, SelectedCloudProfile).Trim()}::{ProfileConfigVariantName(variantName)}";
        var matchingKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(matchingKey) ? null : profiles[matchingKey] as JsonObject;
    }

    private void SetLiveLocalProfileLockState(string variantName, bool unlocked, bool markAsModelDefault = true, string? modelName = null)
    {
        var settings = GetLiveLocalProfileSettings(variantName, modelName);
        if (settings is null)
        {
            return;
        }

        if (markAsModelDefault)
        {
            settings[ProfileIsModelDefaultKey] = true;
        }

        settings[ProfileDefaultUnlockedKey] = unlocked ? "true" : "false";
    }

    private void SetLiveCloudProfileLockState(string variantName, bool unlocked, bool markAsModelDefault = true, string? provider = null, string? modelName = null)
    {
        var settings = GetLiveCloudProfileSettings(variantName, provider, modelName);
        if (settings is null)
        {
            return;
        }

        if (markAsModelDefault)
        {
            settings[ProfileIsModelDefaultKey] = true;
        }

        settings[ProfileDefaultUnlockedKey] = unlocked ? "true" : "false";
    }

    private bool IsLocalGgufMissing(string? modelName)
    {
        return !string.IsNullOrWhiteSpace(modelName) && ResolveLocalModelPath(modelName) is null;
    }

    private bool IsLocalModelCurrentlyRunning(string? modelName)
    {
        return !string.IsNullOrWhiteSpace(_runningLocalModel)
            && _runningLocalModel.Equals(modelName, StringComparison.OrdinalIgnoreCase);
    }

    private int CountLocalProfileCopies(string? modelName)
    {
        var model = (modelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return 0;
        }

        return _config.GetObjectKeys("LocalProfiles").Count(key =>
        {
            var parts = key.Split("::");
            return parts.Length >= 2
                && parts[0].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
                && !parts[1].Trim().Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private int CountCloudProfileCopies(string? provider, string? modelName)
    {
        var providerName = (provider ?? string.Empty).Trim();
        var model = (modelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(model))
        {
            return 0;
        }

        return _config.GetObjectKeys("CloudProfiles").Count(key =>
        {
            var parts = key.Split("::");
            return parts.Length >= 3
                && parts[0].Trim().Equals(providerName, StringComparison.OrdinalIgnoreCase)
                && parts[1].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
                && !parts[2].Trim().Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private bool TryDeleteLocalProfileFamily(string? modelName)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var model = (modelName ?? string.Empty).Trim();
        var keys = profiles
            .Select(entry => entry.Key)
            .Where(key =>
            {
                var parts = key.Split("::");
                return parts.Length >= 2 && parts[0].Trim().Equals(model, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (keys.Length == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            profiles.Remove(key);
        }

        return true;
    }

    private void RemoveEmptyLocalProfileKeys(string? modelName)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return;
        }

        var model = (modelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var staleKeys = profiles
            .Select(entry => entry.Key)
            .Where(key =>
            {
                var parts = (key ?? string.Empty).Split("::");
                return parts.Length >= 2
                    && parts[0].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(parts[1]);
            })
            .ToArray();
        foreach (var key in staleKeys)
        {
            profiles.Remove(key);
        }
    }

    private void RemoveEmptyCloudProfileKeys(string? provider, string? modelName)
    {
        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return;
        }

        var providerName = (provider ?? string.Empty).Trim();
        var model = (modelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var staleKeys = profiles
            .Select(entry => entry.Key)
            .Where(key =>
            {
                var parts = (key ?? string.Empty).Split("::");
                return parts.Length >= 3
                    && parts[0].Trim().Equals(providerName, StringComparison.OrdinalIgnoreCase)
                    && parts[1].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(parts[2]);
            })
            .ToArray();
        foreach (var key in staleKeys)
        {
            profiles.Remove(key);
        }
    }

    private bool TryDeleteCloudProfileFamily(string? provider, string? modelName)
    {
        if (_config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var providerName = (provider ?? string.Empty).Trim();
        var model = (modelName ?? string.Empty).Trim();
        var keys = profiles
            .Select(entry => entry.Key)
            .Where(key =>
            {
                if (!CloudProfileFamilyRetarget.TryParseKey(key, out var keyProvider, out var keyModel, out _))
                {
                    return false;
                }

                return keyProvider.Equals(providerName, StringComparison.OrdinalIgnoreCase)
                    && keyModel.Equals(model, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (keys.Length == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            profiles.Remove(key);
        }

        return true;
    }

    private bool TryDeleteCloudProfileByKey(string? configKey)
    {
        if (string.IsNullOrWhiteSpace(configKey) || _config.Root["CloudProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var matchingKey = profiles
            .Select(entry => entry.Key)
            .FirstOrDefault(key => key.Equals(configKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            return false;
        }

        profiles.Remove(matchingKey);
        return true;
    }

    private bool TryDeleteLocalVariantEntry(string variantName)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var targetKey = $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{NormalizeVariantName(variantName)}";
        var matchingKey = profiles
            .Select(x => x.Key)
            .FirstOrDefault(x => x.Equals(targetKey, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            return false;
        }

        profiles.Remove(matchingKey);
        return true;
    }

    private bool TryRenameLocalVariantEntry(string currentVariantName, string newVariantName)
    {
        if (_config.Root["LocalProfiles"] is not JsonObject profiles)
        {
            return false;
        }

        var currentName = NormalizeVariantName(currentVariantName);
        var nextName = NormalizeVariantName(newVariantName);
        if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(nextName))
        {
            return false;
        }

        if (currentName.Equals(nextName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentKey = $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{currentName}";
        var nextKey = $"{(SelectedLocalProfile ?? string.Empty).Trim()}::{nextName}";
        var matchingCurrentKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(currentKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingCurrentKey))
        {
            return false;
        }

        var matchingNextKey = profiles.Select(x => x.Key).FirstOrDefault(x => x.Equals(nextKey, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matchingNextKey))
        {
            StatusMessage = "Local model profile variant already exists.";
            return false;
        }

        var payload = (profiles[matchingCurrentKey] as JsonObject)?.DeepClone();
        profiles.Remove(matchingCurrentKey);
        profiles[nextKey] = payload ?? new JsonObject();
        return true;
    }

    private void RetargetLocalVariantReferences(string? model, string oldVariant, string newVariant)
    {
        model = (model ?? string.Empty).Trim();
        oldVariant = ProfileConfigVariantName(oldVariant);
        newVariant = ProfileConfigVariantName(newVariant);
        if (string.IsNullOrWhiteSpace(model)
            || string.IsNullOrWhiteSpace(oldVariant)
            || string.IsNullOrWhiteSpace(newVariant)
            || oldVariant.Equals(newVariant, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_runningLocalModel.Equals(model, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(_runningLocalVariant).Equals(oldVariant, StringComparison.OrdinalIgnoreCase))
        {
            _runningLocalVariant = newVariant;
        }

        if (_localVariantEditorBaselineModel.Equals(model, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(_localVariantEditorBaselineVariant).Equals(oldVariant, StringComparison.OrdinalIgnoreCase))
        {
            _localVariantEditorBaselineVariant = newVariant;
        }

        if (SelectedSelection1LocalProfile.Equals(model, StringComparison.OrdinalIgnoreCase)
            && ProfileConfigVariantName(SelectedSelection1LocalVariant).Equals(oldVariant, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSelection1LocalVariant = newVariant;
        }

        _runtimeService.RetargetManagedLocalVariant(model, oldVariant, newVariant);

        if (_config.Root["RouteSlots"] is not JsonArray slots)
        {
            return;
        }

        foreach (var slot in slots.OfType<JsonObject>())
        {
            var slotModel = (slot["localModel"]?.ToString() ?? string.Empty).Trim();
            var slotVariant = ProfileConfigVariantName(slot["localVariant"]?.ToString());
            if (!slotModel.Equals(model, StringComparison.OrdinalIgnoreCase)
                || !slotVariant.Equals(oldVariant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            slot["localVariant"] = newVariant;
        }
    }

    private JsonObject? ResolveCloudVariantObject(JsonObject profiles)
    {
        var provider = (SelectedCloudProvider ?? string.Empty).Trim();
        var model = (SelectedCloudProfile ?? string.Empty).Trim();
        var variant = NormalizeVariantName(SelectedCloudVariant);

        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::");
            if (parts.Length < 3)
            {
                continue;
            }

            if (!string.Equals(parts[0].Trim(), provider, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[1].Trim(), model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(parts[2].Trim(), variant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return entry.Value as JsonObject;
        }

        return profiles
            .Where(entry =>
            {
                var parts = (entry.Key ?? string.Empty).Split("::");
                return parts.Length >= 3
                       && string.Equals(parts[0].Trim(), provider, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(parts[1].Trim(), model, StringComparison.OrdinalIgnoreCase);
            })
            .Select(entry => entry.Value as JsonObject)
            .FirstOrDefault(x => x is not null);
    }

    private JsonObject? ResolveLocalVariantObject(JsonObject profiles)
    {
        var model = (SelectedLocalProfile ?? string.Empty).Trim();
        var variant = NormalizeVariantName(SelectedLocalVariant);

        foreach (var entry in profiles)
        {
            var parts = (entry.Key ?? string.Empty).Split("::");
            if (parts.Length < 2)
            {
                continue;
            }

            if (!string.Equals(parts[0].Trim(), model, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[1].Trim(), variant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return entry.Value as JsonObject;
        }

        return profiles
            .Where(entry =>
            {
                var parts = (entry.Key ?? string.Empty).Split("::");
                return parts.Length >= 2
                       && string.Equals(parts[0].Trim(), model, StringComparison.OrdinalIgnoreCase);
            })
            .Select(entry => entry.Value as JsonObject)
            .FirstOrDefault(x => x is not null);
    }

    private static string RenameDraftFromProfileName(string? variantName)
    {
        var name = (variantName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)
            || name.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("default", StringComparison.OrdinalIgnoreCase)
            || name.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return name;
    }

    private static string NormalizeVariantName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static string ProfileConfigVariantName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Equals(BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
            ? BaseVariantDisplayName
            : trimmed;
    }

    private void UpdateSwitchBenchmarkDisplay(RuntimeActionResult result)
    {
        var lines = result.Details
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        var headline = lines.FirstOrDefault() ?? result.Status;
        var hasTierDetails = lines.Length > 1;
        var details = result.Details ?? string.Empty;
        var handoffCompleted = result.Status.Equals("Switch benchmark completed with fidelity warnings", StringComparison.OrdinalIgnoreCase)
                               || details.Contains("Chat handoff: Profile A response was transferred", StringComparison.OrdinalIgnoreCase);
        var localPressurePattern = details.Contains("local route", StringComparison.OrdinalIgnoreCase)
                                  && (details.Contains("Warm-up retry window expired", StringComparison.OrdinalIgnoreCase)
                                      || details.Contains("did not return a successful chat completion", StringComparison.OrdinalIgnoreCase));

        SwitchBenchmarkSummaryText = handoffCompleted && !result.IsSuccess
            ? "Chat handoff completed across all selected profiles; context fidelity needs attention."
            : headline;
        SwitchBenchmarkRecommendationText = result.IsSuccess
            ? (hasTierDetails
                ? "Recommended: this profile is usable for switch-heavy work. Review the advanced details if you want the tier-by-tier fidelity breakdown."
                : "Recommended: the benchmark completed successfully and the profile appears usable for switch-heavy work.")
            : (handoffCompleted
                ? "Handoff completed: all profile launches and chat completions succeeded. Review the fidelity details for anchors that drifted or distractor facts that leaked; this is a quality warning, not a transport failure."
                : localPressurePattern
                ? "Needs attention: this local model/variant appears practically unusable for switch-heavy work under current settings, likely due to VRAM pressure or startup overhead. Recommendation: choose a much lighter local model/variant, then rerun benchmark and compare readiness-delay stats."
                : "Needs attention: the benchmark did not complete cleanly, so this profile may still lose context or leak chatter under switch pressure.");
        SwitchBenchmarkAdvancedDetailsText = string.IsNullOrWhiteSpace(result.AdvancedDetails)
            ? result.Details ?? string.Empty
            : result.AdvancedDetails;
    }

    private static string NormalizeCacheRamOption(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "Auto";
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) || value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Disabled";
        }

        if (value.Equals("-1", StringComparison.OrdinalIgnoreCase) || value.Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return "Unlimited";
        }

        return value;
    }

    private static string FirstNonEmpty(string? value, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback ?? string.Empty;
    }

    private static void PopulateRows<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        var index = 0;
        foreach (var row in source)
        {
            ApplyRowStripe(row, index);
            target.Add(row);
            index++;
        }
    }

    private void RefreshEnvironmentRowStripes()
    {
        RefreshStripeCollection(CoreDependencyRows);
        RefreshStripeCollection(ProviderAddonRows);
        RefreshStripeCollection(CoreInstallGuideRows);
        RefreshStripeCollection(SdkInstallGuideRows);
        RefreshStripeCollection(ProviderRequirementRows);
        RefreshStripeCollection(HardRequiredPathRows);
        RefreshStripeCollection(RuntimePathRows);
        RefreshStripeCollection(LocalModelCapabilityRows);
    }

    private static void RefreshStripeCollection<T>(ObservableCollection<T> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var copy = rows.ToList();
        rows.Clear();
        var index = 0;
        foreach (var row in copy)
        {
            ApplyRowStripe(row, index);
            rows.Add(row);
            index++;
        }
    }

    private static void ApplyRowStripe<T>(T row, int index)
    {
        var background = global::FluxMux.Avalonia.App.StripeBackground(index);

        switch (row)
        {
            case DependencyMatrixRow dependency:
                dependency.RowBackground = background;
                break;
            case ProviderRequirementRow providerRequirement:
                providerRequirement.RowBackground = background;
                break;
            case PathStatusRow pathStatus:
                pathStatus.RowBackground = background;
                break;
            case LocalModelCapabilityRow localCapability:
                localCapability.RowBackground = background;
                break;
            case InstallGuideRow installGuide:
                installGuide.RowBackground = background;
                break;
        }
    }

    private static void AddIfMissing(ObservableCollection<string> collection, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!collection.Any(x => string.Equals(x, value, System.StringComparison.OrdinalIgnoreCase)))
        {
            collection.Add(value);
        }
    }

    private IEnumerable<JsonObject> BuildCurrentDependencySnapshot()
    {
        foreach (var row in CoreDependencyRows)
        {
            yield return new JsonObject
            {
                ["category"] = "core",
                ["dependency"] = row.Dependency,
                ["minimum"] = row.Minimum,
                ["installed"] = row.Installed,
                ["status"] = row.Status
            };
        }

        foreach (var row in ProviderAddonRows)
        {
            yield return new JsonObject
            {
                ["category"] = "addon",
                ["dependency"] = row.Dependency,
                ["minimum"] = row.Minimum,
                ["installed"] = row.Installed,
                ["status"] = row.Status
            };
        }

        foreach (var row in HardRequiredPathRows)
        {
            yield return new JsonObject
            {
                ["category"] = "configured-path",
                ["dependency"] = row.Component,
                ["minimum"] = row.Kind,
                ["installed"] = row.ResolvedPath,
                ["status"] = row.Status
            };
        }
    }

    private void UpdateDependencyRiskPanels()
    {
        DependencyPinPolicyText =
            "After you update llama-server or the GPU driver, launch a known profile to confirm it still works.";

        var baseline = _config.GetObjectArray("DependencyKnownGoodBaseline");
        if (baseline.Count == 0)
        {
            DependencyDriftSummaryText = "No known-good snapshot captured. Capture one after a working launch if you want later path or llama-server changes highlighted here.";
            return;
        }

        var current = BuildCurrentDependencySnapshot().ToList();
        if (current.Count == 0)
        {
            DependencyDriftSummaryText = "A known-good snapshot exists. Run Refresh to compare the current llama-server version and paths with it.";
            return;
        }

        var baselineMap = baseline
            .Where(IsCurrentEnvironmentSnapshotRow)
            .GroupBy(
                x => (x["category"]?.ToString() ?? string.Empty) + "::" + (x["dependency"]?.ToString() ?? string.Empty),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var drifts = new List<string>();
        foreach (var item in current)
        {
            var key = (item["category"]?.ToString() ?? string.Empty) + "::" + (item["dependency"]?.ToString() ?? string.Empty);
            if (!baselineMap.TryGetValue(key, out var baselineItem))
            {
                drifts.Add((item["dependency"]?.ToString() ?? "unknown") + " added");
                continue;
            }

            var baselineInstalled = baselineItem["installed"]?.ToString() ?? string.Empty;
            var currentInstalled = item["installed"]?.ToString() ?? string.Empty;
            var baselineStatus = baselineItem["status"]?.ToString() ?? string.Empty;
            var currentStatus = item["status"]?.ToString() ?? string.Empty;

            if (!string.Equals(baselineInstalled, currentInstalled, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(baselineStatus, currentStatus, StringComparison.OrdinalIgnoreCase))
            {
                drifts.Add((item["dependency"]?.ToString() ?? "unknown") + $" changed ({baselineInstalled}/{baselineStatus} -> {currentInstalled}/{currentStatus})");
            }
        }

        foreach (var baselineItem in baseline.Where(IsCurrentEnvironmentSnapshotRow))
        {
            var key = (baselineItem["category"]?.ToString() ?? string.Empty) + "::" + (baselineItem["dependency"]?.ToString() ?? string.Empty);
            var exists = current.Any(x =>
                string.Equals((x["category"]?.ToString() ?? string.Empty) + "::" + (x["dependency"]?.ToString() ?? string.Empty), key, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                drifts.Add((baselineItem["dependency"]?.ToString() ?? "unknown") + " missing from current scan");
            }
        }

        if (drifts.Count == 0)
        {
            var capturedAt = _config.GetString("DependencyKnownGoodBaselineCapturedAt", "unknown-time");
            DependencyDriftSummaryText = "No llama-server or path changes since the known-good snapshot captured at " + capturedAt + ".";
            return;
        }

        var visibleChanges = string.Join(" | ", drifts.Take(8));
        if (drifts.Count > 8)
            visibleChanges += $" | +{drifts.Count - 8} more";
        DependencyDriftSummaryText = "Changes since the known-good snapshot: " + visibleChanges;
    }

    private static bool IsCurrentEnvironmentSnapshotRow(JsonObject item)
    {
        var category = item["category"]?.ToString() ?? string.Empty;
        if (category.Equals("addon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = item["dependency"]?.ToString() ?? string.Empty;
        return name.Length > 0
            && !name.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Python", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("pip", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(".NET (this app)", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("gateway", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("openai", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("anthropic", StringComparison.OrdinalIgnoreCase);
    }
}
