using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The Closed-loop mill ceiling counts tools since the person's latest message. Counting the
/// whole chat paused a productive Harness chat (25 Nov) only because it was long.
/// </summary>
public sealed class ClosedLoopCeilingPerTurnTests
{
    private static JsonObject Chat(params int[] toolsPerTurn)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = "You are a coding agent." } };
        var n = 0;
        foreach (var tools in toolsPerTurn)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Next feature, please (" + n + ")." });
            for (var i = 0; i < tools; i++, n++)
            {
                var id = "call_" + n;
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = id, ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "edit", ["arguments"] = new JsonObject { ["file_path"] = "main.js", ["old_string"] = "a" + n, ["new_string"] = "b" + n }.ToJsonString() },
                        },
                    },
                });
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "The file main.js has been updated successfully." });
            }
        }

        return new JsonObject { ["messages"] = messages };
    }

    // Only the ceiling: other Mill rules off, so this tests what the ceiling alone decides.
    private static readonly PortForwardingRules CeilingOnly = PortForwardingRules.Defaults with
    {
        MillAtOmittedEnabled = false,
        ObserveOnlyMillEnabled = false,
        ClosedLoopCeilingEnabled = true,
    };

    [Fact]
    public void A_long_productive_chat_does_not_reach_the_ceiling()
    {
        // The paused chat's shape: 272 tool calls over 10 turns, at most 60 in one turn.
        var chat = Chat(24, 26, 10, 2, 40, 60, 50, 11, 23, 26);
        Assert.Equal(26, LocalChatPayloadSignals.CountToolResultsSinceHumanMessage(chat));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(chat, CeilingOnly));
    }

    [Fact]
    public void Many_tools_in_one_go_without_a_word_from_the_person_still_pause()
    {
        var chat = Chat(5, 256);
        Assert.Equal(256, LocalChatPayloadSignals.CountToolResultsSinceHumanMessage(chat));
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(chat, CeilingOnly));
    }
}
