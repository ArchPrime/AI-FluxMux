using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The default <see cref="IContextEngine"/>. Self-contained: it depends only on an
/// <see cref="IContextIo"/> (for file access) and a <see cref="ContextEngineOptions"/> (for the
/// live config flags). It has no reference to the view model, runtime service, or gateway host,
/// which is what makes it portable and testable.
///
/// The two capabilities:
///   * <b>VIBE_STATE</b> — load/save/render the persistent task/state/next record and build the
///     short re-injection block prepended to every local turn.
///   * <b>Paged context</b> — the conservative forward-prune pipeline, exposed as
///     <see cref="PageContext"/> and gated by the same per-profile flag that gates forward
///     pruning (so it inherits the Cline-like client gate automatically).
/// </summary>
public sealed class ContextEngine : IContextEngine
{
    private readonly IContextIo _io;
    private readonly ContextEngineOptions _options;

    public ContextEngine(IContextIo io, ContextEngineOptions options)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsVibeStateEnabled()
    {
        try
        {
            return _options.IsVibeStateEnabled();
        }
        catch
        {
            // A throwing delegate should never take the app down; default to off.
            return false;
        }
    }

    public bool IsContextPagingEnabled()
    {
        try
        {
            return _options.IsContextPagingEnabled();
        }
        catch
        {
            return false;
        }
    }

    public VibeState LoadVibeState(string vibeStatePath)
    {
        if (string.IsNullOrWhiteSpace(vibeStatePath))
        {
            return VibeState.Empty;
        }

        try
        {
            if (!_io.Exists(vibeStatePath))
            {
                return VibeState.Empty;
            }

            var text = _io.ReadAllText(vibeStatePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return VibeState.Empty;
            }

            var json = VibeStateMarkdown.ExtractJsonBlock(text);
            if (string.IsNullOrWhiteSpace(json))
            {
                // No machine-readable block yet (e.g. a hand-edited file with only prose).
                return VibeState.Empty;
            }

            return VibeState.FromJson(json);
        }
        catch
        {
            // A corrupt or unreadable VIBE_STATE.md must never break a turn; fall back to empty.
            return VibeState.Empty;
        }
    }

    public void SaveVibeState(string vibeStatePath, VibeState state)
    {
        if (string.IsNullOrWhiteSpace(vibeStatePath) || state is null)
        {
            return;
        }

        var stamped = state.WithTimestamp(DateTimeOffset.UtcNow);
        var markdown = RenderVibeStateMarkdown(stamped);
        _io.WriteAllTextResilient(vibeStatePath, markdown);
    }

    public string RenderVibeStateMarkdown(VibeState state)
    {
        return VibeStateMarkdown.Render(state);
    }

    public string BuildReinjectionBlock(string vibeStatePath)
    {
        if (!IsVibeStateEnabled())
        {
            return string.Empty;
        }

        var state = LoadVibeState(vibeStatePath);
        if (state.IsEmpty)
        {
            return string.Empty;
        }

        return VibeStateMarkdown.BuildReinjectionBlock(state);
    }

    public ForwardPruneResult PageContext(
        JsonObject payload,
        int contextTokens,
        int? measuredTokens = null,
        System.Threading.CancellationToken cancellationToken = default,
        ContextSwapStore? swapStore = null)
    {
        if (!IsContextPagingEnabled())
        {
            return ForwardPruneResult.None;
        }

        // The paged-context pipeline is the conservative forward-prune. Reusing it (rather than
        // duplicating it) is the whole point of the DRY strategy: one pipeline, two entry points.
        // When a swap store is supplied (paging on), the truncate stage spills oversized bodies
        // to the store and leaves a swap_id pointer (lossless); when null, it uses the lossy
        // head+tail stub (identical to pre-paging behavior).
        return ForwardPrune.PruneTurn(payload, contextTokens, measuredTokens, swapStore);
    }
}