using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class GeminiThoughtSignaturePassthroughTests
{
    [Fact]
    public void Detects_cline_tool_call_history()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "call_1",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = "default_api:read_files",
                                ["arguments"] = "{}"
                            }
                        }
                    }
                }
            }
        };

        Assert.True(GeminiThoughtSignaturePassthrough.HasToolContinuation(payload));
    }

    [Fact]
    public void Fills_missing_thought_signatures_for_gemini()
    {
        var call = new JsonObject
        {
            ["id"] = "call_1",
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "default_api:read_files",
                ["arguments"] = "{}"
            }
        };
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray { call }
                }
            }
        };

        Assert.Equal(1, GeminiThoughtSignaturePassthrough.EnsureSkipSignaturesForMissingThoughts(payload));
        Assert.Equal(
            GeminiThoughtSignaturePassthrough.SkipValidator,
            call["extra_content"]?["google"]?["thought_signature"]?.ToString());
    }
}
