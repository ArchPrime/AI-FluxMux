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
        Assert.Equal(0, payload["chat_template_kwargs"]!["thinking_budget"]!.GetValue<int>());
        Assert.Equal(0, payload["thinking_budget"]!.GetValue<int>());
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
        Assert.Equal("medium", payload["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("medium", payload["reasoning_effort"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Low", "low")]
    [InlineData("Medium", "medium")]
    [InlineData("XHigh", "xhigh")]
    public void Slot_levels_enable_thinking_and_set_effort(string level, string effort)
    {
        var payload = new JsonObject();

        Assert.False(LocalReasoningPayloadPolicy.ApplyToPayload(payload, level));

        Assert.True(payload["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(effort, payload["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(effort, payload["reasoning_effort"]!.GetValue<string>());
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
        Assert.False(payload.ContainsKey("thinking_budget"));
    }

    [Fact]
    public void ApplyThinkingBudget_caps_low_and_leaves_xhigh_uncapped()
    {
        var low = new JsonObject { ["max_tokens"] = 16384 };
        LocalReasoningPayloadPolicy.ApplyToPayload(low, "Low");
        LocalReasoningPayloadPolicy.ApplyThinkingBudget(low, "Low");
        Assert.Equal(2048, low["thinking_budget"]!.GetValue<int>());
        Assert.Equal(2048, low["chat_template_kwargs"]!["thinking_budget"]!.GetValue<int>());

        var xhigh = new JsonObject { ["max_tokens"] = 32768 };
        LocalReasoningPayloadPolicy.ApplyToPayload(xhigh, "XHigh");
        LocalReasoningPayloadPolicy.ApplyThinkingBudget(xhigh, "XHigh");
        Assert.False(xhigh.ContainsKey("thinking_budget"));
        Assert.False(xhigh["chat_template_kwargs"]!.AsObject().ContainsKey("thinking_budget"));
    }
}
