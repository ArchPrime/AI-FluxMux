using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalThinkOnlyReplyTests
{
    [Fact]
    public void Completion_that_is_only_stripped_thinking_is_think_only()
    {
        var root = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "   "
                    }
                }
            }
        };

        Assert.True(LocalThinkOnlyReply.CompletionIsThinkOnly(root, "Off", strippedThinking: true));
        Assert.False(LocalThinkOnlyReply.CompletionIsThinkOnly(root, "On", strippedThinking: true));
        Assert.False(LocalThinkOnlyReply.CompletionIsThinkOnly(root, "Off", strippedThinking: false));
    }

    [Fact]
    public void Completion_with_a_tool_call_is_not_think_only()
    {
        var root = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["content"] = "",
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject { ["id"] = "call_1" }
                        }
                    }
                }
            }
        };

        Assert.False(LocalThinkOnlyReply.CompletionIsThinkOnly(root, "Off", strippedThinking: true));
    }

    [Fact]
    public void Client_message_names_llama_server_and_does_not_say_the_runner()
    {
        var text = LocalThinkOnlyReply.FormatClientMessage();
        Assert.Contains("llama-server", text, System.StringComparison.Ordinal);
        Assert.Contains("thought", text, System.StringComparison.Ordinal);
        Assert.Contains("This Client-app turn is over", text, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("the runner", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text, FluxMuxGatewayRouting.FormatThinkOnlyMessage(null));
    }
}
