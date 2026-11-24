using System;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The default <see cref="IContextEngine"/>. Self-contained: it depends only on an
/// <see cref="IContextIo"/> (for file access) and a <see cref="ContextEngineOptions"/> (for the
/// live config flags). It has no reference to the view model, runtime service, or gateway host,
/// which is what makes it portable and testable.
///
/// Its capability is <b>VIBE_STATE</b>: load/save/render the persistent task/state/next record
/// and build the short re-injection block added to every local turn. (Paged context, which
/// spilled tool results to a swap store, was removed on 25 Nov: no Client app could read them back.)
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
}