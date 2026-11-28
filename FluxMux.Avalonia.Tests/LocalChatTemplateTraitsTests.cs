using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The loaded model's thinking markers and effort levels, from llama-server's /props (28 Nov). The
/// template lines are Inkling 2 Mini's; its reply reached Cline as
/// "&lt;|content_thinking|&gt;…&lt;|end_message|&gt;391".
/// </summary>
public sealed class LocalChatTemplateTraitsTests
{
    private const string InklingTemplate =
        "{%- macro emit_thinking_effort() -%}{%- set eff = reasoning_effort if reasoning_effort is defined else 0.9 -%}"
        + "{{- \"<|message_system|><|content_text|>Thinking effort level: \" -}}{%- endmacro -%}\n"
        + "{%- if message.role == \"assistant\" and message.reasoning_content is defined and message.reasoning_content -%}\n"
        + "    {{- rtok ~ \"<|content_thinking|>\" ~ message.reasoning_content ~ \"<|end_message|>\" -}}\n"
        + "{%- endif -%}\n"
        + "{{- rtok ~ \"<|content_text|>\" ~ message.content ~ \"<|end_message|>\" -}}";

    private static JsonObject InklingProps() => new()
    {
        ["chat_template"] = InklingTemplate,
        ["chat_template_caps"] = new JsonObject
        {
            ["supports_reasoning_effort"] = true,
            ["reasoning_effort_levels"] = new JsonArray("none", "minimal", "low", "medium", "high", "max"),
        },
    };

    private static LocalChatTemplateTraits Inkling() => LocalChatTemplateTraits.FromProps(InklingProps());

    [Fact]
    public void Inkling_markers_and_Off_effort_come_from_its_template()
    {
        var traits = Inkling();
        Assert.Equal("<|content_thinking|>", traits.ThinkOpen);
        Assert.Equal("<|end_message|>", traits.ThinkClose);
        Assert.True(traits.TranslatesThinking);
        Assert.False(traits.ReadsEnableThinking);
        Assert.Equal("none", traits.OffEffort);
        Assert.Contains("<|content_text|>", traits.ControlTokens);
    }

    [Fact]
    public void A_think_tag_template_is_left_alone()
    {
        var traits = LocalChatTemplateTraits.FromProps(new JsonObject
        {
            ["chat_template"] = "{%- if enable_thinking -%}{{ '<think>\\n' + reasoning_content.strip('\\n') + '\\n</think>' }}{%- endif -%}",
        });
        Assert.Equal("<think>", traits.ThinkOpen);
        Assert.False(traits.TranslatesThinking);
        Assert.Null(traits.OffEffort);
    }

    [Fact]
    public void The_live_reply_becomes_think_tags_then_the_answer()
    {
        var reply = "<|content_thinking|>The user wants just the number. 17*23 = 391.<|end_message|>391";
        Assert.Equal("<think>The user wants just the number. 17*23 = 391.</think>391", new LocalChatTemplateTraits.Translator(Inkling()).Translate(reply));
    }

    [Fact]
    public void Streamed_deltas_are_translated_and_Off_hides_the_thinking()
    {
        var session = new LocalThinkingStreamSession("Off", 0, Inkling());
        var shown = string.Empty;
        foreach (var delta in new[] { "<|content_thinking|>", "The user", " wants 391.", "<|end_message|>", "391" })
        {
            var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = delta } }) };
            session.FilterCompletionJson(chunk);
            shown += chunk["choices"]![0]!["delta"]!["content"]?.ToString();
        }

        Assert.Equal("391", shown.Trim());
    }

    [Fact]
    public void A_whole_reply_with_reasoning_on_keeps_the_thinking_as_think_tags()
    {
        var root = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = "<|content_thinking|>hm<|end_message|>391" } }) };
        Assert.True(LocalEndpointResponseSanitizer.SanitizeCompletionJson(root, "On", Inkling()));
        Assert.Equal("<think>hm</think>391", root["choices"]![0]!["message"]!["content"]!.ToString());
    }

    [Fact]
    public void Reasoning_Off_sends_the_lowest_effort_when_the_template_ignores_enable_thinking()
    {
        var payload = new JsonObject { ["messages"] = new JsonArray() };
        LocalReasoningPayloadPolicy.ApplyToPayload(payload, "Off", null, Inkling());
        Assert.Equal("none", payload["chat_template_kwargs"]!["reasoning_effort"]!.ToString());

        var qwen = new JsonObject { ["messages"] = new JsonArray() };
        LocalReasoningPayloadPolicy.ApplyToPayload(qwen, "Off", null, LocalChatTemplateTraits.None);
        Assert.Null(qwen["chat_template_kwargs"]!["reasoning_effort"]);
    }

    [Fact]
    public void Saved_replies_in_history_are_translated()
    {
        var messages = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "17*23?" },
            new JsonObject { ["role"] = "assistant", ["content"] = "<|content_thinking|>x<|end_message|>391" });
        Assert.True(Inkling().TranslateHistory(messages));
        Assert.Equal("<think>x</think>391", messages[1]!["content"]!.ToString());
        Assert.Equal("17*23?", messages[0]!["content"]!.ToString());
    }
}
