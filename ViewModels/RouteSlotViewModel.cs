using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FluxMux.Avalonia.ViewModels;

public partial class RouteSlotViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isHydrating;

    public RouteSlotViewModel(MainViewModel owner, string slotId, int slotNumber)
    {
        _owner = owner;
        SlotId = slotId;
        SlotNumber = slotNumber;

        DeleteSlotCommand = new RelayCommand(() => _owner.DeleteQuickSelectSlot(this));
        SaveSlotCommand = new RelayCommand(() => _owner.SaveQuickSelectSlot(this));
        LaunchCommand = new AsyncRelayCommand(() => _owner.LaunchQuickSelectSlotAsync(this));
        RestartCommand = new AsyncRelayCommand(() => _owner.RestartQuickSelectSlotAsync(this));
        StopCommand = new AsyncRelayCommand(() => _owner.StopQuickSelectSlotAsync(this));
    }

    public string SlotId { get; internal set; }

    [ObservableProperty]
    public partial int SlotNumber { get; internal set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ActiveRuntimeStatusText)
            ? $"Selection {SlotNumber}"
            : $"Selection {SlotNumber} — {ActiveRuntimeStatusText}";

    private RouteIndicatorState _activeRuntimeState = RouteIndicatorState.Off;

    [ObservableProperty]
    public partial bool IsActiveRuntimeSlot { get; private set; }

    [ObservableProperty]
    public partial bool IsStandbyHotSlot { get; private set; }

    [ObservableProperty]
    public partial string ActiveRuntimeStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ActiveRuntimeTelemetryText { get; private set; } = string.Empty;

    public bool ShowActiveRuntimeStatus => !string.IsNullOrWhiteSpace(ActiveRuntimeStatusText);

    public bool ShowActiveRuntimeTelemetry =>
        (IsActiveRuntimeSlot || IsStandbyHotSlot) && !string.IsNullOrWhiteSpace(ActiveRuntimeTelemetryText);

    public bool IsActiveRuntimeLive => IsActiveRuntimeSlot && _activeRuntimeState == RouteIndicatorState.Live;

    public bool IsActiveRuntimeAmber => IsActiveRuntimeSlot && _activeRuntimeState == RouteIndicatorState.Amber;

    public bool IsActiveRuntimeFault => IsActiveRuntimeSlot && _activeRuntimeState == RouteIndicatorState.Fault;

    public bool IsStandbyHotLive => IsStandbyHotSlot && _activeRuntimeState == RouteIndicatorState.Live;

    public bool IsStandbyHotAmber => IsStandbyHotSlot && _activeRuntimeState == RouteIndicatorState.Amber;

    public bool IsStandbyHotFault => IsStandbyHotSlot && _activeRuntimeState == RouteIndicatorState.Fault;

    public bool IsLoadingRuntimeSlot => IsActiveRuntimeAmber || IsStandbyHotAmber;

    [ObservableProperty]
    public partial bool LoadingPulseOn { get; set; }

    [ObservableProperty]
    public partial double LoadingPulseOpacity { get; set; }

    partial void OnLoadingPulseOnChanged(bool value)
        => LoadingPulseOpacity = value ? 1 : 0;

    public IBrush ActiveRuntimeTelemetryBrush => _activeRuntimeState switch
    {
        RouteIndicatorState.Live => new SolidColorBrush(Color.Parse("#15803D")),
        RouteIndicatorState.Amber => new SolidColorBrush(Color.Parse("#C2410C")),
        RouteIndicatorState.Fault => new SolidColorBrush(Color.Parse("#B91C1C")),
        _ => new SolidColorBrush(Color.Parse("#475467"))
    };

    [ObservableProperty]
    public partial string RouteType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudProvider { get; set; } = "Gemini";

    [ObservableProperty]
    public partial string CloudModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CloudVariant { get; set; } = MainViewModel.BaseVariantDisplayName;

    [ObservableProperty]
    public partial string LocalModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalVariant { get; set; } = MainViewModel.BaseVariantDisplayName;

    [ObservableProperty]
    public partial ValidatedQuickSelectProfileOption? SelectedValidatedProfile { get; set; }

    [ObservableProperty]
    public partial string AttributeSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileSyncNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEndpointTrusted { get; set; } = true;

    public bool ShowProfileSyncNote => !string.IsNullOrWhiteSpace(ProfileSyncNote);

    partial void OnProfileSyncNoteChanged(string value)
        => OnPropertyChanged(nameof(ShowProfileSyncNote));

    partial void OnIsEndpointTrustedChanged(bool value)
        => OnPropertyChanged(nameof(CanLaunchSavedAssignment));

    public bool IsDefaultProfileValidated { get; private set; }

    public string EndpointValidatedUtc { get; private set; } = string.Empty;

    public bool IsCloudRoute => RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase);

    public bool IsLocalRoute => RouteType.Equals("Local", StringComparison.OrdinalIgnoreCase);

    public bool HasSavedAssignment => IsCloudRoute || IsLocalRoute;

    public bool HasValidatedProfile => SelectedValidatedProfile is not null;

    public bool HasUnsavedPopulatedAssignment =>
        SelectedValidatedProfile is not null
        && !(HasSavedAssignment && SelectedValidatedProfile.MatchesSavedAssignment(
            RouteType,
            CloudProvider,
            CloudModel,
            CloudVariant,
            LocalModel,
            LocalVariant));

    public bool ShowSlotFacts => HasSavedAssignment || HasValidatedProfile;

    public bool CanSaveSlot => HasUnsavedPopulatedAssignment;

    public bool CanLaunchSavedAssignment =>
        HasSavedAssignment && !HasUnsavedPopulatedAssignment && IsEndpointTrusted;

    public string SavedAssignmentKey =>
        HasSavedAssignment
            ? string.Join(
                "::",
                RouteType,
                IsCloudRoute ? CloudProvider : string.Empty,
                IsCloudRoute ? CloudModel : LocalModel,
                ValidatedQuickSelectProfileOption.NormalizeVariant(IsCloudRoute ? CloudVariant : LocalVariant))
            : string.Empty;

    public ObservableCollection<ValidatedQuickSelectProfileOption> AvailableValidatedProfiles { get; } = [];

    public ObservableCollection<ValidatedQuickSelectProfileOption> ValidatedProfiles => _owner.ValidatedQuickSelectProfiles;

    public IRelayCommand DeleteSlotCommand { get; }

    public IRelayCommand SaveSlotCommand { get; }

    public IAsyncRelayCommand LaunchCommand { get; }

    public IAsyncRelayCommand RestartCommand { get; }

    public IAsyncRelayCommand StopCommand { get; }

    internal void Hydrate(
        string routeType,
        string cloudProvider,
        string cloudModel,
        string cloudVariant,
        string localModel,
        string localVariant,
        bool isDefaultProfileValidated,
        string endpointValidatedUtc,
        string validatedProfileKey = "")
    {
        _isHydrating = true;
        RouteType = routeType;
        CloudProvider = cloudProvider;
        CloudModel = cloudModel;
        CloudVariant = cloudVariant;
        LocalModel = localModel;
        LocalVariant = localVariant;
        IsDefaultProfileValidated = isDefaultProfileValidated;
        EndpointValidatedUtc = endpointValidatedUtc;
        TryApplyValidatedProfileKey(validatedProfileKey);
        SyncSelectedValidatedProfile();
        _isHydrating = false;
        NotifyRouteStateChanged();
    }

    internal void RefreshSlotNumber(int slotNumber)
    {
        SlotNumber = slotNumber;
        OnPropertyChanged(nameof(DisplayName));
    }

    public bool ShowHarnessOperationalControls => IsActiveRuntimeSlot && _owner.ShowHarnessSetupPanel;

    internal void NotifyHarnessPanelChanged()
    {
        OnPropertyChanged(nameof(ShowHarnessOperationalControls));
    }

    internal void ApplyActiveRuntimeState(
        bool isActive,
        RouteIndicatorState state,
        string telemetryText,
        bool isStandby = false)
    {
        if (isActive)
        {
            isStandby = false;
        }

        var resolvedState = isActive || isStandby ? state : RouteIndicatorState.Off;
        var resolvedStatusText = isActive
            ? state switch
            {
                RouteIndicatorState.Live => "Running",
                RouteIndicatorState.Amber => "Starting",
                RouteIndicatorState.Fault => "Fault",
                _ => string.Empty
            }
            : isStandby
                ? state switch
                {
                    RouteIndicatorState.Live => "Hot — standing by",
                    RouteIndicatorState.Amber => "Hot — starting",
                    RouteIndicatorState.Fault => "Hot — fault",
                    _ => string.Empty
                }
                : string.Empty;
        var resolvedTelemetryText = isActive || isStandby ? telemetryText : string.Empty;
        if (IsActiveRuntimeSlot == isActive
            && IsStandbyHotSlot == isStandby
            && _activeRuntimeState == resolvedState
            && ActiveRuntimeStatusText == resolvedStatusText
            && ActiveRuntimeTelemetryText == resolvedTelemetryText)
        {
            return;
        }

        _activeRuntimeState = resolvedState;
        IsActiveRuntimeSlot = isActive;
        IsStandbyHotSlot = isStandby;
        ActiveRuntimeStatusText = resolvedStatusText;
        ActiveRuntimeTelemetryText = resolvedTelemetryText;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ShowActiveRuntimeStatus));
        OnPropertyChanged(nameof(ShowActiveRuntimeTelemetry));
        OnPropertyChanged(nameof(IsActiveRuntimeLive));
        OnPropertyChanged(nameof(IsActiveRuntimeAmber));
        OnPropertyChanged(nameof(IsActiveRuntimeFault));
        OnPropertyChanged(nameof(IsStandbyHotLive));
        OnPropertyChanged(nameof(IsStandbyHotAmber));
        OnPropertyChanged(nameof(IsStandbyHotFault));
        OnPropertyChanged(nameof(IsLoadingRuntimeSlot));
        if (IsLoadingRuntimeSlot && !LoadingPulseOn)
        {
            LoadingPulseOn = true;
        }
        else if (!IsLoadingRuntimeSlot && LoadingPulseOn)
        {
            LoadingPulseOn = false;
        }

        OnPropertyChanged(nameof(ActiveRuntimeTelemetryBrush));
        OnPropertyChanged(nameof(ShowHarnessOperationalControls));
    }

    internal void NotifyValidatedProfilesRefreshed()
    {
        ReplaceAvailableProfiles(_owner.GetAvailableValidatedProfilesForSlot(this));
        RefreshValidatedProfileOptions();
    }

    internal void SelectDraftProfile(ValidatedQuickSelectProfileOption profile)
    {
        var match = AvailableValidatedProfiles.FirstOrDefault(option =>
            option.Key.Equals(profile.Key, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            AvailableValidatedProfiles.Insert(0, profile);
            match = profile;
        }

        SelectedValidatedProfile = match;
    }

    internal void ReplaceAvailableProfiles(IReadOnlyList<ValidatedQuickSelectProfileOption> profiles)
    {
        var wasHydrating = _isHydrating;
        _isHydrating = true;
        try
        {
            var selectedKey = SelectedValidatedProfile?.Key ?? SavedAssignmentKey;
            AvailableValidatedProfiles.Clear();
            foreach (var profile in profiles)
            {
                AvailableValidatedProfiles.Add(profile);
            }

            if (!string.IsNullOrWhiteSpace(selectedKey))
            {
                var match = AvailableValidatedProfiles.FirstOrDefault(option =>
                    option.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    match = _owner.ValidatedQuickSelectProfiles.FirstOrDefault(option =>
                        option.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        AvailableValidatedProfiles.Insert(0, match);
                    }
                }

                if (!ReferenceEquals(SelectedValidatedProfile, match))
                {
                    SelectedValidatedProfile = match;
                }
            }
        }
        finally
        {
            _isHydrating = wasHydrating;
        }

        OnPropertyChanged(nameof(AvailableValidatedProfiles));
    }

    internal void RefreshValidatedProfileOptions()
    {
        var wasHydrating = _isHydrating;
        _isHydrating = true;
        try
        {
            if (HasUnsavedPopulatedAssignment)
            {
                var draft = SelectedValidatedProfile;
                SyncSelectedValidatedProfile();
                if (draft is not null
                    && !ReferenceEquals(SelectedValidatedProfile, draft)
                    && SelectedValidatedProfile?.Key.Equals(draft.Key, StringComparison.OrdinalIgnoreCase) != true)
                {
                    var restored = AvailableValidatedProfiles
                        .FirstOrDefault(option => option.Key.Equals(draft.Key, StringComparison.OrdinalIgnoreCase))
                        ?? _owner.ValidatedQuickSelectProfiles
                            .FirstOrDefault(option => option.Key.Equals(draft.Key, StringComparison.OrdinalIgnoreCase));
                    if (restored is not null)
                    {
                        SelectedValidatedProfile = restored;
                    }
                }
            }
            else
            {
                SyncSelectedValidatedProfile();
            }
        }
        finally
        {
            _isHydrating = wasHydrating;
            NotifyRouteStateChanged();
        }
    }

    partial void OnSelectedValidatedProfileChanged(ValidatedQuickSelectProfileOption? value)
    {
        if (_isHydrating)
        {
            NotifyRouteStateChanged();
            return;
        }

        NotifyRouteStateChanged();
        _owner.OnQuickSelectSlotDraftChanged(this);
    }

    internal bool TryCommitSelectedProfile()
    {
        if (SelectedValidatedProfile is null)
        {
            return false;
        }

        ApplyValidatedProfile(SelectedValidatedProfile);
        NotifyRouteStateChanged();
        return true;
    }

    internal void MarkEndpointValidated(string validatedUtc)
    {
        IsDefaultProfileValidated = true;
        EndpointValidatedUtc = validatedUtc;
    }

    internal void RetargetCloudModel(string model)
    {
        CloudModel = model ?? string.Empty;
        IsDefaultProfileValidated = false;
        EndpointValidatedUtc = string.Empty;
        NotifyRouteStateChanged();
    }

    private void TryApplyValidatedProfileKey(string validatedProfileKey)
    {
        if (HasSavedAssignment || string.IsNullOrWhiteSpace(validatedProfileKey))
        {
            return;
        }

        var parts = validatedProfileKey.Split(["::"], StringSplitOptions.None);
        if (parts.Length < 4)
        {
            return;
        }

        if (parts[0].Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            RouteType = "Cloud";
            CloudProvider = parts[1];
            CloudModel = parts[2];
            CloudVariant = parts[3];
            LocalModel = string.Empty;
            LocalVariant = MainViewModel.BaseVariantDisplayName;
            return;
        }

        if (parts[0].Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            RouteType = "Local";
            CloudProvider = string.Empty;
            CloudModel = string.Empty;
            CloudVariant = MainViewModel.BaseVariantDisplayName;
            LocalModel = parts[2];
            LocalVariant = parts[3];
        }
    }

    private void ApplyValidatedProfile(ValidatedQuickSelectProfileOption profile)
    {
        RouteType = profile.RouteType;
        if (profile.RouteType.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            CloudProvider = profile.Provider;
            CloudModel = profile.Model;
            CloudVariant = profile.Variant;
            LocalModel = string.Empty;
            LocalVariant = MainViewModel.BaseVariantDisplayName;
        }
        else
        {
            CloudProvider = string.Empty;
            CloudModel = string.Empty;
            CloudVariant = MainViewModel.BaseVariantDisplayName;
            LocalModel = profile.Model;
            LocalVariant = profile.Variant;
        }
    }

    private void SyncSelectedValidatedProfile()
    {
        var match = _owner.FindValidatedQuickSelectProfile(
            RouteType,
            CloudProvider,
            CloudModel,
            CloudVariant,
            LocalModel,
            LocalVariant);
        if (!ReferenceEquals(SelectedValidatedProfile, match))
        {
            SelectedValidatedProfile = match;
        }
    }

    private void NotifyRouteStateChanged()
    {
        OnPropertyChanged(nameof(IsCloudRoute));
        OnPropertyChanged(nameof(IsLocalRoute));
        OnPropertyChanged(nameof(HasSavedAssignment));
        OnPropertyChanged(nameof(HasValidatedProfile));
        OnPropertyChanged(nameof(HasUnsavedPopulatedAssignment));
        OnPropertyChanged(nameof(ShowSlotFacts));
        OnPropertyChanged(nameof(CanSaveSlot));
        OnPropertyChanged(nameof(CanLaunchSavedAssignment));
        OnPropertyChanged(nameof(SavedAssignmentKey));
        _owner.RefreshQuickSelectSlotFacts(this);
    }
}
