using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The planted-fact Live-Switch test (25 Nov). It replaced a benchmark that scored generic words
/// its own prompt supplied, so these pin down that a fact can only be recalled from the chat, that
/// the chat looks like an ordinary supervised agent chat to the gateway (no Mill or repeat pause),
/// and that losses turn into the right advice.
/// </summary>
public sealed class SwitchFidelityTestTests
{
    private static readonly IReadOnlyList<SwitchFidelityTest.Fact> Facts = SwitchFidelityTest.Facts;

    private static string AllText(JsonArray messages) => messages.ToJsonString();

    [Fact]
    public void No_answer_appears_in_its_question_or_in_the_recall_prompt()
    {
        foreach (var fact in Facts)
        {
            Assert.DoesNotContain(fact.Answer, fact.Question, StringComparison.OrdinalIgnoreCase);
        }

        var recall = SwitchFidelityTest.RecallQuestion();
        Assert.All(Facts, f => Assert.DoesNotContain(f.Answer, recall, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(40000)]
    [InlineData(190000)]
    public void The_chat_plants_every_fact_once_and_is_about_the_target_size(int target)
    {
        var chat = SwitchFidelityTest.BuildChat(target);
        var text = AllText(chat);
        foreach (var fact in Facts)
        {
            var first = text.IndexOf(fact.Answer, StringComparison.Ordinal);
            Assert.True(first >= 0, "fact " + fact.Id + " missing");
            Assert.Equal(-1, text.IndexOf(fact.Answer, first + 1, StringComparison.Ordinal));
        }

        var estimate = LocalRequestOverlayRouting.EstimatePromptTokens(new JsonObject { ["messages"] = chat.DeepClone() });
        Assert.InRange(estimate, target * 0.8, target * 1.3);
    }

    [Fact]
    public void Facts_sit_in_order_through_the_chat()
    {
        var text = AllText(SwitchFidelityTest.BuildChat(60000));
        var positions = Facts.Select(f => text.IndexOf(f.Answer, StringComparison.Ordinal)).ToList();
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
        Assert.True(positions[^1] > text.Length * 0.8, "the last fact should be near the end");
    }

    [Fact]
    public void The_chat_does_not_trip_a_Mill_rule_or_the_repeat_pause()
    {
        var sent = SwitchFidelityTest.WithRecallQuestion(SwitchFidelityTest.BuildChat(120000));
        var payload = new JsonObject { ["messages"] = sent };
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, PortForwardingRules.Defaults));
        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));

        // Never more than a handful of tool calls between messages from the person.
        var run = 0;
        foreach (var message in sent.OfType<JsonObject>())
        {
            var role = message["role"]?.ToString();
            run = role == "tool" ? run + 1 : role == "user" ? 0 : run;
            Assert.True(run <= 6, "too many tool results without a user message");
        }
    }

    [Fact]
    public void No_two_tool_results_are_identical_so_the_dedup_pointer_does_not_hide_one()
    {
        var results = SwitchFidelityTest.BuildChat(60000).OfType<JsonObject>()
            .Where(m => m["role"]?.ToString() == "tool")
            .Select(m => m["content"]?.ToString())
            .ToList();
        Assert.Equal(results.Count, results.Distinct().Count());
    }

    [Fact]
    public void Scoring_counts_only_the_planted_answer()
    {
        var perfect = string.Join("\n", Facts.Select(f => f.Id + ". " + f.Answer));
        Assert.All(SwitchFidelityTest.Score(perfect).Recalled, Assert.True);

        var partial = string.Join("\n", Facts.Select(f => f.Id + ". " + (f.Id % 2 == 0 ? f.Answer : "not found")));
        var (recalled, answers) = SwitchFidelityTest.Score(partial);
        Assert.Equal(5, recalled.Count(r => r));
        Assert.Equal("not found", answers[0]);

        // Formatting differences still count: bold numbering, case, spaces in a code.
        Assert.True(SwitchFidelityTest.Score("**1.** kestrel 5821").Recalled[0]);
        Assert.False(SwitchFidelityTest.Score("1. KESTREL-5812").Recalled[0]);
    }

    [Fact]
    public void Chat_sizes_follow_the_two_profiles_Context()
    {
        var different = SwitchFidelityTest.PlanTiers(213056, 163840);
        Assert.Equal(new[] { "Fits both", "Pressure" }, different.Select(t => t.Name));
        Assert.Equal(40000, different[0].TargetTokens);
        Assert.Equal((int)(163840 * 1.15), different[1].TargetTokens); // over the smaller Context, under the larger

        var same = SwitchFidelityTest.PlanTiers(163840, 163840);
        Assert.Equal((int)(163840 * 0.9), same[1].TargetTokens);

        var tiny = SwitchFidelityTest.PlanTiers(4096, 4096);
        Assert.Single(tiny);
    }

    [Fact]
    public void The_return_chat_carries_profile_B_reply_and_asks_again()
    {
        var back = SwitchFidelityTest.ReturnChat(SwitchFidelityTest.BuildChat(8000), "1. WRONG-ANSWER");
        Assert.Contains("WRONG-ANSWER", AllText(back), StringComparison.Ordinal);
        Assert.Equal(SwitchFidelityTest.RecallQuestion(), back[^1]!["content"]!.ToString());
    }

    private static SwitchFidelityTest.PhaseResult Phase(string tier, string phase, string profile, int estimated, int? read, IEnumerable<int> lost, params string[] notes)
    {
        var lostSet = lost.ToHashSet();
        return new SwitchFidelityTest.PhaseResult(
            tier, phase, profile, estimated, read,
            Facts.Select(f => !lostSet.Contains(f.Id)).ToList(),
            Facts.Select(f => lostSet.Contains(f.Id) ? "not found" : f.Answer).ToList(),
            notes, "stop", string.Empty, string.Empty);
    }

    private static readonly SwitchFidelityTest.ProfileSummary A = new("(defaults)", 213056, "Context 213,056, GPU only, KV q8_0/q8_0, compaction by the Client app (AI-FluxMux only at 95 %)");
    private static readonly SwitchFidelityTest.ProfileSummary B = new("YaRN 393k", 393216, "Context 393,216 (YaRN), GPU + CPU, KV q4_0/q4_0, compaction by AI-FluxMux, keep 12 turns, pin 3000 chars");

    [Fact]
    public void Omitted_tool_results_lead_to_Keep_recent_results_advice()
    {
        var tiers = new[] { new SwitchFidelityTest.Tier("Pressure", 188000, "test") };
        var phases = new[]
        {
            Phase("Pressure", "A", "(defaults)", 188000, 186000, []),
            Phase("Pressure", "B", "small", 188000, 120000, [2, 5], "local chat: omitted 96 older tool result(s); llama-server still sees the calls."),
            Phase("Pressure", "A again", "(defaults)", 189000, 187000, []),
        };
        var (summary, details, advice) = SwitchFidelityTest.BuildReport(A, B, tiers, phases);
        Assert.Contains("A 10/10 → B 8/10 → A 10/10", summary, StringComparison.Ordinal);
        Assert.Contains("lost fact 2 (tool result", details, StringComparison.Ordinal);
        Assert.Contains("Keep recent results", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_loss_with_the_whole_chat_read_points_at_the_model_settings()
    {
        var tiers = new[] { new SwitchFidelityTest.Tier("Fits both", 40000, "test") };
        var phases = new[]
        {
            Phase("Fits both", "A", "(defaults)", 40000, 39500, []),
            Phase("Fits both", "B", "YaRN 393k", 40000, 39600, [1, 4]),
            Phase("Fits both", "A again", "(defaults)", 40500, 40000, [1]),
        };
        var (_, _, advice) = SwitchFidelityTest.BuildReport(A, B, tiers, phases);
        Assert.Contains("although it read the whole chat", advice, StringComparison.Ordinal);
        Assert.Contains("q4_0", advice, StringComparison.Ordinal);
        Assert.Contains("YaRN", advice, StringComparison.Ordinal);
        Assert.Contains("repeated Profile B's wrong answer for fact(s) 1", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_run_says_nothing_was_lost()
    {
        var tiers = new[] { new SwitchFidelityTest.Tier("Fits both", 40000, "test") };
        var phases = new[] { "A", "B", "A again" }.Select(p => Phase("Fits both", p, "x", 40000, 39000, [])).ToArray();
        var (_, _, advice) = SwitchFidelityTest.BuildReport(A, B, tiers, phases);
        Assert.Contains("No planted fact was lost", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_lines_that_change_what_the_model_sees_are_kept_from_the_log()
    {
        var notes = SwitchFidelityTest.ShorteningNotes(new[]
        {
            "[2026-11-26 01:00:00] [turn 4] local · 12 ms",
            "[2026-11-26 01:00:00] local chat: omitted 40 older tool result(s); llama-server still sees the calls.",
            "[2026-11-26 01:00:00] [prefill-diag] turn=4 promptTokens=1000 reinflate=False",
            "[2026-11-26 01:00:01] last-resort compact: prompt 180000 tokens over 95 %",
        });
        Assert.Equal(2, notes.Count);
        Assert.StartsWith("local chat: omitted", notes[0], StringComparison.Ordinal);
    }
}
