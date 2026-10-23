using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// A single fill-level result from the context-fill stress test.
/// </summary>
public readonly record struct ContextFillLevelResult(
    int FillPercent,
    int PromptTokens,
    int ContextLimit,
    long TtftMs,
    long TotalMs,
    double TokensPerSecond,
    int CompletionTokens,
    bool IsSuccess,
    string Status);

/// <summary>
/// How a fill level was driven. <see cref="Progressive"/> builds a realistic multi-turn
/// conversation up to the target (most faithful to real chat, including YaRN-extended
/// context). <see cref="SinglePrompt"/> sends one diverse prompt sized to the target
/// (faster, still real content, less faithful to progressive accumulation).
/// </summary>
public enum ContextFillMode
{
    Progressive,
    SinglePrompt
}

/// <summary>
/// The full result of a context-fill stress test run.
/// </summary>
public sealed class ContextFillStressResult
{
    public required string Model { get; init; }
    public required string Variant { get; init; }
    public required int ContextLimit { get; init; }
    public required string HardwareContext { get; init; }
    public required IReadOnlyList<ContextFillLevelResult> Levels { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime CompletedUtc { get; init; }
    public ContextFillMode Mode { get; init; } = ContextFillMode.Progressive;

    /// <summary>
    /// True when the test stopped early because a level failed (prefill cost is monotonic,
    /// so higher fill levels cannot succeed under the same timeout).
    /// </summary>
    public bool StoppedEarly { get; init; }

    /// <summary>
    /// True when the test was aborted by the user (cancellation). The partial results
    /// collected before the abort are still valid and should be reported/graphed, but
    /// the description must clearly state the test was aborted.
    /// </summary>
    public bool Aborted { get; init; }

    public int HighestSuccessfulFillPercent =>
        Levels.Where(l => l.IsSuccess).Select(l => l.FillPercent).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The highest successful target token count (absolute tokens, not percent).
    /// </summary>
    public int HighestSuccessfulTargetTokens =>
        Levels.Where(l => l.IsSuccess).Select(l => l.PromptTokens).DefaultIfEmpty(0).Max();

    public string LimitBehavior
    {
        get
        {
            var firstFailure = Levels.FirstOrDefault(l => !l.IsSuccess);
            if (StoppedEarly && firstFailure is { } ff)
            {
                return $"Stopped at {ContextFillStressProbe.FormatTokens(ff.PromptTokens)} ({ff.Status}) — higher levels cannot succeed under the same timeout (prefill cost is monotonic). Highest successful: {ContextFillStressProbe.FormatTokens(HighestSuccessfulTargetTokens)}.";
            }

            // No levels completed at all (e.g. the very first level failed before producing a
            // result, or the test was aborted immediately). Report that plainly instead of
            // defaulting to a bogus "Failed at 100%" — FirstOrDefault on an empty list returns a
            // default struct (IsSuccess=false, Status=""), which would otherwise read as a
            // 100% failure.
            if (Levels.Count == 0)
            {
                return "No fill level completed — no limit behavior observed.";
            }

            var lastLevel = Levels[^1];
            if (lastLevel.IsSuccess)
            {
                return $"No failure observed up to {ContextFillStressProbe.FormatTokens(lastLevel.PromptTokens)} — the context limit was not reached.";
            }

            // Find the highest successful level and the first failure.
            var highestSuccess = Levels.LastOrDefault(l => l.IsSuccess);
            if (highestSuccess is { } hs)
            {
                return $"Failed at {ContextFillStressProbe.FormatTokens(lastLevel.PromptTokens)} ({lastLevel.Status}); {ContextFillStressProbe.FormatTokens(hs.PromptTokens)} succeeded.";
            }

            return $"Failed at {ContextFillStressProbe.FormatTokens(lastLevel.PromptTokens)} ({lastLevel.Status}).";
        }
    }
}

/// <summary>
/// A context-fill stress test that measures performance (TTFT, tok/s) at increasing
/// context fill levels, up to and past the context limit. This is a "test to failure"
/// — it deliberately exceeds the limit to observe the behavior (graceful error, OOM,
/// hang, or crash). Results are hardware-specific in absolute terms, but the shape of
/// the curve and the limit behavior are generalizable to other hardware.
/// </summary>
public static class ContextFillStressProbe
{
    /// <summary>
    /// Fixed absolute token targets for the stress test (fallback when the context limit is unknown).
    /// </summary>
    public static readonly int[] DefaultFillLevels = [4096, 8192, 16384, 24576, 32768, 40960];

    /// <summary>
    /// Fixed step size (in tokens) for the stress-test fill levels. A single constant is used
    /// for every model (including YaRN-extended ones) so that performance can be compared
    /// directly between models at the same fill level — the ghost lines from a previous run
    /// (a different model profile) line up with the current run's bars. 64K was chosen as the
    /// default: it gives a manageable number of levels for large YaRN contexts (e.g. 393K → 7
    /// levels) while still being meaningful for smaller VRAM-only contexts.
    /// </summary>
    public const int FillLevelStep = 65536; // 64K

    /// <summary>
    /// Computes the stress-test fill levels using a fixed <see cref="FillLevelStep"/> (64K)
    /// interval. The levels are step, 2*step, 3*step, ... and continue just beyond the context
    /// limit (the first multiple of the step that exceeds it) so the user can see the test runs
    /// to — and slightly past — the full context, rather than appearing to stop short.
    /// <param name="contextLimit">The total context limit (OverrideContext, including YaRN extension).</param>
    /// <param name="vramContext">The VRAM-resident context (unused for step sizing — the step is
    /// fixed so results are comparable across models; kept for API compatibility).</param>
    /// <returns>An array of evenly-spaced token targets covering up to just beyond the context limit.</returns>
    /// </summary>
    public static int[] ComputeFillLevels(int contextLimit, int vramContext)
    {
        if (contextLimit <= 0)
        {
            return DefaultFillLevels;
        }

        const int step = FillLevelStep;

        // Generate levels: step, 2*step, 3*step, ... up to and just beyond the context limit.
        // The final level is the first multiple of the step that is >= contextLimit, so the last
        // bar sits at (or just past) the available context and the user does not think the test
        // stopped short. No cap on the number of levels — YaRN-extended contexts simply get more.
        var levels = new List<int>();
        for (int target = step; ; target += step)
        {
            levels.Add(target);
            if (target >= contextLimit)
            {
                break;
            }
        }

        return levels.ToArray();
    }

    /// <summary>
    /// Formats a token count as a short label (e.g., 4096 → "4K", 16384 → "16K").
    /// </summary>
    public static string FormatTokens(int tokens) =>
        tokens >= 1024 ? $"{tokens:N0}" : $"{tokens}";

    /// <summary>
    /// Runs the context-fill stress test.
    /// <param name="mode">
    /// <see cref="ContextFillMode.Progressive"/> (default) builds a realistic multi-turn
    /// conversation up to each fill level, measuring the model's real token count each
    /// turn — the most faithful to real chat, including YaRN-extended context.
    /// <see cref="ContextFillMode.SinglePrompt"/> sends one diverse prompt per level
    /// (faster, still real content).
    /// </param>
    /// <param name="onMeasuredTokens">
    /// Optional callback reporting the model's measured prompt-token count after each
    /// turn/level, so the caller can log the real number the model saw.
    /// </param>
    /// </summary>
    public static async Task<ContextFillStressResult> RunAsync(
        HttpClient httpClient,
        int port,
        string model,
        int contextLimit,
        string hardwareContext,
        int[]? fillLevels = null,
        Action<ContextFillLevelResult>? onLevelComplete = null,
        Action<int, int>? onLevelStart = null,
        Action<int, int, long>? onLevelHeartbeat = null,
        int perLevelTimeoutMs = 0,
        ContextFillMode mode = ContextFillMode.Progressive,
        Action<int, int, int>? onMeasuredTokens = null,
        Func<Task>? onBeforeLevel = null,
        CancellationToken cancellationToken = default)
    {
        var levels = fillLevels ?? DefaultFillLevels;
        var results = new List<ContextFillLevelResult>();
        var startedUtc = DateTime.UtcNow;
        var stoppedEarly = false;
        var aborted = false;

        try
        {
            foreach (var targetTokens in levels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Clear the model's context before each level so the token count corresponds to
                // the actual context usage. Without this, the model's KV cache retains state from
                // the previous level (or from background chat turns), and the gauge's context
                // usage doesn't match the test's target. The clear is best-effort: if it fails,
                // the test still runs (the level just starts from a non-empty context).
                if (onBeforeLevel is not null)
                {
                    await onBeforeLevel().ConfigureAwait(false);
                }

                onLevelStart?.Invoke(targetTokens, targetTokens);
                var result = mode == ContextFillMode.SinglePrompt
                    ? await ProbeSinglePromptAsync(
                        httpClient, port, model, targetTokens, contextLimit,
                        perLevelTimeoutMs, onLevelHeartbeat, onMeasuredTokens, cancellationToken)
                    : await ProbeProgressiveAsync(
                        httpClient, port, model, targetTokens, contextLimit,
                        perLevelTimeoutMs, onLevelHeartbeat, onMeasuredTokens, cancellationToken);
                results.Add(result);
                onLevelComplete?.Invoke(result);

                // Prefill cost is monotonic with prompt size: if this level failed to produce a
                // token within the timeout, every higher level (more tokens, same or less time)
                // will also fail. Stop here — continuing is pure waste. The failure itself is the
                // useful data point (it tells us where the limit is).
                if (!result.IsSuccess)
                {
                    stoppedEarly = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The user cancelled the test. The partial results collected before the
            // cancellation are still valid and should be reported/graphed, but the
            // description must clearly state the test was aborted.
            aborted = true;
        }

        return new ContextFillStressResult
        {
            Model = model,
            Variant = string.Empty,
            ContextLimit = contextLimit,
            HardwareContext = hardwareContext,
            Levels = results,
            StartedUtc = startedUtc,
            CompletedUtc = DateTime.UtcNow,
            StoppedEarly = stoppedEarly,
            Aborted = aborted,
            Mode = mode
        };
    }

    /// <summary>
    /// Progressive multi-turn probe: builds a realistic conversation turn-by-turn up to the
    /// target fill level, measuring the model's real prompt-token count after each turn.
    /// Most faithful to real chat (including YaRN-extended context): context accumulates the
    /// way a real conversation does, and the token count is the model's own measured value.
    /// </summary>
    private static async Task<ContextFillLevelResult> ProbeProgressiveAsync(
        HttpClient httpClient,
        int port,
        string model,
        int targetTokens,
        int contextLimit,
        int perLevelTimeoutMs,
        Action<int, int, long>? onLevelHeartbeat,
        Action<int, int, int>? onMeasuredTokens,
        CancellationToken cancellationToken)
    {
        var fillPercent = contextLimit > 0 ? (int)(targetTokens / (double)contextLimit * 100.0) : 0;
        var contentBlocks = BuildDiverseContentBlocks();
        var messages = new List<JsonObject>();
        var measuredTokens = 0;
        var blockIndex = 0;

        // Build the conversation turn-by-turn, measuring the model's REAL prompt-token count
        // after each turn via llama-server's input_tokens endpoint (the same ground-truth
        // mechanism the gateway uses). Keep adding turns until the measured count reaches the
        // target, so each level is hit at the model's actual token count, not an estimate.
        while (measuredTokens < targetTokens && !cancellationToken.IsCancellationRequested)
        {
            var block = contentBlocks[blockIndex % contentBlocks.Length];
            blockIndex++;
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = block });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Understood." });

            var measured = await MeasurePromptTokensAsync(port, model, messages, cancellationToken);
            if (measured <= 0)
            {
                // Could not measure (endpoint unavailable). Fall back to a chars/4 estimate so
                // the test still progresses, but note it is an estimate.
                measured = EstimateTokens(messages);
            }
            measuredTokens = measured;
            onMeasuredTokens?.Invoke(fillPercent, targetTokens, measuredTokens);
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "Explain the key points above in 3-4 clear sentences."
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long ttftMs = 0;
        var completionChars = 0;
        var usageCompletionTokens = 0;
        var usagePromptTokens = 0;

        var timeoutMs = perLevelTimeoutMs > 0 ? perLevelTimeoutMs : 600000;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = onLevelHeartbeat is null
            ? Task.CompletedTask
            : Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5000, heartbeatCts.Token).ConfigureAwait(false);
                        onLevelHeartbeat(fillPercent, targetTokens, sw.ElapsedMilliseconds);
                    }
                }
                catch (OperationCanceledException) { }
            }, heartbeatCts.Token);

        try
        {
            // Use 128 tokens (not 32) so the tok/s measurement is more representative of the
            // model's true generation speed. With only 32 tokens, the sample is too small: the
            // warmup tokens are a large fraction of the total, and the "summarize in one sentence"
            // prompt elicits a very short reply, making the measured tok/s artificially low.
            // 128 tokens with a "3-4 sentences" prompt gives a more realistic sample without
            // making the test prohibitively slow. TTFT is unaffected (measured to the first token).
            var request = BuildChatRequest(httpClient, port, model, messages, maxTokens: 128);
            // Retry on 503 "still loading" so a cold-load 503 doesn't abort the whole test.
            // A non-loading error (400 context-too-large, etc.) is returned immediately.
            using var response = await SendWithLoadingRetryAsync(
                httpClient, request, timeoutMs, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return new ContextFillLevelResult(
                    fillPercent, measuredTokens, contextLimit, 0, sw.ElapsedMilliseconds, 0, 0, false,
                    $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;
                if (!TryExtractDelta(data, out var delta, out var usageTokens, out var promptTokensFromUsage)) continue;
                if (usageTokens > 0) usageCompletionTokens = usageTokens;
                if (promptTokensFromUsage > 0) usagePromptTokens = promptTokensFromUsage;
                if (delta.Length == 0) continue;
                if (ttftMs <= 0) ttftMs = sw.ElapsedMilliseconds;
                completionChars += delta.Length;
            }

            sw.Stop();
            var actualPromptTokens = usagePromptTokens > 0 ? usagePromptTokens : measuredTokens;
            var completionTokens = usageCompletionTokens > 0 ? usageCompletionTokens : Math.Max(1, completionChars / 4);
            var generateMs = Math.Max(1, sw.ElapsedMilliseconds - Math.Max(0, ttftMs));
            var tokPerSec = completionTokens / (generateMs / 1000d);
            onMeasuredTokens?.Invoke(fillPercent, targetTokens, actualPromptTokens);
            return new ContextFillLevelResult(
                fillPercent, actualPromptTokens, contextLimit,
                ttftMs > 0 ? ttftMs : sw.ElapsedMilliseconds, sw.ElapsedMilliseconds,
                Math.Round(tokPerSec, 1), completionTokens, true, "success");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new ContextFillLevelResult(
                fillPercent, measuredTokens, contextLimit, ttftMs, sw.ElapsedMilliseconds, 0, 0, false,
                $"Timed out after {sw.ElapsedMilliseconds} ms (possible hang at {fillPercent}% fill)");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled the test. Re-throw so RunAsync sees the cancellation and
            // sets Aborted = true. If we swallowed it here, RunAsync would never know the
            // test was aborted and would report it as "complete".
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ContextFillLevelResult(
                fillPercent, measuredTokens, contextLimit, ttftMs, sw.ElapsedMilliseconds, 0, 0, false,
                $"Error: {ex.GetBaseException().Message}");
        }
        finally
        {
            try { heartbeatCts.Cancel(); } catch { }
            try { await heartbeatTask.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Measures the model's real prompt-token count for the given conversation using
    /// llama-server's input_tokens endpoint (the same ground-truth mechanism the gateway
    /// uses). Returns 0 when the count cannot be obtained.
    /// </summary>
    private static async Task<int> MeasurePromptTokensAsync(
        int port,
        string model,
        List<JsonObject> messages,
        CancellationToken cancellationToken)
    {
        var messagesArray = new JsonArray();
        foreach (var m in messages)
        {
            messagesArray.Add(m.DeepClone());
        }
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "local" : model,
            ["messages"] = messagesArray
        };
        var count = await LocalPromptTokenCounter.CountAsync(port, payload, model, cancellationToken);
        return count ?? 0;
    }

    /// <summary>
    /// Rough chars/4 estimate of the prompt token count, used only as a fallback when the
    /// model's input_tokens endpoint is unavailable.
    /// </summary>
    private static int EstimateTokens(List<JsonObject> messages)
    {
        var totalChars = 0;
        foreach (var m in messages)
        {
            totalChars += m["content"]?.ToString()?.Length ?? 0;
        }
        return totalChars / 4;
    }

    /// <summary>
    /// Single-prompt probe: sends one diverse prompt sized to the target fill level.
    /// Faster than progressive, still uses real (non-repeated) content.
    /// </summary>
    private static async Task<ContextFillLevelResult> ProbeSinglePromptAsync(
        HttpClient httpClient,
        int port,
        string model,
        int targetTokens,
        int contextLimit,
        int perLevelTimeoutMs,
        Action<int, int, long>? onLevelHeartbeat,
        Action<int, int, int>? onMeasuredTokens,
        CancellationToken cancellationToken)
    {
        var fillPercent = contextLimit > 0 ? (int)(targetTokens / (double)contextLimit * 100.0) : 0;
        var prompt = BuildDiversePromptForTokens(targetTokens);
        var messages = new List<JsonObject>
        {
            new()
            {
                ["role"] = "user",
                ["content"] = prompt + "\n\nExplain the key points above in 3-4 clear sentences."
            }
        };

        // Measure the model's real prompt-token count for this prompt (ground-truth via
        // llama-server's input_tokens endpoint). Used as the reported token count.
        var measuredPromptTokens = await MeasurePromptTokensAsync(port, model, messages, cancellationToken);
        if (measuredPromptTokens <= 0)
        {
            measuredPromptTokens = targetTokens; // fallback to the target estimate
        }
        onMeasuredTokens?.Invoke(fillPercent, targetTokens, measuredPromptTokens);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long ttftMs = 0;
        var completionChars = 0;
        var usageCompletionTokens = 0;
        var usagePromptTokens = 0;

        var timeoutMs = perLevelTimeoutMs > 0 ? perLevelTimeoutMs : 600000;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = onLevelHeartbeat is null
            ? Task.CompletedTask
            : Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5000, heartbeatCts.Token).ConfigureAwait(false);
                        onLevelHeartbeat(fillPercent, targetTokens, sw.ElapsedMilliseconds);
                    }
                }
                catch (OperationCanceledException) { }
            }, heartbeatCts.Token);

        try
        {
            // Use 128 tokens (not 32) for a more representative tok/s measurement (see the
            // progressive-mode comment for details).
            var request = BuildChatRequest(httpClient, port, model, messages, maxTokens: 128);
            // Retry on 503 "still loading" so a cold-load 503 doesn't abort the whole test.
            // A non-loading error (400 context-too-large, etc.) is returned immediately.
            using var response = await SendWithLoadingRetryAsync(
                httpClient, request, timeoutMs, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return new ContextFillLevelResult(
                    fillPercent, targetTokens, contextLimit, 0, sw.ElapsedMilliseconds, 0, 0, false,
                    $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;
                if (!TryExtractDelta(data, out var delta, out var usageTokens, out var promptTokensFromUsage)) continue;
                if (usageTokens > 0) usageCompletionTokens = usageTokens;
                if (promptTokensFromUsage > 0) usagePromptTokens = promptTokensFromUsage;
                if (delta.Length == 0) continue;
                if (ttftMs <= 0) ttftMs = sw.ElapsedMilliseconds;
                completionChars += delta.Length;
            }

            sw.Stop();
            var actualPromptTokens = usagePromptTokens > 0 ? usagePromptTokens : measuredPromptTokens;
            var completionTokens = usageCompletionTokens > 0 ? usageCompletionTokens : Math.Max(1, completionChars / 4);
            var generateMs = Math.Max(1, sw.ElapsedMilliseconds - Math.Max(0, ttftMs));
            var tokPerSec = completionTokens / (generateMs / 1000d);
            onMeasuredTokens?.Invoke(fillPercent, targetTokens, actualPromptTokens);
            return new ContextFillLevelResult(
                fillPercent, actualPromptTokens, contextLimit,
                ttftMs > 0 ? ttftMs : sw.ElapsedMilliseconds, sw.ElapsedMilliseconds,
                Math.Round(tokPerSec, 1), completionTokens, true, "success");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new ContextFillLevelResult(
                fillPercent, measuredPromptTokens, contextLimit, ttftMs, sw.ElapsedMilliseconds, 0, 0, false,
                $"Timed out after {sw.ElapsedMilliseconds} ms (possible hang at {fillPercent}% fill)");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled the test. Re-throw so RunAsync sees the cancellation and
            // sets Aborted = true.
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ContextFillLevelResult(
                fillPercent, measuredPromptTokens, contextLimit, ttftMs, sw.ElapsedMilliseconds, 0, 0, false,
                $"Error: {ex.GetBaseException().Message}");
        }
        finally
        {
            try { heartbeatCts.Cancel(); } catch { }
            try { await heartbeatTask.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Builds an HTTP request for a chat completion with the given messages.
    /// </summary>
    /// <summary>
    /// Retries a chat-completion request while the local model is still cold-loading (the
    /// gateway/llama-server answers 503 "The local model is still loading"). Without this, a
    /// stress test started right after a launch aborts on the very first level with a 503,
    /// even though the model would have been ready a few seconds later. The retry mirrors the
    /// gateway's own <c>LocalLoadingRetryPolicy</c>: back off and re-send until the model is
    /// ready or the per-level budget is exhausted. A non-loading error (400, 404, …) is NOT
    /// retried — it is returned immediately as a hard failure.
    ///
    /// IMPORTANT: The returned <see cref="HttpResponseMessage"/> is NOT disposed by this method.
    /// The caller is responsible for disposing it (via <c>using</c> or explicit <c>Dispose()</c>).
    /// This is because the response's content stream must remain readable after this method
    /// returns — disposing it here would cause "Cannot access a disposed object" when the
    /// caller reads the body.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithLoadingRetryAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 150;
        var delayMs = 2000;
        var totalWaitMs = 0;
        const int maxTotalWaitMs = 300000; // 300s — enough for a cold YaRN load

        for (int attempt = 1; ; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            // Do NOT use `using` here — the response must outlive this method so the caller
            // can read its content. Disposed responses on the retry path are cleaned up
            // explicitly below.
            var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // Success: return the response (caller disposes it).
                return response;
            }

            // Read the body before deciding whether to retry. This also consumes the response
            // stream, so a disposed response on the retry path won't leak the connection.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                && LooksLikeLoading(body))
            {
                if (attempt >= maxAttempts || totalWaitMs + delayMs > maxTotalWaitMs)
                {
                    // Budget exhausted: surface the 503 as the level's failure. Replace the
                    // (now-consumed) content with the body we just read so the caller can
                    // still read it.
                    response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    return response;
                }
                // Retry: dispose this response (its content is consumed) and loop.
                response.Dispose();
                totalWaitMs += delayMs;
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 10000);
                continue;
            }

            // Non-loading error: return immediately (no retry). Replace the consumed content
            // with the body so the caller can still read it.
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    /// <summary>
    /// True when a response body looks like the llama-server "model still loading" signal.
    /// Matches the same phrases the gateway's <c>LocalLoadingRetryPolicy.LooksLikeDaemonLoading</c>
    /// uses, so the probe and the gateway agree on what counts as a transient cold-load 503.
    /// </summary>
    private static bool LooksLikeLoading(string body)
    {
        var text = body ?? string.Empty;
        return text.Contains("still loading", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Loading model", StringComparison.OrdinalIgnoreCase)
            || text.Contains("model is loading", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not ready for completions", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage BuildChatRequest(
        HttpClient httpClient,
        int port,
        string model,
        List<JsonObject> messages,
        int maxTokens)
    {
        var endpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
        var messagesArray = new JsonArray();
        foreach (var m in messages)
        {
            messagesArray.Add(m.DeepClone());
        }
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "local" : model,
            ["stream"] = true,
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens,
            ["messages"] = messagesArray
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation(
            CloudEndpointValidationProbe.ForceRouteHeader,
            FluxMuxGatewayRouting.Local);
        request.Headers.TryAddWithoutValidation(
            CloudEndpointValidationProbe.ProbeRequestHeader,
            "context-fill-stress");
        return request;
    }

    /// <summary>
    /// Builds a diverse, non-degenerate prompt sized to the target token count.
    /// Used by the single-prompt probe.
    /// </summary>
    private static string BuildDiversePromptForTokens(int targetTokens)
    {
        var blocks = BuildDiverseContentBlocks();
        var targetChars = targetTokens * 4;
        var sb = new StringBuilder();
        var blockIndex = 0;
        while (sb.Length < targetChars)
        {
            sb.Append(blocks[blockIndex % blocks.Length]);
            sb.Append('\n');
            blockIndex++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a diverse, non-degenerate content pool for the progressive probe.
    /// Each block is a realistic mix of prose, code, numbers, and tool-style output,
    /// so the prompt is not compressible and exercises attention/KV the way real
    /// conversation does.
    /// </summary>
    private static string[] BuildDiverseContentBlocks()
    {
        return new[]
        {
            "The system log shows a sequence of events: at 09:15:22 the service started, " +
            "loaded 47 modules, and initialized the database connection pool with 20 connections. " +
            "At 09:15:45 the first request arrived, took 120ms to process, and returned a 200 status. " +
            "Memory usage was 2.3GB at startup, rising to 3.1GB after the first 100 requests. " +
            "The CPU usage averaged 45% during the first minute, spiking to 78% at 09:16:30 when a " +
            "batch job ran. No errors were logged in the first 5 minutes of operation.",

            "```python\n" +
            "def calculate_metrics(data: list[dict]) -> dict:\n" +
            "    if not data:\n" +
            "        return {'avg_latency': 0, 'p95_latency': 0, 'error_rate': 0}\n" +
            "    latencies = [d['latency_ms'] for d in data]\n" +
            "    sorted_lat = sorted(latencies)\n" +
            "    p95_idx = int(len(sorted_lat) * 0.95)\n" +
            "    errors = sum(1 for d in data if d['status'] >= 500)\n" +
            "    return {\n" +
            "        'avg_latency': sum(latencies) / len(latencies),\n" +
            "        'p95_latency': sorted_lat[p95_idx],\n" +
            "        'error_rate': errors / len(data)\n" +
            "    }\n" +
            "```",

            "The financial report for Q3 shows revenue of $4.2M, up 12% from Q2. Operating costs " +
            "were $2.8M, resulting in a gross profit of $1.4M. The customer acquisition cost was " +
            "$185 per user, down from $210 in Q2. Lifetime value per customer was $1,240, giving " +
            "an LTV:CAC ratio of 6.7:1. The churn rate was 2.3% monthly, slightly improved from " +
            "2.5% in Q2. Net revenue retention was 118%, indicating strong expansion revenue from " +
            "existing customers. The board approved a 15% salary increase for the engineering team, " +
            "effective January 1st.",

            "Tool result: {\"status\": \"success\", \"files_modified\": [\"src/main.py\", \"src/utils.py\"], " +
            "\"lines_added\": 45, \"lines_removed\": 12, \"test_results\": {\"passed\": 23, \"failed\": 0, " +
            "\"skipped\": 2}, \"coverage\": 87.3, \"duration_ms\": 2340}. The linter found 3 warnings: " +
            "unused import in main.py line 45, missing type hint in utils.py line 12, and a " +
            "potential null reference in main.py line 78. All tests passed. The build completed " +
            "successfully in 45 seconds.",

            "The user asked: 'Can you help me optimize my database queries? I'm seeing slow response " +
            "times on the /api/users endpoint, taking 2-3 seconds for a simple list query.' The " +
            "assistant responded: 'I'll look at the query plan and add appropriate indexes. The " +
            "current query does a full table scan on the users table which has 2.3M rows. Adding a " +
            "composite index on (status, created_at) should reduce the query time to under 50ms. " +
            "I'll also add a LIMIT clause to prevent fetching all rows at once.' The user replied: " +
            "'That sounds good, but make sure the index doesn't slow down writes. We have about " +
            "10,000 inserts per day.'",

            "The deployment pipeline ran successfully: build completed in 3 minutes, unit tests " +
            "passed (456/456), integration tests passed (89/89), and the deployment to staging " +
            "completed in 45 seconds. The health check passed on the first attempt. Rollback was " +
            "not needed. The new version (v2.4.1) is now live on staging. The release notes include: " +
            "fixed a bug where the pagination offset was off by one, added support for CSV export, " +
            "and improved the error messages for invalid API keys. The next release (v2.5.0) is " +
            "planned for next Tuesday and will include the new dashboard features."
        };
    }

    private static bool TryExtractDelta(string data, out string delta, out int usageCompletionTokens, out int usagePromptTokens)
    {
        delta = string.Empty;
        usageCompletionTokens = 0;
        usagePromptTokens = 0;
        try
        {
            if (JsonNode.Parse(data) is not JsonObject root) return false;
            if (root["usage"]?["completion_tokens"] is JsonValue usage && usage.TryGetValue<int>(out var tokens))
                usageCompletionTokens = tokens;
            if (root["usage"]?["prompt_tokens"] is JsonValue promptUsage && promptUsage.TryGetValue<int>(out var pt))
                usagePromptTokens = pt;
            var choice = root["choices"]?[0] as JsonObject;
            delta = choice?["delta"]?["content"]?.ToString()
                ?? choice?["message"]?["content"]?.ToString()
                ?? choice?["text"]?.ToString()
                ?? string.Empty;
            return true;
        }
        catch { return false; }
    }

    private static string Truncate(string value, int maxLen)
        => string.IsNullOrEmpty(value) || value.Length <= maxLen ? value ?? string.Empty : value[..maxLen] + "...";

    public static string FormatForDiagnostics(ContextFillStressResult result)
    {
        var lines = new List<string>
        {
            $"Context-fill stress test: {result.Model} (limit {FormatTokens(result.ContextLimit)})",
            $"Hardware: {result.HardwareContext}",
            $"Started {result.StartedUtc:HH:mm:ss} UTC, {(result.Aborted ? "aborted" : "completed")} {result.CompletedUtc:HH:mm:ss} UTC",
            "",
            "Target   PromptTok  TTFT(ms)  Total(ms)  Tok/s  Status",
            "------   ---------  --------  ---------  -----  ------"
        };
        foreach (var level in result.Levels)
        {
            lines.Add(
                $"{FormatTokens(level.PromptTokens),6}  {level.PromptTokens,9}  {level.TtftMs,8}  {level.TotalMs,9}  " +
                $"{(level.IsSuccess ? level.TokensPerSecond.ToString("0.#", CultureInfo.InvariantCulture) : "—"),6}  " +
                (level.IsSuccess ? "OK" : level.Status));
        }
        lines.Add("");
        lines.Add($"Highest successful: {FormatTokens(result.HighestSuccessfulTargetTokens)}");
        if (result.Aborted)
        {
            // The abort happens mid-level: the last completed level is the highest successful
            // one, and the in-progress level (the next fill level) produced no result. Report
            // the last completed level as the abort point so it matches the graph's red
            // "untested region" line (which marks the last completed level).
            var lastCompletedIndex = result.Levels.Count - 1;
            var abortedAt = lastCompletedIndex >= 0
                ? result.Levels[lastCompletedIndex].PromptTokens
                : 0;
            lines.Add($"Test ABORTED by user at {FormatTokens(abortedAt)}. Partial results above are valid.");
        }
        else if (result.StoppedEarly)
        {
            lines.Add("Test stopped early: a level failed, and higher levels cannot succeed under the same timeout.");
        }
        lines.Add($"Limit behavior: {result.LimitBehavior}");
        return string.Join(Environment.NewLine, lines);
    }

    public static void WriteToLogFile(ContextFillStressResult result, string logPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string>
            {
                $"# Context-fill stress test: {result.Model}",
                $"# Hardware: {result.HardwareContext}",
                $"# Context limit: {result.ContextLimit}",
                $"# Started: {result.StartedUtc:o}",
                $"# Completed: {result.CompletedUtc:o}",
                "",
                "target_tokens,prompt_tokens,ttft_ms,total_ms,tokens_per_sec,completion_tokens,is_success,status",
            };
            foreach (var level in result.Levels)
            {
                lines.Add(string.Join(",",
                    level.PromptTokens, level.PromptTokens, level.TtftMs, level.TotalMs,
                    level.TokensPerSecond.ToString(CultureInfo.InvariantCulture),
                    level.CompletionTokens, level.IsSuccess,
                    $"\"{level.Status.Replace("\"", "\"\"")}\""));
            }
            lines.Add("");
            lines.Add($"# Highest successful: {FormatTokens(result.HighestSuccessfulTargetTokens)}");
            if (result.StoppedEarly)
            {
                lines.Add("# Test stopped early: a level failed, and higher fill levels cannot succeed under the same timeout.");
            }
            lines.Add($"# Limit behavior: {result.LimitBehavior}");
            File.AppendAllText(logPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch { }
    }
}