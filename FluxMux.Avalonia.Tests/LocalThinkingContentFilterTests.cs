using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalThinkingContentFilterTests
{
    [Fact]
    public void StripThinkingFromText_removes_common_thinking_blocks()
    {
        var text = "Hello " + "\x3cthink\x3e" + "secret chain" + "\x3c/think\x3e" + " world tail";
        var stripped = LocalThinkingContentFilter.StripThinkingFromText(text);
        Assert.Equal("Hello  world tail", stripped);
    }

    [Fact]
    public void StripEmptyThinkingFromText_removes_empty_redacted_blocks()
    {
        var text = "<think> </think>\nHi!";
        var stripped = LocalThinkingContentFilter.StripEmptyThinkingFromText(text);
        Assert.Equal("Hi!", stripped);
    }

    [Fact]
    public void StripEmptyThinkingFromText_keeps_nonempty_think_blocks()
    {
        var thinkBlock = "\x3cthink\x3e" + "plan" + "\x3c/think\x3e";
        var text = "Hi " + thinkBlock + " there";
        var stripped = LocalThinkingContentFilter.StripEmptyThinkingFromText(text);
        Assert.Equal(text, stripped);
    }

    [Fact]
    public void StripThinkingFromCompletion_removes_reasoning_content_field()
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
                        ["content"] = "visible",
                        ["reasoning_content"] = "hidden"
                    }
                }
            }
        };

        Assert.True(LocalThinkingContentFilter.StripThinkingFromCompletion(root));
        var message = root["choices"]![0]!["message"]!.AsObject();
        Assert.False(message.ContainsKey("reasoning_content"));
        Assert.Equal("visible", message["content"]!.GetValue<string>());
    }

    [Fact]
    public void StripThinkingFromMessages_strips_assistant_history()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "old " + "\x3cthink\x3e" + "plan" + "\x3c/think\x3e" + " answer"
            }
        };

        var (stripped, changed) = LocalThinkingContentFilter.StripThinkingFromMessages(messages);
        Assert.True(changed);
        Assert.Equal("old  answer", stripped[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void StripThinkingFromMessages_leaves_the_original_history_unchanged_for_endpoint_passthrough()
    {
        var thinkBlock = "\x3cthink\x3e" + "plan" + "\x3c/think\x3e";
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "old " + thinkBlock + " answer"
            }
        };

        var (stripped, changed) = LocalThinkingContentFilter.StripThinkingFromMessages(messages);

        Assert.True(changed);
        Assert.Contains(thinkBlock, messages[1]!["content"]!.GetValue<string>());
        Assert.DoesNotContain(thinkBlock, stripped[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Reasoning_off_strips_forwarded_history_without_touching_a_separate_completion_payload()
    {
        var thinkBlock = "\x3cthink\x3e" + "plan" + "\x3c/think\x3e";
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "old " + thinkBlock + " answer"
            }
        };
        var completion = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "visible",
                        ["reasoning_content"] = "hidden"
                    }
                }
            }
        };

        var (stripped, changed) = LocalThinkingContentFilter.StripThinkingFromMessages(messages);

        Assert.True(changed);
        Assert.DoesNotContain(thinkBlock, stripped[1]!["content"]!.GetValue<string>());
        Assert.True(completion["choices"]![0]!["message"]!.AsObject().ContainsKey("reasoning_content"));
    }
}
