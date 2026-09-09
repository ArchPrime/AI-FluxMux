using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class OpenAiStreamTelemetryProxyTests
{
    [Fact]
    public async Task Copy_flushes_each_chunk_so_the_endpoint_app_sees_tokens_before_the_stream_ends()
    {
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"x\":1}\n\ndata: [DONE]\n\n"));
        var downstream = new FlushCountingStream();

        await OpenAiStreamTelemetryProxy.CopyFlushingAsync(upstream, downstream, CancellationToken.None);

        Assert.True(downstream.FlushCount >= 1);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", Encoding.UTF8.GetString(downstream.ToArray()));
    }

    [Fact]
    public async Task Line_copy_flushes_after_each_sse_line()
    {
        var tracker = new GenerationSpeedTracker();
        tracker.SetEnabled(true);
        tracker.Begin("local");
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n"));
        var downstream = new FlushCountingStream();

        await OpenAiStreamTelemetryProxy.CopyAsync(upstream, downstream, tracker, CancellationToken.None);

        Assert.True(downstream.FlushCount >= 1);
        Assert.Contains("Hi", Encoding.UTF8.GetString(downstream.ToArray()));
    }

    [Fact]
    public async Task WriteErrorAndDone_ends_an_open_sse_stream_so_the_endpoint_app_can_stop_waiting()
    {
        var downstream = new FlushCountingStream();

        await OpenAiStreamTelemetryProxy.WriteErrorAndDoneAsync(
            downstream,
            "llama-server took too long to finish this reply.",
            "request_timeout",
            "Idle stop means llama-server is no longer generating this turn.");

        var text = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.Contains("chat.completion.chunk", text);
        Assert.Contains("\"finish_reason\":\"stop\"", text);
        Assert.Contains("llama-server took too long", text);
        Assert.DoesNotContain("\"type\"", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("request_timeout", text, System.StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", text);
        Assert.True(downstream.FlushCount >= 1);
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            await base.FlushAsync(cancellationToken);
        }
    }
}
