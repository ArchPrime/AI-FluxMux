using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalHistoryCompactionTests
{
    [Fact]
    public void Near_limit_is_true_when_the_prompt_is_inside_the_last_15_percent()
    {
        Assert.True(LocalHistoryCompaction.IsNearLimit(promptTokens: 14000, contextTokens: 16384));
        Assert.False(LocalHistoryCompaction.IsNearLimit(promptTokens: 8000, contextTokens: 16384));
        Assert.False(LocalHistoryCompaction.IsNearLimit(promptTokens: 16300, contextTokens: 16384));
    }

    [Fact]
    public void Forward_compact_only_runs_when_the_window_is_actually_tight()
    {
        Assert.False(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true, promptTokens: 8000, contextTokens: 16384, promptExceeds: false));
        Assert.True(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true, promptTokens: 14000, contextTokens: 16384, promptExceeds: false));
        Assert.True(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true, promptTokens: 20000, contextTokens: 16384, promptExceeds: true));
        Assert.False(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: false, promptTokens: 14000, contextTokens: 16384, promptExceeds: true));
    }

    [Fact]
    public void Destination_is_tight_when_the_chat_would_not_fit_with_headroom()
    {
        Assert.True(LocalHistoryCompaction.IsDestinationTight(54272, 80000));
        Assert.False(LocalHistoryCompaction.IsDestinationTight(131072, 40000));
    }

    [Fact]
    public void Forced_compact_keeps_recent_turns_and_drops_older_ones()
    {
        var messages = new JsonArray();
        messages.Add(new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." });
        for (var i = 0; i < 12; i++)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Turn " + i + " " + new string('x', 80) });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Reply " + i + " " + new string('y', 80) });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));

        var compacted = payload["messages"] as JsonArray;
        Assert.NotNull(compacted);
        Assert.True(compacted!.Count < messages.Count);
        Assert.Contains("Compressed prior conversation context", compacted.ToJsonString());
        Assert.Contains("Turn 11", compacted.ToJsonString());
        Assert.Equal("system", compacted[0]!["role"]?.ToString());
        Assert.Equal("user", compacted[1]!["role"]?.ToString());
        Assert.DoesNotContain(compacted.OfType<JsonObject>().Skip(1), msg =>
            (msg["role"]?.ToString() ?? string.Empty).Equals("system", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Forced_compact_leaves_a_user_query_for_qwen_after_a_short_tool_loop()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            new JsonObject { ["role"] = "user", ["content"] = "Fix the layout in MainWindow." }
        };
        for (var i = 0; i < 6; i++)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Calling read " + i + "." });
            messages.Add(new JsonObject { ["role"] = "tool", ["content"] = "<tool_response>chunk " + i + "</tool_response>" });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        var compacted = payload["messages"] as JsonArray;
        Assert.NotNull(compacted);
        Assert.True(LocalChatTemplateGuard.HasRealUserQuery(compacted!));
    }
}
