using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalReasoningPayloadPolicyTests
{
    [Fact]
    public void Off_strips_forwarded_history_and_disables_thinking_kwargs()
    {
        var thinkBlock = "\x3cthink\x3e" + "plan" + "\x3c/think\x3e";
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "hi" },
                new JsonObject { ["role"] = "assistant", ["content"] = "old " + thinkBlock + " answer" }
            },
            ["enable_thinking"] = true,
            ["reasoning"] = "high"
        };

        Assert.True(LocalReasoningPayloadPolicy.ApplyToPayload(payload, "Off"));

        var forwarded = payload["messages"]![1]!["content"]!.GetValue<string>();
        Assert.DoesNotContain(thinkBlock, forwarded);
        Assert.False(payload["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.False(payload.ContainsKey("enable_thinking"));
        Assert.False(payload.ContainsKey("reasoning"));
    }

    [Fact]
    public void On_enables_thinking_kwargs_without_stripping_history()
    {
        var thinkBlock = "\x3cthink\x3e" + "plan" + "\x3c/think\x3e";
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "assistant", ["content"] = thinkBlock + " answer" }
            }
        };

        Assert.False(LocalReasoningPayloadPolicy.ApplyToPayload(payload, "On"));

        Assert.Contains(thinkBlock, payload["messages"]![0]!["content"]!.GetValue<string>());
        Assert.True(payload["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void Auto_removes_top_level_enable_thinking_without_forcing_kwargs()
    {
        var payload = new JsonObject
        {
            ["enable_thinking"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "hi" }
            }
        };

        Assert.False(LocalReasoningPayloadPolicy.ApplyToPayload(payload, "Auto"));

        Assert.False(payload.ContainsKey("enable_thinking"));
        Assert.False(payload.ContainsKey("chat_template_kwargs"));
    }
}
