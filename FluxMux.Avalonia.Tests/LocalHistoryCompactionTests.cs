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
        Assert.True(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true,
            promptTokens: 124000,
            contextTokens: 213056,
            promptExceeds: false,
            fillingEstimate: true));
        Assert.False(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true,
            promptTokens: 124000,
            contextTokens: 213056,
            promptExceeds: false,
            fillingEstimate: false));
    }

    [Fact]
    public void Compact_before_filling_400_can_clear_a_tool_heavy_history()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." }
        };
        for (var i = 0; i < 16; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Turn " + i + " " + new string('n', 2200)
            });
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "Reply " + i + " " + new string('n', 2200)
            });
        }

        var payload = new JsonObject { ["messages"] = messages };
        var state = new JsonObject { ["local_context"] = 32768, ["local_vision"] = "Disabled" };
        var promptStd = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.False(LocalHistoryCompaction.IsNearLimit(promptStd, 32768));
        Assert.True(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.True(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true,
            promptTokens: promptStd,
            contextTokens: 32768,
            promptExceeds: false,
            fillingEstimate: true));
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
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

    [Fact]
    public void Compact_does_not_leave_an_orphan_tool_result_at_the_kept_tail()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            new JsonObject { ["role"] = "user", ["content"] = "Fix the layout in MainWindow and keep the Health copy." }
        };
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_" + i,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = i % 2 == 0 ? "read_file" : "replace_in_file",
                            ["arguments"] = "{\"path\":\"MainWindow.axaml\"}"
                        }
                    }
                }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "call_" + i,
                ["content"] = "<tool_response>chunk " + i + "</tool_response>"
            });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        var compacted = payload["messages"] as JsonArray;
        Assert.NotNull(compacted);

        var firstKept = compacted!.OfType<JsonObject>()
            .SkipWhile(msg =>
                (msg["role"]?.ToString() ?? string.Empty).Equals("system", StringComparison.OrdinalIgnoreCase)
                || (msg["content"]?.ToString() ?? string.Empty).StartsWith("Compressed prior", StringComparison.Ordinal)
                || (msg["content"]?.ToString() ?? string.Empty).Contains("Fix the layout", StringComparison.Ordinal))
            .FirstOrDefault();
        Assert.NotNull(firstKept);
        Assert.NotEqual("tool", firstKept!["role"]?.ToString());
        Assert.Contains("Fix the layout in MainWindow", compacted.ToJsonString());
        Assert.Contains(LocalToolResultClearing.AlreadyRanHeader, compacted.ToJsonString());
        Assert.Contains("read_file: MainWindow.axaml", compacted.ToJsonString());
        Assert.DoesNotContain("tool_calls: read_file", compacted.ToJsonString());
        Assert.True(LocalChatTemplateGuard.HasRealUserQuery(compacted));
    }

    [Fact]
    public void Forced_compact_collapses_a_repeated_shell_call_in_the_ledger()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            new JsonObject { ["role"] = "user", ["content"] = "Run the tests and fix what fails." }
        };
        for (var i = 0; i < 8; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_" + i,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "execute_command",
                            ["arguments"] = "{\"command\":\"npm test\"}"
                        }
                    }
                }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "call_" + i,
                ["content"] = new string('x', 400)
            });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        var json = payload["messages"]!.ToJsonString();
        Assert.Contains(LocalToolResultClearing.AlreadyRanHeader, json);
        Assert.Contains("execute_command: npm test", json);
        Assert.Contains("times", json);
    }

    [Fact]
    public void Forced_compact_does_not_paste_a_dropped_file_body_into_the_summary()
    {
        var marker = "UNIQUE_FILE_BODY_SHOULD_NOT_REACH_LLAMA";
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            new JsonObject { ["role"] = "user", ["content"] = "Fix the layout in MainWindow." }
        };
        for (var i = 0; i < 24; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_" + i,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "read_file",
                            ["arguments"] = "{\"path\":\"MainWindow.axaml\"}"
                        }
                    }
                }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "call_" + i,
                ["content"] = (i < 12 ? marker + " " : "later ") + new string('x', 400)
            });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        var json = payload["messages"]!.ToJsonString();
        Assert.Contains(LocalToolResultClearing.AlreadyRanHeader, json);
        Assert.Contains("read_file: MainWindow.axaml", json);
        Assert.DoesNotContain(marker, json);
    }
}
