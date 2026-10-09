using System;
using System.Collections.Generic;
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
        // 70400 chars: 2-char est = 35200 > 32768 (trips compact), 4-char est
        // = 17600 < 32768 (fits, so not a blocking condition). The compact
        // trigger uses the pessimistic 2-char estimate.
        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.True(FluxMuxGatewayRouting.PromptFillsLocalContextForCompact(state, payload));
        Assert.True(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true,
            promptTokens: promptStd,
            contextTokens: 32768,
            promptExceeds: false,
            fillingEstimate: true));
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContextForCompact(state, payload));
    }

    [Fact]
    public void Demonstrate_auto_compact_as_the_context_fills_up()
    {
        // A 32k context window, default auto-compact settings:
        //   trigger 80% (26624 tokens), keep 6 turns, keep 8 tool results,
        //   pin 2000 chars of the original user task.
        const int context = 32768;
        var rules = PortForwardingRules.Defaults; // AutoCompactTriggerPercent=0.80, KeepTurns=6, ...

        // Build a growing conversation: system + original task + N turns of
        // user/assistant pairs (each ~500 chars of real text).
        JsonArray BuildHistory(int turns)
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
                new JsonObject { ["role"] = "user", ["content"] = "Fix the layout in MainWindow and keep the Health copy visible." }
            };
            for (var i = 0; i < turns; i++)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Turn " + i + ": " + new string('a', 480)
                });
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "Reply " + i + ": " + new string('b', 480)
                });
            }

            return messages;
        }

        var lines = new List<string>
        {
            "=== Auto-compact behaviour as the context fills up ===",
            "Context window: " + context + " tokens",
            "Settings: trigger=" + (rules.AutoCompactTriggerPercent * 100).ToString("0") + "%  keepTurns=" + rules.AutoCompactKeepTurns
                + "  keepToolResults=" + rules.AutoCompactKeepToolResults + "  pinUserChars=" + rules.AutoCompactPinUserChars,
            "Trigger token threshold: " + (int)(context * rules.AutoCompactTriggerPercent),
            string.Empty,
            "turns | promptTokens | %full | IsNearLimit | ShouldForwardCompact | compacted? | before->after msgs"
        };

        foreach (var turns in new[] { 0, 5, 10, 15, 20, 25, 30, 40, 50 })
        {
            var messages = BuildHistory(turns);
            var payload = new JsonObject { ["messages"] = messages };
            var promptTokens = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            var pct = 100.0 * promptTokens / context;
            var nearLimit = LocalHistoryCompaction.IsNearLimit(promptTokens, context);
            var shouldForward = LocalHistoryCompaction.ShouldForwardCompact(
                compactEnabled: true,
                promptTokens: promptTokens,
                contextTokens: context,
                promptExceeds: promptTokens > context);

            var before = messages.Count;
            var compacted = LocalHistoryCompaction.TryCompactPayload(payload, force: shouldForward);
            var after = (payload["messages"] as JsonArray)?.Count ?? before;

            lines.Add(
                turns.ToString().PadRight(5) + " | " + promptTokens.ToString().PadRight(12)
                + " | " + pct.ToString("0") + "%" + new string(' ', Math.Max(1, 6 - pct.ToString("0").Length))
                + " | " + nearLimit.ToString().PadRight(13)
                + " | " + shouldForward.ToString().PadRight(20)
                + " | " + (compacted ? "yes" : "no").PadRight(10)
                + " | " + before + "->" + after);
        }

        // Show what the compacted payload actually looks like at the trigger point.
        var triggerMessages = BuildHistory(30);
        var triggerPayload = new JsonObject { ["messages"] = triggerMessages };
        var triggerTokens = LocalRequestOverlayRouting.EstimatePromptTokens(triggerPayload);
        var triggerForward = LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true, promptTokens: triggerTokens, contextTokens: context, promptExceeds: triggerTokens > context);
        var didCompact = LocalHistoryCompaction.TryCompactPayload(triggerPayload, force: triggerForward);

        lines.Add(string.Empty);
        lines.Add("=== At trigger point (30 turns, " + triggerTokens + " tokens, " + (100.0 * triggerTokens / context).ToString("0") + "% full) ===");
        lines.Add("ShouldForwardCompact = " + triggerForward + ", compacted = " + didCompact);
        var compactedArray = triggerPayload["messages"] as JsonArray;
        lines.Add("Compacted message count: " + triggerMessages.Count + " -> " + (compactedArray?.Count ?? triggerMessages.Count));
        if (compactedArray is not null)
        {
            foreach (var msg in compactedArray.OfType<JsonObject>())
            {
                var role = msg["role"]?.ToString() ?? "?";
                var content = msg["content"]?.ToString() ?? string.Empty;
                var preview = content.Length > 90 ? content[..90] + "…" : content;
                lines.Add("  [" + role + "] " + preview);
            }
        }

        var report = string.Join(Environment.NewLine, lines);
        Console.WriteLine(report);

        // Sanity assertions so this doubles as a regression test.
        Assert.True(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true, promptTokens: 30000, contextTokens: context, promptExceeds: false));
        Assert.False(LocalHistoryCompaction.ShouldForwardCompact(
            compactEnabled: true, promptTokens: 10000, contextTokens: context, promptExceeds: false));
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

    // ── Demonstration: walk the context window from empty to full ──────────────

    [Fact]
    public void Auto_compact_decision_walks_the_context_window_from_empty_to_full()
    {
        // Simulate a 32k-token model with default auto-compact settings.
        const int contextTokens = 32768;
        var rules = PortForwardingRules.Defaults; // trigger 0.80, keepTurns 6, keepToolResults 8, pinUserChars 2000

        // Walk the window: at each step, add one more turn and check the decision.
        var lines = new List<string>
        {
            $"Context window: {contextTokens} tokens",
            $"Auto-compact trigger: {rules.AutoCompactTriggerPercent:P0} of context = " +
                $"{(int)(rules.AutoCompactTriggerPercent * contextTokens)} tokens",
            $"Keep turns: {rules.AutoCompactKeepTurns}, Keep tool results: {rules.AutoCompactKeepToolResults}, " +
                $"Pin user chars: {rules.AutoCompactPinUserChars}",
            new string('=', 72)
        };

        var totalTurns = 20;
        for (var turnCount = 0; turnCount <= totalTurns; turnCount++)
        {
            // Build a fresh payload with just the first `turnCount` turns.
            var slice = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." }
            };
            for (var i = 0; i < turnCount; i++)
            {
                slice.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Turn {i}: please examine the file and report back. " + new string('u', 1200)
                });
                slice.Add(new JsonObject
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
                                ["arguments"] = $"{{\"path\":\"file{i}.cs\"}}"
                            }
                        }
                    }
                });
                slice.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = "call_" + i,
                    ["content"] = new string('t', 2500)
                });
                slice.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = $"Turn {i} reply: the file looks fine. " + new string('a', 1200)
                });
            }

            var payload = new JsonObject { ["messages"] = slice };
            var promptTokens = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
            var triggerTokens = (int)(rules.AutoCompactTriggerPercent * contextTokens);
            var shouldCompact = promptTokens >= triggerTokens;
            var isNearLimit = LocalHistoryCompaction.IsNearLimit(promptTokens, contextTokens);

            var decision = shouldCompact ? "COMPACT" : "       ";
            var nearLimit = isNearLimit ? "near-limit" : "         ";
            lines.Add(
                $"Turns: {turnCount,2} | Prompt: {promptTokens,6} tokens " +
                $"({promptTokens * 100.0 / contextTokens,4:0.0}%) | " +
                $"Trigger: {triggerTokens,6} | {decision} | {nearLimit}");
        }

        lines.Add(new string('=', 72));
        lines.Add("Note: 'near-limit' = inside the last 15% of the window (IsNearLimit).");
        lines.Add("Note: 'COMPACT' = prompt tokens >= trigger threshold (auto-compact fires).");

        // Print the walk for the user to see.
        var output = string.Join(Environment.NewLine, lines);
        Console.WriteLine(output);

        // Also verify the key invariants:
        // 1. At 0 turns, no compact.
        var emptyPayload = new JsonObject { ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." }
        } };
        var emptyTokens = LocalRequestOverlayRouting.EstimatePromptTokens(emptyPayload);
        Assert.True(emptyTokens < (int)(rules.AutoCompactTriggerPercent * contextTokens));

        // 2. At full 20 turns, compact should fire.
        // Each turn is sized so the 20-turn payload clearly exceeds the 80% trigger
        // (0.80 * 32768 = 26,214 tokens). ~1,600 tokens/turn * 20 = ~32,000 tokens.
        var fullSlice = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." }
        };
        for (var i = 0; i < 20; i++)
        {
            fullSlice.Add(new JsonObject { ["role"] = "user", ["content"] = $"Turn {i}. " + new string('u', 2000) });
            fullSlice.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_" + i,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" }
                    }
                }
            });
            fullSlice.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_" + i, ["content"] = new string('t', 4000) });
            fullSlice.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"Reply {i}. " + new string('a', 2000) });
        }
        var fullPayload = new JsonObject { ["messages"] = fullSlice };
        var fullTokens = LocalRequestOverlayRouting.EstimatePromptTokens(fullPayload);
        Assert.True(fullTokens >= (int)(rules.AutoCompactTriggerPercent * contextTokens));

        // 3. The trigger threshold is between the two.
        var trigger = (int)(rules.AutoCompactTriggerPercent * contextTokens);
        Assert.InRange(trigger, emptyTokens, fullTokens);
    }
}
