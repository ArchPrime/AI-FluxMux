using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Reads the model's real prompt token count from llama-server instead of estimating it from
/// character length. llama-server exposes <c>POST /v1/chat/completions/input_tokens</c>, which
/// accepts the exact chat-completion body and returns the number of input tokens after the
/// model's chat template is applied. This is the ground-truth count for the prompt we would
/// forward, so it is accurate for dense tokenizers (e.g. Qwen) where a chars/4 estimate
/// over-counts and causes false "context full" blocks.
/// </summary>
public static class LocalPromptTokenCounter
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Returns the real input-token count for <paramref name="payload"/>, or <c>null</c> when the
    /// count cannot be obtained (endpoint missing, request failed, or the payload has no
    /// messages). Callers must treat <c>null</c> as "unknown" and fall back to their estimate.
    /// </summary>
    public static async Task<int?> CountAsync(
        int localPort,
        JsonObject payload,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (localPort <= 0 || payload is null || payload["messages"] is not JsonArray { Count: > 0 })
        {
            return null;
        }

        try
        {
            var body = new JsonObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "local" : model,
                ["messages"] = payload["messages"]!.DeepClone()
            };

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://127.0.0.1:{localPort}/v1/chat/completions/input_tokens")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonNode.Parse(raw) as JsonObject;
            if (parsed?["input_tokens"] is JsonValue value
                && int.TryParse(value.ToString(), out var tokens)
                && tokens >= 0)
            {
                return tokens;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            // Re-throw cancellation so the caller can detect it and stop the stress test.
            // Swallowing it here would cause the test to continue running after the user
            // clicked "Cancel stress test".
            throw;
        }
        catch
        {
            // Endpoint not supported, server busy, or any transport error: fall back to the
            // estimate. Never let a token-count probe fail the turn.
            return null;
        }
    }
}