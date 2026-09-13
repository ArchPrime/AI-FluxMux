using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The Port sequence a closed-loop debug chat must survive: repeated
/// edit/read/python/observe on the same source file, no dump files. That
/// chat must not 400 as a mill or a repeated command. A dir/dir or echo
/// mill still stops. Compact must not reclassify a loop that was already
/// allowed.
/// </summary>
public sealed class LocalClosedLoopPortPolicyTests
{
    private const string CarRacing = @"C:\AI_Workbench\Workspace\game\car_racing.py";

    [Fact]
    public void Closed_loop_edit_read_python_stays_open_after_dozens_of_tools()
    {
        var payload = ClosedLoopCarRacing(rounds: 40);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
        Assert.False(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out _));
        Assert.True(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));

        var dropped = LocalChatPayloadSignals.KeepMostRecentImages(payload);
        Assert.True(dropped >= 1);
        var json = payload.ToJsonString();
        Assert.Contains("shot39", json);
        Assert.DoesNotContain("\"data\":\"shot0\"", json);
        Assert.DoesNotContain("\"data\":\"shot1\"", json);
    }

    [Fact]
    public void Repeated_dir_still_stops_the_turn()
    {
        var payload = PayloadWithAssistantCalls(
            ShellCall("execute_command", "dir"),
            ShellCall("execute_command", "dir"));
        Assert.True(LocalChatPayloadSignals.HasRepeatedToolCommand(payload, out var command));
        Assert.Equal("dir", command);
        Assert.False(LocalChatPayloadSignals.IsClosedLoopFingerprint(command));
    }

    [Fact]
    public void Screenshot_only_trips_before_omit_has_anything_to_drop()
    {
        var payload = ScreenshotOnlyLoop(rounds: 8);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted < LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(LocalChatPayloadSignals.RecentToolsAreObserveOnlyMill(
            payload,
            LocalToolResultClearing.ObserveOnlyMillCount));
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Distinct_python_inspects_do_not_trip_observe_only_mill()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "why is the car invisible" }
        };
        for (var i = 0; i < LocalToolResultClearing.ObserveOnlyMillCount; i++)
        {
            messages.Add(AssistantCall(
                "py_" + i,
                "execute_command",
                command: "python -c print(" + i + ")"));
            messages.Add(ToolResult("py_" + i, "out " + new string('o', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.False(LocalChatPayloadSignals.RecentToolsAreRepeatMill(
            payload,
            LocalToolResultClearing.ObserveOnlyMillCount));
        Assert.False(LocalChatPayloadSignals.RecentToolsAreObserveOnlyMill(
            payload,
            LocalToolResultClearing.ObserveOnlyMillCount));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted: 0));
    }

    [Fact]
    public void Distinct_file_reads_do_not_trip_mill_at_omitted()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "find the draw path" }
        };
        for (var i = 0; i < 50; i++)
        {
            var path = @"C:\AI_Workbench\Workspace\game\file_" + i + ".py";
            messages.Add(AssistantCall("read_" + i, "read", path: path));
            messages.Add(ToolResult("read_" + i, "source " + new string('r', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(omitted < LocalToolResultClearing.ClosedLoopRunawayOmittedResults);
        Assert.False(LocalChatPayloadSignals.RecentToolsAreRepeatMill(
            payload,
            LocalToolResultClearing.ClosedLoopMillLookback));
        Assert.True(LocalChatPayloadSignals.RecentToolsAreDiverseInvestigation(
            payload,
            LocalToolResultClearing.ClosedLoopMillLookback));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Same_file_rereads_after_a_long_loop_stay_open()
    {
        var payload = ClosedLoopCarRacing(rounds: 40);
        for (var i = 0; i < LocalToolResultClearing.ClosedLoopMillLookback; i++)
        {
            payload["messages"]!.AsArray().Add(AssistantCall("reread_" + i, "read_file", path: CarRacing));
            payload["messages"]!.AsArray().Add(ToolResult("reread_" + i, "source " + new string('r', 400)));
        }

        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(omitted < LocalToolResultClearing.ClosedLoopRunawayOmittedResults);
        Assert.False(LocalChatPayloadSignals.RecentToolsAreRepeatMill(
            payload,
            LocalToolResultClearing.ClosedLoopMillLookback));
        Assert.True(LocalChatPayloadSignals.RecentToolsAreFileReadInvestigation(
            payload,
            LocalToolResultClearing.ClosedLoopMillLookback));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Cline_reread_burst_at_117_omitted_stays_open()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "trace the draw path" }
        };
        for (var i = 0; i < 40; i++)
        {
            messages.Add(AssistantCall("edit_" + i, "replace_in_file", path: CarRacing));
            messages.Add(ToolResult("edit_" + i, "updated " + new string('e', 400)));
            messages.Add(AssistantCall("read_" + i, "read_file", path: CarRacing));
            messages.Add(ToolResult("read_" + i, "source " + new string('r', 400)));
            messages.Add(AssistantCall("search_" + i, "search_files", path: @"C:\AI_Workbench\Workspace\game"));
            messages.Add(ToolResult("search_" + i, "hits " + new string('h', 400)));
        }

        for (var i = 0; i < 6; i++)
        {
            messages.Add(AssistantCall("confirm_" + i, "read_file", path: CarRacing));
            messages.Add(ToolResult("confirm_" + i, "source " + new string('r', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= 32);
        Assert.True(omitted < LocalToolResultClearing.ClosedLoopRunawayOmittedResults);
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Truncated_chunk_reads_of_one_file_stay_open()
    {
        var payload = ClosedLoopCarRacing(rounds: 40);
        for (var i = 0; i < LocalToolResultClearing.ClosedLoopMillLookback; i++)
        {
            payload["messages"]!.AsArray().Add(
                AssistantCall(
                    "chunk_" + i,
                    "read_file",
                    path: CarRacing,
                    extra: new JsonObject
                    {
                        ["offset"] = (i * 200).ToString(),
                        ["limit"] = "200"
                    }));
            payload["messages"]!.AsArray().Add(
                ToolResult("chunk_" + i, "chunk " + i + " " + new string('r', 400)));
        }

        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(omitted < LocalToolResultClearing.ClosedLoopRunawayOmittedResults);
        Assert.True(LocalChatPayloadSignals.RecentToolsAreFileReadInvestigation(
            payload,
            LocalToolResultClearing.ClosedLoopMillLookback));
        Assert.False(LocalChatPayloadSignals.RecentToolsAreRepeatMill(
            payload,
            LocalToolResultClearing.ClosedLoopMillLookback));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Reads_then_screenshots_do_not_trip_observe_only_mill()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "why is the car invisible" }
        };
        for (var i = 0; i < 8; i++)
        {
            messages.Add(AssistantCall("read_" + i, "read", path: CarRacing));
            messages.Add(ToolResult("read_" + i, "source " + new string('r', 400)));
        }

        for (var i = 0; i < LocalToolResultClearing.ObserveOnlyMillCount; i++)
        {
            messages.Add(AssistantCall("shot_" + i, "take_screenshot"));
            messages.Add(ToolShot("shot_" + i, "shot" + i, "frame " + new string('f', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted < LocalToolResultClearing.RunawayOmittedResults);
        Assert.False(LocalChatPayloadSignals.RecentToolsAreObserveOnlyMill(
            payload,
            LocalToolResultClearing.ObserveOnlyMillCount));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Screenshot_only_loop_trips_the_omit_brake()
    {
        var payload = ScreenshotOnlyLoop(rounds: 50);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.False(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Python_and_screenshot_without_edits_is_useful_work()
    {
        var payload = PythonObserveLoop(rounds: 40);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(omitted < LocalToolResultClearing.ClosedLoopRunawayOmittedResults);
        Assert.True(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));
        Assert.True(LocalChatPayloadSignals.IsClosedLoopShellFingerprint("python " + CarRacing));
        Assert.False(LocalChatPayloadSignals.IsObserveOnlyFingerprint("python " + CarRacing));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
        Assert.False(LocalChatPayloadSignals.IsFileMutatingFingerprint("python " + CarRacing));
        Assert.False(LocalChatPayloadSignals.IsFileMutatingFingerprint("take_screenshot"));
    }

    [Fact]
    public void Vision_observe_edit_loop_stays_open_after_dozens_of_tools()
    {
        var payload = VisionUiLoop(rounds: 40);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
        Assert.True(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));
        Assert.True(LocalChatPayloadSignals.IsClosedLoopFingerprint("take_screenshot"));
        Assert.True(LocalChatPayloadSignals.IsClosedLoopFingerprint(
            "browser_action {\"action\":\"screenshot\"}"));
    }

    [Fact]
    public void One_echo_among_file_edits_does_not_trip_the_omit_brake()
    {
        var payload = ClosedLoopCarRacing(rounds: 40);
        payload["messages"]!.AsArray().Add(AssistantCall("echo_tail", "execute_command", command: "echo leftover"));
        payload["messages"]!.AsArray().Add(ToolResult("echo_tail", new string('x', 400)));
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
        Assert.True(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));
    }

    [Fact]
    public void Recent_echo_majority_still_trips_the_omit_brake()
    {
        var payload = ClosedLoopCarRacing(rounds: 40);
        for (var i = 0; i < 4; i++)
        {
            payload["messages"]!.AsArray().Add(AssistantCall("echo_m" + i, "execute_command", command: "echo mill " + i));
            payload["messages"]!.AsArray().Add(ToolResult("echo_m" + i, new string('x', 400)));
        }

        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
        Assert.False(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));
    }

    [Fact]
    public void Compact_does_not_reclassify_a_closed_loop_as_a_mill()
    {
        var payload = VisionUiLoop(rounds: 40);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        var haltBeforeCompact = LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted);
        Assert.False(haltBeforeCompact);
        Assert.True(LocalHistoryCompaction.TryCompactPayload(payload, force: true));
        Assert.False(haltBeforeCompact);
    }

    [Fact]
    public void Closed_loop_halts_after_the_omit_ceiling()
    {
        var payload = ClosedLoopCarRacing(rounds: 80);
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        var tight = PortForwardingRules.Defaults with { ClosedLoopRunawayOmittedResults = 128 };
        Assert.True(omitted >= 128);
        Assert.True(omitted < LocalToolResultClearing.ClosedLoopRunawayOmittedResults);
        Assert.True(LocalChatPayloadSignals.RecentToolsAreClosedLoop(payload));
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted, tight));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    [Fact]
    public void Rapid_closed_loop_churn_trips_after_a_few_sub_three_second_turns()
    {
        Assert.Equal(0, LocalToolResultClearing.NextRapidChurnStreak(
            LocalToolResultClearing.RunawayOmittedResults,
            TimeSpan.FromSeconds(4),
            3));
        Assert.Equal(1, LocalToolResultClearing.NextRapidChurnStreak(
            LocalToolResultClearing.RunawayOmittedResults,
            TimeSpan.FromSeconds(1.5),
            0));
        Assert.False(LocalToolResultClearing.ShouldHaltAsRapidChurn(3));
        Assert.True(LocalToolResultClearing.ShouldHaltAsRapidChurn(
            LocalToolResultClearing.RapidChurnConsecutive));
        Assert.Equal(0, LocalToolResultClearing.NextRapidChurnStreak(
            LocalToolResultClearing.RunawayOmittedResults - 1,
            TimeSpan.FromSeconds(1),
            3));
    }

    [Fact]
    public void Unique_echo_mill_still_trips_the_omit_brake()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "probe" }
        };
        for (var i = 0; i < 50; i++)
        {
            messages.Add(AssistantCall("echo_" + i, "execute_command", command: "echo " + i));
            messages.Add(ToolResult("echo_" + i, new string('x', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var omitted = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(omitted >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted));
    }

    private static JsonObject ScreenshotOnlyLoop(int rounds)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "watch the game window" }
        };
        for (var i = 0; i < rounds; i++)
        {
            messages.Add(AssistantCall("shot_" + i, "take_screenshot"));
            messages.Add(ToolShot("shot_" + i, "shot" + i, "frame " + new string('f', 400)));
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject PythonObserveLoop(int rounds)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "run the game and watch it" }
        };
        for (var i = 0; i < rounds; i++)
        {
            messages.Add(AssistantCall("py_" + i, "execute_command", command: "python " + CarRacing));
            messages.Add(ToolShot("py_" + i, "shot" + i, "FAIL frame " + new string('f', 400)));
            messages.Add(AssistantCall("shot_" + i, "take_screenshot"));
            messages.Add(ToolShot("shot_" + i, "ui" + i, "frame " + new string('f', 400)));
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject VisionUiLoop(int rounds)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix the Diagnostics banner" }
        };
        for (var i = 0; i < rounds; i++)
        {
            messages.Add(AssistantCall("edit_" + i, "replace_in_file", path: @"C:\AI_Workbench\Workspace\FluxMux.Avalonia\Views\MainWindow.axaml"));
            messages.Add(ToolResult("edit_" + i, "updated " + new string('e', 400)));
            messages.Add(AssistantCall("shot_" + i, "take_screenshot"));
            messages.Add(ToolShot("shot_" + i, "ui" + i, "frame " + new string('f', 400)));
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject ClosedLoopCarRacing(int rounds)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix the crash in car_racing.py" }
        };
        for (var i = 0; i < rounds; i++)
        {
            messages.Add(AssistantCall("edit_" + i, "edit", path: CarRacing));
            messages.Add(ToolResult("edit_" + i, "updated " + new string('e', 400)));
            messages.Add(AssistantCall("read_" + i, "read", path: CarRacing));
            messages.Add(ToolResult("read_" + i, "source " + new string('r', 400)));
            messages.Add(AssistantCall("py_" + i, "execute_command", command: "python " + CarRacing));
            messages.Add(ToolShot("py_" + i, "shot" + i, "FAIL frame " + new string('f', 400)));
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject PayloadWithAssistantCalls(params JsonObject[] calls)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "do the work" }
        };
        foreach (var call in calls)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray { call }
            });
            messages.Add(new JsonObject { ["role"] = "tool", ["content"] = "ok" });
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject ShellCall(string name, string command)
        => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = "{\"command\":\"" + command + "\"}"
            }
        };

    private static JsonObject AssistantCall(
        string id,
        string name,
        string? path = null,
        string? command = null,
        JsonObject? extra = null)
    {
        var args = extra?.DeepClone().AsObject() ?? new JsonObject();
        if (!string.IsNullOrWhiteSpace(path))
        {
            args["path"] = path;
        }

        if (!string.IsNullOrWhiteSpace(command))
        {
            args["command"] = command;
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = args.ToJsonString()
                    }
                }
            }
        };
    }

    private static JsonObject ToolResult(string id, string content)
        => new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = content
        };

    private static JsonObject ToolShot(string id, string data, string text)
        => new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = data
                    }
                }
            }
        };
}
