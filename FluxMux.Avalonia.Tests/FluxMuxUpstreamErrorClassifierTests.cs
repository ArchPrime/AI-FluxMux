using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class FluxMuxUpstreamErrorClassifierTests
{
    [Fact]
    public void Copilot_token_usage_429_is_quota_exhausted_not_short_rate_limit()
    {
        var detail =
            """{"error":{"message":"Sorry, you have exceeded your Copilot token usage. Please review our Terms of Service.","code":"rate_limited"}}""";

        var (_, errorType, _, _) = FluxMuxUpstreamErrorClassifier.Classify(
            429,
            detail,
            "Copilot GitHub",
            "gpt-4.1");

        Assert.Equal("quota_exhausted", errorType);
    }

    [Fact]
    public void Plain_429_without_quota_language_stays_rate_limited()
    {
        var detail = """{"error":{"message":"Too many requests. Please wait a moment.","code":"rate_limited"}}""";

        var (_, errorType, _, _) = FluxMuxUpstreamErrorClassifier.Classify(
            429,
            detail,
            "Copilot GitHub",
            "gpt-4.1");

        Assert.Equal("rate_limited", errorType);
    }

    [Fact]
    public void Insufficient_quota_is_quota_exhausted()
    {
        var (_, errorType, _, _) = FluxMuxUpstreamErrorClassifier.Classify(
            429,
            """{"error":{"type":"insufficient_quota","message":"You exceeded your current quota"}}""",
            "OpenAI",
            "gpt-4o");

        Assert.Equal("quota_exhausted", errorType);
    }

    [Theory]
    [InlineData("Sorry, you have exceeded your Copilot token usage.", 429, true)]
    [InlineData("rate limit exceeded", 429, false)]
    [InlineData("Payment required", 402, true)]
    public void LooksLikeQuotaExhaustion_matches_expected(string blob, int status, bool expected)
    {
        Assert.Equal(expected, FluxMuxUpstreamErrorClassifier.LooksLikeQuotaExhaustion(blob.ToLowerInvariant(), status));
    }

    [Theory]
    [InlineData(400, "exceeds the available context size", "Local", "local")]
    [InlineData(400, "no user query found in messages", "Local", "local")]
    [InlineData(503, "model is loading", "Local", "local")]
    [InlineData(500, "cuda_error_out_of_memory", "Local", "local")]
    [InlineData(401, "invalid_api_key", "OpenAI", "gpt-4o")]
    [InlineData(429, "rate limit exceeded", "OpenAI", "gpt-4o")]
    [InlineData(404, "model_not_found", "OpenAI", "gpt-4o")]
    [InlineData(504, "timed out", "Local", "local")]
    [InlineData(400, "failed to load image", "Local", "local")]
    public void Endpoint_error_copy_is_short_and_has_no_hint(int status, string detail, string provider, string model)
    {
        var (message, _, _, hint) = FluxMuxUpstreamErrorClassifier.Classify(status, detail, provider, model);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.True(string.IsNullOrEmpty(hint));
        Assert.InRange(message.Length, 1, 180);
        Assert.DoesNotContain("Diagnostics", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Compact only", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".vscode", message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Cloud_image_rejection_names_the_cloud_not_the_loaded_local()
    {
        var (message, errorType, _, _) = FluxMuxUpstreamErrorClassifier.Classify(
            400,
            "invalid_request_error: image input is not supported",
            "Lab Cloud",
            "lab-model");

        Assert.Equal(FluxMuxGatewayRouting.CloudVisionUnavailableType, errorType);
        Assert.Contains("Lab Cloud", message, System.StringComparison.Ordinal);
        Assert.Contains("cannot take pictures", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Images is off", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("loaded model profile", message, System.StringComparison.Ordinal);
    }
}
