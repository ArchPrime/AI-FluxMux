using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalChatTemplateGuardTests
{
    [Fact]
    public void Leaves_a_normal_user_turn_alone()
    {
        var payload = Payload(
            Msg("system", "You are a coding assistant."),
            Msg("user", "Refactor Foo."),
            Msg("assistant", "I will edit Foo."));

        Assert.Equal(0, LocalChatTemplateGuard.EnsureUserQuery(payload));
        Assert.Equal(3, Messages(payload).Count);
    }

    [Fact]
    public void Inserts_after_system_when_only_tools_remain()
    {
        var payload = Payload(
            Msg("system", "You are a coding assistant."),
            Msg("assistant", "Calling read."),
            Msg("tool", "file contents"));

        Assert.Equal(1, LocalChatTemplateGuard.EnsureUserQuery(payload));
        var messages = Messages(payload);
        Assert.Equal(4, messages.Count);
        Assert.Equal("user", messages[1]!["role"]?.ToString());
        Assert.Equal(LocalChatTemplateGuard.SyntheticUserText, messages[1]!["content"]?.ToString());
    }

    [Fact]
    public void Treats_tool_response_user_rows_as_missing_query()
    {
        var payload = Payload(
            Msg("system", "sys"),
            Msg("user", "<tool_response>\nhello\n</tool_response>"));

        Assert.Equal(1, LocalChatTemplateGuard.EnsureUserQuery(payload));
        Assert.True(LocalChatTemplateGuard.HasRealUserQuery(Messages(payload)));
    }

    [Fact]
    public void Keeps_a_real_user_query_even_if_later_rows_are_tool_wrappers()
    {
        var payload = Payload(
            Msg("user", "Fix the layout."),
            Msg("assistant", "Reading."),
            Msg("user", "<tool_response>ok</tool_response>"));

        Assert.Equal(0, LocalChatTemplateGuard.EnsureUserQuery(payload));
        Assert.Equal(3, Messages(payload).Count);
    }

    private static JsonObject Payload(params JsonObject[] messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(message);
        }

        return new JsonObject { ["messages"] = array };
    }

    private static JsonArray Messages(JsonObject payload)
        => (JsonArray)payload["messages"]!;

    private static JsonObject Msg(string role, string content)
        => new() { ["role"] = role, ["content"] = content };
}
