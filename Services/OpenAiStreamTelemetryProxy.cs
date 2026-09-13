using System;
using System.Collections.Generic;
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
public readonly record struct StreamCopyResult(
    bool Sanitized,
    bool ThinkOnly,
    bool Healed,
    bool BlockedWrites = false,
    bool ThinkCutOff = false)
{
    public static implicit operator bool(StreamCopyResult value) => value.Sanitized;
}

public static class OpenAiStreamTelemetryProxy
{
    public const string StreamOpenComment = ":\n\n";

    public static Task<StreamCopyResult> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        CancellationToken cancellationToken)
        => CopyAsync(upstream, downstream, tracker, localReasoningMode: null, cancellationToken);

    public static Task<StreamCopyResult> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        string? localReasoningMode,
        CancellationToken cancellationToken)
        => CopyAsync(upstream, downstream, tracker, localReasoningMode, cancellationToken, onUpstreamBytes: null);

    public static Task<StreamCopyResult> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        string? localReasoningMode,
        CancellationToken cancellationToken,
        Action<int>? onUpstreamBytes)
        => CopyAsync(
            upstream,
            downstream,
            tracker,
            localReasoningMode,
            cancellationToken,
            onUpstreamBytes,
            declaredTools: null);

    public static async Task<StreamCopyResult> CopyAsync(
        Stream upstream,
        Stream downstream,
        GenerationSpeedTracker? tracker,
        string? localReasoningMode,
        CancellationToken cancellationToken,
        Action<int>? onUpstreamBytes,
        IReadOnlyCollection<string>? declaredTools,
        bool allowParallelToolCalls = true,
        LocalSessionArtifacts? artifacts = null,
        int maxTokens = 0)
    {
        var sanitizeResponses = !string.IsNullOrWhiteSpace(localReasoningMode);
        LocalToolCallHealSession? healSession = declaredTools is { Count: > 0 } || artifacts is not null
            ? new LocalToolCallHealSession(declaredTools ?? Array.Empty<string>(), allowParallelToolCalls, artifacts)
            : null;
        var parseLines = sanitizeResponses
            || healSession is not null
            || (tracker is not null && tracker.IsEnabled);
        if (!parseLines)
        {
            await CopyFlushingAsync(upstream, downstream, cancellationToken, onUpstreamBytes).ConfigureAwait(false);
            return new StreamCopyResult(false, false, false);
        }

        using var reader = new StreamReader(upstream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var writer = new StreamWriter(downstream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var responseSanitized = false;
        LocalThinkingStreamSession? streamSession = sanitizeResponses
            ? new LocalThinkingStreamSession(localReasoningMode, maxTokens)
            : null;

        async Task WriteLineAsync(string text)
        {
            await writer.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (streamSession is { IsThinkCutOff: true, WroteThinkOnlyNotice: false })
                {
                    await WriteThinkCutoffAsync(WriteLineAsync, streamSession, localReasoningMode, maxTokens)
                        .ConfigureAwait(false);
                }

                if (healSession is not null)
                {
                    foreach (var extra in healSession.TakeFinishChunks())
                    {
                        await WriteLineAsync(extra).ConfigureAwait(false);
                    }
                }

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

            var forward = true;
            if (healSession is not null)
            {
                var decision = healSession.ApplySseLine(outbound);
                foreach (var extra in decision.ExtraBefore)
                {
                    await WriteLineAsync(extra).ConfigureAwait(false);
                }

                outbound = decision.Line;
                forward = decision.Forward;
            }

            tracker?.ProcessSseLine(line);
            if (streamSession is { IsThinkOnly: true }
                && healSession is not { Healed: true }
                && IsDoneDataLine(line)
                && !streamSession.WroteThinkOnlyNotice)
            {
                await WriteLineAsync("data: " + ThinkOnlyFinishChunk()).ConfigureAwait(false);
                streamSession.MarkThinkOnlyNoticeWritten();
            }

            if (streamSession is { IsThinkCutOff: true }
                && healSession is not { Healed: true }
                && IsDoneDataLine(line)
                && !streamSession.WroteThinkOnlyNotice)
            {
                await WriteThinkCutoffAsync(WriteLineAsync, streamSession, localReasoningMode, maxTokens)
                    .ConfigureAwait(false);
            }

            if (forward)
            {
                await WriteLineAsync(outbound).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await downstream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new StreamCopyResult(
            responseSanitized,
            streamSession is { IsThinkOnly: true, WroteThinkOnlyNotice: true },
            healSession?.Healed == true,
            healSession?.BlockedWrites == true,
            streamSession is { IsThinkCutOff: true, WroteThinkOnlyNotice: true });
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

    private static bool IsDoneDataLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Substring(5).Trim().Equals("[DONE]", StringComparison.Ordinal);
    }

    private static async Task WriteThinkCutoffAsync(
        Func<string, Task> writeLineAsync,
        LocalThinkingStreamSession session,
        string? reasoningMode,
        int maxTokens)
    {
        if (session.OpenedThinkTag && session.InsideThink)
        {
            await writeLineAsync("data: " + ThinkCloseChunk()).ConfigureAwait(false);
        }

        await writeLineAsync("data: " + ThinkCutoffFinishChunk(reasoningMode, maxTokens))
            .ConfigureAwait(false);
        session.MarkThinkOnlyNoticeWritten();
    }

    private static string ThinkOnlyFinishChunk()
        => new JsonObject
        {
            ["id"] = "chatcmpl-fluxmux-think-only",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = "local",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = ControlLabelMarkup.ForClientApp(LocalThinkOnlyReply.FormatClientMessage())
                    },
                    ["finish_reason"] = "stop"
                }
            }
        }.ToJsonString();

    private static string ThinkCloseChunk()
        => new JsonObject
        {
            ["id"] = "chatcmpl-fluxmux-think-close",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = "local",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["content"] = "</think>\n\n"
                    },
                    ["finish_reason"] = (string?)null
                }
            }
        }.ToJsonString();

    private static string ThinkCutoffFinishChunk(string? reasoningMode, int maxTokens)
        => new JsonObject
        {
            ["id"] = "chatcmpl-fluxmux-think-cutoff",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = "local",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = ControlLabelMarkup.ForClientApp(
                            LocalThinkBudgetNotice.FormatClientMessage(reasoningMode, maxTokens))
                    },
                    ["finish_reason"] = "stop"
                }
            }
        }.ToJsonString();

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
        var detail = ControlLabelMarkup.ForClientApp(
            string.IsNullOrWhiteSpace(message)
                ? EndpointTurnEndedStreamText
                : message.Trim());
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
