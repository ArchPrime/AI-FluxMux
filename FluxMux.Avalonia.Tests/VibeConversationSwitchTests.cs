using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Which conversation a request's session note (VIBE_STATE) and essence belong to (27 Nov):
/// reopening an older Cline task after working in another one gave it the other task's note,
/// because every long chat with an unfamiliar opening was taken for the current chat after a
/// compaction. Now only a chat that shows a compaction keeps the current conversation.
/// </summary>
public sealed class VibeConversationSwitchTests
{
    private static JsonArray LongChat(string firstUserText, int turns = 8)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Cline, an AI coding agent." },
            new JsonObject { ["role"] = "user", ["content"] = firstUserText },
        };
        for (var i = 0; i < turns; i++)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Working on step " + i + "." });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Carry on with step " + (i + 1) + "." });
        }

        return messages;
    }

    [Fact]
    public void A_reopened_older_chat_is_another_conversation()
    {
        var reopened = LongChat("<user_input mode=\"act\">Port the exporter to Rust.</user_input>");
        Assert.False(FluxMuxGatewayHost.LooksCompacted(reopened));
        Assert.False(FluxMuxGatewayHost.KeepsCurrentConversation(reopened, humanMessageCount: 9, hasCurrent: true, keySeenBefore: false));
    }

    [Theory]
    [InlineData("Context summary:\n\nThe person asked to port the exporter…")]
    [InlineData("Compressed prior conversation context (older turns):\n- user: port it")]
    [InlineData("Context summary:\n\n" + HandoverBrief.RescueSummaryHeading + " to make room.")]
    public void A_chat_whose_opening_a_compaction_replaced_is_the_same_conversation(string summary)
    {
        var compacted = LongChat(summary);
        Assert.True(FluxMuxGatewayHost.LooksCompacted(compacted));
        Assert.True(FluxMuxGatewayHost.KeepsCurrentConversation(compacted, humanMessageCount: 9, hasCurrent: true, keySeenBefore: false));
    }

    [Fact]
    public void A_new_chat_or_one_seen_before_switches()
    {
        var compacted = LongChat("Context summary:\n\nSomething.");
        // Seen before: switching restores its own note.
        Assert.False(FluxMuxGatewayHost.KeepsCurrentConversation(compacted, 9, hasCurrent: true, keySeenBefore: true));
        // A new chat (one or two of the person's messages) always starts its own note.
        Assert.False(FluxMuxGatewayHost.KeepsCurrentConversation(LongChat("Context summary: x", turns: 0), 1, hasCurrent: true, keySeenBefore: false));
        // Nothing current yet: nothing to keep.
        Assert.False(FluxMuxGatewayHost.KeepsCurrentConversation(compacted, 9, hasCurrent: false, keySeenBefore: false));
    }
}
