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
    public void A_short_chat_is_quoted_whole_with_opening_and_current_task()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Build the exporter as report-Q3.csv." },
            new JsonObject { ["role"] = "assistant", ["content"] = "I'll start with the header row." },
            new JsonObject { ["role"] = "user", ["content"] = "Use semicolons, not commas." },
        };

        var brief = HandoverBrief.Build(messages, null, DateTime.UtcNow)!;
        Assert.Contains("## Original request\nBuild the exporter as report-Q3.csv.", brief, StringComparison.Ordinal);
        Assert.Contains("- assistant: I'll start with the header row.", brief, StringComparison.Ordinal);
        Assert.Contains("## Your latest message\nUse semicolons, not commas.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("summarised)\n", brief, StringComparison.Ordinal);
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
