using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalToolResultDedupTests
{
    private static readonly string FileBody = "class Settings { }\n" + new string('x', 3000);

    [Fact]
    public void A_repeated_identical_read_is_replaced_and_the_first_copy_kept()
    {
        var payload = Chat(Read("c1", FileBody), Read("c2", "other file " + new string('y', 3000)), Read("c3", FileBody));

        Assert.Equal(1, LocalToolResultDedup.Apply(payload));
        Assert.Equal(FileBody, Content(payload, "c1"));
        Assert.Equal(LocalToolResultDedup.Pointer, Content(payload, "c3"));
        Assert.StartsWith("other file", Content(payload, "c2"));
    }

    [Fact]
    public void A_file_that_changed_in_between_is_not_matched()
    {
        var payload = Chat(Read("c1", FileBody), Read("c2", FileBody + "// edited"));
        Assert.Equal(0, LocalToolResultDedup.Apply(payload));
    }

    [Fact]
    public void Short_results_are_left_alone()
    {
        var payload = Chat(Read("c1", "Build succeeded."), Read("c2", "Build succeeded."));
        Assert.Equal(0, LocalToolResultDedup.Apply(payload));
    }

    [Fact]
    public void If_the_earlier_copy_was_stubbed_the_later_copy_stays_in_full()
    {
        // Omit runs first and stubs the older body, so its text no longer matches.
        var payload = Chat(Read("c1", "[older tool output omitted]"), Read("c2", FileBody));
        Assert.Equal(0, LocalToolResultDedup.Apply(payload));
        Assert.Equal(FileBody, Content(payload, "c2"));
    }

    [Fact]
    public void A_result_with_a_picture_is_never_replaced()
    {
        var withPicture = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = FileBody },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } }
        };
        var payload = Chat(Read("c1", FileBody));
        payload["messages"]!.AsArray().Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c2", ["content"] = withPicture });

        Assert.Equal(0, LocalToolResultDedup.Apply(payload));
    }

    [Fact]
    public void The_rewrite_is_the_same_every_turn_so_the_prompt_start_does_not_change()
    {
        var turn1 = Chat(Read("c1", FileBody), Read("c2", FileBody));
        var turn2 = Chat(Read("c1", FileBody), Read("c2", FileBody), Read("c3", "new " + new string('z', 2000)));
        LocalToolResultDedup.Apply(turn1);
        LocalToolResultDedup.Apply(turn2);

        var first = turn1["messages"]!.ToJsonString();
        var second = turn2["messages"]!.ToJsonString();
        Assert.StartsWith(first.TrimEnd(']'), second);
    }

    private static JsonObject Chat(params JsonObject[] results)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "rules" },
            new JsonObject { ["role"] = "user", ["content"] = "do the task" }
        };
        foreach (var result in results)
        {
            var id = result["tool_call_id"]!.GetValue<string>();
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "read_files", ["arguments"] = "{\"paths\":[\"a.cs\"]}" }
                    }
                }
            });
            messages.Add(result);
        }

        return new JsonObject { ["messages"] = messages };
    }

    private static JsonObject Read(string id, string body)
        => new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = body };

    private static string Content(JsonObject payload, string id)
    {
        foreach (var node in payload["messages"]!.AsArray())
        {
            if (node is JsonObject m && m["tool_call_id"]?.GetValue<string>() == id)
            {
                return m["content"]!.GetValue<string>();
            }
        }

        return string.Empty;
    }
}
