using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public readonly record struct LocalValidateSpeedMetrics(
    bool IsSuccess,
    long TtftMs,
    long TotalMs,
    double TokensPerSecond,
    int CompletionTokens,
    string Details);

public static class LocalValidateSpeedProbe
{
    public static async Task<LocalValidateSpeedMetrics> ProbePublicEndpointAsync(
        HttpClient httpClient,
        int publicPort,
        string model,
        CancellationToken cancellationToken,
        int maxTokens = 32,
        int timeoutMs = 90000)
    {
        var endpoint = $"http://127.0.0.1:{publicPort}/v1/chat/completions";
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "local" : model,
            ["stream"] = true,
            ["temperature"] = 0.2,
            ["max_tokens"] = Math.Clamp(maxTokens, 8, 128),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Reply with exactly: OK"
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation(
            CloudEndpointValidationProbe.ForceRouteHeader,
            FluxMuxGatewayRouting.Local);

        var sw = Stopwatch.StartNew();
        long ttftMs = 0;
        var completionChars = 0;
        var usageCompletionTokens = 0;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                sw.Stop();
                return new LocalValidateSpeedMetrics(
                    false,
                    0,
                    sw.ElapsedMilliseconds,
                    0,
                    0,
                    HttpErrorResponseFormatter.FormatHttpErrorDetail((int)response.StatusCode, response.ReasonPhrase, body));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!TryExtractDeltaText(data, out var delta, out var usageTokens))
                {
                    continue;
                }

                if (usageTokens > 0)
                {
                    usageCompletionTokens = usageTokens;
                }

                if (delta.Length == 0)
                {
                    continue;
                }

                if (ttftMs <= 0)
                {
                    ttftMs = sw.ElapsedMilliseconds;
                }

                completionChars += delta.Length;
            }

            sw.Stop();
            var completionTokens = usageCompletionTokens > 0 ? usageCompletionTokens : Math.Max(1, completionChars / 4);
            var generateMs = Math.Max(1, sw.ElapsedMilliseconds - Math.Max(0, ttftMs));
            var tokPerSec = completionTokens / (generateMs / 1000d);
            return new LocalValidateSpeedMetrics(
                true,
                ttftMs > 0 ? ttftMs : sw.ElapsedMilliseconds,
                sw.ElapsedMilliseconds,
                Math.Round(tokPerSec, 1),
                completionTokens,
                $"Validate speed: TTFT {ttftMs} ms, {tokPerSec:0.#} tok/s over {completionTokens} tokens.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new LocalValidateSpeedMetrics(
                false,
                ttftMs,
                sw.ElapsedMilliseconds,
                0,
                0,
                "Validate speed probe timed out.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new LocalValidateSpeedMetrics(
                false,
                ttftMs,
                sw.ElapsedMilliseconds,
                0,
                0,
                ex.GetBaseException().Message);
        }
    }

    private static bool TryExtractDeltaText(string data, out string delta, out int usageCompletionTokens)
    {
        delta = string.Empty;
        usageCompletionTokens = 0;
        try
        {
            if (JsonNode.Parse(data) is not JsonObject root)
            {
                return false;
            }

            if (root["usage"]?["completion_tokens"] is JsonValue usage
                && usage.TryGetValue<int>(out var tokens))
            {
                usageCompletionTokens = tokens;
            }

            var choice = root["choices"]?[0] as JsonObject;
            delta = choice?["delta"]?["content"]?.ToString()
                ?? choice?["message"]?["content"]?.ToString()
                ?? choice?["text"]?.ToString()
                ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
