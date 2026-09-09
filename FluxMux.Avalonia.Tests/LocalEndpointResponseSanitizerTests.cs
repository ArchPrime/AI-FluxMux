using System.Text;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalEndpointResponseSanitizerTests
{
    [Fact]
    public void SanitizeJsonBody_strips_empty_redacted_thinking_when_reasoning_on()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": "<think> </think>\nHi!"
                }
              }]
            }
            """);

        var (sanitized, changed) = LocalEndpointResponseSanitizer.SanitizeJsonBody(body, "On");

        Assert.True(changed);
        var text = JsonNode.Parse(sanitized)!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Equal("Hi!", text);
    }

    [Fact]
    public void SanitizeJsonBody_strips_nonempty_thinking_when_reasoning_off()
    {
        var thinkBlock = "\x3cthink\x3e" + "secret" + "\x3c/think\x3e";
        var body = Encoding.UTF8.GetBytes("""
            {
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": "Hello"
                }
              }]
            }
            """.Replace("Hello", "Hello " + thinkBlock + " world", System.StringComparison.Ordinal));

        var (sanitized, changed) = LocalEndpointResponseSanitizer.SanitizeJsonBody(body, "Off");

        Assert.True(changed);
        var text = JsonNode.Parse(sanitized)!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Equal("Hello  world", text);
    }

    [Fact]
    public void SanitizeSseLine_strips_empty_thinking_from_stream_delta()
    {
        var line = "data: {\"choices\":[{\"delta\":{\"content\":\"<think> </think>\"}}]}";
        var (sanitized, changed) = LocalEndpointResponseSanitizer.SanitizeSseLine(line, "On");

        Assert.True(changed);
        Assert.Contains("\"content\":\"\"", sanitized, System.StringComparison.Ordinal);
    }
}
