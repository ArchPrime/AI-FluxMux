using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Compaction keeps the opening request, the person's earlier messages and the values found in
/// older tool output (26 Nov). On the Live-Switch test's chat, max fidelity's compaction lost the
/// codename from the opening request, a port from a config read, a ruled-out library and a flaky
/// test name — the summary kept only the commands that had run and the assistant's decisions.
/// </summary>
public sealed class CompactionKeepsFactsTests
{
    [Fact]
    public void Every_planted_fact_survives_compacting_the_switch_test_chat()
    {
        // The Pressure chat, compacted as max fidelity does (80 % trigger, fill to 60 %).
        var payload = new JsonObject { ["messages"] = SwitchFidelityTest.WithRecallQuestion(SwitchFidelityTest.BuildChat(130000)) };
        const int context = 163840;
        Assert.True(LocalHistoryCompaction.TryCompactPayloadToFill(payload, PortForwardingRules.Defaults, context, 0.6, 1.5,
            t => LocalRequestOverlayRouting.EstimatePromptTokens(t) * 1.5 + 257 <= context));

        var sent = payload.ToJsonString();
        var lost = SwitchFidelityTest.Facts.Where(f => !sent.Contains(f.Answer, StringComparison.Ordinal)).Select(f => f.Id).ToList();
        Assert.True(lost.Count == 0, "lost facts: " + string.Join(", ", lost));
        Assert.True(LocalRequestOverlayRouting.EstimatePromptTokens(payload) * 1.5 < context * 0.62);
    }

    private static JsonObject ToolRound(string id, string command, string output)
        => new()
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "run_command", ["arguments"] = new JsonObject { ["command"] = command }.ToJsonString() } } },
        };

    [Fact]
    public void Code_lines_are_not_taken_for_values_but_settings_and_outcomes_are()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Fix the service." },
            ToolRound("a", "type app.py", ""),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "a", ["content"] = "def run(batch):\n    total = sum(x.amount for x in batch)\n    return total\n" },
            ToolRound("b", "type settings.ini", ""),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "b", ["content"] = "[db]\npool_size = 12\nhost = db.internal\n" },
            ToolRound("c", "pytest", ""),
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c", ["content"] = "3 failed, 40 passed\n" },
        };
        for (var i = 0; i < 30; i++)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Carry on " + i });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Working on step " + i + "." });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, PortForwardingRules.Defaults, force: true));
        var summary = payload["messages"]!.AsArray().OfType<JsonObject>()
            .First(m => m["content"]?.ToString().StartsWith("Compressed prior conversation context", StringComparison.Ordinal) == true)["content"]!.ToString();
        Assert.Contains("pool_size = 12", summary, StringComparison.Ordinal);
        Assert.Contains("host = db.internal", summary, StringComparison.Ordinal);
        Assert.Contains("3 failed, 40 passed", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("total = sum", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= LocalHistoryCompaction.MaxSummaryChars);
    }

    [Fact]
    public void Values_are_found_inside_Cline_4_result_lists_and_its_commands_are_named()
    {
        // Cline 4.x: run_commands {"commands": [...]}, output as the JSON of its result list.
        JsonObject Call(string id, string command) => new()
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "run_commands", ["arguments"] = new JsonObject { ["commands"] = new JsonArray { command } }.ToJsonString() } } },
        };
        JsonObject Result(string id, string command, string output) => new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = new JsonArray { new JsonObject { ["query"] = command, ["result"] = output } }.ToJsonString(),
        };

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Tidy the billing service." },
            Call("a", "Get-Content config/service.toml"),
            Result("a", "Get-Content config/service.toml", "[service]\nname = \"billing\"\nmetrics_port = 47193\n"),
        };
        for (var i = 0; i < 30; i++)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Carry on " + i });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Working on step " + i + "." });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, PortForwardingRules.Defaults, force: true));
        var summary = payload["messages"]!.AsArray().OfType<JsonObject>()
            .First(m => m["content"]?.ToString().StartsWith("Compressed prior conversation context", StringComparison.Ordinal) == true)["content"]!.ToString();
        // Values are labelled as "Already ran" names the call (27 Nov).
        Assert.Contains("run_commands: Get-Content config/service.toml: metrics_port = 47193", summary, StringComparison.Ordinal);
        Assert.Contains("- run_commands: Get-Content config/service.toml", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_opening_request_is_pinned_ahead_of_the_summary()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Build the exporter. Output must be UTF-8 CSV named report-Q3.csv." },
        };
        for (var i = 0; i < 40; i++)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Next step " + i });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Done " + i + "." });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, PortForwardingRules.Defaults, force: true));
        var kept = payload["messages"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal("system", kept[0]["role"]!.ToString());
        Assert.Contains("report-Q3.csv", kept[1]["content"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Cline_environment_dump_does_not_crowd_out_what_the_person_wrote()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "<task>Port the exporter to Rust.</task>" },
        };
        for (var i = 0; i < 30; i++)
        {
            var env = "<environment_details>\n# VSCode Visible Files\nsrc/a" + i + ".rs\nsrc/b" + i + ".rs\nsrc/c" + i + ".rs\n</environment_details>";
            var said = i == 3 ? "Keep the CSV header order: id, name, total_v2." : "Continue.";
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "<feedback>" + said + "</feedback>\n" + env });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Working on step " + i + "." });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, PortForwardingRules.Defaults, force: true));
        var summary = payload["messages"]!.AsArray().OfType<JsonObject>()
            .First(m => m["content"]?.ToString().StartsWith("Compressed prior conversation context", StringComparison.Ordinal) == true)["content"]!.ToString();
        Assert.Contains("total_v2", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("VSCode Visible Files", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pinned_opening_is_labelled_and_the_newest_dropped_messages_are_kept()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "agent" },
            new JsonObject { ["role"] = "user", ["content"] = "Audit the app for DPI scaling in MainWindow.axaml and Help_files/." },
        };
        for (var i = 0; i < 40; i++)
        {
            // Wordy, value-heavy early messages would outscore a short late reversal.
            var said = i == 25 ? "No, revert that." : i < 30 ? "Check src/View" + i + ".axaml and Styles/Theme" + i + ".xaml at 150% and 200%." : "Next " + i;
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = said });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Done " + i + "." });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, PortForwardingRules.Defaults, force: true));
        var kept = payload["messages"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.StartsWith(LocalHistoryCompaction.OpeningLabel, kept[1]["content"]!.ToString(), StringComparison.Ordinal);
        var summary = kept.First(m => m["content"]?.ToString().StartsWith("Compressed prior conversation context", StringComparison.Ordinal) == true)["content"]!.ToString();
        Assert.Contains(HandoverBrief.LatestWinsRule, summary, StringComparison.Ordinal);
        // A short late reversal among wordy, value-heavy messages: picked by informativeness alone it
        // was dropped from the notes; as one of the newest dropped messages it is kept.
        Assert.Contains("- user: No, revert that.", summary, StringComparison.Ordinal);
    }
}
