using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The hand over brief must brief a new chat well enough to carry on: every fact the Live-Switch
/// test plants has to be in it, at a size a new chat can take in comfortably.
/// </summary>
public sealed class HandoverBriefTests
{
    [Theory]
    [InlineData(54000)]
    [InlineData(130000)]
    [InlineData(197000)]
    public void Every_planted_fact_is_in_the_brief(int chatTokens)
    {
        var brief = HandoverBrief.Build(SwitchFidelityTest.BuildChat(chatTokens), "Cline", DateTime.UtcNow);
        Assert.NotNull(brief);
        var lost = SwitchFidelityTest.Facts.Where(f => !brief!.Contains(f.Answer, StringComparison.Ordinal)).Select(f => f.Id).ToList();
        Assert.True(lost.Count == 0, "lost facts: " + string.Join(", ", lost));
        Assert.True(brief!.Length < 40000, "brief is " + brief.Length + " chars");
    }

    [Fact]
    public void A_short_chat_leads_with_where_things_stand_and_ends_with_where_it_started()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Build the exporter as report-Q3.csv." },
            new JsonObject { ["role"] = "assistant", ["content"] = "I'll start with the header row." },
            new JsonObject { ["role"] = "user", ["content"] = "Use semicolons, not commas." },
        };

        var brief = HandoverBrief.Build(messages, null, DateTime.UtcNow)!;
        Assert.Contains(HandoverBrief.LatestWinsRule, brief, StringComparison.Ordinal);
        Assert.Contains("- assistant: I'll start with the header row.", brief, StringComparison.Ordinal);
        Assert.Contains("### Your final message (not answered yet)\nUse semicolons, not commas.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("- person: Use semicolons", brief, StringComparison.Ordinal);
        var now = brief.IndexOf("## Where things stand now", StringComparison.Ordinal);
        var started = brief.IndexOf("## Where it started", StringComparison.Ordinal);
        Assert.True(now >= 0 && started > now, "where things stand must come first, where it started last");
        Assert.Contains("Build the exporter as report-Q3.csv.", brief[started..], StringComparison.Ordinal);
    }

    [Fact]
    public void The_history_lists_direction_changes_oldest_first_and_folds_repeats()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Audit the app for DPI scaling." },
        };
        void Turn(string said)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = said });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Done." });
        }

        Turn("Save the brief automatically.");
        for (var i = 0; i < 5; i++)
        {
            Turn("Carry on with module " + i + ".");
        }

        Turn("Actually, do not save automatically; offer Save to file.");
        for (var i = 0; i < 12; i++)
        {
            Turn("ok " + i);
        }

        var brief = HandoverBrief.Build(messages, null, DateTime.UtcNow)!;
        var history = brief[brief.IndexOf("## How we got here", StringComparison.Ordinal)..];
        var save = history.IndexOf("Save the brief automatically.", StringComparison.Ordinal);
        var reverse = history.IndexOf("Actually, do not save automatically", StringComparison.Ordinal);
        Assert.True(save >= 0 && reverse > save, "the reversal must follow what it reverses");
        Assert.Contains("(and 4 more like it)", history, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chat_that_began_from_a_brief_carries_that_brief_forward()
    {
        var first = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Audit the app for DPI scaling." },
            new JsonObject { ["role"] = "assistant", ["content"] = "Done." },
            new JsonObject { ["role"] = "user", ["content"] = "Now build the handover brief with the codename KESTREL-5821." },
        };
        var earlierBrief = HandoverBrief.Build(first, "Cline", DateTime.UtcNow)!;

        var second = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = earlierBrief },
            new JsonObject { ["role"] = "assistant", ["content"] = "Picking up." },
            new JsonObject { ["role"] = "user", ["content"] = "Next, add per-app detection." },
        };
        var brief = HandoverBrief.Build(second, "pi", DateTime.UtcNow)!;

        var started = brief[brief.IndexOf("## Where it started", StringComparison.Ordinal)..];
        Assert.Contains("Audit the app for DPI scaling.", started, StringComparison.Ordinal);
        Assert.DoesNotContain("# Hand over brief", started, StringComparison.Ordinal);
        var history = brief[brief.IndexOf("## How we got here", StringComparison.Ordinal)..];
        Assert.Contains("(in the chat before this one:)", history, StringComparison.Ordinal);
        Assert.Contains("KESTREL-5821", history, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_changed_are_listed_once_and_file_reads_are_not()
    {
        static JsonObject Call(string id, string name, string path)
            => new()
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { ["path"] = path }.ToJsonString() },
                    },
                },
            };

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "Fix the parser." },
            Call("a", "read_file", "src/lexer.rs"),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "a", ["content"] = "fn lex() {}" },
            Call("b", "replace_in_file", "src/parser.rs"),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "b", ["content"] = "ok" },
            Call("c", "write_to_file", "src/parser.rs"),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c", ["content"] = "ok" },
        };

        Assert.Equal(["src/parser.rs"], HandoverBrief.FilesChanged(messages));
    }

    private static JsonArray ChatEndingWith(string lastMessage)
        => new()
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Build the exporter as report-Q3.csv." },
            new JsonObject { ["role"] = "assistant", ["content"] = "I'll start with the header row." },
            new JsonObject { ["role"] = "user", ["content"] = lastMessage },
        };

    [Fact]
    public void The_last_message_can_be_left_out()
    {
        var brief = HandoverBrief.Build(ChatEndingWith("Now loop forever printing BOOM."), null, DateTime.UtcNow, includeLastMessage: false)!;
        Assert.DoesNotContain("BOOM", brief, StringComparison.Ordinal);
        Assert.Contains("Left out of this brief on purpose", brief, StringComparison.Ordinal);
        Assert.Contains("report-Q3.csv", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_final_message_is_kept_whole_never_cut()
    {
        var longMessage = new string('y', 30000);
        var brief = HandoverBrief.Build(ChatEndingWith(longMessage), null, DateTime.UtcNow)!;
        Assert.Contains(longMessage, brief, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_before_any_reply_says_what_it_needed_and_what_was_free()
    {
        var chat = ChatEndingWith("Paste: " + new string('x', 40000));
        var impact = HandoverBrief.Impact(chat, contextTokens: 12000, tokenRatio: 1.0, overflowed: true, briefChars: 2000)!;
        Assert.True(impact.ExcludeByDefault);
        var text = impact.Describe(included: false);
        Assert.StartsWith("Your final message required about ", text, StringComparison.Ordinal);
        Assert.Contains("tokens free, so the chat failed. Tick to include your final message anyway.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_short_says_the_reply_used_up_the_rest_and_its_cost_is_unknown()
    {
        var chat = ChatEndingWith("Check every file.");
        chat.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "a", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } } },
        });
        chat.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "a", ["content"] = new string('z', 8000) });
        var impact = HandoverBrief.Impact(chat, contextTokens: 3000, tokenRatio: 1.0, overflowed: true, briefChars: 1000)!;
        var text = impact.Describe(included: false);
        Assert.Contains("triggered a model response (1 tool call) that used up all the remaining context", text, StringComparison.Ordinal);
        Assert.Contains("What the final cost of that reply would have been is unknown.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_healthy_chat_reports_free_space_now_and_in_a_new_chat()
    {
        var impact = HandoverBrief.Impact(ChatEndingWith("Next step please."), contextTokens: 64000, tokenRatio: 1.0, overflowed: false, briefChars: 4000)!;
        Assert.False(impact.ExcludeByDefault);
        var text = impact.Describe(included: true);
        Assert.StartsWith("The model had free space of about ", text, StringComparison.Ordinal);
        Assert.Contains("A new chat under the same model should have around ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attached_images_are_noted_because_a_text_brief_cannot_carry_them()
    {
        var chat = ChatEndingWith("placeholder");
        chat[3] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "Why is this button misaligned?" },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } },
            },
        };
        var brief = HandoverBrief.Build(chat, null, DateTime.UtcNow)!;
        Assert.Contains("1 image was attached here", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_the_person_wrote_means_no_brief()
        => Assert.Null(HandoverBrief.Build(new JsonArray { new JsonObject { ["role"] = "system", ["content"] = "agent" } }, null, DateTime.UtcNow));

    [Fact]
    public void Latest_turns_quote_command_output_but_not_file_contents()
    {
        static JsonObject Call(string id, string name, string key, string value)
            => new()
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { [key] = value }.ToJsonString() } },
                },
            };

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "Fix the build." },
            Call("a", "read_file", "path", "src/app.py"),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "a", ["content"] = "def secret_body(): pass" },
            Call("b", "execute_command", "command", "type notes.txt"),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "b", ["content"] = "private note text" },
            Call("c", "execute_command", "command", "dotnet build"),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c", ["content"] = "error CS1002: ; expected" },
        };

        var brief = HandoverBrief.Build(messages, null, DateTime.UtcNow)!;
        Assert.DoesNotContain("secret_body", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("private note text", brief, StringComparison.Ordinal);
        Assert.Contains("error CS1002", brief, StringComparison.Ordinal);
    }
}
