using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class GenerationSpeedTrackerTests
{
    [Fact]
    public void Disabled_tracker_does_not_record()
    {
        var tracker = new GenerationSpeedTracker();
        tracker.Begin("local");
        tracker.ProcessSseLine("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}");
        tracker.Complete();

        Assert.Null(tracker.ReadSnapshot());
    }

    [Fact]
    public void Enabled_tracker_uses_llama_timings_when_present()
    {
        var tracker = new GenerationSpeedTracker();
        tracker.SetEnabled(true);
        tracker.Begin("local");
        tracker.ProcessSseLine("data: {\"timings\":{\"predicted_n\":12,\"predicted_per_second\":48.5}}");
        tracker.Complete();

        var snapshot = tracker.ReadSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(48.5, snapshot!.TokensPerSecond, 1);
        Assert.Equal(12, snapshot.CompletionTokens);
        Assert.Contains("local", snapshot.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enabled_tracker_estimates_from_delta_content()
    {
        var tracker = new GenerationSpeedTracker();
        tracker.SetEnabled(true);
        tracker.Begin("cloud");
        tracker.ProcessSseLine("data: {\"choices\":[{\"delta\":{\"content\":\"abcd\"}}]}");
        tracker.ProcessSseLine("data: {\"usage\":{\"completion_tokens\":10}}");
        tracker.Complete();

        var snapshot = tracker.ReadSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot!.CompletionTokens);
        Assert.Contains("cloud", snapshot.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateTokenCount_uses_character_heuristic()
    {
        Assert.Equal(0, GenerationSpeedTracker.EstimateTokenCount(string.Empty));
        Assert.Equal(1, GenerationSpeedTracker.EstimateTokenCount("hi"));
        Assert.Equal(3, GenerationSpeedTracker.EstimateTokenCount("hello world"));
    }

    [Fact]
    public void CountDelta_tokens_includes_tool_call_arguments()
    {
        var root = JsonNode.Parse("""
            {"choices":[{"delta":{"tool_calls":[{"function":{"name":"read","arguments":"{}"}}]}}]}
            """) as JsonObject;
        Assert.NotNull(root);
        Assert.True(GenerationSpeedTracker.CountDeltaTokens(root) > 0);
    }

    [Fact]
    public void CountDelta_tokens_includes_reasoning_content()
    {
        var root = JsonNode.Parse("""{"choices":[{"delta":{"reasoning_content":"think"}}]}""") as JsonObject;
        Assert.NotNull(root);
        Assert.Equal(2, GenerationSpeedTracker.CountDeltaTokens(root));
    }
}
