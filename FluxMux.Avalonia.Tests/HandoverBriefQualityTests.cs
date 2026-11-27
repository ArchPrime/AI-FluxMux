using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// What a review of a real brief found wrong (27 Nov, a 1,175-message Cline chat): the person's
/// answers to the model's questions were lost behind Cline's hook notes, nothing said what the
/// task had become, a conclusion the person had rejected was listed as a decision to treat as
/// settled, values repeated and contradicted one another, and command output came as raw JSON.
/// </summary>
public sealed class HandoverBriefQualityTests
{
    private const string Hook = "<hook_context source=\"ToolSuccess\" tool_name=\"ask__question\" tool_call_id=\"q1\">\nThe user has answered the question. Continue working on their task using that answer.\n</hook_context>";

    private static JsonObject Call(string id, string name, string arguments)
        => new()
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
                },
            },
        };

    private static JsonObject Result(string id, string content)
        => new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content };

    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

    private static JsonObject Assistant(string text) => new() { ["role"] = "assistant", ["content"] = text };

    private static JsonArray Chat()
    {
        var m = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Cline, an AI coding agent." },
            User("<user_input mode=\"act\">The settings tab apps listing has stopped working</user_input>"),
            Call("q1", "ask_question", "{\"question\":\"What do you want me to fix?\",\"options\":[\"Both\",\"Speed\"]}"),
            Result("q1", "this took something like 10 minutes, fix the speed"),
            User(Hook),
            Assistant("There it is. The answer is clear. A brand-new NVIDIA driver installed yesterday is the cause of the slowdown."),
            User("no the slowdown unambiguously predates the driver update"),
            Call("c1", "run_commands", "{\"commands\":[\"Write-Host '=== Check the cache flags ==='; Get-Content C:\\\\logs\\\\launch.txt | Select-String cache-reuse\"]}"),
            Result("c1", "[{\"query\":\"Write-Host ...\",\"result\":\"\\u001b[32;1mcache-reuse: False\\u001b[0m\"}]"),
            Assistant("Checking the flags again after the rebuild."),
            Call("c2", "run_commands", "{\"commands\":[\"Write-Host '=== Check the cache flags again ==='; Get-Content C:\\\\logs\\\\launch.txt | Select-String cache-reuse\"]}"),
            Result("c2", "[{\"query\":\"Write-Host ...\",\"result\":\"cache-reuse: True\"}]"),
        };
        for (var i = 0; i < 30; i++)
        {
            m.Add(User("redeployed " + i));
            m.Add(Assistant("Looking at module " + i + " of the router."));
        }

        m.Add(User("what i am seeing is discrepancies between the Cline context gauge and the AI-FluxMux gauge"));
        m.Add(Assistant("I will compare the two context limits."));
        m.Add(User("because fluxmux is a router, it needs to route models within a given chat"));
        for (var i = 0; i < 14; i++)
        {
            m.Add(Call("t" + i, "read_files", "{\"paths\":[\"Services/F" + i + ".cs\"]}"));
            m.Add(Result("t" + i, "file body " + i));
        }

        m.Add(User("we dont want to make cline reread the chat in full every turn"));
        m.Add(Assistant("Understood: the gauge must update without Cline re-reading the chat."));
        return m;
    }

    [Fact]
    public void Answers_to_the_models_questions_are_the_persons_words_and_hook_notes_are_not()
    {
        var brief = HandoverBrief.Build(Chat(), "Cline", DateTime.UtcNow)!;
        Assert.Contains("(answering \"What do you want me to fix?\") this took something like 10 minutes", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("hook_context", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("The user has answered the question", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void The_brief_leads_with_what_the_person_is_working_on_now_and_the_final_message_precedes_its_answer()
    {
        var brief = HandoverBrief.Build(Chat(), "Cline", DateTime.UtcNow)!;
        var now = brief.IndexOf("### What you are working on now", StringComparison.Ordinal);
        var final = brief.IndexOf("### Your final message", StringComparison.Ordinal);
        var turns = brief.IndexOf("### Latest turns", StringComparison.Ordinal);
        Assert.True(now >= 0 && now < final && final < turns, "now, then the final message, then the turns that answer it");
        var nowSection = brief[now..final];
        Assert.Contains("it needs to route models within a given chat", nowSection, StringComparison.Ordinal);
        Assert.Contains("discrepancies between the Cline context gauge", nowSection, StringComparison.Ordinal);
        Assert.Contains("(and ", nowSection, StringComparison.Ordinal); // "redeployed n" folded
    }

    [Fact]
    public void A_conclusion_the_person_rejected_carries_their_objection_and_nothing_is_treated_as_settled()
    {
        var brief = HandoverBrief.Build(Chat(), "Cline", DateTime.UtcNow)!;
        Assert.Contains("NVIDIA driver installed yesterday is the cause of the slowdown. (the person replied: \"no the slowdown unambiguously predates the driver update\")", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Treat the facts above as settled", brief, StringComparison.Ordinal);
        Assert.Contains("may be out of date or wrong", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_read_again_replaces_the_earlier_reading_and_colour_codes_are_gone()
    {
        var brief = HandoverBrief.Build(Chat(), "Cline", DateTime.UtcNow)!;
        Assert.Contains("cache-reuse: True", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("cache-reuse: False", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_that_echoes_a_heading_is_named_by_it()
    {
        Assert.Equal(
            "Find where hotContext is defined — Select-String -Path a.cs",
            LocalToolResultClearing.CommandGist("Write-Host '=== Find where hotContext is defined ==='; Select-String -Path a.cs"));
        Assert.Equal("dotnet build", LocalToolResultClearing.CommandGist("dotnet build"));
        Assert.Equal(
            "Check the MTP model's variant — $c = 1",
            LocalToolResultClearing.CommandGist("Write-Host '=== Check the MTP model's variant ==='; $c = 1"));
        var brief = HandoverBrief.Build(Chat(), "Cline", DateTime.UtcNow)!;
        Assert.Contains("run_commands: Check the cache flags —", brief, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Let me look. This is the key. Let me analyze it and check the recent history for what changed a week ago.")]
    [InlineData("I checked the logs and found nothing useful in them at all today, so I'll move on")]
    public void A_conclusion_is_whole_sentences(string reply)
    {
        var m = new JsonArray { User("Fix the router."), Assistant(reply) };
        for (var i = 0; i < 20; i++)
        {
            m.Add(User("next " + i));
            m.Add(Assistant("ok"));
        }

        var summary = LocalHistoryCompaction.BuildForwardSummary(m, m.Count - 4) ?? string.Empty;
        // A marker in mid-sentence ("the key.", "so I'll") no longer starts the note there.
        Assert.Contains("- " + reply, summary, StringComparison.Ordinal);
    }
}
