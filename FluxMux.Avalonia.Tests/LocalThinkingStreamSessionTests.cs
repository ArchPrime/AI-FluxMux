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
    }
}
