using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.ViewModels;

public partial class PortForwardingRulesViewModel : ObservableObject
{
    private readonly PortForwardingRulesStore _store;
    private readonly Action<PortForwardingRules> _apply;
    private bool _hydrating;

    public PortForwardingRulesViewModel(
        PortForwardingRulesStore store,
        Action<PortForwardingRules> apply)
    {
        _store = store;
        _apply = apply;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                || e.PropertyName == nameof(StatusText)
                || e.PropertyName.EndsWith("Tally", StringComparison.Ordinal))
            {
                return;
            }

            RefreshTallies();
        };
        Hydrate(PortForwardingRules.Defaults);
    }

    public IReadOnlyList<string> ClientMaxTokensModeOptions => PortForwardingRules.ClientMaxTokensModeLabels;

    public IReadOnlyList<string> StopHygieneModeOptions => PortForwardingRules.StopHygieneModeLabels;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial bool CompactEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool OmitEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool MaxPicturesEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool MillAtOmittedEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool ClosedLoopLookbackEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool ClosedLoopCeilingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool ObserveOnlyMillEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool RapidChurnEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool HangEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool Loading503Enabled { get; set; } = true;

    [ObservableProperty]
    public partial bool RepeatedCommandEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool DiagnosticDumpEnabled { get; set; } = true;

    [ObservableProperty]
    public partial decimal CompactWatermarkPercent { get; set; } = 85;

    [ObservableProperty]
    public partial decimal CompactKeepTurns { get; set; } = LocalHistoryCompaction.KeepTurns;

    [ObservableProperty]
    public partial decimal CompactToolKeepTurns { get; set; } = LocalHistoryCompaction.ToolKeepTurns;

    [ObservableProperty]
    public partial decimal CompactHeadroom { get; set; } = LocalHistoryCompaction.Headroom;

    [ObservableProperty]
    public partial decimal CompactPreservedUserChars { get; set; } = LocalHistoryCompaction.PreservedUserChars;

    [ObservableProperty]
    public partial decimal KeepRecentResults { get; set; } = LocalToolResultClearing.KeepRecentResults;

    [ObservableProperty]
    public partial decimal PinLatestShellResults { get; set; } = LocalToolResultClearing.PinLatestShellResults;

    [ObservableProperty]
    public partial decimal RunawayOmittedResults { get; set; } = LocalToolResultClearing.RunawayOmittedResults;

    [ObservableProperty]
    public partial decimal ClosedLoopRunawayOmittedResults { get; set; } = LocalToolResultClearing.ClosedLoopRunawayOmittedResults;

    [ObservableProperty]
    public partial decimal ObserveOnlyMillCount { get; set; } = LocalToolResultClearing.ObserveOnlyMillCount;

    [ObservableProperty]
    public partial decimal ClosedLoopMillLookback { get; set; } = LocalToolResultClearing.ClosedLoopMillLookback;

    [ObservableProperty]
    public partial decimal RapidChurnSeconds { get; set; } = (decimal)LocalToolResultClearing.RapidChurnSeconds;

    [ObservableProperty]
    public partial decimal RapidChurnConsecutive { get; set; } = LocalToolResultClearing.RapidChurnConsecutive;

    [ObservableProperty]
    public partial decimal MinResultCharsToClear { get; set; } = LocalToolResultClearing.MinResultCharsToClear;

    [ObservableProperty]
    public partial decimal MaxForwardedImages { get; set; } = LocalChatPayloadSignals.MaxForwardedImages;

    [ObservableProperty]
    public partial decimal FirstByteSeconds { get; set; } = LocalStreamHangPolicy.FirstByteSeconds;

    [ObservableProperty]
    public partial decimal ThinkTokensPerSecond { get; set; } = LocalStreamHangPolicy.ThinkTokensPerSecond;

    [ObservableProperty]
    public partial decimal MaxThinkFirstByteSeconds { get; set; } = LocalStreamHangPolicy.MaxThinkFirstByteSeconds;

    [ObservableProperty]
    public partial decimal StallSeconds { get; set; } = LocalStreamHangPolicy.StallSeconds;

    [ObservableProperty]
    public partial decimal WaitLongerSeconds { get; set; } = LocalStreamHangPolicy.WaitLongerSeconds;

    [ObservableProperty]
    public partial decimal DecisionSeconds { get; set; } = LocalStreamHangPolicy.DecisionSeconds;

    [ObservableProperty]
    public partial decimal LoadingRetryCount { get; set; } = LocalLoadingRetryPolicy.DefaultRetryCount;

    [ObservableProperty]
    public partial decimal LoadingRetryDelaySeconds { get; set; } = LocalLoadingRetryPolicy.DefaultDelaySeconds;

    [ObservableProperty]
    public partial bool SkipPrefixCacheAfterCompact { get; set; } = true;

    [ObservableProperty]
    public partial string SelectedClientMaxTokensMode { get; set; } = PortForwardingRules.ClientMaxTokensProfileLabel;

    [ObservableProperty]
    public partial string SelectedStopHygieneMode { get; set; } = PortForwardingRules.StopPassThroughLabel;

    /// <summary>
    /// Save / Load / Revert / dirty-file notice only. A Port-rule stop
    /// goes to Diagnostics, not this line.
    /// </summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MillAtOmittedTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClosedLoopCeilingTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ObserveOnlyTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RapidChurnTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MaxPicturesTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HangTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Loading503Tally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DumpTally { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepeatedCommandTally { get; set; } = string.Empty;

    private PortRulesTelemetry _lastTelemetry = PortRulesTelemetry.Empty;

    public void ApplyTelemetry(PortRulesTelemetry? telemetry)
    {
        _lastTelemetry = telemetry ?? PortRulesTelemetry.Empty;
        RefreshTallies();
    }

    private void RefreshTallies()
    {
        var live = ToRules();
        var snap = _lastTelemetry;
        MillAtOmittedTally = snap.MillAtOmittedLine(live);
        ClosedLoopCeilingTally = snap.ClosedLoopCeilingLine(live);
        ObserveOnlyTally = snap.ObserveOnlyLine(live);
        RapidChurnTally = snap.RapidChurnLine(live);
        MaxPicturesTally = snap.MaxPicturesLine(live);
        HangTally = snap.HangLine(live);
        Loading503Tally = snap.Loading503Line(live);
        DumpTally = snap.DumpLine(live);
        RepeatedCommandTally = snap.RepeatedCommandLine(live);
    }

    public PortForwardingRules ToRules()
        => new PortForwardingRules
        {
            Enabled = Enabled,
            CompactEnabled = CompactEnabled,
            OmitEnabled = OmitEnabled,
            MaxPicturesEnabled = MaxPicturesEnabled,
            MillAtOmittedEnabled = MillAtOmittedEnabled,
            ClosedLoopLookbackEnabled = ClosedLoopLookbackEnabled,
            ClosedLoopCeilingEnabled = ClosedLoopCeilingEnabled,
            ObserveOnlyMillEnabled = ObserveOnlyMillEnabled,
            RapidChurnEnabled = RapidChurnEnabled,
            HangEnabled = HangEnabled,
            Loading503Enabled = Loading503Enabled,
            RepeatedCommandEnabled = RepeatedCommandEnabled,
            DiagnosticDumpEnabled = DiagnosticDumpEnabled,
            CompactWatermarkPercent = (int)CompactWatermarkPercent,
            CompactKeepTurns = (int)CompactKeepTurns,
            CompactToolKeepTurns = (int)CompactToolKeepTurns,
            CompactHeadroom = (int)CompactHeadroom,
            CompactPreservedUserChars = (int)CompactPreservedUserChars,
            KeepRecentResults = (int)KeepRecentResults,
            PinLatestShellResults = (int)PinLatestShellResults,
            RunawayOmittedResults = (int)RunawayOmittedResults,
            ClosedLoopRunawayOmittedResults = (int)ClosedLoopRunawayOmittedResults,
            ObserveOnlyMillCount = (int)ObserveOnlyMillCount,
            ClosedLoopMillLookback = (int)ClosedLoopMillLookback,
            RapidChurnSeconds = (double)RapidChurnSeconds,
            RapidChurnConsecutive = (int)RapidChurnConsecutive,
            MinResultCharsToClear = (int)MinResultCharsToClear,
            MaxForwardedImages = (int)MaxForwardedImages,
            FirstByteSeconds = (int)FirstByteSeconds,
            ThinkTokensPerSecond = (int)ThinkTokensPerSecond,
            MaxThinkFirstByteSeconds = (int)MaxThinkFirstByteSeconds,
            StallSeconds = (int)StallSeconds,
            WaitLongerSeconds = (int)WaitLongerSeconds,
            DecisionSeconds = (int)DecisionSeconds,
            LoadingRetryCount = (int)LoadingRetryCount,
            LoadingRetryDelaySeconds = (int)LoadingRetryDelaySeconds,
            SkipPrefixCacheAfterCompact = SkipPrefixCacheAfterCompact,
            ClientMaxTokensMode = PortForwardingRules.NormalizeClientMaxTokensMode(SelectedClientMaxTokensMode),
            StopHygieneMode = PortForwardingRules.NormalizeStopHygieneMode(SelectedStopHygieneMode)
        }.Clamp();

    public PortForwardingRules Load()
    {
        var rules = _store.Load();
        Hydrate(rules);
        _apply(rules);
        StatusText = string.IsNullOrWhiteSpace(_store.LastLoadNotice)
            ? string.Empty
            : _store.LastLoadNotice;
        return rules;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_hydrating)
        {
            return;
        }

        var rules = ToRules();
        _store.Save(rules);
        _apply(rules);
        StatusText = value
            ? "Port rules on. They apply to the next Client-app turn."
            : "Port rules off. Client-app turns go to llama-server without intervention.";
    }

    [RelayCommand]
    private void Save()
    {
        var rules = ToRules();
        Hydrate(rules);
        _store.Save(rules);
        _apply(rules);
        StatusText = "Port rules saved. They apply to the next Client-app turn.";
    }

    [RelayCommand]
    private void LoadSaved()
    {
        Load();
        if (!string.IsNullOrWhiteSpace(StatusText))
        {
            return;
        }

        StatusText = File.Exists(_store.Path)
            ? "Loaded the last saved Port rules. They apply to the next Client-app turn."
            : "No saved Port rules file. Showing built-in defaults.";
    }

    [RelayCommand]
    private void Reset()
    {
        var rules = PortForwardingRules.Defaults.Clamp();
        Hydrate(rules);
        _apply(rules);
        StatusText = "Showing built-in defaults. Save Port rules to keep them, or Load Port rules to put the last saved file back.";
    }

    private void Hydrate(PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        _hydrating = true;
        try
        {
        Enabled = live.Enabled;
        CompactEnabled = live.CompactEnabled;
        OmitEnabled = live.OmitEnabled;
        MaxPicturesEnabled = live.MaxPicturesEnabled;
        MillAtOmittedEnabled = live.MillAtOmittedEnabled;
        ClosedLoopLookbackEnabled = live.ClosedLoopLookbackEnabled;
        ClosedLoopCeilingEnabled = live.ClosedLoopCeilingEnabled;
        ObserveOnlyMillEnabled = live.ObserveOnlyMillEnabled;
        RapidChurnEnabled = live.RapidChurnEnabled;
        HangEnabled = live.HangEnabled;
        Loading503Enabled = live.Loading503Enabled;
        RepeatedCommandEnabled = live.RepeatedCommandEnabled;
        DiagnosticDumpEnabled = live.DiagnosticDumpEnabled;
        CompactWatermarkPercent = live.CompactWatermarkPercent;
        CompactKeepTurns = live.CompactKeepTurns;
        CompactToolKeepTurns = live.CompactToolKeepTurns;
        CompactHeadroom = live.CompactHeadroom;
        CompactPreservedUserChars = live.CompactPreservedUserChars;
        KeepRecentResults = live.KeepRecentResults;
        PinLatestShellResults = live.PinLatestShellResults;
        RunawayOmittedResults = live.RunawayOmittedResults;
        ClosedLoopRunawayOmittedResults = live.ClosedLoopRunawayOmittedResults;
        ObserveOnlyMillCount = live.ObserveOnlyMillCount;
        ClosedLoopMillLookback = live.ClosedLoopMillLookback;
        RapidChurnSeconds = (decimal)live.RapidChurnSeconds;
        RapidChurnConsecutive = live.RapidChurnConsecutive;
        MinResultCharsToClear = live.MinResultCharsToClear;
        MaxForwardedImages = live.MaxForwardedImages;
        FirstByteSeconds = live.FirstByteSeconds;
        ThinkTokensPerSecond = live.ThinkTokensPerSecond;
        MaxThinkFirstByteSeconds = live.MaxThinkFirstByteSeconds;
        StallSeconds = live.StallSeconds;
        WaitLongerSeconds = live.WaitLongerSeconds;
        DecisionSeconds = live.DecisionSeconds;
        LoadingRetryCount = live.LoadingRetryCount;
        LoadingRetryDelaySeconds = live.LoadingRetryDelaySeconds;
        SkipPrefixCacheAfterCompact = live.SkipPrefixCacheAfterCompact;
        SelectedClientMaxTokensMode = PortForwardingRules.ClientMaxTokensModeLabel(live.ClientMaxTokensMode);
        SelectedStopHygieneMode = PortForwardingRules.StopHygieneModeLabel(live.StopHygieneMode);
        }
        finally
        {
            _hydrating = false;
        }
    }
}
