using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Hail Mary follows the Client app that sent the chat, named by its own system prompt, because
/// the Client app dropdown can say Cline while the stuck chat is in Harness.
/// </summary>
public sealed class ClientAppDetectorTests
{
    private static JsonObject Payload(JsonNode systemContent) => new()
    {
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemContent },
            new JsonObject { ["role"] = "user", ["content"] = "hi" },
        },
    };

    [Fact]
    public void Harness_is_named_from_its_system_prompt_parts()
    {
        // Harness 0.2.3 sends the system prompt as text parts; this is its first one.
        var payload = Payload(new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "You are an AI agent powered by DeepSeek Harness." },
            new JsonObject { ["type"] = "text", ["text"] = "You are a coding agent powered by the local model." },
        });

        Assert.Equal(ClientAppDetector.Harness, ClientAppDetector.FromPayload(payload));
    }

    [Fact]
    public void Cline_is_named_from_its_system_prompt()
        => Assert.Equal(ClientAppDetector.Cline, ClientAppDetector.FromPayload(
            Payload("You are Cline, a highly skilled software engineer with extensive knowledge.")));

    [Fact]
    public void An_unknown_client_falls_back_to_the_dropdown()
    {
        Assert.Null(ClientAppDetector.FromPayload(Payload("You are a helpful assistant.")));
        Assert.Null(ClientAppDetector.FromPayload(new JsonObject { ["messages"] = new JsonArray() }));
    }
}
