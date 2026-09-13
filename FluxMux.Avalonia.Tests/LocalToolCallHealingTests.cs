using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalToolCallHealingTests
{
    [Fact]
    public void CollectDeclaredToolNames_reads_openai_and_legacy_lists()
    {
        var payload = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "execute_command" }
                }
            },
            ["functions"] = new JsonArray
            {
                new JsonObject { ["name"] = "read_file" }
            }
        };

        var names = LocalToolCallHealing.CollectDeclaredToolNames(payload);
        Assert.Contains("execute_command", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public void Heal_promotes_qwen_json_tool_xml_and_strips_it_from_content()
    {
        var root = Completion(
            "<tool_call>{\"name\":\"execute_command\",\"arguments\":{\"command\":\"npm test\"}}</tool_call>");

        Assert.True(LocalToolCallHealing.HealCompletion(root, ["execute_command"]));
        var message = root["choices"]![0]!["message"]!.AsObject();
        Assert.Equal("tool_calls", root["choices"]![0]!["finish_reason"]!.ToString());
        Assert.Equal("execute_command", message["tool_calls"]![0]!["function"]!["name"]!.ToString());
        Assert.Contains("npm test", message["tool_calls"]![0]!["function"]!["arguments"]!.ToString());
        Assert.True(string.IsNullOrWhiteSpace(message["content"]?.ToString()));
    }

    [Fact]
    public void Heal_does_not_invent_a_tool_the_client_app_did_not_declare()
    {
        var root = Completion(
            "<tool_call>{\"name\":\"execute_command\",\"arguments\":{\"command\":\"rm -rf /\"}}</tool_call>");

        Assert.False(LocalToolCallHealing.HealCompletion(root, ["read_file"]));
        Assert.Null(root["choices"]![0]!["message"]!["tool_calls"]);
        Assert.Contains("execute_command", root["choices"]![0]!["message"]!["content"]!.ToString());
    }

    [Fact]
    public void Heal_skips_a_call_rehearsed_inside_think()
    {
        var root = Completion(
            "<think><tool_call>{\"name\":\"execute_command\",\"arguments\":{\"command\":\"pwd\"}}</tool_call></think>ok");

        Assert.False(LocalToolCallHealing.HealCompletion(root, ["execute_command"]));
        Assert.Contains("ok", root["choices"]![0]!["message"]!["content"]!.ToString());
        Assert.Null(root["choices"]![0]!["message"]!["tool_calls"]);
    }

    [Fact]
    public void Heal_promotes_cline_xml_when_that_name_is_declared()
    {
        var root = Completion("<execute_command><command>npm test</command></execute_command>");

        Assert.True(LocalToolCallHealing.HealCompletion(root, ["execute_command"]));
        var args = root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.ToString();
        Assert.Contains("npm test", args);
    }

    [Fact]
    public void Heal_coerces_invalid_argument_json()
    {
        var root = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "",
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "call_1",
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = "execute_command",
                                    ["arguments"] = "{command: 'npm test'}"
                                }
                            }
                        }
                    }
                }
            }
        };

        Assert.True(LocalToolCallHealing.HealCompletion(root, ["execute_command"], allowParallel: true, out var stats));
        Assert.True(stats.Coerced >= 1);
        var args = root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.ToString();
        Assert.Contains("\"command\"", args);
        Assert.Contains("npm test", args);
        Assert.True(JsonNode.Parse(args) is JsonObject);
    }

    [Fact]
    public void Heal_dedupes_identical_calls_and_caps_when_parallel_is_off()
    {
        var root = Completion(
            "<tool_call>{\"name\":\"execute_command\",\"arguments\":{\"command\":\"npm test\"}}</tool_call>"
            + "<tool_call>{\"name\":\"execute_command\",\"arguments\":{\"command\":\"npm test\"}}</tool_call>"
            + "<tool_call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}}</tool_call>");

        Assert.True(LocalToolCallHealing.HealCompletion(root, ["execute_command", "read_file"], allowParallel: false));
        var calls = root["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
        Assert.Single(calls);
        Assert.Equal("execute_command", calls[0]!["function"]!["name"]!.ToString());
    }

    [Fact]
    public void Heal_leaves_well_formed_calls_byte_identical()
    {
        var args = "{\"command\":\"npm test\"}";
        var root = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["finish_reason"] = "tool_calls",
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "",
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "call_keep",
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = "execute_command",
                                    ["arguments"] = args
                                }
                            }
                        }
                    }
                }
            }
        };

        Assert.False(LocalToolCallHealing.HealCompletion(root, ["execute_command"]));
        Assert.Equal("call_keep", root["choices"]![0]!["message"]!["tool_calls"]![0]!["id"]!.ToString());
        Assert.Equal(args, root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.ToString());
    }

    [Fact]
    public async Task Stream_promotes_split_xml_and_does_not_leak_the_tags()
    {
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Sure.\\n<tool_call>\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"name\\\":\\\"execute_command\\\",\\\"arguments\\\":{\\\"command\\\":\\\"npm test\\\"}}\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"</tool_call>\"},\"finish_reason\":\"stop\"}]}\n"
            + "data: [DONE]\n"));
        var downstream = new MemoryStream();

        var copy = await OpenAiStreamTelemetryProxy.CopyAsync(
            upstream,
            downstream,
            tracker: null,
            localReasoningMode: "Off",
            CancellationToken.None,
            onUpstreamBytes: null,
            declaredTools: ["execute_command"]);

        var text = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.True(copy.Healed);
        Assert.Contains("tool_calls", text, System.StringComparison.Ordinal);
        Assert.Contains("execute_command", text, System.StringComparison.Ordinal);
        Assert.Contains("npm test", text, System.StringComparison.Ordinal);
        Assert.Contains("Sure.", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_forwards_native_tool_calls_and_drops_held_xml()
    {
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"<tool_call>\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"execute_command\",\"arguments\":\"{\\\"command\\\":\\\"ls\\\"}\"}}]}}]}\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n"
            + "data: [DONE]\n"));
        var downstream = new MemoryStream();

        var copy = await OpenAiStreamTelemetryProxy.CopyAsync(
            upstream,
            downstream,
            tracker: null,
            localReasoningMode: null,
            CancellationToken.None,
            onUpstreamBytes: null,
            declaredTools: ["execute_command"]);

        var text = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.False(copy.Healed);
        Assert.Contains("call_1", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeJsonBody_heals_when_tools_are_declared()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<tool_call>{\\\"name\\\":\\\"read_file\\\",\\\"arguments\\\":{\\\"path\\\":\\\"a.cs\\\"}}</tool_call>\"}}]}");
        var (_, thinking, healed) = LocalEndpointResponseSanitizer.SanitizeJsonBody(
            body,
            "Off",
            ["read_file"]);
        Assert.False(thinking);
        Assert.True(healed);
    }

    private static JsonObject Completion(string content)
        => new()
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content
                    }
                }
            }
        };
}
