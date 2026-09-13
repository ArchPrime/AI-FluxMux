using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalToolResultClearingTests
{
    [Fact]
    public void ClearOlderResults_keeps_the_call_and_the_last_results()
    {
        var payload = ToolLoopPayload(rounds: 20, resultChars: 400);
        var cleared = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.Equal(4, cleared);

        var messages = payload["messages"]!.AsArray();
        var results = messages.OfType<JsonObject>().Where(LocalToolResultClearing.IsToolResult).ToList();
        Assert.Equal(20, results.Count);
        Assert.All(results.Take(4), result =>
            Assert.StartsWith(LocalToolResultClearing.OmittedMark, result["content"]?.ToString()));
        Assert.All(results.Skip(4), result =>
            Assert.DoesNotContain(LocalToolResultClearing.OmittedMark, result["content"]?.ToString() ?? string.Empty));
        Assert.Contains("already ran execute_command: npm test", results[0]["content"]?.ToString());
        Assert.Contains("\"name\":\"execute_command\"", payload.ToJsonString());
    }

    [Fact]
    public void ClearOlderResults_leaves_short_results_alone()
    {
        var payload = ToolLoopPayload(rounds: 8, resultChars: 40);
        Assert.Equal(0, LocalToolResultClearing.ClearOlderResults(payload));
        Assert.DoesNotContain(LocalToolResultClearing.OmittedMark, payload.ToJsonString());
    }

    [Fact]
    public void ClearOlderResults_is_idempotent()
    {
        var payload = ToolLoopPayload(rounds: 18, resultChars: 400);
        Assert.Equal(2, LocalToolResultClearing.ClearOlderResults(payload));
        Assert.Equal(0, LocalToolResultClearing.ClearOlderResults(payload));
    }

    [Fact]
    public void ClearOlderResults_stubs_a_cline_tool_response_user_message()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "inspect the game" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "<execute_command><command>npm test</command></execute_command>"
                },
                new JsonObject { ["role"] = "user", ["content"] = "<tool_response>" + new string('o', 400) + "</tool_response>" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "<execute_command><command>npm test</command></execute_command>"
                },
                new JsonObject { ["role"] = "user", ["content"] = "<tool_response>later</tool_response>" },
                new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                new JsonObject { ["role"] = "user", ["content"] = "<tool_response>keep1 " + new string('k', 20) + "</tool_response>" },
                new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                new JsonObject { ["role"] = "user", ["content"] = "<tool_response>keep2</tool_response>" },
                new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                new JsonObject { ["role"] = "user", ["content"] = "<tool_response>keep3</tool_response>" },
                new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                new JsonObject { ["role"] = "user", ["content"] = "<tool_response>keep4</tool_response>" }
            }
        };
        for (var i = 0; i < 12; i++)
        {
            payload["messages"]!.AsArray().Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "<tool_response>keep_extra_" + i + "</tool_response>"
            });
        }

        Assert.Equal(1, LocalToolResultClearing.ClearOlderResults(payload));
        var firstResult = payload["messages"]!.AsArray().OfType<JsonObject>()
            .First(LocalToolResultClearing.IsToolResult);
        var text = firstResult["content"]?.ToString() ?? string.Empty;
        Assert.StartsWith("<tool_response>" + LocalToolResultClearing.OmittedMark, text);
        Assert.EndsWith("</tool_response>", text);
    }

    [Fact]
    public void ClearOlderResults_tells_the_model_the_user_can_attach_an_omitted_picture()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "inspect" }
        };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "img" + i } }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "img" + i,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = "frame" + i
                        }
                    }
                }
            });
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(4, LocalToolResultClearing.ClearOlderResults(payload));
        var first = payload["messages"]!.AsArray().OfType<JsonObject>()
            .First(LocalToolResultClearing.IsToolResult);
        var text = first["content"]?.ToString() ?? string.Empty;
        Assert.Contains("picture was not forwarded to llama-server", text, StringComparison.Ordinal);
        Assert.Contains("This Client-app chat can continue", text, StringComparison.Ordinal);
        Assert.Contains("attach", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Do not interrogate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearOlderResults_keeps_the_latest_read_of_a_file_still_being_edited()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix MainWindow.axaml" }
        };
        messages.Add(FileCall("read_0", "read_file", "MainWindow.axaml"));
        messages.Add(FileResult("read_0", "OLD FILE " + new string('a', 400)));
        messages.Add(FileCall("read_1", "read_file", "MainWindow.axaml"));
        messages.Add(FileResult("read_1", "CURRENT FILE " + new string('b', 400)));
        for (var i = 0; i < 16; i++)
        {
            messages.Add(FileCall("cmd_" + i, "execute_command", command: "npm test"));
            messages.Add(FileResult("cmd_" + i, new string('x', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.Equal(1, LocalToolResultClearing.ClearOlderResults(payload));
        var json = payload.ToJsonString();
        Assert.Contains("CURRENT FILE", json);
        Assert.DoesNotContain("OLD FILE", json);
        Assert.Contains(LocalToolResultClearing.OmittedMark, json);
    }

    [Fact]
    public void ClearOlderResults_keeps_cline_created_screenshots_through_later_tools()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "find the visual defect" }
        };
        messages.Add(FileCall("shot_0", "browser_action"));
        messages.Add(new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = "shot_0",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = "CLINE_SHOT"
                    }
                }
            }
        });
        for (var i = 0; i < 20; i++)
        {
            messages.Add(FileCall("cmd_" + i, "execute_command", command: "npm test"));
            messages.Add(FileResult("cmd_" + i, new string('x', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalToolResultClearing.ClearOlderResults(payload) >= 1);
        Assert.Contains("CLINE_SHOT", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClearOlderResults_keeps_the_latest_test_output_after_later_shells()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "fix the tests" }
        };
        messages.Add(FileCall("test_0", "execute_command", command: "npm test"));
        messages.Add(FileResult("test_0", "FAIL camera.js " + new string('f', 400)));
        for (var i = 0; i < 20; i++)
        {
            messages.Add(FileCall("echo_" + i, "execute_command", command: "echo " + i));
            messages.Add(FileResult("echo_" + i, new string('x', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        Assert.True(LocalToolResultClearing.ClearOlderResults(payload) >= 1);
        Assert.Contains("FAIL camera.js", payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClearOlderResults_omits_enough_unique_shells_to_trip_runaway()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "probe" }
        };
        for (var i = 0; i < 50; i++)
        {
            messages.Add(FileCall("echo_" + i, "execute_command", command: "echo " + i));
            messages.Add(FileResult("echo_" + i, new string('x', 400)));
        }

        var payload = new JsonObject { ["messages"] = messages };
        var cleared = LocalToolResultClearing.ClearOlderResults(payload);
        Assert.True(cleared >= LocalToolResultClearing.RunawayOmittedResults);
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(payload, cleared));
    }

    [Fact]
    public void CollectAlreadyRanLines_collapses_a_repeated_command()
    {
        var messages = ToolLoopPayload(rounds: 3, resultChars: 20)["messages"]!.AsArray();
        var lines = LocalToolResultClearing.CollectAlreadyRanLines(messages, messages.Count);
        Assert.Equal(new[] { "- execute_command: npm test (3 times)" }, lines);
    }

    private static JsonObject ToolLoopPayload(int rounds, int resultChars)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "run the tests" }
        };
        for (var i = 0; i < rounds; i++)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_" + i,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "execute_command",
                            ["arguments"] = "{\"command\":\"npm test\"}"
                        }
                    }
                }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "call_" + i,
                ["content"] = new string('x', resultChars)
            });
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject FileCall(string id, string name, string? path = null, string? command = null)
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

    private static JsonObject FileResult(string id, string content)
        => new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = content
        };
}
