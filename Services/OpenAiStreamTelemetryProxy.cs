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
        int maxTokens = 0,
        Action? onFirstContentToken = null,
        Action? onFirstReasoningToken = null)
    {
        var sanitizeResponses = !string.IsNullOrWhiteSpace(localReasoningMode);
        LocalToolCallHealSession? healSession = declaredTools is { Count: > 0 } || artifacts is not null
            ? new LocalToolCallHealSession(declaredTools ?? Array.Empty<string>(), allowParallelToolCalls, artifacts)
            : null;
        var parseLines = sanitizeResponses
            || healSession is not null
            || (tracker is not null && tracker.IsEnabled)
            || onFirstContentToken is not null
            || onFirstReasoningToken is not null;
        if (!parseLines)
        {
            await CopyFlushingAsync(upstream, downstream, cancellationToken, onUpstreamBytes).ConfigureAwait(false);
            return new StreamCopyResult(false, false, false);
        }

        using var reader = new StreamReader(upstream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var writer = new StreamWriter(downstream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var responseSanitized = false;
        var firstContentTokenFired = false;
        var firstReasoningTokenFired = false;
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

            // Detect the first *reasoning* token (thinking) and the first *visible content*
            // token separately. The first byte of an SSE stream arrives immediately (headers /
            // empty data line) and does NOT mark the end of prefill. For a thinking model the
            // first token is a reasoning token (the model is "thinking"), and the first visible
            // content token (content / text / tool_calls) is when the model has finished
            // thinking and started generating the actual reply.
            //
            // - onFirstReasoningToken fires on the first reasoning token → UI shows "Thinking…".
            // - onFirstContentToken fires on the first VISIBLE content token → UI shows
            //   "Generating…" AND signals the hang clock that the prefill/thinking window is
            //   over (GotContentToken). It does NOT fire on reasoning tokens, so a long thinking
            //   phase does not prematurely switch the UI to "Generating…" or the hang clock to
            //   the post-first-byte stall window.
            if (!firstReasoningTokenFired && HasReasoningToken(line))
            {
                firstReasoningTokenFired = true;
                onFirstReasoningToken?.Invoke();
            }
            if (onFirstContentToken is not null && !firstContentTokenFired && HasVisibleContentToken(line))
            {
                firstContentTokenFired = true;
                onFirstContentToken();
            }

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

    /// <summary>
    /// True when an SSE <c>data:</c> line carries actual content — i.e. the model has produced a
    /// real token (content, reasoning_content, text, or tool_calls). This is the signal that
    /// prefill is complete, as opposed to the first byte of the stream (which arrives
    /// immediately and is just headers / an empty data line). Mirrors the detection used by
    /// <see cref="GenerationSpeedTracker"/> for its first-token timestamp.
    /// </summary>
    private static bool HasContentToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = line.Substring(5).Trim();
        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            if (root is null)
            {
                return false;
            }

            return GenerationSpeedTracker.CountDeltaTokens(root) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when an SSE <c>data:</c> line carries a REASONING (thinking) token — i.e. the model
    /// is thinking, not yet producing visible content. This is the signal for the UI to switch
    /// from "Prefilling…" to "Thinking…". Detects <c>reasoning_content</c> / <c>reasoning</c>
    /// fields on the delta, or raw thinking tags in <c>content</c>.
    /// </summary>
    private static bool HasReasoningToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = line.Substring(5).Trim();
        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            if (root is null)
            {
                return false;
            }

            if (root["choices"] is not JsonArray choices || choices.Count == 0)
            {
                return false;
            }

            if (choices[0] is not JsonObject firstChoice)
            {
                return false;
            }

            // reasoning_content / reasoning fields on the delta (llama-server with --reasoning auto).
            if (firstChoice["delta"] is JsonObject delta)
            {
                if (HasNonEmptyString(delta["reasoning_content"]) || HasNonEmptyString(delta["reasoning"]))
                {
                    return true;
                }
            }

            // Raw thinking tags in content (llama-server with --reasoning-format none).
            if (firstChoice["delta"] is JsonObject delta2 && HasNonEmptyString(delta2["content"]))
            {
                var content = delta2["content"]!.ToString() ?? string.Empty;
                if (content.Contains("\x3cthink\x3e", StringComparison.Ordinal)
                    || content.Contains("\x3c/think\x3e", StringComparison.Ordinal)
                    || content.Contains("<thinking>", StringComparison.Ordinal)
                    || content.Contains("\x3creacted_reasoning\x3e", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when an SSE <c>data:</c> line carries VISIBLE content — i.e. the model has produced a
    /// real content / text / tool_calls token that is NOT reasoning. This is the signal for the UI
    /// to switch from "Thinking…" (or "Prefilling…") to "Generating…", and for the hang clock to
    /// mark the prefill/thinking window as over. Reasoning tokens do NOT count as visible content.
    /// </summary>
    private static bool HasVisibleContentToken(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = line.Substring(5).Trim();
        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            if (root is null)
            {
                return false;
            }

            if (root["choices"] is not JsonArray choices || choices.Count == 0)
            {
                return false;
            }

            if (choices[0] is not JsonObject firstChoice)
            {
                return false;
            }

            if (firstChoice["delta"] is not JsonObject delta)
            {
                // Non-delta shapes (e.g. a full message) — fall back to the generic count.
                return GenerationSpeedTracker.CountDeltaTokens(root) > 0;
            }

            // tool_calls are visible content (a tool call is the model's action).
            if (delta["tool_calls"] is JsonArray { Count: > 0 } toolCalls)
            {
                foreach (var callNode in toolCalls)
                {
                    if (callNode is JsonObject call
                        && call["function"] is JsonObject function
                        && (HasNonEmptyString(function["name"]) || HasNonEmptyString(function["arguments"])))
                    {
                        return true;
                    }
                }
            }

            // content / text that is NOT a raw thinking tag.
            if (HasNonEmptyString(delta["content"]))
            {
                var content = delta["content"]!.ToString() ?? string.Empty;
                if (!IsThinkingOnly(content))
                {
                    return true;
                }
            }

            if (HasNonEmptyString(delta["text"]))
            {
                var text = delta["text"]!.ToString() ?? string.Empty;
                if (!IsThinkingOnly(text))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNonEmptyString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// True when the text is ONLY thinking markup (raw thinking tags / their fragments) with no
    /// visible content after stripping. Used to avoid counting a thinking delta as visible
    /// content.
    /// </summary>
    private static bool IsThinkingOnly(string text)
    {
        var stripped = LocalThinkingContentFilter.StripThinkingFromText(text);
        return string.IsNullOrWhiteSpace(stripped);
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
