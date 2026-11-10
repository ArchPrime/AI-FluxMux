using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ForwardPruneTests
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
    public void PruneTurn_is_a_noop_when_the_prompt_already_fits()
    {
        var payload = Payload(User("hello"), Assistant("hi there"));
        var result = ForwardPrune.PruneTurn(payload, contextTokens: 100000);
        Assert.Same(ForwardPruneResult.None, result);
        Assert.Equal(2, payload["messages"]!.AsArray().Count);
    }

    [Fact]
    public void PruneTurn_runs_on_a_fresh_turn_not_gated_behind_a_threshold()
    {
        // A short chat (well under any compaction threshold) with a duplicate message. Forward
        // pruning must still dedupe it — it is a per-turn throttle, not a threshold-gated compact.
        var payload = Payload(
            User("Fix the layout bug in MainWindow."),
            Assistant("I'll look into it."),
            User("Fix the layout bug in MainWindow."),
            Assistant("Done."));
        var result = ForwardPrune.PruneTurn(payload, contextTokens: 10, measuredTokens: 1000);
        Assert.True(result.Pruned);
        var messages = payload["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Contains("dedupe", result.DiagnosticsLine);
    }

    [Fact]
    public void PruneTurn_uses_the_measured_count_when_provided()
    {
        // A payload whose 4-chars/token estimate is small enough to fit the window (so the
        // estimate-based short-circuit would make it a no-op), but whose measured token count is
        // large. With only the estimate the prune must be a no-op; with the measured count it
        // must run. This isolates the measured-count path (the Qwen dense-tokenizer case).
        var payload = Payload(
            User("Fix the layout bug in MainWindow."),
            Assistant("I'll look into it."),
            User("Fix the layout bug in MainWindow."),
            Assistant("Done."));
        // Estimate of this payload is well under 100000, so the estimate-based call is a no-op.
        var byEstimate = ForwardPrune.PruneTurn(
            Payload(payload["messages"]!.AsArray().Select(m => (JsonObject)m.DeepClone()).ToArray()),
            contextTokens: 100000);
        Assert.False(byEstimate.Pruned);

        // Same content, but a measured count that exceeds the window forces the prune to run.
        var byMeasured = ForwardPrune.PruneTurn(
            Payload(payload["messages"]!.AsArray().Select(m => (JsonObject)m.DeepClone()).ToArray()),
            contextTokens: 100000,
            measuredTokens: 100001);
        Assert.True(byMeasured.Pruned);
        Assert.Contains("dedupe", byMeasured.DiagnosticsLine);
    }

    [Fact]
    public void PruneTurn_collapses_repeated_tool_loops()
    {
        // The same shell command issued three times: first kept, repeats stubbed.
        var payload = Payload(
            User("run the build"),
            AssistantWithCall("c1", "execute_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("build ok 1", "c1"),
            AssistantWithCall("c2", "execute_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("build ok 2", "c2"),
            AssistantWithCall("c3", "execute_command", "{\"command\":\"dotnet build\"}"),
            ToolResult("build ok 3", "c3"),
            Assistant("done"));
        var result = ForwardPrune.PruneTurn(payload, contextTokens: 10, measuredTokens: 100000);
        Assert.True(result.Pruned);
        Assert.Contains("loops", result.DiagnosticsLine);
        var stubbed = payload["messages"]!.AsArray().Count(m =>
            m["content"]?.ToString().Contains("already ran earlier", StringComparison.Ordinal) == true);
        Assert.True(stubbed >= 1, "expected at least one stubbed tool result");
    }

    [Fact]
    public void PruneTurn_truncates_oversized_older_tool_results()
    {
        // 10 oversized older tool results + a recent one. The older ones get truncated to the cap.
        var messages = new List<JsonObject> { User("read the logs") };
        for (var i = 0; i < 10; i++)
        {
            messages.Add(AssistantWithCall("c" + i, "read_file", "{\"path\":\"/tmp/log" + i + "\"}"));
            messages.Add(ToolResult("line".PadRight(6000, '.'), "c" + i));
        }

        messages.Add(Assistant("done"));
        var payload = Payload(messages.ToArray());
        var result = ForwardPrune.PruneTurn(payload, contextTokens: 10, measuredTokens: 100000);
        Assert.True(result.Pruned);
        Assert.Contains("results", result.DiagnosticsLine);
        var anyTruncated = payload["messages"]!.AsArray().Any(m =>
            m["content"] is not null
            && m["content"]!.ToString().Contains("chars elided", StringComparison.Ordinal));
        Assert.True(anyTruncated, "expected at least one truncated tool result");
    }

    [Fact]
    public void PruneTurn_does_not_strip_images()
    {
        // Forward pruning is conservative: it never strips embedded images (that stays in Hail Mary).
        var imageContent = new JsonArray
        {
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } }
        };
        var withImage = new JsonObject { ["role"] = "user", ["content"] = imageContent };
        var payload = Payload(
            withImage,
            User("Fix the layout bug in MainWindow."),
            Assistant("I'll look into it."),
            User("Fix the layout bug in MainWindow."),
            Assistant("Done."));
        ForwardPrune.PruneTurn(payload, contextTokens: 10, measuredTokens: 100000);
        var first = payload["messages"]!.AsArray()[0] as JsonObject;
        Assert.NotNull(first);
        var stillHasImage = (first!["content"] as JsonArray)?.Any(p => p is JsonObject o && o["type"]?.ToString() == "image_url") == true;
        Assert.True(stillHasImage, "forward pruning must not strip images");
        // The dedupe still happened (the duplicate user text is gone).
        Assert.Equal(4, payload["messages"]!.AsArray().Count);
    }

    // ── Step 4: lossless paging via ContextSwapStore ────────────────────────────

    [Fact]
    public void TruncateOversizedToolResults_with_swap_store_spills_body_and_leaves_pointer()
    {
        // Build 9 tool results so the first one is "older" (beyond KeepRecentResults=8) and gets truncated.
        string bigBody = new string('X', 20000); // ~5000 tokens, exceeds the 1000-token cap
        var payload = Payload(
            User("Run the test suite."),
            AssistantWithCall("call_1", "run_tests", "{}"),
            ToolResult(bigBody, "call_1"),
            AssistantWithCall("call_2", "read_file", "{}"),
            ToolResult("small result 2", "call_2"),
            AssistantWithCall("call_3", "read_file", "{}"),
            ToolResult("small result 3", "call_3"),
            AssistantWithCall("call_4", "read_file", "{}"),
            ToolResult("small result 4", "call_4"),
            AssistantWithCall("call_5", "read_file", "{}"),
            ToolResult("small result 5", "call_5"),
            AssistantWithCall("call_6", "read_file", "{}"),
            ToolResult("small result 6", "call_6"),
            AssistantWithCall("call_7", "read_file", "{}"),
            ToolResult("small result 7", "call_7"),
            AssistantWithCall("call_8", "read_file", "{}"),
            ToolResult("small result 8", "call_8"),
            AssistantWithCall("call_9", "read_file", "{}"),
            ToolResult("small result 9", "call_9"),
            User("Fix the layout bug in MainWindow."));

        // Use a real swap store backed by a temp file.
        var tempDir = Directory.CreateTempSubdirectory("fluxmux-swap-test");
        try
        {
            var store = new ContextSwapStore(
                new FileSystemContextIo(),
                Path.Combine(tempDir.FullName, "context_swap.json"));

            ForwardPrune.PruneTurn(payload, contextTokens: 10, measuredTokens: 100000, swapStore: store);

            // The first tool result (the oversized one) should now contain a swap_id pointer.
            var toolMsgs = payload["messages"]!.AsArray().Where(m => m is JsonObject o && o["role"]?.ToString() == "tool").ToList();
            Assert.True(toolMsgs.Count >= 9, $"expected at least 9 tool results, got {toolMsgs.Count}");
            var firstTool = toolMsgs[0] as JsonObject;
            string content = firstTool!["content"]?.ToString() ?? string.Empty;
            Assert.Contains("swap_id", content, StringComparison.Ordinal);
            Assert.DoesNotContain(bigBody, content, StringComparison.Ordinal);

            // Extract the swap_id from the pointer and verify the full body is on disk.
            var match = System.Text.RegularExpressions.Regex.Match(content, @"swap_id=([a-z0-9_]+)");
            Assert.True(match.Success, $"expected a swap_id in the pointer, got: {content}");
            string swapId = match.Groups[1].Value;
            var page = store.ReadPage(swapId, 0, 100_000);
            Assert.Equal(bigBody, page.Text);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TruncateOversizedToolResults_without_swap_store_uses_lossy_stub()
    {
        // Build 9 tool results so the first one is "older" (beyond KeepRecentResults=8) and gets truncated.
        string bigBody = new string('Y', 20000); // ~5000 tokens, exceeds the 1000-token cap
        var payload = Payload(
            User("Run the test suite."),
            AssistantWithCall("call_1", "run_tests", "{}"),
            ToolResult(bigBody, "call_1"),
            AssistantWithCall("call_2", "read_file", "{}"),
            ToolResult("small result 2", "call_2"),
            AssistantWithCall("call_3", "read_file", "{}"),
            ToolResult("small result 3", "call_3"),
            AssistantWithCall("call_4", "read_file", "{}"),
            ToolResult("small result 4", "call_4"),
            AssistantWithCall("call_5", "read_file", "{}"),
            ToolResult("small result 5", "call_5"),
            AssistantWithCall("call_6", "read_file", "{}"),
            ToolResult("small result 6", "call_6"),
            AssistantWithCall("call_7", "read_file", "{}"),
            ToolResult("small result 7", "call_7"),
            AssistantWithCall("call_8", "read_file", "{}"),
            ToolResult("small result 8", "call_8"),
            AssistantWithCall("call_9", "read_file", "{}"),
            ToolResult("small result 9", "call_9"),
            User("Fix the layout bug in MainWindow."));

        // No swap store: the lossy head+tail stub is used.
        ForwardPrune.PruneTurn(payload, contextTokens: 10, measuredTokens: 100000, swapStore: null);

        var toolMsgs = payload["messages"]!.AsArray().Where(m => m is JsonObject o && o["role"]?.ToString() == "tool").ToList();
        Assert.True(toolMsgs.Count >= 9, $"expected at least 9 tool results, got {toolMsgs.Count}");
        var firstTool = toolMsgs[0] as JsonObject;
        string content = firstTool!["content"]?.ToString() ?? string.Empty;
        // The lossy stub contains the head and tail of the original body, but not the full body.
        Assert.DoesNotContain(bigBody, content, StringComparison.Ordinal);
        // The stub should be much shorter than the original.
        Assert.True(content.Length < bigBody.Length / 2, $"expected stub to be shorter, got {content.Length} chars");
    }
}

