using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Abstractions;

/// <summary>
/// The outcome of an in-place update attempt.
/// </summary>
public sealed record UpdateOutcome(
    bool Succeeded,
    string StatusText,
    string? BackupPath = null);

/// <summary>
/// Abstraction over the platform's in-place update mechanism. On Windows this downloads
/// the newer build zip, backs up the current install, and extracts the new build in place
/// (the app must be closed because Windows locks the running .exe/.dll). On Linux/macOS
/// the mechanism differs (e.g. replacing files in /Applications or a user install dir,
/// AppImage, .app bundle, etc.).
///
/// The app depends only on this interface, so a community member can add a new platform's
/// update mechanism without touching the core update-check logic (which is shared and
/// platform-agnostic — it just finds the newest version and download URL).
/// </summary>
public interface IUpdateService
{
    /// <summary>True when the platform supports in-place updates.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Performs an in-place update: downloads <paramref name="downloadUrl"/>, backs up the
    /// current install, and extracts the new build into <paramref name="installDir"/>.
    /// The caller must ensure the app is not running.
    /// </summary>
    Task<UpdateOutcome> UpdateAsync(
        HttpClient http,
        string installDir,
        string downloadUrl,
        Action<string>? reportProgress = null,
        CancellationToken cancellationToken = default,
        string? backupIntoFolder = null,
        Action<long, long>? reportBytes = null);
}