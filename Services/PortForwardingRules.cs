using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Port-wide forwarding hygiene. Not llama-server launch args and not a
/// model profile. Missing or dirty JSON falls back to <see cref="Defaults"/>.
/// </summary>
public sealed record PortForwardingRules
{
    public const string ClientMaxTokensProfile = "profile";
    public const string ClientMaxTokensClient = "client";
    public const string ClientMaxTokensSmaller = "smaller";
    public const string StopPassThrough = "passthrough";
    public const string StopIgnore = "ignore";

    public const string ClientMaxTokensProfileLabel = "Prefer profile";
    public const string ClientMaxTokensClientLabel = "Prefer Client app";
    public const string ClientMaxTokensSmallerLabel = "Prefer smaller of the two";
    public const string StopPassThroughLabel = "Pass through";
    public const string StopIgnoreLabel = "Ignore Client-app stop";

    public static readonly string[] ClientMaxTokensModeLabels =
    [
        ClientMaxTokensProfileLabel,
        ClientMaxTokensClientLabel,
        ClientMaxTokensSmallerLabel
    ];

    public static readonly string[] StopHygieneModeLabels =
    [
        StopPassThroughLabel,
        StopIgnoreLabel
    ];

    public static PortForwardingRules Defaults { get; } = new();

    public bool Enabled { get; init; } = true;
    public bool CompactEnabled { get; init; } = true;
    public bool OmitEnabled { get; init; } = true;
    public bool MaxPicturesEnabled { get; init; } = true;
    public bool MillAtOmittedEnabled { get; init; } = true;
    public bool ClosedLoopLookbackEnabled { get; init; } = true;
    public bool ClosedLoopCeilingEnabled { get; init; } = true;
    public bool ObserveOnlyMillEnabled { get; init; } = true;
    public bool RapidChurnEnabled { get; init; } = true;
    public bool HangEnabled { get; init; } = true;
    public bool Loading503Enabled { get; init; } = true;
    public bool RepeatedCommandEnabled { get; init; } = true;
    public bool DiagnosticDumpEnabled { get; init; } = true;

    public int CompactWatermarkPercent { get; init; } = 85;
    public int CompactKeepTurns { get; init; } = LocalHistoryCompaction.KeepTurns;
    public int CompactToolKeepTurns { get; init; } = LocalHistoryCompaction.ToolKeepTurns;
    public int CompactHeadroom { get; init; } = LocalHistoryCompaction.Headroom;
    public int CompactPreservedUserChars { get; init; } = LocalHistoryCompaction.PreservedUserChars;

    public int KeepRecentResults { get; init; } = LocalToolResultClearing.KeepRecentResults;
    public int PinLatestShellResults { get; init; } = LocalToolResultClearing.PinLatestShellResults;
    public int RunawayOmittedResults { get; init; } = LocalToolResultClearing.RunawayOmittedResults;
    public int ClosedLoopRunawayOmittedResults { get; init; } = LocalToolResultClearing.ClosedLoopRunawayOmittedResults;
    public int ObserveOnlyMillCount { get; init; } = LocalToolResultClearing.ObserveOnlyMillCount;
    public int ClosedLoopMillLookback { get; init; } = LocalToolResultClearing.ClosedLoopMillLookback;
    public double RapidChurnSeconds { get; init; } = LocalToolResultClearing.RapidChurnSeconds;
    public int RapidChurnConsecutive { get; init; } = LocalToolResultClearing.RapidChurnConsecutive;
    public int MinResultCharsToClear { get; init; } = LocalToolResultClearing.MinResultCharsToClear;
    public int MaxForwardedImages { get; init; } = LocalChatPayloadSignals.MaxForwardedImages;

    public int FirstByteSeconds { get; init; } = LocalStreamHangPolicy.FirstByteSeconds;
    public int ThinkTokensPerSecond { get; init; } = LocalStreamHangPolicy.ThinkTokensPerSecond;
    public int MaxThinkFirstByteSeconds { get; init; } = LocalStreamHangPolicy.MaxThinkFirstByteSeconds;
    public int StallSeconds { get; init; } = LocalStreamHangPolicy.StallSeconds;
    public int WaitLongerSeconds { get; init; } = LocalStreamHangPolicy.WaitLongerSeconds;
    public int DecisionSeconds { get; init; } = LocalStreamHangPolicy.DecisionSeconds;

    public int LoadingRetryCount { get; init; } = LocalLoadingRetryPolicy.DefaultRetryCount;
    public int LoadingRetryDelaySeconds { get; init; } = LocalLoadingRetryPolicy.DefaultDelaySeconds;
    public bool SkipPrefixCacheAfterCompact { get; init; } = true;
    public string ClientMaxTokensMode { get; init; } = ClientMaxTokensProfile;
    public string StopHygieneMode { get; init; } = StopPassThrough;

    public double CompactWatermark => CompactWatermarkPercent / 100.0;

    public int MinCopyTimeoutSeconds
        => FirstByteSeconds + DecisionSeconds + WaitLongerSeconds + DecisionSeconds;

    public int CopyTimeoutExtendSeconds
        => WaitLongerSeconds + DecisionSeconds + 30;

    /// <summary>
    /// Rules the gateway should honor. When <see cref="Enabled"/> is off,
    /// Client-app turns go to llama-server as usual.
    /// </summary>
    public PortForwardingRules ForForwarding()
    {
        var live = Clamp();
        if (live.Enabled)
        {
            return live;
        }

        return live with
        {
            CompactEnabled = false,
            OmitEnabled = false,
            MaxPicturesEnabled = false,
            MillAtOmittedEnabled = false,
            ClosedLoopLookbackEnabled = false,
            ClosedLoopCeilingEnabled = false,
            ObserveOnlyMillEnabled = false,
            RapidChurnEnabled = false,
            HangEnabled = false,
            Loading503Enabled = false,
            RepeatedCommandEnabled = false,
            DiagnosticDumpEnabled = false,
            SkipPrefixCacheAfterCompact = false,
            ClientMaxTokensMode = ClientMaxTokensClient,
            StopHygieneMode = StopPassThrough
        };
    }

    public PortForwardingRules Clamp()
        => this with
        {
            CompactWatermarkPercent = Math.Clamp(CompactWatermarkPercent, 50, 95),
            CompactKeepTurns = Math.Clamp(CompactKeepTurns, 4, 24),
            CompactToolKeepTurns = Math.Clamp(CompactToolKeepTurns, 6, 32),
            CompactHeadroom = Math.Clamp(CompactHeadroom, 64, 4096),
            CompactPreservedUserChars = Math.Clamp(CompactPreservedUserChars, 500, 20000),
            KeepRecentResults = Math.Clamp(KeepRecentResults, 4, 64),
            PinLatestShellResults = Math.Clamp(PinLatestShellResults, 1, 16),
            RunawayOmittedResults = Math.Clamp(RunawayOmittedResults, 8, 128),
            ClosedLoopRunawayOmittedResults = Math.Max(
                Math.Clamp(RunawayOmittedResults, 8, 128),
                Math.Clamp(ClosedLoopRunawayOmittedResults, 32, 512)),
            ObserveOnlyMillCount = Math.Clamp(ObserveOnlyMillCount, 4, 24),
            ClosedLoopMillLookback = Math.Clamp(ClosedLoopMillLookback, 2, 16),
            RapidChurnSeconds = Math.Clamp(RapidChurnSeconds, 1, 15),
            RapidChurnConsecutive = Math.Clamp(RapidChurnConsecutive, 2, 12),
            MinResultCharsToClear = Math.Clamp(MinResultCharsToClear, 80, 2000),
            MaxForwardedImages = Math.Clamp(MaxForwardedImages, 1, 8),
            FirstByteSeconds = Math.Clamp(FirstByteSeconds, 5, 120),
            ThinkTokensPerSecond = Math.Clamp(ThinkTokensPerSecond, 5, 80),
            MaxThinkFirstByteSeconds = Math.Clamp(MaxThinkFirstByteSeconds, 30, 300),
            StallSeconds = Math.Clamp(StallSeconds, 10, 180),
            WaitLongerSeconds = Math.Clamp(WaitLongerSeconds, 15, 180),
            DecisionSeconds = Math.Clamp(DecisionSeconds, 10, 120),
            LoadingRetryCount = Math.Clamp(LoadingRetryCount, 0, 12),
            LoadingRetryDelaySeconds = Math.Clamp(LoadingRetryDelaySeconds, 1, 10),
            ClientMaxTokensMode = NormalizeClientMaxTokensMode(ClientMaxTokensMode),
            StopHygieneMode = NormalizeStopHygieneMode(StopHygieneMode)
        };

    public static string NormalizeClientMaxTokensMode(string? mode)
    {
        var value = (mode ?? string.Empty).Trim();
        if (value.Equals(ClientMaxTokensClient, StringComparison.OrdinalIgnoreCase)
            || value.Equals(ClientMaxTokensClientLabel, StringComparison.OrdinalIgnoreCase))
        {
            return ClientMaxTokensClient;
        }

        if (value.Equals(ClientMaxTokensSmaller, StringComparison.OrdinalIgnoreCase)
            || value.Equals(ClientMaxTokensSmallerLabel, StringComparison.OrdinalIgnoreCase))
        {
            return ClientMaxTokensSmaller;
        }

        return ClientMaxTokensProfile;
    }

    public static string NormalizeStopHygieneMode(string? mode)
    {
        var value = (mode ?? string.Empty).Trim();
        if (value.Equals(StopIgnore, StringComparison.OrdinalIgnoreCase)
            || value.Equals(StopIgnoreLabel, StringComparison.OrdinalIgnoreCase))
        {
            return StopIgnore;
        }

        return StopPassThrough;
    }

    public static string ClientMaxTokensModeLabel(string? mode)
        => NormalizeClientMaxTokensMode(mode) switch
        {
            ClientMaxTokensClient => ClientMaxTokensClientLabel,
            ClientMaxTokensSmaller => ClientMaxTokensSmallerLabel,
            _ => ClientMaxTokensProfileLabel
        };

    public static string StopHygieneModeLabel(string? mode)
        => NormalizeStopHygieneMode(mode) == StopIgnore
            ? StopIgnoreLabel
            : StopPassThroughLabel;
}
