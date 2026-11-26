using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The live Harness rescue: the refusal speaks the standard overflow wording so Harness starts
/// its own compaction, Harness's summary request is made to fit (or answered from the session
/// note), and Hail Mary asks Harness to retry the failed turn in the same chat.
/// </summary>
public sealed class ClientCompactionRescueTests
{
    private static JsonObject StateFor(string endpointApp) => new()
    {
        ["routing_enabled"] = true,
        ["endpoint_app"] = endpointApp,
        ["mode"] = "route",
        ["local_phase"] = "ready",
        ["local_preferred_model"] = "local.gguf",
    };

    [Theory]
    [InlineData("DeepSeek Harness", true)]
    [InlineData("Other", true)]
    [InlineData("Cline", false)]
    public void Too_large_refusal_carries_the_standard_overflow_wording_except_for_Cline(string app, bool expected)
    {
        var state = StateFor(app);
        var payload = new JsonObject { ["messages"] = new JsonArray() };
        var filling = FluxMuxGatewayRouting.FormatFillingBlockedMessage(state, payload);
        var overflow = FluxMuxGatewayRouting.FormatLocalContextOverflowMessage(state);
        Assert.Equal(expected, filling.Contains(FluxMuxGatewayRouting.StandardOverflowPhrase, StringComparison.Ordinal));
        Assert.Equal(expected, overflow.Contains(FluxMuxGatewayRouting.StandardOverflowPhrase, StringComparison.Ordinal));
        Assert.Contains("Chat handover brief", filling, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_wording_survives_the_client_error_body_and_is_added_once()
    {
        var state = StateFor(FluxMuxGatewayRouting.HarnessEndpointApp);
        var once = FluxMuxGatewayRouting.WithStandardOverflowPhrase(state, "Too big.");
        Assert.Equal(once, FluxMuxGatewayRouting.WithStandardOverflowPhrase(state, once));
        var body = FluxMuxGatewayRouting.EndpointErrorBody(once);
        // Harness's detector (dsh-llm isContextWindowExceededError) matches "context length exceeded".
        Assert.Matches("(?i)(?:^|[^a-z0-9])context[\\s_-](?:length|window)[\\s_-]exceed(?:ed|s)?(?:$|[^a-z0-9])", body["message"]!.ToString());
    }

    private static JsonObject HarnessSummaryRequest(int middleMessages, int charsEach, bool arrayContent = false)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding agent." },
            new JsonObject { ["role"] = "user", ["content"] = "Please build the feature." },
        };
        for (var i = 0; i < middleMessages; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = i % 2 == 0 ? "assistant" : "user",
                ["content"] = "turn " + i + " " + new string('x', charsEach)
            });
        }

        var instruction = ClientCompactionRequest.HarnessInstructionStart + " Condense the conversation ABOVE.";
        messages.Add(arrayContent
            ? new JsonObject { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = instruction } } }
            : new JsonObject { ["role"] = "user", ["content"] = instruction });
        return new JsonObject { ["messages"] = messages, ["max_tokens"] = 4096 };
    }

    [Fact]
    public void Harness_summary_request_is_recognised_in_either_content_shape()
    {
        Assert.True(ClientCompactionRequest.IsSummaryRequest(HarnessSummaryRequest(4, 10)));
        Assert.True(ClientCompactionRequest.IsSummaryRequest(HarnessSummaryRequest(4, 10, arrayContent: true)));
        var ordinary = HarnessSummaryRequest(4, 10);
        ((JsonArray)ordinary["messages"]!).Add(new JsonObject { ["role"] = "user", ["content"] = "Now fix the bug." });
        Assert.False(ClientCompactionRequest.IsSummaryRequest(ordinary));
        Assert.False(ClientCompactionRequest.IsSummaryRequest(new JsonObject()));
    }

    [Fact]
    public void Reply_room_uses_max_tokens_within_half_the_context()
    {
        Assert.Equal(4096, ClientCompactionRequest.ReplyTokens(new JsonObject { ["max_tokens"] = 4096 }, 32768));
        Assert.Equal(16384, ClientCompactionRequest.ReplyTokens(new JsonObject { ["max_tokens"] = 100000 }, 32768));
        Assert.Equal(4096, ClientCompactionRequest.ReplyTokens(new JsonObject(), 32768));
    }

    [Fact]
    public void Oversized_summary_request_is_trimmed_to_fit_keeping_system_brief_and_instruction()
    {
        const int context = 8192;
        var payload = HarnessSummaryRequest(40, 2000);
        var reply = ClientCompactionRequest.ReplyTokens(payload, context);
        Assert.True(LocalRequestOverlayRouting.PromptExceedsContext(payload, context, reply));

        var result = HailMaryCompaction.FitClientSummaryRequest(payload, context, reply);

        Assert.True(result.MiddleCutDropped > 0);
        Assert.False(LocalRequestOverlayRouting.PromptExceedsContext(payload, context, reply));
        var messages = (JsonArray)payload["messages"]!;
        Assert.Equal("system", messages[0]!["role"]!.ToString());
        Assert.Contains("Please build the feature.", messages.Select(m => m!["content"]?.ToString()));
        Assert.True(ClientCompactionRequest.IsSummaryRequest(payload));
    }

    [Fact]
    public void Summary_request_that_fits_is_left_alone()
    {
        var payload = HarnessSummaryRequest(4, 100);
        var before = payload.ToJsonString();
        var result = HailMaryCompaction.FitClientSummaryRequest(payload, 32768, 4096);
        Assert.Equal(0, result.MiddleCutDropped);
        Assert.Equal(before, payload.ToJsonString());
    }

    [Fact]
    public void Session_note_is_added_to_the_instruction_in_either_content_shape()
    {
        var plain = HarnessSummaryRequest(1, 10);
        ClientCompactionRequest.AppendToInstruction(plain, "\nNOTE");
        Assert.EndsWith("\nNOTE", ((JsonArray)plain["messages"]!).Last()!["content"]!.ToString());

        var parts = HarnessSummaryRequest(1, 10, arrayContent: true);
        ClientCompactionRequest.AppendToInstruction(parts, "\nNOTE");
        var content = (JsonArray)((JsonArray)parts["messages"]!).Last()!["content"]!;
        Assert.Equal("\nNOTE", content.Last()!["text"]!.ToString());
        Assert.True(ClientCompactionRequest.IsSummaryRequest(parts));
    }

    [Fact]
    public void Fallback_checkpoint_keeps_Harness_sections_and_the_session_note()
    {
        var text = ClientCompactionRequest.BuildFallbackCheckpoint("Goal: ship the rescue.");
        foreach (var heading in new[]
                 {
                     "## Primary Request and Intent", "## Key Technical Concepts", "## Files and Code",
                     "## Errors and Fixes", "## Pending Jobs", "## Current Work", "## Next Step", "## Critical Context"
                 })
        {
            Assert.Contains(heading, text, StringComparison.Ordinal);
        }

        Assert.EndsWith("Goal: ship the rescue.", text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ClientCompactionRequest.BuildFallbackCheckpoint(null)));
    }

}
