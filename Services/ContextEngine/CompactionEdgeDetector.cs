using System;
using System.Collections.Generic;
using System.Linq;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// Detects the moments when a session's context has been reset or sharply reduced — i.e. a
/// compaction just happened, or a chat was (re)started/resumed. These are the edges at which the
/// essence must be re-asserted. Uses multiple signals so it is robust to the variety of ways a
/// context can be reset (not just a fixed fill-percentage threshold):
///   * Sharp token drop   — previous promptTokens high, current much lower (a compaction).
///   * New session key    — the Cline/local session identity changed (new or resumed chat).
///   * First turn         — the first measurement since process start (cold resume / power outage).
///   * Context-limit jump — the window size changed (profile/model switch that resets context).
/// Pure state + logic, no I/O, trivially testable.
/// </summary>
public sealed class CompactionEdgeDetector
{
    /// <summary>How much the prompt-token count must drop (as a fraction of the previous value)
    /// to count as a compaction. 0.35 = a 35% reduction.</summary>
    public const double DropFraction = 0.35;

    /// <summary>The previous prompt-token count must be at least this many tokens for a drop to be
    /// meaningful (avoids false edges from tiny early-chat counts).</summary>
    public const int MinPreviousTokens = 2_000;

    private sealed record Sample(int PromptTokens, int ContextLimit, string Model);

    private readonly Dictionary<string, Sample> _lastBySession = new(StringComparer.Ordinal);
    private bool _anySampleSeen;

    /// <summary>
    /// Records a turn's measurement and returns true when a compaction/restart edge was detected
    /// for <paramref name="sessionKey"/>. Safe to call every turn.
    /// </summary>
    public bool Observe(int promptTokens, int contextLimit, string sessionKey, string model)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            sessionKey = "_default";
        }

        var current = new Sample(promptTokens, contextLimit, model ?? string.Empty);

        Sample? previous;
        var isNewSession = !_lastBySession.TryGetValue(sessionKey, out previous);

        bool edge;
        if (isNewSession || !_anySampleSeen)
        {
            // First turn for this session (or first turn since process start) → cold resume / start.
            edge = true;
        }
        else
        {
            // Sharp token drop (compaction) OR context-limit jump (profile/model reset).
            var drop = previous.PromptTokens >= MinPreviousTokens
                && promptTokens < previous.PromptTokens * (1.0 - DropFraction);
            var limitJump = previous.ContextLimit > 0
                && contextLimit > 0
                && Math.Abs(contextLimit - previous.ContextLimit) > previous.ContextLimit * 0.25;
            edge = drop || limitJump;
        }

        _lastBySession[sessionKey] = current;
        _anySampleSeen = true;

        // Bound the dictionary so long-running processes don't leak session entries.
        if (_lastBySession.Count > 256)
        {
            var oldestKey = _lastBySession.Keys.First();
            _lastBySession.Remove(oldestKey);
        }

        return edge;
    }

    /// <summary>True when at least one turn has been observed (used to distinguish cold start).</summary>
    public bool HasObserved => _anySampleSeen;
}