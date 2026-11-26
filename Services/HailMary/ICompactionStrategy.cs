using System;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// The outcome of a Hail Mary compaction strategy. Carries a human-readable message, an
/// optional path to a written .compaction.json (rescue path), and an optional summary preview
/// (rescue path, shown to the user before the file is written).
/// </summary>
public readonly record struct CompactionResult(
    bool Success,
    string Message,
    string? CompactionPath = null,
    string? SummaryPreview = null,
    string? Warning = null)
{
    public static CompactionResult Ok(string message) => new(true, message);
    public static CompactionResult Fail(string message) => new(false, message);
}

/// <summary>
/// Everything a compaction strategy needs to do its job. Passed in by the ViewModel so the
/// strategies stay decoupled from the UI and from the runtime service (testable in isolation).
/// </summary>
public sealed class CompactionContext
{
    /// <summary>Path to the session VIBE_STATE.md (the maintained task/state/next/files digest).</summary>
    public string VibeStatePath { get; init; } = string.Empty;

    /// <summary>
    /// Path to the session essence.json (the durable essence log: current snapshot + goal
    /// evolution + intent-shifts + decisions + dead ends). When present and non-empty, the
    /// fidelity source prefers it over the VIBE_STATE-only summary because it carries the
    /// "wider mission" context (how the goal evolved, key decisions, dead ends) that the
    /// .messages.json (which may have been compacted) and the VIBE_STATE current block (which
    /// only holds the latest focus) do not. Empty when the essence log is not available.
    /// </summary>
    public string EssencePath { get; init; } = string.Empty;

    /// <summary>The Cline session id (e.g. "1792478967620_pcp87"). Resolved at run time.</summary>
    public string? ClineSessionId { get; init; }

    /// <summary>Full path to the Cline session .messages.json (the full conversation).</summary>
    public string? ClineMessagesPath { get; init; }

    /// <summary>Optional log sink (e.g. launch_monitor.log). Null-safe: strategies no-op if absent.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// The free share of the Context the session-note summary must leave (used only without a
    /// <see cref="Plan"/>). With <see cref="PromptTokens"/> 0, as Hail Mary passes, it never cuts the note.
    /// </summary>
    public double TargetFreeFraction { get; init; } = 0.40;

    /// <summary>
    /// The sized rebuild (<see cref="RescuePlanner"/>) when the stuck chat as the Client app last sent
    /// it is known; null falls back to the session note, with the whole chat summarised.
    /// </summary>
    public RescuePlan? Plan { get; init; }

    /// <summary>The current prompt-token count (from the context gauge). 0 if unknown.</summary>
    public int PromptTokens { get; init; }

    /// <summary>The context window size in tokens. 0 if unknown.</summary>
    public int ContextLimit { get; init; }

    public void LogLine(string line)
    {
        try { Log?.Invoke(line); } catch { /* logging must never break a strategy */ }
    }
}

/// <summary>
/// A Hail Mary rebuild of a stuck chat in the Client app's own files: ExternalRescueStrategy
/// (Cline's .compaction.json) or ConfiguredRescueStrategy (a RescueAdapter, such as Harness).
/// ExecuteAsync prepares the summary; CommitAsync writes it.
/// </summary>
public interface ICompactionStrategy
{
    /// <summary>Stable, log-friendly name of the strategy (e.g. "wait-for-cline").</summary>
    string Name { get; }

    /// <summary>Human-readable label shown in the picker (e.g. "Wait for Cline to become ready").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Runs the strategy. For the rescue path this is the PREPARE step: it builds the summary
    /// preview but does NOT write the file yet (the ViewModel shows a confirmation first and then
    /// calls <see cref="CommitAsync"/>). For the wait-for-cline path, ExecuteAsync does the whole job
    /// and CommitAsync is a no-op that returns the same result.
    /// </summary>
    Task<CompactionResult> ExecuteAsync(CompactionContext ctx);

    /// <summary>
    /// Commits the prepared action (writes the .compaction.json for the rescue path). For
    /// strategies that do everything in ExecuteAsync (wait-for-cline), this returns the
    /// already-completed result unchanged.
    /// </summary>
    Task<CompactionResult> CommitAsync(CompactionContext ctx, CompactionResult prepared);
}
