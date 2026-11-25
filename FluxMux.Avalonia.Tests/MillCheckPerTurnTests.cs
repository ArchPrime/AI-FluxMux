using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Mill check counts tools since the person's latest message, and shell reads count as reading.
/// Live test 25 Nov: a chat with 304 older tool results, a fresh message, then 40 shell reads
/// (<c>type file.txt</c>) was paused because Mill check counted the whole chat and the lookback
/// did not recognise shell reads.
/// </summary>
public sealed class MillCheckPerTurnTests
{
    private static JsonObject Call(int n, string command)
        => new()
        {
            ["role"] = "assistant",
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "c" + n,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "run_command",
                        ["arguments"] = new JsonObject { ["command"] = command }.ToJsonString(),
                    },
                },
            },
        };

    private static JsonObject Result(int n, string text)
        => new() { ["role"] = "tool", ["tool_call_id"] = "c" + n, ["content"] = text };

    /// <summary>Earlier turns, then a fresh message from the person, then <paramref name="latest"/> calls.</summary>
    private static JsonObject LongChatThen(int earlierTools, int latestTools, Func<int, string> latest)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Build the feature." } };
        var n = 0;
        for (; n < earlierTools; n++)
        {
            messages.Add(Call(n, "python build_step_" + n + ".py"));
            messages.Add(Result(n, "step " + n + " ok"));
        }

        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Done." });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Now look round the project and tell me what is left." });
        for (var i = 0; i < latestTools; i++, n++)
        {
            messages.Add(Call(n, latest(i)));
            messages.Add(Result(n, "contents " + i));
        }

        return new JsonObject { ["messages"] = messages };
    }

    [Fact]
    public void A_long_chat_does_not_pause_the_first_tools_after_a_fresh_message()
    {
        // 300 earlier tools, then 20 echoes (not useful work): under Mill check's 32 since the message.
        var chat = LongChatThen(300, 20, i => "echo probe " + i);
        Assert.Equal(20, LocalChatPayloadSignals.CountToolResultsSinceHumanMessage(chat));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(chat, PortForwardingRules.Defaults with { ObserveOnlyMillEnabled = false }));
    }

    [Fact]
    public void Shell_reads_past_mill_check_count_as_investigation()
    {
        var chat = LongChatThen(300, 40, i => "type src\\part" + i + "\\notes.txt");
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(chat, PortForwardingRules.Defaults));
    }

    [Fact]
    public void Echoes_past_mill_check_still_pause()
    {
        var chat = LongChatThen(0, 40, i => "echo probe " + i);
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(chat, PortForwardingRules.Defaults));
    }

    [Fact]
    public void The_same_shell_read_coming_back_is_still_a_mill()
    {
        var chat = LongChatThen(0, 40, i => i % 2 == 0 ? "dir" : "type notes.txt");
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(chat, PortForwardingRules.Defaults));
    }

    [Theory]
    [InlineData("cat src/main.js", true)]
    [InlineData("type C:\\proj\\notes.txt", true)]
    [InlineData("dir /s *.cs", true)]
    [InlineData("ls -la && cat package.json", true)]
    [InlineData("cd src && grep -n draw main.js | head -20", true)]
    [InlineData("git status", true)]
    [InlineData("git log --oneline -5", true)]
    [InlineData("Get-Content app.ps1 | Select-Object -First 40", true)]
    [InlineData("rg TODO", true)]
    [InlineData("cd src", false)]
    [InlineData("git commit -m wip", false)]
    [InlineData("cat a.txt > b.txt", false)]
    [InlineData("find . -name *.tmp -delete", false)]
    [InlineData("ls && rm -rf build", false)]
    [InlineData("echo hello", false)]
    [InlineData("python main.py", false)]
    public void Shell_read_recognition(string command, bool isRead)
        => Assert.Equal(isRead, LocalChatPayloadSignals.IsShellReadFingerprint(command));

    [Fact]
    public void A_small_prompt_on_a_long_load_profile_gets_no_minute_long_estimate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-prefill-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            // Long-load profile (Context 163,840 is above 131,072), no RAM spill, cold, 87 tokens.
            var scenario = new ModelScenario(false, false, true, true, false, false, true, false, "Normal", 163840, 163840, 87, 0, 0.0, false);
            Assert.True(supervisor.PredictPrefillSeconds(scenario, 87, new PrefillPerformanceModel(Path.Combine(dir, "prefill_model.json"))) < 30);
            Assert.True(supervisor.PredictPrefillSeconds(scenario, 87, null) < 30);
            // A large prompt keeps the conservative floor.
            Assert.True(supervisor.PredictPrefillSeconds(scenario, 200000, null) >= 60);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
