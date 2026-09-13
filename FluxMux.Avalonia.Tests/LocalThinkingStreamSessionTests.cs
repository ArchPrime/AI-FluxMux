using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalThinkingStreamSessionTests
{
    [Fact]
    public void Off_drops_think_body_across_sse_chunks_including_space_deltas()
    {
        var session = new LocalThinkingStreamSession("Off");
        Assert.Equal(string.Empty, session.FilterText("<think>"));
        Assert.Equal(string.Empty, session.FilterText("The"));
        Assert.Equal(string.Empty, session.FilterText(" "));
        Assert.Equal(string.Empty, session.FilterText("quick"));
        Assert.Equal(string.Empty, session.FilterText("</think>"));
        Assert.Equal("Final answer", session.FilterText("Final answer"));
    }

    [Fact]
    public void Off_keeps_spaces_in_visible_answer_after_think_span()
    {
        var session = new LocalThinkingStreamSession("Off");
        Assert.Equal("Hello ", session.FilterText("Hello <think>secret"));
        Assert.Equal(string.Empty, session.FilterText(" plan "));
        Assert.Equal(" world", session.FilterText("</think> world"));
    }

    [Fact]
    public void On_preserves_nonempty_think_text_with_spaces()
    {
        var session = new LocalThinkingStreamSession("On");
        var text = "<think>The quick brown</think>\nDone";
        Assert.Equal(text, session.FilterText(text));
    }

    [Theory]
    [InlineData("On")]
    [InlineData("XHigh")]
    [InlineData("Medium")]
    public void Thinking_on_keeps_space_only_deltas_inside_think(string mode)
    {
        var session = new LocalThinkingStreamSession(mode);
        Assert.Equal("<think>", session.FilterText("<think>"));
        Assert.Equal("The", session.FilterText("The"));
        Assert.Equal(" ", session.FilterText(" "));
        Assert.Equal("quick", session.FilterText("quick"));
        Assert.Equal("</think>", session.FilterText("</think>"));
    }

    [Fact]
    public void Thinking_on_keeps_space_only_reasoning_content_deltas()
    {
        var session = new LocalThinkingStreamSession("XHigh");
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["choices"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["delta"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["reasoning_content"] = " "
                    }
                }
            }
        };

        Assert.False(session.FilterCompletionJson(root));
        Assert.Equal(" ", root["choices"]![0]!["delta"]!["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public void Off_filter_completion_removes_reasoning_content_field()
    {
        var session = new LocalThinkingStreamSession("Off");
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["choices"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["delta"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["reasoning_content"] = "The",
                        ["content"] = (string?)null
                    }
                }
            }
        };

        Assert.True(session.FilterCompletionJson(root));
        var delta = root["choices"]![0]!["delta"]!.AsObject();
        Assert.False(delta.ContainsKey("reasoning_content"));
        Assert.True(session.IsThinkOnly);
    }

    [Fact]
    public void Off_think_only_finish_chunk_is_not_an_empty_complete()
    {
        var session = new LocalThinkingStreamSession("Off");
        session.FilterText("<think>keep looping</think>");
        Assert.True(session.IsThinkOnly);

        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["choices"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["delta"] = new System.Text.Json.Nodes.JsonObject(),
                    ["finish_reason"] = "stop"
                }
            }
        };

        Assert.True(session.FilterCompletionJson(root));
        Assert.Contains(
            LocalThinkOnlyReply.Message,
            root["choices"]![0]!["delta"]!["content"]!.ToString(),
            System.StringComparison.Ordinal);
        Assert.True(session.WroteThinkOnlyNotice);
    }

    [Fact]
    public void Off_visible_answer_after_think_is_not_think_only()
    {
        var session = new LocalThinkingStreamSession("Off");
        session.FilterText("<think>secret</think>");
        session.FilterText("use the file on disk");
        Assert.False(session.IsThinkOnly);
    }

    [Fact]
    public void Low_open_think_without_an_answer_is_a_cutoff()
    {
        var session = new LocalThinkingStreamSession("Low");
        Assert.Equal("<think>", session.FilterText("<think>"));
        Assert.Equal("The plan is", session.FilterText("The plan is"));
        Assert.True(session.InsideThink);
        Assert.True(session.OpenedThinkTag);
        Assert.True(session.IsThinkCutOff);
        Assert.False(session.IsThinkOnly);
    }

    [Fact]
    public void Low_answer_after_think_is_not_a_cutoff()
    {
        var session = new LocalThinkingStreamSession("Low");
        session.FilterText("<think>secret</think>");
        session.FilterText("use the file on disk");
        Assert.False(session.IsThinkCutOff);
        Assert.True(session.SawVisibleContent);
    }
}
