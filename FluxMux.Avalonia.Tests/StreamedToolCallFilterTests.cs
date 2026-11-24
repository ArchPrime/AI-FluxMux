using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// A streamed tool call arrives in fragments (id + name first, argument text after). The
/// outgoing-write filter used to judge each fragment alone, dropping "write with no path yet"
/// and passing the nameless argument fragments, so the Client app assembled a call with no name
/// and broken arguments (Harness: 'Tool call "": Unexpected non-whitespace character after JSON').
/// </summary>
public sealed class StreamedToolCallFilterTests
{
    private static string Chunk(JsonObject delta, string? finish = null) => "data: " + new JsonObject
    {
        ["id"] = "c1",
        ["object"] = "chat.completion.chunk",
        ["choices"] = new JsonArray
        {
            new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish },
        },
    }.ToJsonString();

    private static JsonObject Fragment(int index, string? id, string? name, string args)
    {
        var function = new JsonObject { ["arguments"] = args };
        if (name is not null)
        {
            function["name"] = name;
        }

        var call = new JsonObject { ["index"] = index, ["function"] = function };
        if (id is not null)
        {
            call["id"] = id;
            call["type"] = "function";
        }

        return new JsonObject { ["tool_calls"] = new JsonArray(call) };
    }

    /// <summary>Runs the stream through the session and assembles it as a Client app would.</summary>
    private static (List<JsonObject> Calls, string Content, string? Finish, LocalToolCallHealSession Session) Run(params string[] lines)
    {
        var session = new LocalToolCallHealSession(["write", "read", "bash"]);
        var sent = new List<string>();
        foreach (var line in lines)
        {
            var decision = session.ApplySseLine(line);
            sent.AddRange(decision.ExtraBefore);
            if (decision.Forward)
            {
                sent.Add(decision.Line);
            }
        }

        var calls = new SortedDictionary<int, JsonObject>();
        var content = string.Empty;
        string? finish = null;
        foreach (var line in sent.Where(l => l.StartsWith("data: {")))
        {
            var choice = JsonNode.Parse(line[6..])!["choices"]![0]!;
            if (choice["finish_reason"]?.ToString() is { Length: > 0 } f)
            {
                finish = f;
            }

            if (choice["delta"]?["content"]?.ToString() is { Length: > 0 } text)
            {
                content += text;
            }

            foreach (var part in (choice["delta"]?["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var index = part["index"]!.GetValue<int>();
                if (!calls.TryGetValue(index, out var call))
                {
                    call = new JsonObject { ["name"] = "", ["arguments"] = "" };
                    calls[index] = call;
                }

                call["name"] = call["name"]!.ToString() + (part["function"]?["name"]?.ToString() ?? "");
                call["arguments"] = call["arguments"]!.ToString() + (part["function"]?["arguments"]?.ToString() ?? "");
            }
        }

        return (calls.Values.ToList(), content, finish, session);
    }

    [Fact]
    public void A_streamed_write_reaches_the_Client_app_whole()
    {
        var (calls, _, finish, session) = Run(
            Chunk(Fragment(0, "call_1", "write", "")),
            Chunk(Fragment(0, null, null, "{\"path\":\"src/app.py\",")),
            Chunk(Fragment(0, null, null, "\"content\":\"print(1)\"}")),
            Chunk(new JsonObject(), "tool_calls"),
            "data: [DONE]");

        var call = Assert.Single(calls);
        Assert.Equal("write", call["name"]!.ToString());
        Assert.Equal("src/app.py", JsonNode.Parse(call["arguments"]!.ToString())!["path"]!.ToString());
        Assert.Equal("tool_calls", finish);
        Assert.False(session.BlockedWrites);
    }

    [Fact]
    public void A_blocked_write_is_dropped_whole_and_no_nameless_fragment_leaks()
    {
        var (calls, content, finish, session) = Run(
            Chunk(Fragment(0, "call_1", "write", "")),
            Chunk(Fragment(0, null, null, "{\"path\":\"shot.png\",")),
            Chunk(Fragment(0, null, null, "\"content\":\"...\"}")),
            Chunk(new JsonObject(), "tool_calls"),
            "data: [DONE]");

        Assert.Empty(calls);
        Assert.True(session.BlockedWrites);
        Assert.Equal("stop", finish);
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public void Parallel_calls_keep_their_own_arguments_and_only_the_blocked_one_goes()
    {
        var (calls, _, finish, _) = Run(
            Chunk(Fragment(0, "call_1", "read", "")),
            Chunk(Fragment(1, "call_2", "write", "")),
            Chunk(Fragment(0, null, null, "{\"path\":\"a.txt\"}")),
            Chunk(Fragment(1, null, null, "{\"path\":\"img.png\",\"content\":\"x\"}")),
            Chunk(new JsonObject(), "tool_calls"),
            "data: [DONE]");

        var call = Assert.Single(calls);
        Assert.Equal("read", call["name"]!.ToString());
        Assert.Equal("a.txt", JsonNode.Parse(call["arguments"]!.ToString())!["path"]!.ToString());
        Assert.Equal("tool_calls", finish);
    }

    [Fact]
    public void A_stream_that_ends_without_a_finish_reason_still_delivers_the_call()
    {
        var (calls, _, finish, _) = Run(
            Chunk(Fragment(0, "call_1", "bash", "")),
            Chunk(Fragment(0, null, null, "{\"command\":\"ls\"}")),
            "data: [DONE]");

        var call = Assert.Single(calls);
        Assert.Equal("bash", call["name"]!.ToString());
        Assert.Equal("ls", JsonNode.Parse(call["arguments"]!.ToString())!["command"]!.ToString());
        Assert.Equal("tool_calls", finish);
    }

    [Fact]
    public void Text_before_a_call_is_still_streamed()
    {
        var (calls, content, _, _) = Run(
            Chunk(new JsonObject { ["content"] = "Let me check." }),
            Chunk(Fragment(0, "call_1", "read", "{\"path\":\"a.txt\"}")),
            Chunk(new JsonObject(), "tool_calls"),
            "data: [DONE]");

        Assert.Equal("Let me check.", content);
        Assert.Single(calls);
    }
}
