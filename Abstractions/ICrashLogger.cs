using System;

namespace FluxMux.Avalonia.Abstractions;

/// <summary>
/// Abstraction over the platform's crash-logging mechanism. On Windows this writes
/// unhandled-exception details to a file on the user's Desktop (one file per crash,
/// timestamped) and, for startup failures, writes a sidecar text file plus shows an error
/// dialog. On Linux/macOS the "Desktop" location and dialog mechanism differ, so this is
/// behind an interface.
///
/// The app depends only on this interface, so a community member can add a new platform's
/// crash-logging behavior without touching the core.
/// </summary>
public interface ICrashLogger
{
    /// <summary>
    /// Logs an unhandled exception to a crash-report file. Best-effort: must never throw.
    /// </summary>
    void LogUnhandled(Exception exception, string source);

    /// <summary>
    /// Reports a startup failure (the app could not show its main window). Writes a log,
    /// a sidecar file the user can find, and shows an error dialog with recovery guidance.
    /// Best-effort: must never throw.
    /// </summary>
    void ReportStartupFailure(Exception exception, bool missingCompiledXaml);

    /// <summary>
    /// Marks that the main window is now on screen. After this point a crash is not a
    /// startup failure, and the recovery guidance should not tell the user to rebuild.
    /// </summary>
    void MarkWindowShown();
}