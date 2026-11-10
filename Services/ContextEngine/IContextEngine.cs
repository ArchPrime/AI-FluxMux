using System;
using System.Threading;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The public contract for the context engine: the unified, modular strategy that keeps a local
/// session's "vibe" (task / state / next) persistent and re-injectable, and that pages the working
/// context so it fills more slowly.
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
    /// True when context paging is enabled for the current profile/variant. Paging is assimilated
    /// into forward-prune, so this is gated by the same per-profile flag that gates forward
    /// pruning (and therefore inherits the Cline-like client gate automatically).
    /// </summary>
    bool IsContextPagingEnabled();

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
    /// so the model always knows "where we are" even after the working context has been paged
    /// away. Returns an empty string when VIBE_STATE is disabled or the state is empty.
    /// </summary>
    string BuildReinjectionBlock(string vibeStatePath);

    /// <summary>
    /// Pages the working context: trims the turn's own waste (dedupe, collapse repeated tool
    /// loops, truncate oversized older tool results) so the context fills more slowly. This is the
    /// paged-context half of the strategy and is the same conservative pipeline as forward
    /// pruning. Mutates <paramref name="payload"/> in place. Returns a no-op result when paging is
    /// disabled or there is nothing to trim.
    ///
    /// When <paramref name="swapStore"/> is non-null (paging on), the truncate stage spills each
    /// oversized older tool-result body to the store and leaves a short <c>swap_id</c> pointer in
    /// the prompt (lossless: the model can read it back via <c>read_swap_log</c>). When null, the
    /// lossy head+tail stub is used (identical to pre-paging behavior).
    /// </summary>
    ForwardPruneResult PageContext(
        System.Text.Json.Nodes.JsonObject payload,
        int contextTokens,
        int? measuredTokens = null,
        CancellationToken cancellationToken = default,
        ContextSwapStore? swapStore = null);
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

    /// <summary>
    /// Returns whether context paging is enabled for the current profile/variant. Backed by the
    /// per-profile <c>LocalContextPagingEnabled</c> flag, which is assimilated into forward-prune
    /// and therefore inherits the Cline-like client gate.
    /// </summary>
    public Func<bool> IsContextPagingEnabled { get; init; } = () => true;
}