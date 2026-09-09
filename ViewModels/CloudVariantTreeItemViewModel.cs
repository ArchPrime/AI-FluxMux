using System;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.ViewModels;

public sealed partial class CloudVariantTreeItemViewModel : ObservableObject
{
    private static readonly IBrush DefaultHeaderBackground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
    private static readonly IBrush DefaultHeaderBorder = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly IBrush DefaultHeaderForeground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly IBrush DefaultHeaderMuted = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly IBrush CopyHeaderBackground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly IBrush CopyHeaderBorder = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly IBrush CopyHeaderForeground = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
    private static readonly IBrush CopyHeaderMuted = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
    private static readonly IBrush ValidatedStatusForeground = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly IBrush StaleStatusForeground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly IBrush PendingStatusForeground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));

    public CloudVariantTreeItemViewModel(
        string modelName,
        string variantName,
        bool isBase,
        string settingsSummary,
        string provider = "",
        bool isMissingLocalFile = false,
        bool isUnlocked = false,
        string warningTooltip = "",
        bool isValidated = false,
        MainViewModel? editorHost = null,
        string configKey = "")
    {
        ModelName = modelName;
        Provider = provider;
        _editorHost = editorHost;
        Name = variantName;
        IsBase = isBase;
        IsUnlocked = isUnlocked;
        RowMargin = new Thickness(isBase ? 0 : 16, 0, 0, 0);
        DisplayName = BuildDisplayName(modelName, variantName, isBase);
        SettingsSummary = settingsSummary;
        IsExpanded = false;
        var logo = CorporateLogoCatalog.Resolve(provider, modelName);
        LogoAssetKey = logo?.AssetKey;
        LogoCompanyName = logo?.CompanyName ?? string.Empty;
        HasLogo = logo is not null;
        ConfigKey = configKey ?? string.Empty;
        ApplyEndpointStatus(
            showWarning: isMissingLocalFile || !string.IsNullOrWhiteSpace(warningTooltip),
            warningTooltip: isMissingLocalFile
                ? MainViewModel.MissingLocalFileWarningTooltip
                : warningTooltip,
            isValidatedOnDisk: isValidated,
            hasUnsavedEdits: false);
    }

    public void ApplyVariantName(string variantName)
    {
        Name = variantName;
        RefreshDisplayName();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
    }

    public void ApplyModelName(string modelName)
    {
        ModelName = modelName ?? string.Empty;
        RefreshDisplayName();
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(DisplayName));
    }

    private void RefreshDisplayName()
    {
        DisplayName = BuildDisplayName(ModelName, Name, IsBase);
    }

    private static string BuildDisplayName(string modelName, string variantName, bool isBase)
    {
        var modelLabel = string.IsNullOrWhiteSpace(modelName) ? "Missing model id" : modelName;
        return isBase
            ? variantName.Equals(MainViewModel.BaseVariantDisplayName, StringComparison.OrdinalIgnoreCase)
                ? $"{modelLabel} (default)"
                : $"{modelLabel}: {variantName} (default)"
            : $"{modelLabel}: {variantName}";
    }

    public void ApplyProfileWarning(bool show, string tooltip)
        => ApplyEndpointStatus(show, tooltip, isValidatedOnDisk: false, hasUnsavedEdits: false);

    public void ApplyEndpointStatus(bool showWarning, string warningTooltip, bool isValidatedOnDisk, bool hasUnsavedEdits)
    {
        ShowMissingFileWarning = showWarning;
        var compactTooltip = string.IsNullOrWhiteSpace(warningTooltip)
            ? string.Empty
            : LocalHealthGuidance.FormatProfilePanelWarning(warningTooltip);
        MissingFileWarningTooltip = string.IsNullOrWhiteSpace(compactTooltip)
            ? MainViewModel.DefaultEndpointWarningTooltip
            : compactTooltip;
        ShowValidatedCheck = !showWarning && isValidatedOnDisk && !hasUnsavedEdits;
        if (showWarning)
        {
            EndpointStatusText = MissingFileWarningTooltip;
            EndpointStatusForeground = StaleStatusForeground;
        }
        else if (isValidatedOnDisk && hasUnsavedEdits)
        {
            EndpointStatusText = MainViewModel.UnsavedEditsAfterValidateStatusText;
            EndpointStatusForeground = StaleStatusForeground;
        }
        else if (isValidatedOnDisk)
        {
            EndpointStatusText = MainViewModel.ValidatedEndpointStatusText;
            EndpointStatusForeground = ValidatedStatusForeground;
        }
        else
        {
            EndpointStatusText = MainViewModel.NotYetValidatedEndpointStatusText;
            EndpointStatusForeground = PendingStatusForeground;
        }

        OnPropertyChanged(nameof(ShowMissingFileWarning));
        OnPropertyChanged(nameof(MissingFileWarningTooltip));
        OnPropertyChanged(nameof(ShowValidatedCheck));
        OnPropertyChanged(nameof(EndpointStatusText));
        OnPropertyChanged(nameof(EndpointStatusForeground));
        OnPropertyChanged(nameof(ProfileParadigmTooltip));
    }

    public string ModelName { get; private set; }

    public string Provider { get; }

    public string ConfigKey { get; }

    public string Name { get; private set; }

    public bool IsBase { get; }

    public string DisplayName { get; private set; }

    public string? LogoAssetKey { get; }

    public string LogoCompanyName { get; }

    public bool HasLogo { get; }

    public bool IsEditable => !IsBase || IsUnlocked;

    public bool CanEditSettings => IsEditable && IsActiveEditor;

    public bool CanDeleteProfile => IsActiveEditor && (IsEditable || string.IsNullOrWhiteSpace(ModelName));

    public bool CanValidateToEndpoint => IsActiveEditor;

    public bool ShowDefaultLockToggle => IsBase;

    public bool ShowLockedPadlock => IsBase && !IsUnlocked;

    public bool ShowUnlockedPadlock => IsBase && IsUnlocked;

    private readonly MainViewModel? _editorHost;

    private bool IsActiveEditor => IsSelected || IsExpanded;

    public object? InlineEditor => IsExpanded ? _editorHost : null;

    public string DefaultLockTooltip => IsUnlocked
        ? "Lock this default again. Saved settings are kept."
        : "Unlock this default to edit, rename, or delete it.";

    public bool ShowMissingFileWarning { get; private set; }

    public bool ShowValidatedCheck { get; private set; }

    public string MissingFileWarningTooltip { get; private set; } = string.Empty;

    public string EndpointStatusText { get; private set; } = MainViewModel.NotYetValidatedEndpointStatusText;

    public IBrush EndpointStatusForeground { get; private set; } = PendingStatusForeground;

    public string RoleLabel =>
        IsBase
            ? (IsUnlocked ? "unlocked default model profile" : "read-only default model profile")
            : "editable model profile variant";

    public bool ShowEditingMarker => IsExpanded && IsEditable;

    public IBrush HeaderBackground => IsBase ? DefaultHeaderBackground : CopyHeaderBackground;

    public IBrush HeaderBorderBrush => IsBase ? DefaultHeaderBorder : CopyHeaderBorder;

    public IBrush HeaderForeground => IsBase ? DefaultHeaderForeground : CopyHeaderForeground;

    public IBrush HeaderMutedForeground => IsBase ? DefaultHeaderMuted : CopyHeaderMuted;

    public string? ProfileParadigmTooltip
    {
        get
        {
            var missing = ShowMissingFileWarning
                ? " Warning: " + MissingFileWarningTooltip
                : ShowValidatedCheck
                    ? " " + MainViewModel.ValidatedEndpointStatusText
                    : string.Empty;
            var deleteNote = string.IsNullOrWhiteSpace(Provider)
                ? " Delete removes this model and its copies from AI-FluxMux (the local model on disk is not deleted)."
                : " Delete removes this model and its copies from AI-FluxMux (nothing is deleted at the cloud company).";
            return IsBase
                ? (IsUnlocked
                    ? "This is the default for this model, and it is unlocked. You can edit, rename, or delete it. Click the open padlock to lock it again. Make New Variant still creates a copy."
                    : "Locked default for this model. Click the padlock if the automatic settings are wrong or you want to edit this default. Make New Variant still creates a copy." + deleteNote)
                    + missing
                : "Your editable model profile variant. Changes here do not alter the default." + missing;
        }
    }

    public Thickness RowMargin { get; }

    [ObservableProperty]
    public partial string SettingsSummary { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsUnlocked { get; set; }

    partial void OnIsSelectedChanged(bool value) => NotifyDefaultEditability();

    partial void OnIsExpandedChanged(bool value)
    {
        NotifyDefaultEditability();
        OnPropertyChanged(nameof(InlineEditor));
    }

    partial void OnIsUnlockedChanged(bool value) => NotifyDefaultEditability();

    private void NotifyDefaultEditability()
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanValidateToEndpoint));
        OnPropertyChanged(nameof(ShowDefaultLockToggle));
        OnPropertyChanged(nameof(ShowLockedPadlock));
        OnPropertyChanged(nameof(ShowUnlockedPadlock));
        OnPropertyChanged(nameof(DefaultLockTooltip));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(ShowEditingMarker));
        OnPropertyChanged(nameof(InlineEditor));
        OnPropertyChanged(nameof(ProfileParadigmTooltip));
    }
}
