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

    [Theory]
    [InlineData("You are Roo, a highly skilled software engineer with extensive knowledge.", ClientAppDetector.RooCode)]
    [InlineData("You are Kilo, a highly skilled software engineer with extensive knowledge.", ClientAppDetector.KiloCode)]
    [InlineData("You are an AI agent running in OpenCode, a coding agent harness. Help the user.", ClientAppDetector.OpenCode)]
    [InlineData("You are a coding assistant running inside pi, a coding agent harness.", ClientAppDetector.Pi)]
    [InlineData("You are an expert coding assistant operating inside pi, a coding agent harness.", ClientAppDetector.Pi)]
    [InlineData("Act as an expert software developer. Always use best practices when coding.", ClientAppDetector.Aider)]
    [InlineData("<important_rules> You are in agent mode.", ClientAppDetector.Continue)]
    [InlineData("You are a general-purpose AI agent called goose, created by AAIF.", ClientAppDetector.Goose)]
    [InlineData("You are an expert AI programming assistant, working with a user in the VS Code editor.", ClientAppDetector.GitHubCopilot)]
    public void Other_Client_apps_are_named_from_their_system_prompt(string opening, string expected)
        => Assert.Equal(expected, ClientAppDetector.FromPayload(Payload(opening)));

    [Theory]
    [InlineData("Cline/4.1.27", null, ClientAppDetector.Cline)]
    [InlineData("RooCode/3.30.0", null, ClientAppDetector.RooCode)]
    [InlineData("Kilo-Code/5.0.1", null, ClientAppDetector.KiloCode)]
    [InlineData("opencode/latest/1.2.3/opencode", null, ClientAppDetector.OpenCode)]
    [InlineData("OpenAI/JS 5.0.0", "Roo Code", ClientAppDetector.RooCode)]
    public void The_User_Agent_or_X_Title_names_the_Client_app(string userAgent, string? title, string expected)
        => Assert.Equal(expected, ClientAppDetector.FromHeaders(userAgent, title));

    [Fact]
    public void The_header_wins_over_a_replaced_prompt_and_an_SDK_agent_is_not_a_tell()
    {
        // A Roo custom mode can replace the opening; the User-Agent still says Roo Code.
        Assert.Equal(ClientAppDetector.RooCode, ClientAppDetector.FromRequest(Payload("You are my custom reviewer."), "RooCode/3.30.0"));
        // An SDK's own name is shared by many apps, so the prompt decides.
        Assert.Null(ClientAppDetector.FromHeaders("OpenAI/JS 5.0.0", null));
        Assert.Equal(ClientAppDetector.Aider, ClientAppDetector.FromRequest(Payload("Act as an expert software developer."), "OpenAI/Python 1.99"));
    }
}
