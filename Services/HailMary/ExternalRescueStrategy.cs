using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Hail Mary for Cline ("Cline can't continue"): writes a .compaction.json that Cline applies when
/// the chat is reopened. ExecuteAsync builds the summary — from <see cref="CompactionContext.Plan"/>
/// (the stuck chat as Cline last sent it, sized by <see cref="RescuePlanner"/>) or, without one, from
/// the session note — and CommitAsync writes the file. With a plan only the older part of the chat
/// is compacted: the newest messages that fit the plan's budget stay word for word after the summary
/// (26 Nov). Before, every message was replaced, so a rescued Cline chat held the summary alone.
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
                "If so, use Cline's own compact button, or Chat handover brief.");
        }

        var messagesPath = ctx.ClineMessagesPath
            ?? Path.Combine(ClineCompactionWriter.GetSessionDir(sessionId), sessionId + ".messages.json");

        var prepareCtx = new CompactionContext
        {
            VibeStatePath = ctx.VibeStatePath,
            EssencePath = ctx.EssencePath,
            ClineSessionId = sessionId,
            ClineMessagesPath = messagesPath,
            Log = ctx.Log,
            TargetFreeFraction = ctx.TargetFreeFraction,
            PromptTokens = ctx.PromptTokens,
            ContextLimit = ctx.ContextLimit,
        };

        if (ctx.Plan is { } plan)
        {
            ctx.LogLine("[HailMary] strategy=" + Name + ": summary from the chat as Cline last sent it (" + plan.Summary.Length + " chars; " + plan.Describe() + ")");
            return new CompactionResult(true, "Ready to rebuild Cline session " + sessionId + ": " + plan.Describe() + ".", SummaryPreview: plan.Summary);
        }

        var snapshot = await _fidelity.BuildAsync(prepareCtx).ConfigureAwait(false);
        if (snapshot.IsEmpty)
        {
            return CompactionResult.Fail(
                "No fidelity data available to compact with (VIBE_STATE empty and Cline .messages.json unreadable). " +
                "Use Cline's own compact button, or Chat handover brief.");
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
        var compacted = 0;
        if (sourceMessages is not null && sourceMessages.Count > 0)
        {
            // Cline rebuilds the chat as [summary] + the messages after the compacted count. With a
            // plan, keep the newest messages that fit its budget; without one, compact them all.
            compacted = ctx.Plan is { } plan
                ? CompactCount(sourceMessages, plan.KeptTokens, plan.TokenRatio)
                : sourceMessages.Count;
            ctx.LogLine("[HailMary] strategy=" + Name + ": compacting " + compacted + " of " + sourceMessages.Count + " messages; "
                + (sourceMessages.Count - compacted) + " kept word for word.");
            written = ClineCompactionWriter.WriteCompactionFileValid(
                sessionId,
                prepared.SummaryPreview,
                sourceMessages,
                compacted);
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
                "Failed to write the .compaction.json file. Cline was NOT compacted. Use Cline's own compact button, or Chat handover brief."));
        }

        ctx.LogLine("[HailMary] strategy=" + Name + ": WROTE .compaction.json at " + written +
                    " (source messages=" + (sourceMessages?.Count ?? 0) + ") — Cline applies it when the session is reloaded.");

        // NOTE: We intentionally do NOT replace .messages.json here. Cline validates the
        // .compaction.json against the loaded .messages.json at session load. If we replace
        // .messages.json with a compacted version, the hash will no longer match and Cline
        // will reject the compaction. The .compaction.json alone is sufficient: when the user
        // closes and reopens the chat, Cline loads the original .messages.json, validates the
        // .compaction.json (hash matches), and applies the compaction.

        var keptNote = sourceMessages is { Count: > 0 } && compacted < sourceMessages.Count
            ? " The last " + (sourceMessages.Count - compacted) + " messages are kept word for word."
            : string.Empty;
        return Task.FromResult(new CompactionResult(
            Success: true,
            Message: "Cline chat rebuilt: the older part is now a summary." + keptNote
                     + " Close and reopen this Cline chat (or restart Cline), then type retry.",
            CompactionPath: written,
            SummaryPreview: prepared.SummaryPreview));
    }

    /// <summary>
    /// How many leading messages of Cline's own chat file to compact so the rest fits
    /// <paramref name="keptTokens"/>. The kept part never starts on a tool result whose call is
    /// compacted, and at least one message is compacted.
    /// </summary>
    internal static int CompactCount(System.Text.Json.Nodes.JsonArray messages, int keptTokens, double tokenRatio)
    {
        var tokens = messages.Select(m => RescuePlanner.MessageTokens(m?["content"], tokenRatio)).ToList();
        var start = RescuePlanner.TailStart(tokens, Math.Max(0, keptTokens), 1);
        while (start < messages.Count - 1 && StartsWithToolResult(messages[start]))
        {
            start++;
        }

        return Math.Clamp(start, 1, messages.Count);
    }

    private static bool StartsWithToolResult(System.Text.Json.Nodes.JsonNode? message)
        => message?["content"] is System.Text.Json.Nodes.JsonArray parts
           && parts.OfType<System.Text.Json.Nodes.JsonObject>().Any(p => p["type"]?.ToString() == "tool_result");

    private static string? ResolveSessionId(CompactionContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.ClineSessionId))
        {
            return ctx.ClineSessionId;
        }

        return ClineCompactionWriter.GetMostRecentSessionId();
    }
}
