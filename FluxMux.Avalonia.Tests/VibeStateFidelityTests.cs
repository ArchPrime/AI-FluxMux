using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// VIBE_STATE fidelity fixes (Nov 2026): what a person wrote is separated from Client-app
/// control text; duplicates are not stored; each conversation keeps its own files; and the
/// injected block goes at the end of the newest user message, not into the system prompt.
/// </summary>
public sealed class VibeStateFidelityTests
{
    [Theory]
    [InlineData("<task>\nAdd a dark mode toggle\n</task>\n<environment_details>cwd=C:\\x</environment_details>", "Add a dark mode toggle")]
    [InlineData("<feedback>\nuse the orange icon instead\n</feedback>", "use the orange icon instead")]
    [InlineData("[TASK RESUMPTION] Please continue where you left off.", "")]
    [InlineData("Summarize this session for continuation. Be concise and factual.", "")]
    [InlineData("[read_file for 'Services/x.cs'] Result:\nusing System;", "")]
    [InlineData("[execute_command for 'dotnet build'] Result:\nBuild succeeded.", "")]
    [InlineData("<environment_details>only the environment</environment_details>", "")]
    [InlineData("are vision models made slower by this fix?", "are vision models made slower by this fix?")]
    [InlineData("  plain Harness message  ", "plain Harness message")]
    [InlineData("Compressed prior conversation context (older turns):\n- read Settings.cs", "")]
    [InlineData("Context summary:\n\n## Goal\nmake me a 3d rpg game", "")]
    [InlineData("Note from AI-FluxMux: you just ran exactly the same command as your previous step.", "")]
    public void Human_part_drops_client_control_text(string input, string expected)
    {
        Assert.Equal(expected, ConversationText.HumanPart(input));
    }

    [Fact]
    public void Same_intent_shift_is_stored_once()
    {
        var log = new EssenceLog(new FakeContextIo(), "/tmp/essence.json");
        for (var i = 0; i < 30; i++)
        {
            log.RecordIntentShift("i dont have access to another machine", "build the app");
        }

        log.RecordIntentShift("I dont  have access to ANOTHER machine", "build the app"); // same, other spacing/case
        log.RecordIntentShift("are vision models made slower?", "build the app");
        Assert.Equal(2, log.Load().IntentShifts.Count);
    }

    [Fact]
    public void A_goal_change_to_the_same_goal_is_not_recorded()
    {
        var log = new EssenceLog(new FakeContextIo(), "/tmp/essence.json");
        log.RecordEvolution("Install HOL Guard", "Install HOL Guard", "user pivoted");
        log.RecordEvolution("Install HOL Guard", "Speed up prompt reading", "user pivoted");
        var evolution = log.Load().Evolution;
        Assert.Single(evolution);
        Assert.Equal("Speed up prompt reading", evolution[0].To);
    }

    [Fact]
    public void Conversations_keep_their_own_files_and_come_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-vibe-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vibe = Path.Combine(dir, "VIBE_STATE.md");
            var essence = Path.Combine(dir, "essence.json");
            File.WriteAllText(vibe, "mixed legacy state");
            var scope = new VibeConversationScope(vibe, essence);

            // First keyed conversation: the legacy mix is set aside, files start empty.
            Assert.True(scope.Ensure("chat-a"));
            Assert.False(File.Exists(vibe));
            Assert.True(scope.HasArchive(VibeConversationScope.LegacyKey));
            File.WriteAllText(vibe, "state of A");

            // Same conversation again: nothing moves.
            Assert.False(scope.Ensure("chat-a"));

            // Another conversation: A is archived, B starts empty.
            Assert.True(scope.Ensure("chat-b"));
            Assert.False(File.Exists(vibe));
            File.WriteAllText(vibe, "state of B");

            // Back to A: its state returns; B is archived.
            Assert.True(scope.Ensure("chat-a"));
            Assert.Equal("state of A", File.ReadAllText(vibe));
            Assert.True(scope.HasArchive("chat-b"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Conversation_key_ignores_case_and_spacing()
    {
        Assert.Equal(
            VibeConversationScope.KeyFor("chat-", "Add a  dark mode toggle"),
            VibeConversationScope.KeyFor("chat-", "add a dark mode toggle "));
        Assert.NotEqual(
            VibeConversationScope.KeyFor("chat-", "Add a dark mode toggle"),
            VibeConversationScope.KeyFor("chat-", "Fix the build script"));
    }

    [Fact]
    public void Plain_openai_messages_are_not_matched_to_a_cline_session()
    {
        var plain = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "s" },
            new JsonObject { ["role"] = "user", ["content"] = "hi" }
        };
        var withIds = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hi", ["id"] = "msg_123" }
        };
        Assert.False(FluxMuxGatewayHost.RequestCarriesMessageIds(plain));
        Assert.True(FluxMuxGatewayHost.RequestCarriesMessageIds(withIds));
    }

    [Fact]
    public void Block_is_appended_to_string_and_multipart_messages()
    {
        var text = new JsonObject { ["role"] = "user", ["content"] = "hello" };
        FluxMuxGatewayHost.AppendTextToMessage(text, "\n\nnote");
        Assert.Equal("hello\n\nnote", text["content"]!.GetValue<string>());

        var parts = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "image_url" } }
        };
        FluxMuxGatewayHost.AppendTextToMessage(parts, "note");
        var array = (JsonArray)parts["content"]!;
        Assert.Equal(2, array.Count);
        Assert.Equal("note", array[1]!["text"]!.GetValue<string>());
    }
}
