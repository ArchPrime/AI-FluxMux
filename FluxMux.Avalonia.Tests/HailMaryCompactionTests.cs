using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HailMaryCompactionTests
{
    private static JsonObject Payload(params JsonObject[] messages)
        => new() { ["messages"] = new JsonArray(messages) };

    private static JsonObject User(string text)
        => new() { ["role"] = "user", ["content"] = text };

    private static JsonObject Assistant(string text)
        => new() { ["role"] = "assistant", ["content"] = text };

    private static JsonObject ToolResult(string text, string? toolCallId = null)
    {
        var msg = new JsonObject { ["role"] = "tool", ["content"] = text };
        if (toolCallId is not null)
        {
            msg["tool_call_id"] = toolCallId;
        }

        return msg;
    }

    private static JsonObject AssistantWithCall(string callId, string toolName, string argsJson)
    {
        var call = new JsonObject
        {
            ["id"] = callId,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = argsJson
            }
        };

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["tool_calls"] = new JsonArray { call }
        };
    }

    [Fact]
    public void Salvage_is_a_noop_when_the_prompt_already_fits()
    {
        var payload = Payload(
            User("hello"),
            Assistant("hi there"));
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 100000);
        Assert.Same(HailMaryResult.None, result);
        Assert.Equal(2, payload["messages"]!.AsArray().Count);
    }

    [Fact]
    public void Stage1_dedupes_identical_messages_and_keeps_the_first()
    {
        // A Cline retry: the same user message sent twice.
        var payload = Payload(
            User("Fix the layout bug in MainWindow."),
            Assistant("I'll look into it."),
            User("Fix the layout bug in MainWindow."),
            Assistant("Done."));
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        Assert.True(result.TokensSaved > 0);
        var messages = payload["messages"]!.AsArray();
        // The duplicate user message is gone; the first copy remains.
        Assert.Equal(3, messages.Count);
        Assert.Equal("Fix the layout bug in MainWindow.", messages[0]!["content"]!.ToString());
        Assert.Contains("dedupe", result.DiagnosticsLine);
    }

    [Fact]
    public void Stage1_never_dedupes_the_system_prompt()
    {
        var payload = Payload(
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("hi"));
        HailMaryCompaction.Salvage(payload, contextTokens: 10);
        var systems = payload["messages"]!.AsArray().Count(m => m!["role"]!.ToString() == "system");
        Assert.Equal(2, systems);
    }

    [Fact]
    public void Stage2_collapses_repeated_shell_command_loops_and_stubs_the_result()
    {
        // The same shell command run 4 times (a loop). Keep first + last, stub the middle.
        var payload = Payload(
            User("Run the build and tell me the result."),
            AssistantWithCall("c1", "run_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("Build succeeded (1).", "c1"),
            AssistantWithCall("c2", "run_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("Build succeeded (2).", "c2"),
            AssistantWithCall("c3", "run_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("Build succeeded (3).", "c3"),
            AssistantWithCall("c4", "run_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("Build succeeded (4).", "c4"));
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        // The loop was collapsed: 4 calls -> 2 (first + last kept).
        Assert.Equal(2, result.LoopsCollapsed);
        var messages = payload["messages"]!.AsArray();
        // Count remaining run_command calls: first + last = 2.
        var remainingCalls = 0;
        foreach (var m in messages)
        {
            if (m!["tool_calls"] is JsonArray calls)
            {
                remainingCalls += calls.Count;
            }
        }

        Assert.Equal(2, remainingCalls);
        // The pruned middle results are stubbed with the "already ran" marker.
        var stubs = messages.Count(m =>
            m!["content"]!.ToString().Contains("already ran earlier in the conversation"));
        Assert.True(stubs >= 2, "expected at least 2 stubbed results, got " + stubs);
        Assert.Contains("loops", result.DiagnosticsLine);
    }

    [Fact]
    public void Stage2_collapses_repeated_read_loops_keyed_on_path_and_range()
    {
        // The same read_file (same path + range) repeated 3 times.
        var args = "{\"path\":\"C:\\\\proj\\\\Main.cs\",\"start_line\":1,\"end_line\":200}";
        var payload = Payload(
            User("Read Main.cs and explain it."),
            AssistantWithCall("r1", "read_file", args),
            ToolResult("line 1\nline 2\n... (1)", "r1"),
            AssistantWithCall("r2", "read_file", args),
            ToolResult("line 1\nline 2\n... (2)", "r2"),
            AssistantWithCall("r3", "read_file", args),
            ToolResult("line 1\nline 2\n... (3)", "r3"));
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        // 3 repeats >= threshold: keep first + last = 2, so 1 collapsed.
        Assert.Equal(1, result.LoopsCollapsed);
        var messages = payload["messages"]!.AsArray();
        var remainingCalls = 0;
        foreach (var m in messages)
        {
            if (m!["tool_calls"] is JsonArray calls)
            {
                remainingCalls += calls.Count;
            }
        }

        // 3 repeats >= threshold: keep first + last = 2.
        Assert.Equal(2, remainingCalls);
    }

    [Fact]
    public void Stage2_keeps_distinct_read_ranges()
    {
        // Two reads of the same path but DIFFERENT ranges — both are kept.
        var payload = Payload(
            User("Read Main.cs."),
            AssistantWithCall("r1", "read_file", "{\"path\":\"C:\\\\proj\\\\Main.cs\",\"start_line\":1,\"end_line\":100}"),
            ToolResult("part one", "r1"),
            AssistantWithCall("r2", "read_file", "{\"path\":\"C:\\\\proj\\\\Main.cs\",\"start_line\":101,\"end_line\":200}"),
            ToolResult("part two", "r2"));
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        var messages = payload["messages"]!.AsArray();
        var remainingCalls = 0;
        foreach (var m in messages)
        {
            if (m!["tool_calls"] is JsonArray calls)
            {
                remainingCalls += calls.Count;
            }
        }

        // Different ranges are different keys — neither is pruned.
        Assert.Equal(2, remainingCalls);
        Assert.Equal(0, result.LoopsCollapsed);
    }

    [Fact]
    public void Stage3_truncates_oversized_older_tool_results_but_keeps_recent()
    {
        // 10 tool results; the 2 oldest are huge, the 8 most recent are kept.
        var messages = new List<JsonObject> { User("Investigate the build.") };
        for (var i = 0; i < 10; i++)
        {
            var id = "t" + i;
            messages.Add(AssistantWithCall(id, "run_command", "{\"command\":\"step " + i + "\"}"));
            // Make the two oldest results oversized (> 4000 chars).
            var body = i < 2 ? new string('x', 8000) : "short result " + i;
            messages.Add(ToolResult(body, id));
        }

        var payload = Payload(messages.ToArray());
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        Assert.True(result.ResultsTruncated >= 2, "expected >=2 truncated, got " + result.ResultsTruncated);
        Assert.Contains("results", result.DiagnosticsLine);
    }

    [Fact]
    public void Stage4_strips_older_images_but_keeps_the_most_recent()
    {
        // A large base64 blob so the image actually costs tokens.
        var blob = "data:image/png;base64," + new string('A', 8000);
        var imageContent = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "see the screenshot" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = blob } }
        };

        var older = new JsonObject { ["role"] = "user", ["content"] = imageContent.DeepClone() };
        var recent = new JsonObject { ["role"] = "user", ["content"] = imageContent.DeepClone() };
        // Make the two image messages distinct so dedupe (stage 1) does not collapse
        // them before the image-strip stage (stage 4) gets a chance to run.
        older["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "first screenshot" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = blob } }
        };
        var payload = Payload(User("Here are two screenshots."), older, recent);
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        Assert.True(result.ImagesStripped >= 1, "expected >=1 image stripped, got " + result.ImagesStripped + " stages=[" + string.Join("|", result.Stages) + "]");
        var messages = payload["messages"]!.AsArray();
        // The most recent image is kept.
        Assert.Contains("data:image/png", messages[^1]!["content"]!.ToString());
        // The older image is replaced with a marker.
        Assert.Contains("image not forwarded", messages[^2]!["content"]!.ToString());
        Assert.Contains("images", result.DiagnosticsLine);
    }

    [Fact]
    public void Stage4_image_strip_is_off_for_automatic_paths_and_on_for_the_button()
    {
        // A large base64 blob so the image actually costs tokens.
        var blob = "data:image/png;base64," + new string('A', 8000);
        var imageContent = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "see the screenshot" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = blob } }
        };

        var older = new JsonObject { ["role"] = "user", ["content"] = imageContent.DeepClone() };
        var recent = new JsonObject { ["role"] = "user", ["content"] = imageContent.DeepClone() };
        // Make the two image messages distinct so dedupe (stage 1) does not collapse
        // them before the image-strip stage (stage 4) gets a chance to run.
        older["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "first screenshot" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = blob } }
        };

        // Automatic path (rejoin prune / compact fallback): stripImages=false.
        // Both images must be kept so scenarios that compare one image with another survive.
        var autoPayload = Payload(User("Here are two screenshots."), (JsonObject)older.DeepClone(), (JsonObject)recent.DeepClone());
        var autoResult = HailMaryCompaction.Salvage(autoPayload, contextTokens: 10, allowMiddleCut: false, stripImages: false);
        Assert.Equal(0, autoResult.ImagesStripped);
        var autoMessages = autoPayload["messages"]!.AsArray();
        Assert.Contains("data:image/png", autoMessages[^1]!["content"]!.ToString());
        Assert.Contains("data:image/png", autoMessages[^2]!["content"]!.ToString());

        // Manual Hail Mary button: stripImages=true (default). The older image is stripped.
        var buttonPayload = Payload(User("Here are two screenshots."), (JsonObject)older.DeepClone(), (JsonObject)recent.DeepClone());
        var buttonResult = HailMaryCompaction.Salvage(buttonPayload, contextTokens: 10, allowMiddleCut: true, stripImages: true);
        Assert.True(buttonResult.ImagesStripped >= 1, "expected >=1 image stripped on the button, got " + buttonResult.ImagesStripped);
        Assert.Contains("images", buttonResult.DiagnosticsLine);
    }

    [Fact]
    public void RearContextPrune_drops_oldest_turns_to_headroom_and_preserves_brief_task_and_recent()
    {
        // A long conversational history: system + initial brief + many middle turns + final task.
        // The middle turns are where the bloat lives; the fix must drop the OLDEST of them,
        // keeping the system prompt, the initial brief (first user task), the last task, and
        // the most recent turns.
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("Original task: fix the bug."),
        };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(User("Turn " + i + " " + new string('n', 300)));
            messages.Add(Assistant("Reply " + i + " " + new string('n', 300)));
        }
        messages.Add(User("Final task: now do X."));

        // Use a context window the prompt actually exceeds, so the rear prune has to drop turns.
        // 20 turns x ~150 tokens + overhead ~= 3500 tokens; a 2000-token window forces a drop.
        const int contextTokens = 2000;

        // Aggressive: keep ~2 recent turns, leave ~40% headroom (target = 1200 tokens).
        var payload = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var before = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.True(before > contextTokens * 0.6, "setup: prompt must exceed the aggressive target (before=" + before + ")");
        var result = HailMaryCompaction.RearContextPrune(payload, contextTokens, HailMaryCompaction.HailMaryAggression.Aggressive, stripImages: false);
        Assert.True(result.MiddleCutDropped > 0, "expected oldest turns dropped, got " + result.MiddleCutDropped);
        // The prune must free space: the result is smaller than the original prompt.
        Assert.True(result.AfterTokens < before, "after " + result.AfterTokens + " should be < before " + before);

        var kept = payload["messages"]!.AsArray();
        // System prompt preserved.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "system");
        // Initial brief (first user task) preserved — the original request.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Original task"));
        // Last real user task preserved.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Final task"));
        // The OLDEST middle turns are the ones dropped: "Turn 0" should be gone, while a
        // recent middle turn (e.g. "Turn 18") should be kept.
        Assert.DoesNotContain(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Turn 0 "));
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Turn 18 "));

        // Both stops drop turns and free space. (The exact count depends on where the prompt
        // crosses the headroom target relative to the keep-turns floor, so we assert the
        // invariant — turns dropped + space freed + essentials preserved — not a fixed count.)
        var payload2 = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var gentle = HailMaryCompaction.RearContextPrune(payload2, contextTokens, HailMaryCompaction.HailMaryAggression.Gentle, stripImages: false);
        Assert.True(gentle.MiddleCutDropped > 0, "Gentle should also drop turns, got " + gentle.MiddleCutDropped);
        Assert.True(gentle.AfterTokens < before, "Gentle should free space: " + gentle.AfterTokens + " < " + before);
    }

    [Fact]
    public void RearContextPrune_is_noop_when_prompt_already_fits_the_headroom_target()
    {
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "sys" },
            User("Hello."),
            Assistant("Hi there."),
        };
        var payload = Payload(messages.ToArray());
        var result = HailMaryCompaction.RearContextPrune(payload, contextTokens: 100000, HailMaryCompaction.HailMaryAggression.Aggressive, stripImages: false);
        Assert.Equal(0, result.MiddleCutDropped);
        Assert.Equal(HailMaryResult.None, result);
    }

    [Fact]
    public void Stage5_middle_cut_is_button_gated_and_tool_pair_safe()
    {
        // A long conversational history. The tool-pair sits in the RECENT floor (just before
        // the last task), so it must survive the middle-cut (tool-pair safety: a kept call's
        // result is never dropped). The oldest turns (after the initial brief) are the ones
        // dropped.
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("Original task: fix the bug."),
        };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(User("Turn " + i + " " + new string('n', 300)));
            messages.Add(Assistant("Reply " + i + " " + new string('n', 300)));
        }
        // A tool-pair in the recent floor, then the final task.
        messages.Add(AssistantWithCall("k1", "run_command", "{\"command\":\"dotnet test\"}"));
        messages.Add(ToolResult("tests passed", "k1"));
        messages.Add(User("Final task: now do X."));

        var payload = Payload(messages.ToArray());
        // Without the button, stage 5 must NOT run.
        var autoResult = HailMaryCompaction.Salvage(payload, contextTokens: 10, allowMiddleCut: false);
        Assert.Equal(0, autoResult.MiddleCutDropped);
        Assert.DoesNotContain("middle-cut", autoResult.DiagnosticsLine);

        // With the button, the middle-cut drops the oldest turns but keeps the system prompt,
        // the initial brief, the last task, and the recent tool-pair.
        var payload2 = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var buttonResult = HailMaryCompaction.Salvage(payload2, contextTokens: 10, allowMiddleCut: true);
        Assert.True(buttonResult.MiddleCutDropped > 0);
        var kept = payload2["messages"]!.AsArray();
        // System prompt survives.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "system");
        // The recent tool result survives (its call is kept — tool-pair safety).
        Assert.Contains(kept, m => m!["role"]!.ToString() == "tool"
            && m!["content"]!.ToString().Contains("tests passed"));
        // The oldest turns are dropped.
        Assert.DoesNotContain(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Turn 0 "));
        Assert.Contains("middle-cut", buttonResult.DiagnosticsLine);
    }

    [Fact]
    public void Diagnostics_line_names_the_token_delta()
    {
        var payload = Payload(
            User("Fix the layout bug in MainWindow."),
            Assistant("I'll look into it."),
            User("Fix the layout bug in MainWindow."),
            Assistant("Done."));
        var result = HailMaryCompaction.Salvage(payload, contextTokens: 10);
        Assert.Contains("hail-mary:", result.DiagnosticsLine);
        Assert.Contains("tokens", result.DiagnosticsLine);
        Assert.True(result.BeforeTokens > result.AfterTokens);
    }
    [Fact]
    public void RearContextPrune_prunes_with_measured_count_where_estimate_would_noop()
    {
        // A payload whose 4-chars/token estimate is small enough to fit the headroom target, but
        // whose measured token count exceeds it. Without the measured count the rear prune is a
        // no-op (before <= target); with it, the prune must drop older turns. This is the Qwen
        // dense-tokenizer case that made the Hail Mary button appear to do nothing.
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "sys" },
            User("Original task: fix the bug."),
        };
        for (var i = 0; i < 12; i++)
        {
            messages.Add(User("Turn " + i + " " + new string('n', 120)));
            messages.Add(Assistant("Reply " + i + " " + new string('n', 120)));
        }

        var contextTokens = 100000;
        var aggressiveHeadroom = (int)(contextTokens * (1.0 - HailMaryCompaction.HeadroomFractionFor(HailMaryCompaction.HailMaryAggression.Aggressive)));

        // Estimate-based (no measured count): the estimate of this payload is far under the
        // headroom target, so the prune is a no-op.
        var byEstimate = HailMaryCompaction.RearContextPrune(
            Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray()),
            contextTokens,
            HailMaryCompaction.HailMaryAggression.Aggressive,
            stripImages: false,
            measuredTokens: null);
        Assert.Equal(0, byEstimate.MiddleCutDropped);

        // Measured-based: a measured count that exceeds the headroom target forces the prune.
        var byMeasured = HailMaryCompaction.RearContextPrune(
            Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray()),
            contextTokens,
            HailMaryCompaction.HailMaryAggression.Aggressive,
            stripImages: false,
            measuredTokens: aggressiveHeadroom + 1000);
        Assert.True(byMeasured.MiddleCutDropped > 0, "rear prune must drop turns when the measured count exceeds the headroom target");
    }

    [Fact]
    public void MiddleCut_drops_oldest_turns_first_not_recent()
    {
        // A long history where the OLDEST turns are large (tool-result-heavy) and the recent
        // turns are small. The fix must drop the oldest large turns first, keeping the recent
        // small turns intact.
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("Original task: fix the bug."),
        };
        // 10 large oldest turns (each ~400 tokens of filler).
        for (var i = 0; i < 10; i++)
        {
            messages.Add(User("Old turn " + i + " " + new string('o', 1600)));
            messages.Add(Assistant("Old reply " + i + " " + new string('r', 1600)));
        }
        // 4 small recent turns.
        for (var i = 0; i < 4; i++)
        {
            messages.Add(User("Recent " + i));
            messages.Add(Assistant("Recent reply " + i));
        }
        messages.Add(User("Final task: now do X."));

        const int contextTokens = 2000;
        var payload = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var before = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.True(before > contextTokens, "setup: prompt must exceed the window (before=" + before + ")");

        var result = HailMaryCompaction.RearContextPrune(payload, contextTokens, HailMaryCompaction.HailMaryAggression.Aggressive, stripImages: false);
        Assert.True(result.MiddleCutDropped > 0, "expected oldest turns dropped, got " + result.MiddleCutDropped);

        var kept = payload["messages"]!.AsArray();
        // The oldest large turns are dropped.
        Assert.DoesNotContain(kept, m => m!["role"]!.ToString() == "user" && m!["content"]!.ToString().Contains("Old turn 0 "));
        Assert.DoesNotContain(kept, m => m!["role"]!.ToString() == "user" && m!["content"]!.ToString().Contains("Old turn 1 "));
        // The recent small turns are kept verbatim.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user" && m!["content"]!.ToString().Contains("Recent 3"));
        Assert.Contains(kept, m => m!["role"]!.ToString() == "assistant" && m!["content"]!.ToString().Contains("Recent reply 3"));
        // Initial brief + last task preserved.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user" && m!["content"]!.ToString().Contains("Original task"));
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user" && m!["content"]!.ToString().Contains("Final task"));
    }

    [Fact]
    public void MiddleCut_preserves_initial_brief_even_when_many_middle_turns_dropped()
    {
        // Even when the prune drops many middle turns, the initial brief (first user task)
        // must survive so the model retains the original intent.
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("Original task: build the feature."),
        };
        for (var i = 0; i < 30; i++)
        {
            messages.Add(User("Turn " + i + " " + new string('n', 400)));
            messages.Add(Assistant("Reply " + i + " " + new string('n', 400)));
        }
        messages.Add(User("Final task: ship it."));

        const int contextTokens = 1500;
        var payload = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var result = HailMaryCompaction.RearContextPrune(payload, contextTokens, HailMaryCompaction.HailMaryAggression.Aggressive, stripImages: false);
        Assert.True(result.MiddleCutDropped > 0, "expected turns dropped, got " + result.MiddleCutDropped);

        var kept = payload["messages"]!.AsArray();
        // The initial brief survives even though many middle turns were dropped.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Original task: build the feature."));
        // The last task survives.
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user"
            && m!["content"]!.ToString().Contains("Final task: ship it."));
    }

    [Fact]
    public void MiddleCut_reports_still_over_when_a_single_turn_alone_exceeds_window()
    {
        // A single huge new turn + enough history that some turns CAN be dropped. Even after
        // dropping all droppable history down to the protected floor, the prompt still exceeds
        // the target because the new turn itself is too big. The result must flag
        // MiddleCutStillOver so the caller can surface an honest "turn too large" message.
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("Original task: small."),
        };
        // 6 history turns (more than minKeepTurns=2, so some can be dropped).
        for (var i = 0; i < 6; i++)
        {
            messages.Add(User("Turn " + i + " " + new string('n', 200)));
            messages.Add(Assistant("Reply " + i + " " + new string('n', 200)));
        }
        // A single huge final turn — bigger than the whole window.
        messages.Add(User("Final task: " + new string('x', 8000)));

        const int contextTokens = 1000;
        var payload = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var result = HailMaryCompaction.RearContextPrune(payload, contextTokens, HailMaryCompaction.HailMaryAggression.Aggressive, stripImages: false);

        // Some history turns were dropped (there were 6, more than the keep floor of 2).
        Assert.True(result.MiddleCutDropped > 0, "expected some history turns dropped, got " + result.MiddleCutDropped);
        // The huge final turn alone exceeds the window, so even after trimming history the
        // prompt is still over. The flag must be set.
        Assert.True(result.MiddleCutStillOver,
            "expected MiddleCutStillOver when the final turn alone exceeds the window (after=" + result.AfterTokens + ", target=" + result.TargetTokens + ")");
        // The last task (the huge turn) is preserved — it's the protected floor.
        var kept = payload["messages"]!.AsArray();
        Assert.Contains(kept, m => m!["role"]!.ToString() == "user" && m!["content"]!.ToString().StartsWith("Final task:"));
    }

    [Fact]
    public void MiddleCut_tool_pair_safety_across_drop_boundary()
    {
        // A tool-pair straddling the drop boundary must stay intact: if the assistant call is
        // kept (in the protected recent floor), its tool result must NOT be dropped (orphaned).
        var messages = new List<JsonObject>
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
            User("Original task: run tests."),
        };
        // 6 large oldest turns to force a drop.
        for (var i = 0; i < 6; i++)
        {
            messages.Add(User("Old " + i + " " + new string('o', 800)));
            messages.Add(Assistant("Old reply " + i + " " + new string('r', 800)));
        }
        // A tool-pair in the recent floor: assistant call + tool result, then the final task.
        messages.Add(AssistantWithCall("k1", "run_command", "{\"command\":\"dotnet test\"}"));
        messages.Add(ToolResult("tests passed", "k1"));
        messages.Add(User("Final task: now do X."));

        const int contextTokens = 2000;
        var payload = Payload(messages.Select(m => (JsonObject)m.DeepClone()).ToArray());
        var before = LocalRequestOverlayRouting.EstimatePromptTokens(payload);
        Assert.True(before > contextTokens, "setup: prompt must exceed the window (before=" + before + ")");

        var result = HailMaryCompaction.RearContextPrune(payload, contextTokens, HailMaryCompaction.HailMaryAggression.Aggressive, stripImages: false);
        Assert.True(result.MiddleCutDropped > 0, "expected oldest turns dropped, got " + result.MiddleCutDropped);

        var kept = payload["messages"]!.AsArray();
        // The tool result in the recent floor must be kept (its call is kept).
        Assert.Contains(kept, m => m!["role"]!.ToString() == "tool" && m!["content"]!.ToString().Contains("tests passed"));
        // Its matching assistant call must also be kept (no orphaned call or result).
        Assert.Contains(kept, m => m!["role"]!.ToString() == "assistant" && m!["tool_calls"] is JsonArray tc && tc.Count > 0);
    }
}

