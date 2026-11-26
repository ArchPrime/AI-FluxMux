using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Hail Mary for Cline ("Cline can't continue"): writes a .compaction.json that Cline applies when
/// the chat is reopened. ExecuteAsync takes the summary from <see cref="CompactionContext.Plan"/>
/// (the stuck chat as Cline last sent it, sized by <see cref="RescuePlanner"/>) and CommitAsync
/// writes the file: only the older part of the chat is compacted, the newest messages that fit the
/// plan's budget stay word for word after the summary. Without a plan, or with a .messages.json it
/// cannot read, it writes nothing and fails (Hail Mary then prepares the brief): the session-note
/// summary and a compaction file without Cline's source-prefix hash, which Cline always ignores, were
/// removed on 27 Nov. An existing .compaction.json (one Cline wrote itself) is backed up first.
/// </summary>
public sealed class ExternalRescueStrategy : ICompactionStrategy
{
    public string Name => "external-rescue";
    public string DisplayName => "Cline can't continue — compact externally & resurrect";

    public Task<CompactionResult> ExecuteAsync(CompactionContext ctx)
    {
        ctx.LogLine("[HailMary] strategy=" + Name + ": preparing external compaction");

        var sessionId = ResolveSessionId(ctx);
        if (string.IsNullOrEmpty(sessionId))
        {
            return Task.FromResult(CompactionResult.Fail(
                "Could not find a Cline session to compact. Is Cline running on this PC? " +
                "If so, use Cline's own compact button, or Chat handover brief."));
        }

        if (ctx.Plan is { } plan)
        {
            ctx.LogLine("[HailMary] strategy=" + Name + ": summary from the chat as Cline last sent it (" + plan.Summary.Length + " chars; " + plan.Describe() + ")");
            return Task.FromResult(new CompactionResult(true, "Ready to rebuild Cline session " + sessionId + ": " + plan.Describe() + ".", SummaryPreview: plan.Summary));
        }

        // No plan available — fail clearly. The plan is always available in practice (the ViewModel
        // builds it from the saved chat), so this path is a safety net for unexpected cases.
        ctx.LogLine("[HailMary] strategy=" + Name + ": FAILED: No rescue plan available.");
        return Task.FromResult(CompactionResult.Fail(
            "No rescue plan available to compact with. This should not happen in normal operation. "
            + "Use Cline's own compact button, or Chat handover brief."));
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
            // No readable .messages.json — fail clearly. Writing a compaction without the correct
            // source_prefix_hash would be silently rejected by Cline, so it's better to not write
            // anything and let the user know the rescue couldn't proceed.
            ctx.LogLine("[HailMary] strategy=" + Name + ": FAILED: Could not read .messages.json (path=" + messagesPath + "). No compaction written.");
            return Task.FromResult(CompactionResult.Fail(
                "Could not read Cline's .messages.json file, so the compaction could not be written. "
                + "The file may be locked by Cline or corrupted. Try again, or use Cline's own compact button."));
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
                     + " Cline reads the rebuild when the chat is opened: open another task, reopen this one from history, then type retry. "
                     + "Cline still shows the full history; the summary is in what it sends to the model.",
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
