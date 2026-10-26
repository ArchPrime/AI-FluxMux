using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
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
}

