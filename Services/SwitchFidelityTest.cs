using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// The Live-Switch fidelity test (rebuilt 25 Nov). It measures what actually survives a model
/// profile switch in one chat: ten facts that cannot be guessed are planted through a realistic
/// agent chat (in the opening request, tool results, assistant decisions, later user messages),
/// the whole chat is sent through Port to Profile A, then B, then A again — exactly as a Client
/// app resends it — and each profile is asked questions only those facts answer.
///
/// Why it replaced the old test: the old one sent a fresh made-up conversation per phase, carried
/// only the last 1,200 characters of the previous reply, marked every request as a probe (which
/// the gateway skips compaction for), and passed when the final reply contained generic words such
/// as "context" and "switch" — words its own prompt supplied. It could not see a fact being lost.
///
/// Everything here is pure (no I/O) so it is unit-tested; FluxMuxRuntimeService launches the
/// profiles, sends the chats and reads the gateway log lines for each turn.
/// </summary>
public static class SwitchFidelityTest
{
    public sealed record Fact(int Id, string Where, string Question, string Answer, double Position);

    public sealed record Tier(string Name, int TargetTokens, string Purpose);

    /// <summary>One phase of one tier: what the profile was sent and what it recalled.</summary>
    public sealed record PhaseResult(
        string Tier,
        string Phase,
        string Profile,
        int EstimatedTokens,
        int? PromptTokens,
        IReadOnlyList<bool> Recalled,
        IReadOnlyList<string> Answers,
        IReadOnlyList<string> GatewayNotes,
        string FinishReason,
        string Error,
        string Reply)
    {
        public bool Failed => !string.IsNullOrWhiteSpace(Error);

        public int RecalledCount => Recalled.Count(r => r);
    }

    /// <summary>The settings of a profile that can change what survives a switch.</summary>
    public sealed record ProfileSummary(string Label, int Context, string Detail);

    public const string Opening = "opening request";
    public const string ToolResult = "tool result";
    public const string AssistantDecision = "assistant decision";
    public const string UserMessage = "later user message";

    /// <summary>
    /// The planted facts. The answers are fixed (so runs compare) but made up, so no model can
    /// know them without reading this chat, and none appears in its question.
    /// </summary>
    public static IReadOnlyList<Fact> Facts { get; } =
    [
        new(1, Opening, "What release codename did I give at the start of this task?", "KESTREL-5821", 0.0),
        new(2, ToolResult, "Which metrics port is set in config/service.toml?", "47193", 0.12),
        new(3, AssistantDecision, "Which session store did you decide we would use?", "VaultLite-7412", 0.24),
        new(4, UserMessage, "Which library did I rule out after the security audit?", "quillstack-2.9", 0.36),
        new(5, ToolResult, "Which test did the test run report as flaky?", "test_ledger_rollover_88", 0.48),
        new(6, UserMessage, "Who did I say will review this change?", "Ondrea Vatsk", 0.58),
        new(7, AssistantDecision, "What retry budget, in milliseconds, did you find in client.py?", "1375", 0.68),
        new(8, ToolResult, "What was the hash of the newest commit in the git log output?", "9f3c2ab", 0.78),
        new(9, UserMessage, "Which branch did I say we will ship on after the change of plan?", "release/tern-14", 0.88),
        new(10, UserMessage, "What rollback ticket number did I mention?", "OPS-6620", 0.97),
    ];

    /// <summary>
    /// Two chat sizes. "Fits both" stays well under the smaller Context (and under Omit's start at
    /// half the Context), so it measures the models with the whole chat in view. "Pressure" is
    /// larger than the smaller profile's Context (or close to a shared one), so that profile has to
    /// shorten the chat — which is where real switches lose facts.
    /// </summary>
    public static IReadOnlyList<Tier> PlanTiers(int contextA, int contextB)
    {
        var smaller = Math.Max(4096, Math.Min(contextA, contextB));
        var larger = Math.Max(contextA, contextB);
        var tiers = new List<Tier>
        {
            new("Fits both", Math.Clamp((int)(smaller * 0.35), 4000, 40000),
                "the whole chat fits both profiles, so a lost fact is the model's own recall"),
        };
        var pressure = larger > smaller * 1.1
            ? Math.Min((int)(smaller * 1.15), (int)(larger * 0.9))
            : (int)(smaller * 0.9);
        pressure = Math.Min(pressure, 200000);
        if (pressure > tiers[0].TargetTokens * 1.5)
        {
            // The 200K cap can bring the chat back under the smaller Context; say what it really is.
            tiers.Add(new("Pressure", pressure,
                pressure > smaller
                    ? "the chat is larger than the smaller profile's Context, so it has to be shortened there"
                    : "the chat nearly fills the smaller profile's Context, so shortening starts there"));
        }

        return tiers;
    }

    private const string SystemPrompt =
        "You are a coding agent working in the user's repository. Use the tools to read files and run "
        + "commands. Keep replies short and factual.";

    /// <summary>The chat a Client app would hold after working on the task, about
    /// <paramref name="targetTokens"/> long (chars / 4, as the gateway estimates).</summary>
    public static JsonArray BuildChat(int targetTokens)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Task: tidy the billing service and get the release ready. The release codename for this "
                              + "work is " + Facts[0].Answer + " — use it in the changelog. Read the modules one at a time "
                              + "and tell me what needs changing.",
            },
        };

        // Rounds of read-file calls with ~1.2K-token results, a user message every four calls (so the
        // Mill rules see an ordinary, supervised chat), and the facts at their positions.
        var rounds = Math.Max(Facts.Count + 4, targetTokens / 1250);
        // Each filler function is about 33 tokens; size them so the whole chat lands near the target.
        var functionsPerRound = Math.Clamp((targetTokens - 1500) / rounds / 33, 3, 34);
        var placed = new HashSet<int> { 1 };
        for (var r = 0; r < rounds; r++)
        {
            foreach (var fact in Facts.Where(f => f.Id != 1 && !placed.Contains(f.Id) && (int)(f.Position * rounds) <= r))
            {
                placed.Add(fact.Id);
                AddFact(messages, fact, r);
            }

            if (r > 0 && r % 4 == 0)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Good. Carry on with the next module (" + r + ")." });
            }

            AddToolRound(messages, "call_" + r, "read_file", "src/billing/module_" + r.ToString("000", CultureInfo.InvariantCulture) + ".py", Filler(r, functionsPerRound));
        }

        foreach (var fact in Facts.Where(f => !placed.Contains(f.Id)))
        {
            AddFact(messages, fact, rounds);
        }

        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "I have read all the modules and noted what needs changing." });
        return messages;
    }

    private static void AddFact(JsonArray messages, Fact fact, int round)
    {
        switch (fact.Id)
        {
            case 2:
                AddToolRound(messages, "call_cfg", "read_file", "config/service.toml",
                    "[service]\nname = \"billing\"\nworkers = 6\nmetrics_port = " + fact.Answer + "\nlog_level = \"info\"\n");
                break;
            case 3:
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Decision: we will store sessions in " + fact.Answer + ", not Redis, because it keeps the audit trail." });
                break;
            case 4:
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Do not use " + fact.Answer + " anywhere; it failed the security audit." });
                break;
            case 5:
                AddToolRound(messages, "call_tests", "run_command", "pytest -q",
                    "........F..\n1 flaky (passed on retry): " + fact.Answer + "\n212 passed in 41.3s\n");
                break;
            case 6:
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "When this is ready, " + fact.Answer + " will review the change." });
                break;
            case 7:
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Root cause found: client.py sets the retry budget to " + fact.Answer + " ms, which is too short for the ledger." });
                break;
            case 8:
                AddToolRound(messages, "call_log", "run_command", "git log --oneline -3",
                    fact.Answer + " Fix ledger rounding\n41be07d Add billing metrics\nc8d1e90 Bump dependencies\n");
                break;
            case 9:
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Change of plan: we will ship on " + fact.Answer + " instead of main." });
                break;
            case 10:
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "If anything goes wrong after release, the rollback ticket is " + fact.Answer + "." });
                break;
        }

        _ = round;
    }

    private static void AddToolRound(JsonArray messages, string id, string tool, string argument, string result)
    {
        var argName = tool == "run_command" ? "command" : "path";
        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool,
                        ["arguments"] = new JsonObject { [argName] = argument }.ToJsonString(),
                    },
                },
            },
        });
        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
    }

    /// <summary>Unique, code-like file content (no two results identical, so the dedup pointer
    /// does not fire), about 33 tokens per function, containing none of the planted answers.</summary>
    private static string Filler(int round, int functions)
    {
        var text = new StringBuilder();
        text.Append("# billing/module_").Append(round.ToString("000", CultureInfo.InvariantCulture)).Append(".py\n");
        for (var i = 0; i < functions; i++)
        {
            text.Append("def step_").Append(round).Append('_').Append(i).Append("(ledger, batch):\n")
                .Append("    total = sum(item.amount_").Append(i).Append(" for item in batch)\n")
                .Append("    return ledger.post(\"acct-").Append(round.ToString("0000", CultureInfo.InvariantCulture))
                .Append('-').Append(i.ToString("000", CultureInfo.InvariantCulture)).Append("\", total)\n\n");
        }

        return text.ToString();
    }

    public static string RecallQuestion()
    {
        var text = new StringBuilder(
            "Pause the task. Answer these from earlier in this conversation only, one line each, as \"n. answer\". "
            + "If something is not in the conversation, write \"n. not found\". Do not guess.\n");
        foreach (var fact in Facts)
        {
            text.Append(fact.Id).Append(". ").Append(fact.Question).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The chat plus the recall question — what is sent to Profile A, then Profile B.</summary>
    public static JsonArray WithRecallQuestion(JsonArray chat)
    {
        var messages = (JsonArray)chat.DeepClone();
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = RecallQuestion() });
        return messages;
    }

    /// <summary>The chat as it stands when the Client app returns to Profile A: Profile B's turn is
    /// in it, so a wrong answer from B is in front of A — as it would be in real use.</summary>
    public static JsonArray ReturnChat(JsonArray chat, string profileBReply)
    {
        var messages = WithRecallQuestion(chat);
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = profileBReply ?? string.Empty });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Thanks. Before we carry on, check those once more." });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = RecallQuestion() });
        return messages;
    }

    private static string Normalise(string? text)
        => Regex.Replace((text ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);

    /// <summary>Which facts the reply recalled, and the line it gave for each question.</summary>
    public static (IReadOnlyList<bool> Recalled, IReadOnlyList<string> Answers) Score(string? reply)
    {
        var lines = (reply ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var recalled = new List<bool>();
        var answers = new List<string>();
        foreach (var fact in Facts)
        {
            var line = lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\**\s*" + fact.Id + @"\s*[\.\):]"));
            var answerText = line is null ? string.Empty : Regex.Replace(line, @"^\**\s*\d+\s*[\.\):]\s*", string.Empty).Trim();
            var found = Normalise(line ?? string.Empty).Contains(Normalise(fact.Answer), StringComparison.Ordinal);
            // A reply that answers without the numbering still counts if the answer is anywhere in it.
            if (line is null)
            {
                found = Normalise(reply).Contains(Normalise(fact.Answer), StringComparison.Ordinal);
            }

            recalled.Add(found);
            answers.Add(line is null ? (found ? "(answered, unnumbered)" : "(no answer)") : answerText);
        }

        return (recalled, answers);
    }

    /// <summary>The gateway log lines that change what the model sees, so a loss can be tied to
    /// the step that caused it.</summary>
    public static IReadOnlyList<string> ShorteningNotes(IEnumerable<string> logLines)
        => logLines
            .Where(l => Regex.IsMatch(l, @"omitted|compact|last-resort|pointer|essence: re-injected|vibe-state: appended|repeated the same|dropped \d+ older|hail-mary", RegexOptions.IgnoreCase))
            .Where(l => !l.Contains("prefill-diag", StringComparison.Ordinal))
            // The essence edge detector says "context was compacted" whenever the chat looks shorter
            // than the last one it saw — a profile switch is enough — so it is not a shortening.
            .Where(l => !l.Contains("essence: context was compacted", StringComparison.Ordinal))
            .Select(l => Regex.Replace(l, @"^\[[^\]]+\]\s*", string.Empty))
            .Distinct()
            .ToList();

    /// <summary>The headline, the per-tier detail, and recommendations drawn from what was lost.</summary>
    public static (string Summary, string Details, string Recommendations) BuildReport(
        ProfileSummary profileA,
        ProfileSummary profileB,
        IReadOnlyList<Tier> tiers,
        IReadOnlyList<PhaseResult> phases)
    {
        var details = new StringBuilder();
        var advice = new List<string>();
        details.AppendLine("Profile A: " + profileA.Label + " — " + profileA.Detail);
        details.AppendLine("Profile B: " + profileB.Label + " — " + profileB.Detail);
        var headline = new List<string>();

        foreach (var tier in tiers)
        {
            var a = phases.FirstOrDefault(p => p.Tier == tier.Name && p.Phase == "A");
            var b = phases.FirstOrDefault(p => p.Tier == tier.Name && p.Phase == "B");
            var back = phases.FirstOrDefault(p => p.Tier == tier.Name && p.Phase == "A again");
            details.AppendLine();
            details.AppendLine(tier.Name + " (~" + Thousands(tier.TargetTokens) + " tokens: " + tier.Purpose + ")");
            foreach (var phase in new[] { a, b, back })
            {
                if (phase is null)
                {
                    continue;
                }

                details.AppendLine("  " + PhaseLine(phase));
                var lost = Facts.Where((f, i) => !phase.Failed && !phase.Recalled[i]).ToList();
                foreach (var fact in lost)
                {
                    details.AppendLine("      lost fact " + fact.Id + " (" + fact.Where + ", " + (int)(fact.Position * 100) + " % into the chat): answered \""
                                       + phase.Answers[fact.Id - 1] + "\", expected \"" + fact.Answer + "\"");
                }

                foreach (var note in phase.GatewayNotes)
                {
                    details.AppendLine("      AI-FluxMux: " + note);
                }
            }

            if (a is null || b is null || a.Failed || b.Failed)
            {
                headline.Add(tier.Name + ": did not complete");
                continue;
            }

            headline.Add(tier.Name + ": A " + a.RecalledCount + "/10 → B " + b.RecalledCount + "/10"
                         + (back is { Failed: false } ? " → A " + back.RecalledCount + "/10" : string.Empty));
            advice.AddRange(Advise(tier, profileA, profileB, a, b, back));
        }

        if (advice.Count == 0)
        {
            advice.Add("No planted fact was lost at these chat sizes. Switching between these two profiles kept everything the test checks.");
        }

        var summary = "Facts recalled — " + string.Join("; ", headline) + ".";
        return (summary, details.ToString().TrimEnd(), string.Join("\n", advice.Distinct().Select(a => "• " + a)));
    }

    private static string PhaseLine(PhaseResult p)
    {
        if (p.Failed)
        {
            return p.Phase + " (" + p.Profile + "): did not complete — " + p.Error;
        }

        var sent = p.PromptTokens is int tokens
            ? "llama-server read " + Thousands(tokens) + " of ~" + Thousands(p.EstimatedTokens) + " tokens"
            : "~" + Thousands(p.EstimatedTokens) + " tokens sent";
        return p.Phase + " (" + p.Profile + "): " + p.RecalledCount + "/10 facts; " + sent
               + (p.FinishReason == "length" ? "; reply cut off by max tokens" : string.Empty);
    }

    private static bool WasShortened(PhaseResult p)
        => p.PromptTokens is int tokens && tokens < p.EstimatedTokens * 0.85
           || p.GatewayNotes.Any(n => Regex.IsMatch(n, "omitted|compact|last-resort", RegexOptions.IgnoreCase));

    private static IEnumerable<string> Advise(Tier tier, ProfileSummary profileA, ProfileSummary profileB, PhaseResult a, PhaseResult b, PhaseResult? back)
    {
        var lostOnSwitch = Facts.Where((f, i) => a.Recalled[i] && !b.Recalled[i]).ToList();
        if (a.RecalledCount < Facts.Count && !WasShortened(a))
        {
            yield return tier.Name + ": Profile A itself missed " + (Facts.Count - a.RecalledCount)
                         + " fact(s) with the whole chat in view, so its own recall is the limit, not the switch. " + ModelSideAdvice(profileA);
        }

        if (lostOnSwitch.Count > 0)
        {
            var toolLost = lostOnSwitch.Where(f => f.Where == ToolResult).ToList();
            var talkLost = lostOnSwitch.Where(f => f.Where != ToolResult).ToList();
            var omitted = b.GatewayNotes.Any(n => n.Contains("omitted", StringComparison.OrdinalIgnoreCase));
            var compacted = b.GatewayNotes.Any(n => Regex.IsMatch(n, "compact|last-resort", RegexOptions.IgnoreCase));
            var ids = string.Join(", ", lostOnSwitch.Select(f => f.Id));
            if (!WasShortened(b))
            {
                yield return tier.Name + ": Profile B lost fact(s) " + ids + " although it read the whole chat. " + ModelSideAdvice(profileB);
            }
            else
            {
                if (toolLost.Count > 0 && omitted)
                {
                    yield return tier.Name + ": Omit shortened older tool results on Profile B and the facts inside them were lost ("
                                 + string.Join(", ", toolLost.Select(f => f.Id)) + "). Raise Port rule Keep recent results, or give Profile B a larger Context, "
                                 + "so tool output that still matters is sent in full.";
                }

                if (talkLost.Count > 0 && compacted)
                {
                    yield return tier.Name + ": Compaction on Profile B kept the newest part of the chat and summarised the rest; the summary did not carry fact(s) "
                                 + string.Join(", ", talkLost.Select(f => f.Id)) + ". A larger Context on Profile B keeps more of the chat in full. Keep VIBE_STATE on so the session note "
                                 + "carries goals and decisions, and raise Preserved user chars if the opening request matters.";
                }

                if ((toolLost.Count > 0 && !omitted) || (talkLost.Count > 0 && !compacted))
                {
                    yield return tier.Name + ": Profile B read a shortened chat (" + Thousands(b.PromptTokens ?? 0) + " of ~" + Thousands(b.EstimatedTokens)
                                 + " tokens) and lost fact(s) " + ids + ". A larger Context on Profile B is the surest fix.";
                }
            }
        }

        if (back is { Failed: false })
        {
            var carried = Facts.Where((f, i) => a.Recalled[i] && !b.Recalled[i] && !back.Recalled[i]).ToList();
            if (carried.Count > 0)
            {
                yield return tier.Name + ": after switching back, Profile A repeated Profile B's wrong answer for fact(s) "
                             + string.Join(", ", carried.Select(f => f.Id)) + ". A wrong turn from the other profile stays in the chat, so the cost of a lossy switch outlasts it.";
            }
        }

        foreach (var p in new[] { a, b, back })
        {
            if (p is { FinishReason: "length" })
            {
                yield return tier.Name + ": the reply on " + p.Profile + " was cut off by its max tokens; raise Max tokens on that profile or its Quick Select slot.";
            }
        }
    }

    private static string ModelSideAdvice(ProfileSummary profile)
    {
        var parts = new List<string>();
        if (profile.Detail.Contains("KV q4", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("its KV cache is q4_0 — q8_0 keeps older context more precisely");
        }

        if (profile.Detail.Contains("YaRN", StringComparison.Ordinal))
        {
            parts.Add("it stretches the model past its native Context with YaRN, which blurs recall far back in the chat");
        }

        return parts.Count == 0
            ? "Try a lower Temperature or a stronger model for this profile."
            : "On " + profile.Label + ", " + string.Join("; and ", parts) + ".";
    }

    private static string Thousands(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
