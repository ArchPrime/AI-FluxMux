using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Cline (and other OpenAI-compatible apps) keep one Port address and
/// model id <c>local</c>. They do not bind a task to a GGUF or cloud name.
/// Live-Switch Testing only probes Port after each profile is ready — that
/// is Cline's next message, not an in-flight Cline turn.
/// Printed follow-through says Client app so any OpenAI-compatible app
/// fits. Name Cline or Harness only on Help topics and Harness-only
/// controls (yaml, Quick Select launcher). More than one Client app can
/// use Port at once; the Servers list is not a nomination of who is talking.
/// </summary>
public static class ClineSwitchSurvivalPolicy
{
    public const string SameTaskAfterFailedTurnAdvice = PortRulesPostMortem.PortRuleStopAdvice;

    public const string MillStopAdvice = PortRulesPostMortem.PortRuleStopAdvice;

    public const string SameTaskAfterReloadAdvice =
        "This Client-app turn cannot stay open through a llama-server reload. After the new model is ready, the next message in this Client-app chat can use it if that app keeps the conversation. Start a new chat in the Client app if it does not pick up the new model.";

    public const string HarnessIfAlsoOnPortAdvice =
        "A Client app may sit on reconnecting and not show a failed turn — do not continue that chat.";

    public static string FormatFailedTurnAdvice(string? endpointApp = null)
    {
        _ = endpointApp;
        return PortRulesPostMortem.PortRuleStopAdvice;
    }

    public static string FormatMillStopAdvice(string? endpointApp = null)
    {
        _ = endpointApp;
        return PortRulesPostMortem.PortRuleStopAdvice;
    }

    public static string FormatReloadAdvice(string? endpointApp = null)
    {
        _ = endpointApp;
        return SameTaskAfterReloadAdvice + " " + HarnessIfAlsoOnPortAdvice;
    }

    public static bool ThisTurnSurvives(
        bool targetAlreadyReady,
        bool llamaServerMustReload,
        bool thisTurnAlreadyFailed)
        => targetAlreadyReady
           && !llamaServerMustReload
           && !thisTurnAlreadyFailed;

    public static bool SameTaskNextMessageCanUseNewModel()
        => true;

    public static RecoveryChatFollowThrough Decide(
        bool harnessEndpoint,
        bool targetAlreadyReady,
        bool llamaServerMustReload,
        bool thisTurnAlreadyFailed)
    {
        _ = harnessEndpoint;
        if (ThisTurnSurvives(targetAlreadyReady, llamaServerMustReload, thisTurnAlreadyFailed))
        {
            return RecoveryChatFollowThrough.ContinueThisChat;
        }

        return RecoveryChatFollowThrough.ContinueSameTaskNextMessage;
    }
}
