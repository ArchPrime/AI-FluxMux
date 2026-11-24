using System;
using System.Threading;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The public contract for the context engine: the unified, modular strategy that keeps a local
/// session's "vibe" (task / state / next) persistent and re-injectable.
///
/// This is the seam the rest of the app (the gateway host, the runtime service, the view model)
/// talks to. It is deliberately small and dependency-free: the engine receives its config flags
/// via injected delegates (see <see cref="ContextEngineOptions"/>) and its I/O via
/// <see cref="IContextIo"/>, so it has no hard dependency on the god objects
/// (<c>MainViewModel</c>, <c>FluxMuxRuntimeService</c>, <c>FluxMuxGatewayHost</c>).
///
/// All methods are safe to call on every turn: when the relevant capability is disabled (or there
/// is nothing to do) they are cheap no-ops.
/// </summary>
public interface IContextEngine
{
    /// <summary>
    /// True when the VIBE_STATE capability is enabled for the current profile/variant. Read live
    /// each call so toggling takes effect on the next turn without a reload.
    /// </summary>
    bool IsVibeStateEnabled();

    /// <summary>
    /// Loads the current <see cref="VibeState"/> for the session at <paramref name="vibeStatePath"/>.
    /// Returns <see cref="VibeState.Empty"/> when the file does not exist or cannot be parsed.
    /// </summary>
    VibeState LoadVibeState(string vibeStatePath);

    /// <summary>
    /// Persists <paramref name="state"/> to <paramref name="vibeStatePath"/> as a human-readable
    /// <c>VIBE_STATE.md</c> (a fenced JSON block plus a rendered summary). The write is resilient
    /// (never throws) and the state is stamped with the current UTC time.
    /// </summary>
    void SaveVibeState(string vibeStatePath, VibeState state);

    /// <summary>
    /// Renders the <c>VIBE_STATE.md</c> text for <paramref name="state"/> without writing it.
    /// Exposed for tests and for any UI that wants to preview the file.
    /// </summary>
    string RenderVibeStateMarkdown(VibeState state);

    /// <summary>
    /// Builds the short re-injection block to prepend to the system prompt on every local turn,
    /// so the model always knows "where we are" even after older turns have been shortened.
    /// Returns an empty string when VIBE_STATE is disabled or the state is empty.
    /// </summary>
    string BuildReinjectionBlock(string vibeStatePath);
}

/// <summary>
/// The injected configuration the <see cref="IContextEngine"/> needs. The engine has no hard
/// dependency on the view model or runtime service; instead the host wires these delegates to the
/// live per-profile flags. This keeps the module self-contained and testable (a test supplies
/// constant delegates).
/// </summary>
public sealed class ContextEngineOptions
{
    /// <summary>
    /// Returns whether the VIBE_STATE capability is enabled for the current profile/variant.
    /// Backed by the per-profile <c>LocalVibeStateEnabled</c> flag (default on).
    /// </summary>
    public Func<bool> IsVibeStateEnabled { get; init; } = () => true;
}