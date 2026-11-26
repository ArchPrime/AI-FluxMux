using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>What a Hail Mary rebuild will write: the summary, and how much of the chat stays word for word.</summary>
public sealed record RescuePlan(
    string Summary,
    int KeepRecent,
    int SystemTokens,
    int SummaryTokens,
    int KeptTokens,
    int ContextTokens,
    double TokenRatio)
{
    public int TotalTokens => SystemTokens + SummaryTokens + KeptTokens;

    /// <summary>"summary + 84 recent messages, about 58 % of the Context".</summary>
    public string Describe()
        => "summary + " + KeepRecent + " recent message" + (KeepRecent == 1 ? string.Empty : "s")
           + (ContextTokens > 0 ? ", about " + (int)Math.Round(100.0 * TotalTokens / ContextTokens) + " % of the Context" : string.Empty);
}

/// <summary>
/// Sizes a Hail Mary rebuild (26 Nov). The goal is to keep as much of the chat as possible and still
/// leave room for the person to type "retry" and have the message that overflowed taken in: the
/// rebuilt chat is filled to <see cref="TargetFillShare"/> of the loaded Context — the same 60 % that
/// compaction cuts back to — leaving about 40 % for the retried turn and its reply. The summary
/// (<see cref="HandoverBrief.BuildRescueSummary"/>) is built from the stuck chat exactly as the Client
/// app last sent it; the newest messages that fit stay word for word.
///
/// It replaced a fixed keep count (6 for Harness, none for Cline, whose compaction file replaced the
/// whole chat) and a "headroom" setting that capped the summary against the size of the chat being
/// replaced: on any chat over 80 % full that cap was negative, so all three settings wrote the same
/// 154-character note.
/// </summary>
public static class RescuePlanner
{
    public const double TargetFillShare = 0.60;

    /// <summary>The summary's share of the Context at most, and its hard cap in tokens.</summary>
    public const double SummaryShare = 0.15;
    public const int SummaryMaxTokens = 8000;

    /// <summary>Used when no measured tokens / estimate ratio is known (a code-heavy agent chat measured 1.5x).</summary>
    public const double DefaultTokenRatio = 1.5;

    /// <summary>Estimated tokens of one message: its JSON / 4, scaled by the measured ratio. The JSON
    /// includes field names, so this errs on the side of keeping a little less.</summary>
    public static int MessageTokens(JsonNode? message, double tokenRatio)
        => message is null ? 0 : (int)Math.Ceiling(message.ToJsonString().Length / 4.0 * NormaliseRatio(tokenRatio));

    public static double NormaliseRatio(double tokenRatio)
        => tokenRatio is > 0 and < 10 ? Math.Clamp(tokenRatio, 1.0, 3.0) : DefaultTokenRatio;

    /// <summary>Tokens set aside for the summary: <see cref="SummaryShare"/> of the Context, at most <see cref="SummaryMaxTokens"/>.</summary>
    public static int SummaryBudgetTokens(int contextTokens)
        => (int)Math.Min(SummaryMaxTokens, contextTokens * SummaryShare);

    /// <summary>
    /// Plans the rebuild from the chat as the Client app last sent it (OpenAI messages). Null when
    /// the chat holds nothing the person wrote or the Context is unknown.
    /// </summary>
    public static RescuePlan? FromChat(JsonArray? messages, int contextTokens, double tokenRatio)
    {
        if (messages is null || messages.Count < 2 || contextTokens <= 0)
        {
            return null;
        }

        var ratio = NormaliseRatio(tokenRatio);
        var target = (int)(contextTokens * TargetFillShare);
        var systemTokens = messages.OfType<JsonObject>()
            .Where(m => LocalHistoryCompaction.RoleOf(m) == "system")
            .Sum(m => MessageTokens(m, ratio));
        var summaryBudget = SummaryBudgetTokens(contextTokens);
        var tailBudget = Math.Max(0, target - systemTokens - summaryBudget);

        var first = LocalHistoryCompaction.IndexOfFirstRealUser(messages);
        var tokens = messages.Select(m => LocalHistoryCompaction.RoleOf(m as JsonObject ?? new JsonObject()) == "system" ? 0 : MessageTokens(m, ratio)).ToList();
        var keepStart = TailStart(tokens, tailBudget, Math.Max(1, first + 1));
        // Never start the kept part on a tool result whose call was summarised away.
        keepStart = LocalHistoryCompaction.AlignKeepStart(messages, keepStart);

        var summary = HandoverBrief.BuildRescueSummary(messages, keepStart, (int)(summaryBudget * 4 / ratio));
        if (summary is null)
        {
            return null;
        }

        var kept = messages.Skip(keepStart).OfType<JsonObject>().Count(m => LocalHistoryCompaction.RoleOf(m) != "system");
        return new RescuePlan(
            summary,
            kept,
            systemTokens,
            (int)Math.Ceiling(summary.Length / 4.0 * ratio),
            tokens.Skip(keepStart).Sum(),
            contextTokens,
            ratio);
    }

    /// <summary>
    /// The first index of the newest messages whose tokens fit <paramref name="budget"/>, never
    /// before <paramref name="floor"/>; at least the last message is kept, even when it alone is
    /// over the budget (that turn is the one the person will retry).
    /// </summary>
    public static int TailStart(IReadOnlyList<int> tokens, int budget, int floor)
    {
        var start = tokens.Count;
        var used = 0;
        while (start - 1 >= floor && used + tokens[start - 1] <= budget)
        {
            start--;
            used += tokens[start];
        }

        return Math.Min(start, Math.Max(floor, tokens.Count - 1));
    }
}
