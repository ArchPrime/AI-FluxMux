using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HttpErrorResponseFormatterTests
{
    [Fact]
    public void FormatHttpErrorDetail_extracts_openai_style_message_and_hint()
    {
        const string body =
            """
            {
              "error": {
                "message": "Copilot GitHub did not accept 'gemini-3.6-flash' on the chat route AI-FluxMux uses. Refresh Models can list ids before chat/completions accepts them.",
                "type": "model_not_supported",
                "code": 400,
                "hint": "Refresh the cloud model list in AI-FluxMux and pick an available model for this provider."
              }
            }
            """;

        var detail = HttpErrorResponseFormatter.FormatHttpErrorDetail(400, "Bad Request", body);

        Assert.StartsWith("HTTP 400 Bad Request.", detail);
        Assert.Contains("Copilot GitHub did not accept 'gemini-3.6-flash'", detail);
        Assert.Contains("Refresh the cloud model list in AI-FluxMux", detail);
        Assert.DoesNotContain("\\u0027", detail);
        Assert.DoesNotContain("model_not_supported", detail);
    }

    [Fact]
    public void FormatHttpErrorDetail_reads_a_plain_string_error()
    {
        const string body =
            """
            {
              "message": "This chat turn cannot continue: the request included a picture, and **Images** is off on the loaded model profile.",
              "error": "This chat turn cannot continue: the request included a picture, and **Images** is off on the loaded model profile."
            }
            """;

        var detail = HttpErrorResponseFormatter.FormatHttpErrorDetail(400, "Bad Request", body);

        Assert.Contains("This chat turn cannot continue", detail);
        Assert.DoesNotContain("local_context_filling", detail);
        Assert.DoesNotContain("\"type\"", detail);
    }

    [Fact]
    public void FormatHttpErrorDetail_avoids_partial_json_when_unparseable()
    {
        var detail = HttpErrorResponseFormatter.FormatHttpErrorDetail(502, "Bad Gateway", "{\"error\":{\"message\":\"upstream");

        Assert.Equal("HTTP 502 Bad Gateway.", detail);
    }
}
