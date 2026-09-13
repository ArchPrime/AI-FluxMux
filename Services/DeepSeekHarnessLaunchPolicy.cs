using System;
using System.Globalization;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Keep a live Harness web process when a Client-app turn is still on Port.
/// Restarting dsh to mint a fresh ?token= URL drops sessionController and
/// the open chat shows reconnecting / failed to load history.
/// </summary>
public static class DeepSeekHarnessLaunchPolicy
{
    public static readonly TimeSpan PreserveLiveWebWindow = TimeSpan.FromMinutes(2);

    public const string PreserveLiveWebMessage =
        "Harness web is already running and a Client-app chat just used Port. Restarting it now would drop that session. Use the open Harness tab.";

    public const string PreserveHygieneMessage =
        "Harness settings were updated. Leave the open Harness tab; start a new Harness chat after this turn finishes. Closing AI-FluxMux does not stop Harness web.";

    /// <summary>
    /// Closing AI-FluxMux must not kill dsh. That drops sessionController and
    /// the open tab sits on reconnecting with no Client-app error.
    /// </summary>
    public static bool ShouldStopManagedWebOnFluxMuxExit() => false;

    public static bool ShouldStopManagedWebForHygiene(
        bool webPortListening,
        int inFlightChatCount,
        DateTimeOffset? lastClientAppTurnUtc,
        DateTimeOffset utcNow)
        => !ShouldPreserveLiveWeb(
            webPortListening,
            inFlightChatCount,
            lastClientAppTurnUtc,
            utcNow);

    /// <summary>
    /// Harness web chat on a running Quick Select slot must not Stop a
    /// dsh that is already listening. That drops the chat FluxMux just
    /// opened (reconnecting, no Client-app error). Restart for a missing
    /// ?token= only when that process has been idle.
    /// </summary>
    public static bool ShouldRestartWebForFreshAuth(
        bool webPortListening,
        bool needsFreshAuth,
        int inFlightChatCount,
        DateTimeOffset? lastClientAppTurnUtc,
        DateTimeOffset? harnessStartedUtc,
        DateTimeOffset utcNow)
    {
        if (!webPortListening || !needsFreshAuth)
        {
            return false;
        }

        if (ShouldPreserveLiveWeb(
                webPortListening,
                inFlightChatCount,
                lastClientAppTurnUtc,
                utcNow))
        {
            return false;
        }

        if (harnessStartedUtc is { } started
            && utcNow - started >= TimeSpan.Zero
            && utcNow - started < PreserveLiveWebWindow)
        {
            return false;
        }

        return true;
    }

    public static bool ShouldPreserveLiveWeb(
        bool webPortListening,
        int inFlightChatCount,
        DateTimeOffset? lastClientAppTurnUtc,
        DateTimeOffset utcNow)
    {
        if (!webPortListening)
        {
            return false;
        }

        if (inFlightChatCount > 0)
        {
            return true;
        }

        return lastClientAppTurnUtc is { } last
               && utcNow - last >= TimeSpan.Zero
               && utcNow - last < PreserveLiveWebWindow;
    }

    public static bool TryParseServedUtc(string? updatedUtc, out DateTimeOffset servedUtc)
    {
        servedUtc = default;
        if (string.IsNullOrWhiteSpace(updatedUtc))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
                   updatedUtc,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out servedUtc)
               || DateTimeOffset.TryParse(updatedUtc, out servedUtc);
    }
}
