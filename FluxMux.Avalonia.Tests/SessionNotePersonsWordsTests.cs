using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The session note (VIBE_STATE) carries only the person's words (27 Nov). Its State used to be
/// cut from the model's latest reply, so when a Cline chat's model went wrong ("Let me refocus on
/// the actual issue: the apps listing has stopped working", the opening request, hours old) the
/// note handed that back as the session's state on every later turn.
/// </summary>
public sealed class SessionNotePersonsWordsTests
{
    private static JsonArray Chat()
        => new()
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Cline." },
            new JsonObject { ["role"] = "user", ["content"] = "<user_input mode=\"act\">fluxmux settings tab apps listing has stopped working</user_input>" },
            new JsonObject { ["role"] = "assistant", ["content"] = "Looking at the listing." },
            new JsonObject { ["role"] = "user", ["content"] = "the context gauges disagree after a model change" },
            new JsonObject { ["role"] = "assistant", ["content"] = "Checking the gauges." },
            new JsonObject { ["role"] = "user", ["content"] = "sorry, why have we pivoted to igpu & dgpu? please leave them alone - they work fine." },
            new JsonObject { ["role"] = "assistant", ["content"] = "You're right. Let me refocus on the actual issue: the apps listing in the settings tab has stopped working." },
        };

    [Fact]
    public void The_note_state_is_the_persons_latest_messages_not_the_models_reply()
    {
        var state = FluxMuxGatewayHost.PersonsLatestWords(Chat());
        Assert.Contains("please leave them alone", state, System.StringComparison.Ordinal);
        Assert.Contains("the context gauges disagree", state, System.StringComparison.Ordinal);
        Assert.DoesNotContain("refocus", state, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Checking the gauges", state, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_block_has_no_Next_line_from_the_model()
    {
        var block = VibeStateMarkdown.BuildReinjectionBlock(new VibeState { Task = "t", State = "s", Next = "an old next step from a reply" });
        Assert.DoesNotContain("Next:", block, System.StringComparison.Ordinal);
        Assert.DoesNotContain("an old next step", block, System.StringComparison.Ordinal);
    }
}
