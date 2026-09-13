using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalSessionArtifactPolicyTests
{
    [Theory]
    [InlineData("debug_overlay.png", true)]
    [InlineData("projection_diagnostics.md", true)]
    [InlineData("wireframe-dump.json", true)]
    [InlineData("game.js", false)]
    [InlineData("MainWindow.axaml", false)]
    public void IsDumpNamed_matches_analysis_files_not_project_source(string path, bool dump)
    {
        Assert.Equal(dump, LocalSessionArtifactPolicy.IsDumpNamed(path));
        Assert.Equal(path.EndsWith(".js", StringComparison.Ordinal)
            || path.EndsWith(".axaml", StringComparison.Ordinal),
            LocalSessionArtifactPolicy.IsSourcePath(path));
    }

    [Fact]
    public void Apply_keeps_an_observation_screenshot_and_omits_later_dump_pictures()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "find the visual defect" }
        };
        messages.Add(Call("shot_0", "browser_action"));
        messages.Add(Picture("shot_0", "CLINE_SHOT"));
        for (var i = 0; i < 5; i++)
        {
            messages.Add(Call("dump_" + i, "write_to_file", "debug_overlay_" + i + ".png"));
            messages.Add(Picture("dump_" + i, "DUMP_SHOT_" + i));
            messages.Add(Call("read_" + i, "read_file", "debug_overlay_" + i + ".png"));
            messages.Add(Picture("read_" + i, "DUMP_READ_" + i));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var applied = LocalSessionArtifactPolicy.Apply(payload);
        Assert.True(applied.Omitted >= 5);
        LocalToolResultClearing.ClearOlderResults(payload);
        LocalChatPayloadSignals.KeepMostRecentImages(payload);

        var json = payload.ToJsonString();
        Assert.Contains("CLINE_SHOT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DUMP_SHOT_", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DUMP_READ_", json, StringComparison.Ordinal);
        Assert.Contains(LocalSessionArtifactPolicy.ForwardNote, json, StringComparison.Ordinal);
        Assert.DoesNotContain("new Cline task", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not interrogate", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_omits_session_created_markdown_and_keeps_a_new_source_file()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix the projection" }
        };
        messages.Add(Call("src_0", "write_to_file", "camera.js"));
        messages.Add(Text("src_0", "export function project() { return 1; } " + new string('s', 400)));
        messages.Add(Call("note_0", "write_to_file", "projection_analysis.md"));
        messages.Add(Text("note_0", "# vanishing lines " + new string('n', 400)));

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(1, LocalSessionArtifactPolicy.Apply(payload).Omitted);
        var json = payload.ToJsonString();
        Assert.Contains("export function project", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vanishing lines", json, StringComparison.Ordinal);
        Assert.Contains("session-created diagnostic file", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_does_not_treat_replace_on_existing_source_as_a_dump()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix camera.js" }
        };
        messages.Add(Call("rep_0", "replace_in_file", "camera.js"));
        messages.Add(Text("rep_0", "CURRENT CAMERA " + new string('c', 400)));

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(0, LocalSessionArtifactPolicy.Apply(payload).Omitted);
        Assert.Contains("CURRENT CAMERA", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_asks_for_one_change_after_repeated_edits_of_the_same_file()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix camera.js" }
        };
        for (var i = 0; i < LocalSessionArtifactPolicy.EditLoopThreshold; i++)
        {
            messages.Add(Call("rep_" + i, "replace_in_file", "camera.js"));
            messages.Add(Text("rep_" + i, "edit " + i + " " + new string('e', 40)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var applied = LocalSessionArtifactPolicy.Apply(payload);
        Assert.Equal(0, applied.Omitted);
        Assert.Equal(LocalSessionArtifactPolicy.EditLoopThreshold, applied.HottestEdits);
        Assert.Contains(LocalSessionArtifactPolicy.EditLoopNote, payload.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(LocalSessionArtifactPolicy.ForwardNote, payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "inspect" }
        };
        messages.Add(Call("dump_0", "write_to_file", "debug_overlay.png"));
        messages.Add(Picture("dump_0", "DUMP"));

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(1, LocalSessionArtifactPolicy.Apply(payload).Omitted);
        Assert.Equal(0, LocalSessionArtifactPolicy.Apply(payload).Omitted);
    }

    [Fact]
    public void Filter_drops_dump_pictures_and_keeps_agent_file_writes()
    {
        var message = new JsonObject
        {
            ["tool_calls"] = new JsonArray
            {
                Outgoing("write_to_file", "debug_overlay.png"),
                Outgoing("write_to_file", "camera.js"),
                Outgoing("write_to_file", "overlay.js"),
                Outgoing("write_to_file", "package.json"),
                Outgoing("write_to_file", "README.md"),
                Outgoing("replace_in_file", "camera.js")
            }
        };

        Assert.Equal(1, LocalSessionArtifactPolicy.FilterMessageToolCalls(message, artifacts: null));
        var json = message.ToJsonString();
        Assert.Contains("camera.js", json, StringComparison.Ordinal);
        Assert.Contains("overlay.js", json, StringComparison.Ordinal);
        Assert.Contains("package.json", json, StringComparison.Ordinal);
        Assert.Contains("README.md", json, StringComparison.Ordinal);
        Assert.Contains("replace_in_file", json, StringComparison.Ordinal);
        Assert.DoesNotContain("debug_overlay.png", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_caps_new_writes_per_reply()
    {
        var calls = new JsonArray();
        for (var i = 0; i < LocalSessionArtifactPolicy.MaxNewWritesPerReply + 1; i++)
        {
            calls.Add(Outgoing("write_to_file", "file" + i + ".js"));
        }

        var message = new JsonObject { ["tool_calls"] = calls };
        Assert.Equal(1, LocalSessionArtifactPolicy.FilterMessageToolCalls(message, artifacts: null));
        var json = message.ToJsonString();
        Assert.Contains("file0.js", json, StringComparison.Ordinal);
        Assert.Contains("file" + (LocalSessionArtifactPolicy.MaxNewWritesPerReply - 1) + ".js", json, StringComparison.Ordinal);
        Assert.DoesNotContain("file" + LocalSessionArtifactPolicy.MaxNewWritesPerReply + ".js", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_drops_a_bulk_dump_shell_call_and_keeps_a_normal_write()
    {
        var dump = new JsonObject
        {
            ["tool_calls"] = new JsonArray
            {
                Outgoing("execute_command", command: "for ($i=0; $i -lt 200; $i++) { Set-Content \"debug_$i.png\" \"x\" }")
            }
        };
        Assert.Equal(1, LocalSessionArtifactPolicy.FilterMessageToolCalls(dump, artifacts: null));
        Assert.Equal(LocalSessionArtifactPolicy.DroppedWritesNote, dump["content"]?.ToString());
        Assert.Null(dump["tool_calls"]);

        var source = new JsonObject
        {
            ["tool_calls"] = new JsonArray
            {
                Outgoing("execute_command", command: "Set-Content camera.js 'export function project() {}'"),
                Outgoing("execute_command", command: "foreach ($f in Get-ChildItem *.cs) { dotnet format $f }")
            }
        };
        Assert.Equal(0, LocalSessionArtifactPolicy.FilterMessageToolCalls(source, artifacts: null));
        var json = source.ToJsonString();
        Assert.Contains("camera.js", json, StringComparison.Ordinal);
        Assert.Contains("dotnet format", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_keeps_a_new_project_config_file()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "add a package manifest" }
        };
        messages.Add(Call("pkg_0", "write_to_file", "package.json"));
        messages.Add(Text("pkg_0", "{ \"name\": \"game\" } " + new string('j', 400)));

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(0, LocalSessionArtifactPolicy.Apply(payload).Omitted);
        Assert.Contains("game", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_halts_after_enough_session_dumps()
    {
        var messages = new JsonArray();
        for (var i = 0; i < LocalSessionArtifactPolicy.HaltThreshold; i++)
        {
            messages.Add(Call("dump_" + i, "write_to_file", "debug_" + i + ".png"));
        }

        var artifacts = LocalSessionArtifactPolicy.Inspect(messages);
        Assert.True(artifacts.ShouldHalt);
        Assert.Equal(LocalSessionArtifactPolicy.HaltThreshold, artifacts.ArtifactCount);
    }

    [Fact]
    public void Inspect_reads_xml_write_paths()
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "<write_to_file><path>debug_overlay.png</path><content>x</content></write_to_file>"
            }
        };

        var artifacts = LocalSessionArtifactPolicy.Inspect(messages);
        Assert.True(artifacts.IsArtifact("debug_overlay.png"));
    }

    private static JsonObject Outgoing(string name, string? path = null, string? command = null)
    {
        var args = new JsonObject();
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
            ["id"] = "call_" + name,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = args.ToJsonString()
            }
        };
    }

    private static JsonObject Call(string id, string name, string? path = null)
    {
        var args = new JsonObject();
        if (!string.IsNullOrWhiteSpace(path))
        {
            args["path"] = path;
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

    private static JsonObject Text(string id, string content)
        => new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = content
        };

    private static JsonObject Picture(string id, string data)
        => new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = new JsonArray
            {
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
