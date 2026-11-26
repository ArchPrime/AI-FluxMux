using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Hail Mary's one-press rebuild (26 Nov): the rebuilt chat must keep as much as possible, keep
/// every planted fact, and still leave room for "retry" — about 60 % of the Context, never over.
/// </summary>
public sealed class RescuePlannerTests
{
    [Theory]
    [InlineData(130000, 163840)]
    [InlineData(197000, 163840)]
    [InlineData(260000, 213056)]
    public void A_stuck_chat_is_rebuilt_to_about_sixty_percent_with_every_fact(int chatTokens, int context)
    {
        var chat = SwitchFidelityTest.BuildChat(chatTokens);
        var plan = RescuePlanner.FromChat(chat, context, 1.0)!;
        Assert.NotNull(plan);

        var fill = (double)plan.TotalTokens / context;
        Assert.InRange(fill, 0.45, RescuePlanner.TargetFillShare + 0.01);
        Assert.True(plan.KeepRecent > 20, "kept only " + plan.KeepRecent + " messages");

        // What the rebuilt chat holds: the summary plus the kept messages word for word.
        var kept = string.Concat(chat.Skip(chat.Count - plan.KeepRecent).Select(m => m!.ToJsonString()));
        var lost = SwitchFidelityTest.Facts.Where(f => !plan.Summary.Contains(f.Answer, StringComparison.Ordinal) && !kept.Contains(f.Answer, StringComparison.Ordinal)).Select(f => f.Id).ToList();
        Assert.True(lost.Count == 0, "lost facts: " + string.Join(", ", lost));
    }

    [Fact]
    public void The_kept_part_never_starts_on_an_orphaned_tool_result()
    {
        var chat = SwitchFidelityTest.BuildChat(130000);
        for (var context = 40000; context <= 200000; context += 7919)
        {
            var plan = RescuePlanner.FromChat(chat, context, 1.3)!;
            var firstKept = chat[chat.Count - plan.KeepRecent] as JsonObject;
            Assert.NotEqual("tool", firstKept!["role"]!.ToString());
        }
    }

    [Fact]
    public void The_newest_message_is_always_kept_even_when_it_alone_is_over_budget()
        => Assert.Equal(3, RescuePlanner.TailStart([10, 10, 10, 5000], budget: 100, floor: 1));

    [Fact]
    public void Cline_compaction_keeps_the_tail_and_does_not_start_on_a_tool_result()
    {
        static JsonObject Msg(string role, string type, int size)
            => new() { ["role"] = role, ["content"] = new JsonArray { new JsonObject { ["type"] = type, ["text"] = new string('x', size) } } };

        var messages = new JsonArray { Msg("user", "text", 400) };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(Msg("assistant", "tool_use", 400));
            messages.Add(Msg("user", "tool_result", 400));
        }

        var count = ExternalRescueStrategy.CompactCount(messages, keptTokens: 1000, tokenRatio: 1.0);
        Assert.InRange(count, 1, messages.Count - 1);
        Assert.Equal("assistant", messages[count]!["role"]!.ToString());
        Assert.Equal(messages.Count, ExternalRescueStrategy.CompactCount(messages, keptTokens: 0, tokenRatio: 1.0) + 1);
    }

    [Fact]
    public void Describe_names_what_was_kept()
        => Assert.Equal("summary + 84 recent messages, about 58 % of the Context",
            new RescuePlan("s", 84, 1000, 2000, 92000, 163840, 1.0).Describe());
}
