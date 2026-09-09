using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Copies an OpenAI SSE stream to the Client app, flushing every chunk so
/// Harness/Cline can show tokens as llama-server produces them instead of when
/// the HTTP connection later closes.
/// </summary>
public static class OpenAiStreamTelemetryProxy
{
    public const string StreamOpenComment = ":\n\n";

    public static Task<bool> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        CancellationToken cancellationToken)
        => CopyAsync(upstream, downstream, tracker, localReasoningMode: null, cancellationToken);

    public static Task<bool> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        string? localReasoningMode,
        CancellationToken cancellationToken)
        => CopyAsync(upstream, downstream, tracker, localReasoningMode, cancellationToken, onUpstreamBytes: null);

    public static async Task<bool> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        string? localReasoningMode,
        CancellationToken cancellationToken,
        Action<int>? onUpstreamBytes)
    {
        var sanitizeResponses = !string.IsNullOrWhiteSpace(localReasoningMode);
        var parseLines = sanitizeResponses || (tracker is not null && tracker.IsEnabled);
        if (!parseLines)
        {
            await CopyFlushingAsync(upstream, downstream, cancellationToken, onUpstreamBytes).ConfigureAwait(false);
            return false;
        }

        using var reader = new StreamReader(upstream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var writer = new StreamWriter(downstream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var responseSanitized = false;
        LocalThinkingStreamSession? streamSession = sanitizeResponses
            ? new LocalThinkingStreamSession(localReasoningMode)
            : null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            onUpstreamBytes?.Invoke(Math.Max(1, Encoding.UTF8.GetByteCount(line) + 1));

            var outbound = line;
            if (streamSession is not null)
            {
                var (sanitized, changed) = LocalEndpointResponseSanitizer.SanitizeSseLine(
                    line,
                    localReasoningMode,
                    streamSession);
                outbound = sanitized;
                responseSanitized |= changed;
            }

            tracker?.ProcessSseLine(line);
            await writer.WriteLineAsync(outbound.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return responseSanitized;
    }

    public static Task CopyFlushingAsync(
        Stream upstream,
        Stream downstream,
        CancellationToken cancellationToken)
        => CopyFlushingAsync(upstream, downstream, cancellationToken, onUpstreamBytes: null);

    public static async Task CopyFlushingAsync(
        Stream upstream,
        Stream downstream,
        CancellationToken cancellationToken,
        Action<int>? onUpstreamBytes)
    {
        var buffer = new byte[1024];
        while (true)
        {
            var read = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            onUpstreamBytes?.Invoke(read);
            await downstream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public const string EndpointTurnEndedStreamText =
        "This turn ended before a reply finished. Start a fresh chat.";

    public static async Task WriteErrorAndDoneAsync(
        Stream downstream,
        string message,
        string errorType,
        string hint = "",
        CancellationToken cancellationToken = default)
    {
        _ = errorType;
        _ = hint;
        var detail = string.IsNullOrWhiteSpace(message)
            ? EndpointTurnEndedStreamText
            : message.Trim();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completion = new JsonObject
        {
            ["id"] = "chatcmpl-fluxmux-ended",
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = "local",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = detail
                    },
                    ["finish_reason"] = "stop"
                }
            }
        };

        var frame = "data: " + completion.ToJsonString()
            + "\n\ndata: [DONE]\n\n";
        var bytes = Encoding.UTF8.GetBytes(frame);
        await downstream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
