using System;
using System.IO;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Stage 1 strategy (the rescue path): when Cline is non-responsive and cannot run /smol itself,
/// we compact externally. ExecuteAsync is the PREPARE step — it builds the Cline-format summary
/// (from the fidelity source) and returns it as a preview WITHOUT writing anything. The ViewModel
/// shows a confirmation dialog; on confirm it calls CommitAsync, which writes the .compaction.json
/// via ClineCompactionWriter. Cline then reads that file on its next turn and resumes with the
/// compacted, fidelity-preserving context.
/// </summary>
public sealed class ExternalRescueStrategy : ICompactionStrategy
{
    private readonly IFidelitySource _fidelity;

    public ExternalRescueStrategy(IFidelitySource? fidelity = null)
    {
        _fidelity = fidelity ?? new VibeStatePlusMessagesFidelitySource();
    }

    public string Name => "external-rescue";
    public string DisplayName => "Cline can't continue — compact externally & resurrect";

    public async Task<CompactionResult> ExecuteAsync(CompactionContext ctx)
    {
        ctx.LogLine("[HailMary] strategy=" + Name + ": preparing external compaction (fidelity source=" + _fidelity.Name + ")");

        var sessionId = ResolveSessionId(ctx);
        if (string.IsNullOrEmpty(sessionId))
        {
            return CompactionResult.Fail(
                "Could not find a Cline session to compact. Is Cline running on this PC? " +
                "If so, try the /smol option, or type /smol manually in Cline's chat input.");
        }

        var messagesPath = ctx.ClineMessagesPath
            ?? Path.Combine(ClineCompactionWriter.GetSessionDir(sessionId), sessionId + ".messages.json");

        var prepareCtx = new CompactionContext
        {
            VibeStatePath = ctx.VibeStatePath,
            ClineSessionId = sessionId,
            ClineMessagesPath = messagesPath,
            Log = ctx.Log,
            TargetFreeFraction = ctx.TargetFreeFraction,
            PromptTokens = ctx.PromptTokens,
            ContextLimit = ctx.ContextLimit,
        };

        var snapshot = await _fidelity.BuildAsync(prepareCtx).ConfigureAwait(false);
        if (snapshot.IsEmpty)
        {
            return CompactionResult.Fail(
                "No fidelity data available to compact with (VIBE_STATE empty and Cline .messages.json unreadable). " +
                "Try the /smol option, or type /smol manually in Cline's chat input.");
        }

        var summary = snapshot.ToClineSummary();
        var headroom = snapshot.HeadroomEstimate();
        ctx.LogLine("[HailMary] strategy=" + Name + ": prepared summary (" + summary.Length + " chars, " +
                    snapshot.SourceMessageCount + " source messages, fidelity=" + snapshot.SourceLabel +
                    "; " + headroom + ")");

        // If the session already has at least the requested free headroom, compacting is
        // counterproductive (it discards context to gain space the user already has). Carry a
        // non-fatal warning so the confirmation dialog can flag it; the user may still proceed.
        string? warning = null;
        if (snapshot.AlreadyHasHeadroom)
        {
            warning = "You already have the requested free headroom (" +
                      (snapshot.AppliedFreeFraction * 100).ToString("0") +
                      "% free). Compacting now would discard context to gain space you already have — " +
                      "it is likely counterproductive. Confirm only if you still want to compact.";
            ctx.LogLine("[HailMary] strategy=" + Name + ": WARNING — session already has the requested headroom; compaction may be unnecessary.");
        }

        return new CompactionResult(
            Success: true,
            Message: "Ready to externally compact Cline session " + sessionId + ". " + headroom,
            CompactionPath: null,
            SummaryPreview: summary,
            Warning: warning);
    }

    public Task<CompactionResult> CommitAsync(CompactionContext ctx, CompactionResult prepared)
    {
        if (!prepared.Success || string.IsNullOrWhiteSpace(prepared.SummaryPreview))
        {
            return Task.FromResult(prepared);
        }

        var sessionId = ctx.ClineSessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            return Task.FromResult(CompactionResult.Fail("No Cline session id available to write the compaction file."));
        }

        // Read the real .messages.json so we can compute the Cline source-prefix hash that Cline
        // validates at session load. Without a matching hash, Cline silently discards the
        // compaction file (see ClineCompactionWriter.ComputeClineSourcePrefixHash).
        var messagesPath = ctx.ClineMessagesPath
            ?? Path.Combine(ClineCompactionWriter.GetSessionDir(sessionId), sessionId + ".messages.json");
        var sourceMessages = ClineCompactionWriter.ReadMessagesArray(messagesPath);

        string? written;
        if (sourceMessages is not null && sourceMessages.Count > 0)
        {
            // Compact the ENTIRE history: replace all N messages with the summary. Cline's sd()
            // then rebuilds the context as [summary] + [] (no tail), which is what we want for a
            // truly stuck chat.
            ctx.LogLine("[HailMary] strategy=" + Name + ": Using WriteCompactionFileValid with " + sourceMessages.Count + " source messages.");
            written = ClineCompactionWriter.WriteCompactionFileValid(
                sessionId,
                prepared.SummaryPreview,
                sourceMessages,
                sourceMessages.Count);
        }
        else
        {
            // No readable .messages.json — fall back to the legacy writer (best-effort). The
            // compaction may be rejected by Cline, but we still write something.
            ctx.LogLine("[HailMary] strategy=" + Name + ": WARNING: Could not read .messages.json (path=" + messagesPath + "). Falling back to legacy WriteCompactionFile.");
            written = ClineCompactionWriter.WriteCompactionFile(
                sessionId,
                prepared.SummaryPreview,
                0,
                string.Empty);
        }

        if (written is null)
        {
            ctx.LogLine("[HailMary] strategy=" + Name + ": FAILED to write .compaction.json for " + sessionId);
            return Task.FromResult(CompactionResult.Fail(
                "Failed to write the .compaction.json file. Cline was NOT compacted. Try the /smol option, or type /smol manually."));
        }

        ctx.LogLine("[HailMary] strategy=" + Name + ": WROTE .compaction.json at " + written +
                    " (source messages=" + (sourceMessages?.Count ?? 0) + ") — Cline applies it when the session is reloaded.");

        // NOTE: We intentionally do NOT replace .messages.json here. Cline validates the
        // .compaction.json against the loaded .messages.json at session load. If we replace
        // .messages.json with a compacted version, the hash will no longer match and Cline
        // will reject the compaction. The .compaction.json alone is sufficient: when the user
        // closes and reopens the chat, Cline loads the original .messages.json, validates the
        // .compaction.json (hash matches), and applies the compaction.

        return Task.FromResult(new CompactionResult(
            Success: true,
            Message: "Wrote external compaction for Cline session " + sessionId +
                     ". To apply it, close and reopen this Cline chat (or restart Cline) so the session reloads with the compacted context.",
            CompactionPath: written,
            SummaryPreview: prepared.SummaryPreview));
    }

    private static string? ResolveSessionId(CompactionContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ClineSessionId))
        {
            return ctx.ClineSessionId;
        }

        return ClineCompactionWriter.GetMostRecentSessionId();
    }
}
