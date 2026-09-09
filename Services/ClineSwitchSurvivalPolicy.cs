using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Cline (and other OpenAI-compatible apps) keep one Port address and
/// model id <c>local</c>. They do not bind a task to a GGUF or cloud name.
/// Live-Switch Testing only probes Port after each profile is ready — that
/// is Cline's next message, not an in-flight Cline turn.
/// Routing-dialog copy always covers Cline and Harness together: more than
/// one Client app can use Port at once, so the Servers list is not a
/// nomination of who is talking.
/// </summary>
public static class ClineSwitchSurvivalPolicy
{
    public const string SameTaskAfterFailedTurnAdvice =
        "This Cline turn is over. After a model that can take this thread is ready, the next message in this task can use it. Start a new task if the same error would repeat or Cline is looping a command.";

    public const string SameTaskAfterReloadAdvice =
        "This Cline turn cannot stay open through a llama-server reload. After the new model is ready, the next message in this task can use it. Start a new task if the same error would repeat or Cline is looping a command.";

    public const string HarnessIfAlsoOnPortAdvice =
        "If Harness is also using Port, do not continue that Harness chat after a llama-server reload — open Harness chat from that model's Quick Select slot.";

    public static string FormatFailedTurnAdvice(string? endpointApp = null)
    {
        _ = endpointApp;
        return SameTaskAfterFailedTurnAdvice + " " + HarnessIfAlsoOnPortAdvice;
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
