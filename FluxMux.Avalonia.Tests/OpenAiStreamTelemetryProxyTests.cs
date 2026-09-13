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
    public async Task Think_only_stream_is_not_an_empty_complete()
    {
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>loop\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"</think>\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n"
            + "data: [DONE]\n"));
        var downstream = new MemoryStream();

        var copy = await OpenAiStreamTelemetryProxy.CopyAsync(
            upstream,
            downstream,
            tracker: null,
            localReasoningMode: "Off",
            CancellationToken.None);

        var text = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.True(copy.ThinkOnly);
        Assert.Contains(LocalThinkOnlyReply.Message, text, System.StringComparison.Ordinal);
        Assert.Contains("This Client-app turn is over", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Low_think_cutoff_closes_think_and_tells_the_Client_app_why()
    {
        var upstream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>\"}}]}\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"keep looping\"}}]}\n"
            + "data: [DONE]\n"));
        var downstream = new MemoryStream();

        var copy = await OpenAiStreamTelemetryProxy.CopyAsync(
            upstream,
            downstream,
            tracker: null,
            localReasoningMode: "Low",
            CancellationToken.None,
            onUpstreamBytes: null,
            declaredTools: null,
            allowParallelToolCalls: true,
            artifacts: null,
            maxTokens: 32768);

        var text = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.True(copy.ThinkCutOff);
        Assert.False(copy.ThinkOnly);
        Assert.True(
            text.Contains("</think>", System.StringComparison.Ordinal)
            || text.Contains("\\u003c/think\\u003e", System.StringComparison.OrdinalIgnoreCase)
            || text.Contains("\\u003C/think\\u003E", System.StringComparison.Ordinal),
            "The Client app must receive a think close so Harness leaves thought mode.");
        Assert.Contains("Reasoning is Low", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("**Reasoning**", text, System.StringComparison.Ordinal);
        Assert.Contains("2,048", text, System.StringComparison.Ordinal);
        Assert.Contains("This Client-app turn is over", text, System.StringComparison.Ordinal);
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

    [Fact]
    public async Task Steer_notice_is_assistant_content_not_an_HTTP_400()
    {
        var finding = PortRulesPostMortem.FormatMill(32, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults);
        var notice = PortRulesPostMortem.FormatSteerNotice(finding);
        var downstream = new MemoryStream();

        await OpenAiStreamTelemetryProxy.WriteErrorAndDoneAsync(
            downstream,
            notice,
            "notice");

        var text = Encoding.UTF8.GetString(downstream.ToArray());
        Assert.Contains("chat.completion.chunk", text, System.StringComparison.Ordinal);
        Assert.Contains("\"role\":\"assistant\"", text, System.StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":400", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\": 400", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain(PortRulesPostMortem.PortRuleStopAdvice, text, System.StringComparison.Ordinal);
        Assert.Contains("steer", text, System.StringComparison.OrdinalIgnoreCase);
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
