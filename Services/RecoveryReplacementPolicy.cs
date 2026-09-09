using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public enum RecoveryChatFollowThrough
{
    ContinueThisChat,
    ContinueSameTaskNextMessage,
    NewHarnessChat,
    NewClineTask,
    NewChat
}

public readonly record struct RecoveryReplacementChoice(
    bool IsCloud,
    bool LlamaServerMustReload,
    bool TurnStillOpen,
    bool CloudAlreadyReady = true,
    bool LocalStillAlive = false);

/// <summary>
/// What to tell the operator after they pick a replacement on a switch prompt.
/// FluxMux cannot close Client-app tabs; this only chooses the advice.
/// </summary>
public static class RecoveryReplacementPolicy
{
    public const string ChooseOwnLabel = "Or choose a different model";
    public const string SwitchToSelectedLabel = "Switch to selected";

    public const string ContinueAdvice =
        "This Client-app chat can continue. The next turn uses the model you selected.";

    public const string SameTaskNextMessageAdvice =
        ClineSwitchSurvivalPolicy.SameTaskAfterReloadAdvice
        + " "
        + ClineSwitchSurvivalPolicy.HarnessIfAlsoOnPortAdvice;

    public const string NewHarnessAdvice =
        "Do not try to continue this chat. After you approve, AI-FluxMux opens Harness chat from that model's Quick Select slot.";

    public const string NewClineAdvice =
        ClineSwitchSurvivalPolicy.SameTaskAfterFailedTurnAdvice;

    public const string NewChatAdvice =
        "Do not try to continue this chat. Start a new chat in the Client app after the model is ready.";

    public static RecoveryChatFollowThrough Decide(
        string? endpointApp,
        RecoveryReplacementChoice choice)
    {
        var targetAlreadyReady = choice.IsCloud || !choice.LlamaServerMustReload;
        return ClineSwitchSurvivalPolicy.Decide(
            FluxMuxGatewayRouting.IsHarnessEndpoint(EndpointState(endpointApp)),
            targetAlreadyReady,
            choice.LlamaServerMustReload,
            thisTurnAlreadyFailed: !choice.TurnStillOpen);
    }

    public static string FormatAdvice(RecoveryChatFollowThrough followThrough)
        => followThrough switch
        {
            RecoveryChatFollowThrough.ContinueThisChat => ContinueAdvice,
            RecoveryChatFollowThrough.ContinueSameTaskNextMessage => SameTaskNextMessageAdvice,
            RecoveryChatFollowThrough.NewHarnessChat => NewHarnessAdvice,
            RecoveryChatFollowThrough.NewClineTask => NewClineAdvice,
            _ => NewChatAdvice
        };

    public static bool CloudPickIsAlreadyHot(
        bool cloudRouteActive,
        string? mountedProvider,
        string? mountedModel,
        string? pickProvider,
        string? pickModel)
    {
        if (!cloudRouteActive
            || string.IsNullOrWhiteSpace(pickProvider)
            || string.IsNullOrWhiteSpace(pickModel))
        {
            return false;
        }

        return pickProvider.Equals(mountedProvider ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && pickModel.Equals(mountedModel ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Any hop to cloud keeps this Harness chat. Harness only calls Port.
    /// </summary>
    public static bool ShouldOpenHarnessAfterCloudLaunch(
        bool harnessEndpoint,
        bool launchedNewCloud,
        bool localStillAlive)
    {
        _ = harnessEndpoint;
        _ = launchedNewCloud;
        _ = localStillAlive;
        return false;
    }

    public static bool SameChooserKeysAndNames(
        IReadOnlyList<string> currentKeys,
        IReadOnlyList<string> currentNames,
        IReadOnlyList<string> nextKeys,
        IReadOnlyList<string> nextNames)
    {
        if (currentKeys.Count != nextKeys.Count || currentNames.Count != nextNames.Count
            || currentKeys.Count != currentNames.Count)
        {
            return false;
        }

        for (var i = 0; i < currentKeys.Count; i++)
        {
            if (!currentKeys[i].Equals(nextKeys[i], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentNames[i], nextNames[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ShouldOpenHarnessAfterPromptedLaunch(bool harnessEndpoint, bool launchedNewRuntime)
        => harnessEndpoint && launchedNewRuntime;

    public static bool ShouldOpenHarnessAfterLocalSwap(bool harnessEndpoint, bool llamaServerMustReload)
        => ShouldOpenHarnessAfterPromptedLaunch(harnessEndpoint, llamaServerMustReload);

    public static bool LlamaServerMustReload(string? runningFingerprint, string? replacementFingerprint)
    {
        if (string.IsNullOrWhiteSpace(replacementFingerprint))
        {
            return true;
        }

        return !string.Equals(
            runningFingerprint ?? string.Empty,
            replacementFingerprint,
            StringComparison.Ordinal);
    }

    public static bool TurnStillOpen(bool cloudConsentWaiting, bool failOnTimeout, string? cloudSource)
    {
        if (RouteRecoveryPolicy.IsLocalHangAbortSource(cloudSource)
            || RouteRecoveryPolicy.IsCloudFailureSource(cloudSource)
            || RouteRecoveryPolicy.IsLocalHangSource(cloudSource))
        {
            return false;
        }

        return cloudConsentWaiting
               && RouteRecoveryPolicy.IsHotHopSource(cloudSource);
    }

    private static System.Text.Json.Nodes.JsonObject EndpointState(string? endpointApp)
        => new() { ["endpoint_app"] = endpointApp ?? string.Empty };
}
