using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// A fidelity snapshot: the content used to seed a Cline .compaction.json when Cline is
/// non-responsive and we compact externally. It combines the maintained VIBE_STATE digest
/// (task-level fidelity) with the most recent conversation turns (detail-level nuance).
/// </summary>
public sealed class FidelitySnapshot
{
    /// <summary>The goal / what the user is working on (from VIBE_STATE Task).</summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>Current state: done / in progress / blocked (from VIBE_STATE State).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The next concrete step (from VIBE_STATE Next).</summary>
    public string Next { get; set; } = string.Empty;

    /// <summary>Key files / symbols / decisions (from VIBE_STATE Files).</summary>
    public string Files { get; set; } = string.Empty;

    /// <summary>
    /// How the goal evolved over the conversation: the original ask plus any major pivots.
    /// Captures "the task changed from X to Y" so the resumed model doesn't lose the thread.
    /// </summary>
    public string GoalEvolution { get; set; } = string.Empty;

    /// <summary>
    /// Key decisions and findings distilled from the (denoised) conversation. The "why" and the
    /// conclusions — the essence that isn't in the last few turns.
    /// </summary>
    public string KeyDecisions { get; set; } = string.Empty;

    /// <summary>
    /// Dead ends / failed approaches, as one-liners. Tells the resumed model what NOT to retry.
    /// </summary>
    public string DeadEnds { get; set; } = string.Empty;

    /// <summary>
    /// The most recent user/assistant text turns, in order. Detail-level nuance read from the
    /// Cline .messages.json at rescue time. May be empty if the messages file is unavailable.
    /// </summary>
    public IReadOnlyList<string> RecentTurns { get; set; } = new List<string>();

    /// <summary>Estimated token size of the rendered summary (for the headroom display).</summary>
    public int EstimatedTokens { get; set; }

    /// <summary>The target free-context fraction that was applied (for the headroom display).</summary>
    public double AppliedFreeFraction { get; set; }

    /// <summary>Number of messages in the source conversation (for the .compaction.json header).</summary>
    public int SourceMessageCount { get; set; }

    /// <summary>The last message key/id in the source conversation (for the .compaction.json header).</summary>
    public string SourceLastMessageKey { get; set; } = string.Empty;

    /// <summary>Where the fidelity came from (for logging / the confirmation dialog).</summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>True when the snapshot carries no meaningful content at all.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Goal)
        && string.IsNullOrWhiteSpace(State)
        && string.IsNullOrWhiteSpace(Next)
        && string.IsNullOrWhiteSpace(Files)
        && string.IsNullOrWhiteSpace(GoalEvolution)
        && string.IsNullOrWhiteSpace(KeyDecisions)
        && string.IsNullOrWhiteSpace(DeadEnds)
        && RecentTurns.Count == 0;

    /// <summary>Renders the snapshot into a Cline-style context summary (the compaction body).</summary>
    public string ToClineSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Context summary:");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(Dash(Goal));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(GoalEvolution))
        {
            sb.AppendLine("## Goal evolution (how the task changed)");
            sb.AppendLine(GoalEvolution.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## State");
        sb.AppendLine(Dash(State));
        sb.AppendLine();
        sb.AppendLine("## Next");
        sb.AppendLine(Dash(Next));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(KeyDecisions))
        {
            sb.AppendLine("## Key decisions & findings");
            sb.AppendLine(KeyDecisions.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(DeadEnds))
        {
            sb.AppendLine("## Dead ends (do not retry)");
            sb.AppendLine(DeadEnds.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## Files");
        sb.AppendLine(Dash(Files));

        if (RecentTurns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent turns (verbatim, most recent last)");
            foreach (var turn in RecentTurns)
            {
                sb.AppendLine();
                sb.AppendLine(turn);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A short, human-readable estimate of the summary's token size and the headroom it leaves,
    /// for the confirmation dialog. Uses a ~4 chars/token heuristic.
    /// </summary>
    public string HeadroomEstimate()
    {
        var summary = ToClineSummary();
        var tokens = EstimateTokens(summary);
        EstimatedTokens = tokens;
        if (ContextLimit <= 0)
        {
            return "Summary ≈ " + tokens + " tokens.";
        }

        var freeAfter = ContextLimit - PromptTokens - tokens;
        var freePct = ContextLimit > 0 ? (100.0 * Math.Max(0, freeAfter) / ContextLimit) : 0;
        return "Summary ≈ " + tokens + " tokens; leaves ≈ " + freePct.ToString("0") +
               "% of the " + ContextLimit + "-token window free (target " +
               (AppliedFreeFraction * 100).ToString("0") + "%).";
    }

    /// <summary>Context window size (for the headroom estimate). Set by the fidelity source.</summary>
    public int ContextLimit { get; set; }

    /// <summary>Current prompt-token count (for the headroom estimate). Set by the fidelity source.</summary>
    public int PromptTokens { get; set; }

    /// <summary>
    /// True when the session ALREADY has at least the requested free headroom, so compacting
    /// would be counterproductive (it discards context to gain space the user already has).
    /// Computed from the current prompt-token count vs. the target free fraction.
    /// </summary>
    public bool AlreadyHasHeadroom
    {
        get
        {
            if (ContextLimit <= 0 || PromptTokens <= 0)
            {
                return false; // unknown — don't warn
            }

            var currentFreeFraction = 1.0 - ((double)PromptTokens / ContextLimit);
            // If the current free fraction already meets or exceeds the target, compaction is
            // not needed to make headroom. Give a small epsilon so we don't warn on a razor-thin
            // margin (e.g. current free = 39.9% vs target 40%).
            return currentFreeFraction >= (AppliedFreeFraction - 0.01);
        }
    }

    /// <summary>Rough token estimate (~4 chars per token).</summary>
    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    private static string Dash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
}

/// <summary>
/// Builds a <see cref="FidelitySnapshot"/> for the current session. Stage 1 ships
/// VibeStatePlusMessagesFidelitySource (VIBE_STATE + Cline .messages.json). Later stages can add
/// richer sources (e.g. a durable archive, or local-model enrichment) behind this same seam.
/// </summary>
public interface IFidelitySource
{
    /// <summary>Stable, log-friendly name (e.g. "vibe-state-plus-messages").</summary>
    string Name { get; }

    /// <summary>Builds the snapshot. Never throws; returns a degraded (possibly empty) snapshot on failure.</summary>
    Task<FidelitySnapshot> BuildAsync(CompactionContext ctx);
}
