using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// AI-FluxMux's Context gauge counts what the model held, from llama-server's own usage on the
/// reply, as a Client app's gauge does (27 Nov): it used to count the chat before AI-FluxMux
/// shortened it, so the two gauges disagreed whenever it did.
/// </summary>
public sealed class ReplyUsageTests
{
    [Fact]
    public void Usage_is_read_from_a_stream_line_and_a_body()
    {
        Assert.True(ReplyUsage.TryRead("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":33532,\"completion_tokens\":410,\"total_tokens\":33942}}", out var p, out var c));
        Assert.Equal((33532, 410), (p, c));
        Assert.True(ReplyUsage.TryRead("{\"choices\":[{\"message\":{\"content\":\"OK\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":1}}", out p, out c));
        Assert.Equal((12, 1), (p, c));
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}")]
    [InlineData("data: [DONE]")]
    [InlineData("data: {\"usage\":null}")]
    [InlineData("")]
    public void A_line_without_usage_reports_nothing(string line)
        => Assert.False(ReplyUsage.TryRead(line, out _, out _));

    [Fact]
    public async Task The_stream_copy_reports_the_usage_and_still_forwards_every_line()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n"
                  + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":7}}\n\n"
                  + "data: [DONE]\n\n";
        using var upstream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var downstream = new MemoryStream();
        (int, int)? seen = null;
        await OpenAiStreamTelemetryProxy.CopyAsync(
            upstream, downstream, tracker: null, localReasoningMode: null, CancellationToken.None,
            onUpstreamBytes: null, declaredTools: null, onUsage: (p, c) => seen = (p, c));
        Assert.Equal((900, 7), seen);
        var forwarded = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.Contains("\"prompt_tokens\":900", forwarded, System.StringComparison.Ordinal);
        Assert.Contains("[DONE]", forwarded, System.StringComparison.Ordinal);
    }
}
