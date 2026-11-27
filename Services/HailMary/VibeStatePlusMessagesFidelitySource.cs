using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services.ContextEngine;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// Stage 1b fidelity source: builds a "just enough" rescue summary. It reads the maintained
/// VIBE_STATE.md (task-level fidelity) and the FULL Cline .messages.json, then:
///   * DENOISES the history — drops repeated tool calls, failed tool results (kept as one-liners),
///     and retry loops, so the summary carries signal not noise.
///   * EXTRACTS ESSENCE — goal evolution (original ask + pivots), key decisions/findings, and
///     dead ends — so the resumed model keeps the thread even though the raw turns are gone.
///   * BOUNDS THE SIZE — caps the summary so that, after Cline reinflates, the user's chosen
///     headroom (free-context fraction) remains. If the essence exceeds the cap, sections are
///     dropped from the bottom up (recent turns first, core VIBE_STATE fields last).
/// Pure file reads; never throws.
/// </summary>
public sealed class VibeStatePlusMessagesFidelitySource : IFidelitySource
{
    private const int MaxTurnChars = 4_000;
    private const int MaxGoalEvolutionChars = 2_000;
    private const int MaxKeyDecisionsChars = 4_000;
    private const int MaxDeadEndsChars = 1_500;

    public string Name => "vibe-state-plus-messages";

    public Task<FidelitySnapshot> BuildAsync(CompactionContext ctx)
    {
        var snapshot = new FidelitySnapshot
        {
            ContextLimit = ctx.ContextLimit,
            PromptTokens = ctx.PromptTokens,
            AppliedFreeFraction = ctx.TargetFreeFraction,
        };

        // Load the durable essence log (essence.json) first. It carries the "wider mission"
        // context — the full goal-evolution history, key decisions, and dead ends — that the
        // VIBE_STATE current block (latest focus only) and the .messages.json (possibly
        // compacted to the recent segment) do not. When the essence has content, it becomes the
        // PRIMARY source for Goal/State/Next/Files/GoalEvolution/KeyDecisions/DeadEnds, so the
        // compaction reflects the whole mission rather than just the most recent side quest.
        var essence = LoadEssence(ctx.EssencePath);
        bool essenceHasContent = essence is not null && essence.HasContent();

        var vibe = LoadVibeState(ctx.VibeStatePath);
        if (essenceHasContent)
        {
            // Essence is the richer, more up-to-date source. Use its Current snapshot for the
            // goal/state/next/files, and its durable history for the evolution/decisions/dead-ends.
            snapshot.Goal = essence!.Current.Task;
            snapshot.State = essence.Current.State;
            snapshot.Next = essence.Current.Next;
            snapshot.Files = essence.Files.Count > 0 ? string.Join(", ", essence.Files) : essence.Current.Files;
            snapshot.GoalEvolution = FormatEssenceEvolution(essence.Evolution);
            // The essence no longer keeps decisions or dead ends (27 Nov): they were the model's
            // own words. The model's conclusions come from the messages below, labelled as such.
        }
        else
        {
            // No essence available — fall back to the VIBE_STATE current block.
            snapshot.Goal = vibe.Task;
            snapshot.State = vibe.State;
            snapshot.Next = vibe.Next;
            snapshot.Files = vibe.Files;
        }

        var conv = LoadConversation(ctx.ClineMessagesPath);
        if (conv is not null)
        {
            snapshot.SourceMessageCount = conv.MessageCount;
            snapshot.SourceLastMessageKey = conv.LastMessageKey;
            // Recent turns are ALWAYS pulled from the .messages.json (verbatim detail of the
            // most recent exchange) — the essence does not carry verbatim turns.
            snapshot.RecentTurns = conv.RecentTurns;
            // The goal's evolution comes from the essence when it has one. The model's conclusions
            // and the failed tool calls always come from the messages.
            if (!essenceHasContent)
            {
                snapshot.GoalEvolution = ExtractGoalEvolution(conv.UserMessages);
            }

            snapshot.KeyDecisions = ExtractKeyDecisions(conv.AssistantTexts);
            snapshot.DeadEnds = ExtractDeadEnds(conv.FailedToolNotes);
        }

        snapshot.SourceLabel = (essenceHasContent ? "ESSENCE" : "VIBE_STATE")
            + (conv is not null ? " + Cline .messages.json (denoised)" : string.Empty);
        ApplyHeadroomBudget(snapshot, ctx.TargetFreeFraction);
        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Loads the durable essence log from <paramref name="path"/>. Returns null when the path is
    /// blank or the file is missing/unreadable (never throws). The essence is the single source of
    /// truth for a session's meaning — the current snapshot plus the history of how the goal
    /// evolved, the key decisions, and the dead ends.
    /// </summary>
    private static global::FluxMux.Avalonia.Services.ContextEngine.Essence? LoadEssence(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            return global::FluxMux.Avalonia.Services.ContextEngine.Essence.FromJson(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Formats the essence's goal-evolution history into the "## Goal evolution" body.</summary>
    private static string FormatEssenceEvolution(
        System.Collections.Generic.List<global::FluxMux.Avalonia.Services.ContextEngine.EssenceEvolutionEntry> evolution)
    {
        if (evolution.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        // Show the most recent pivots (the essence log is bounded to 64; the last 12 carry the
        // most relevant "how the task changed" signal for a resume).
        var recent = evolution.Skip(Math.Max(0, evolution.Count - 12)).ToList();
        foreach (var ev in recent)
        {
            sb.AppendLine("- " + (string.IsNullOrWhiteSpace(ev.From) ? "—" : ev.From.Trim())
                + " → " + (string.IsNullOrWhiteSpace(ev.To) ? "—" : ev.To.Trim())
                + (string.IsNullOrWhiteSpace(ev.Why) ? string.Empty : " (" + ev.Why.Trim() + ")"));
        }

        return Truncate(sb.ToString().TrimEnd(), MaxGoalEvolutionChars);
    }

    private static void ApplyHeadroomBudget(FidelitySnapshot s, double targetFreeFraction)
    {
        if (s.ContextLimit <= 0)
        {
            return;
        }

        var usedByPrompt = s.PromptTokens;
        var maxUsed = (int)((1.0 - targetFreeFraction) * s.ContextLimit);
        var budget = Math.Max(0, maxUsed - usedByPrompt);
        if (budget <= 0)
        {
            s.RecentTurns = Array.Empty<string>();
            s.DeadEnds = string.Empty;
            s.KeyDecisions = string.Empty;
            s.GoalEvolution = string.Empty;
            return;
        }

        var budgetChars = budget * 4;
        while (FidelitySnapshot.EstimateTokens(s.ToClineSummary()) * 4 > budgetChars)
        {
            if (s.RecentTurns.Count > 0)
            {
                var list = s.RecentTurns.ToList();
                list.RemoveAt(list.Count - 1);
                s.RecentTurns = list;
                continue;
            }

            if (s.DeadEnds.Length > 0)
            {
                s.DeadEnds = string.Empty;
                continue;
            }

            if (s.KeyDecisions.Length > 0)
            {
                s.KeyDecisions = Truncate(s.KeyDecisions, s.KeyDecisions.Length / 2);
                continue;
            }

            if (s.GoalEvolution.Length > 0)
            {
                s.GoalEvolution = Truncate(s.GoalEvolution, s.GoalEvolution.Length / 2);
                continue;
            }

            break;
        }
    }

    private static string Truncate(string text, int maxChars) =>
        maxChars <= 0 ? string.Empty : text.Length <= maxChars ? text : text[..maxChars] + " …[truncated]";

    // ── Essence extraction (heuristic, CPU-only) ───────────────────────────────────

    private static string ExtractGoalEvolution(List<string> userMessages)
    {
        if (userMessages.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        var original = CleanUserText(userMessages[0]);
        sb.AppendLine("Original ask: " + original);

        for (int i = 1; i < userMessages.Count; i++)
        {
            var prev = CleanUserText(userMessages[i - 1]);
            var cur = CleanUserText(userMessages[i]);
            if (cur.Length < 20)
            {
                continue;
            }

            if (OverlapRatio(prev, cur) < 0.35)
            {
                sb.AppendLine("Pivot: " + Truncate(cur, 300));
            }
        }

        return Truncate(sb.ToString().TrimEnd(), MaxGoalEvolutionChars);
    }

    private static string ExtractKeyDecisions(List<string> assistantTexts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var text in assistantTexts)
        {
            var cleaned = CleanAssistantText(text);
            // Filter only trivial noise (e.g. "ok", "done", "I'll try that"). The threshold is
            // deliberately low (15 chars) because a concise but important finding — e.g.
            // "the root cause was the config" (31 chars) — is a perfectly valid key decision and
            // must not be dropped. The LooksLikeDecisionOrFinding check below is what ensures the
            // content is actually meaningful (contains a decision/finding marker), so the length
            // gate only needs to remove one-word acknowledgements, not short-but-substantive lines.
            if (cleaned.Length < 15)
            {
                continue;
            }

            if (LooksLikeDecisionOrFinding(cleaned))
            {
                sb.AppendLine("• " + Truncate(cleaned, 500));
            }

            if (sb.Length > MaxKeyDecisionsChars)
            {
                break;
            }
        }

        return Truncate(sb.ToString().TrimEnd(), MaxKeyDecisionsChars);
    }

    private static string ExtractDeadEnds(List<string> failedToolNotes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var note in failedToolNotes.Distinct().Take(10))
        {
            sb.AppendLine("• " + Truncate(note, 200));
            if (sb.Length > MaxDeadEndsChars)
            {
                break;
            }
        }

        return Truncate(sb.ToString().TrimEnd(), MaxDeadEndsChars);
    }

    private static bool LooksLikeDecisionOrFinding(string text)
    {
        string[] markers =
        {
            "decision", "decided", "concluded", "finding", "found that", "the root cause",
            "root cause", "the fix", "the issue is", "the problem is", "implemented", "added",
            "changed", "refactored", "verified", "confirmed", "the reason", "because",
        };

        var lower = text.ToLowerInvariant();
        return markers.Any(m => lower.Contains(m));
    }

    private static double OverlapRatio(string a, string b)
    {
        var wa = new HashSet<string>(Tokenize(a));
        var wb = new HashSet<string>(Tokenize(b));
        if (wa.Count == 0 || wb.Count == 0)
        {
            return 0;
        }

        int common = 0;
        foreach (var token in wa)
        {
            if (wb.Contains(token))
            {
                common++;
            }
        }

        var min = Math.Min(wa.Count, wb.Count);
        return min == 0 ? 0 : (double)common / min;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var words = text.ToLowerInvariant().Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Where(w => w.Length > 3);
    }

    private static string CleanUserText(string text)
    {
        var start = text.IndexOf(">");
        var end = text.LastIndexOf("</user_input>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            return text[(start + 1)..end].Trim();
        }

        return text.Trim();
    }

    private static string CleanAssistantText(string text)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed;
    }

    // ── Conversation loading + denoising ───────────────────────────────────────────

    private static Conversation? LoadConversation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var conv = new Conversation();
            var seenToolCalls = new HashSet<string>();
            var recent = new List<string>();

            foreach (var msg in messages.EnumerateArray())
            {
                conv.MessageCount++;
                var id = msg.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(id))
                {
                    conv.LastMessageKey = id;
                }

                var role = msg.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? string.Empty : string.Empty;

                switch (role)
                {
                    case "user":
                        HandleUser(msg, conv, recent);
                        break;
                    case "assistant":
                        HandleAssistant(msg, conv, seenToolCalls, recent);
                        break;
                    case "tool":
                    case "tool_result":
                        HandleToolResult(msg, conv);
                        break;
                }
            }

            while (recent.Count > 8)
            {
                recent.RemoveAt(0);
            }

            conv.RecentTurns = recent;
            return conv;
        }
        catch
        {
            return null;
        }
    }

    private static void HandleUser(JsonElement msg, Conversation conv, List<string> recent)
    {
        var text = ExtractText(msg);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var cleaned = CleanUserText(text);
        if (cleaned.Length == 0)
        {
            return;
        }

        conv.UserMessages.Add(cleaned);
        var capped = cleaned.Length > MaxTurnChars ? cleaned[..MaxTurnChars] + " …[truncated]" : cleaned;
        recent.Add("[user] " + capped);
    }

    private static void HandleAssistant(JsonElement msg, Conversation conv, HashSet<string> seenToolCalls, List<string> recent)
    {
        var textParts = new List<string>();
        var toolCalls = new List<string>();

        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var txt))
                {
                    textParts.Add(txt.GetString() ?? string.Empty);
                }
                else if (type == "tool_use")
                {
                    var tool = part.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var input = part.TryGetProperty("input", out var i) ? i.GetRawText() : string.Empty;
                    var hash = StableHash(tool + "|" + input);
                    if (seenToolCalls.Add(hash))
                    {
                        toolCalls.Add(tool);
                    }
                }
            }
        }

        var combinedText = string.Join("\n", textParts.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (!string.IsNullOrWhiteSpace(combinedText))
        {
            conv.AssistantTexts.Add(combinedText);
            var capped = combinedText.Length > MaxTurnChars ? combinedText[..MaxTurnChars] + " …[truncated]" : combinedText;
            recent.Add("[assistant] " + capped);
        }
    }

    private static void HandleToolResult(JsonElement msg, Conversation conv)
    {
        var isFailed = false;
        string note = string.Empty;

        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (part.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    isFailed = true;
                    note = err.GetString() ?? string.Empty;
                }

                if (part.TryGetProperty("success", out var succ) && succ.ValueKind == JsonValueKind.False)
                {
                    isFailed = true;
                }
            }
        }

        if (isFailed && !string.IsNullOrWhiteSpace(note))
        {
            conv.FailedToolNotes.Add(note);
        }
    }

    private static string ExtractText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }

        return sb.ToString();
    }

    private static string StableHash(string input)
    {
        unchecked
        {
            int hash = 23;
            foreach (var c in input)
            {
                hash = hash * 31 + c;
            }

            return hash.ToString("x8");
        }
    }

    private static VibeState LoadVibeState(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return VibeState.Empty;
        }

        try
        {
            var text = File.ReadAllText(path);
            var json = VibeStateMarkdown.ExtractJsonBlock(text);
            if (string.IsNullOrWhiteSpace(json))
            {
                return VibeState.Empty;
            }

            return VibeState.FromJson(json);
        }
        catch
        {
            return VibeState.Empty;
        }
    }

    private sealed class Conversation
    {
        public int MessageCount { get; set; }
        public string LastMessageKey { get; set; } = string.Empty;
        public List<string> UserMessages { get; } = new();
        public List<string> AssistantTexts { get; } = new();
        public List<string> FailedToolNotes { get; } = new();
        public List<string> RecentTurns { get; set; } = new();
    }
}
