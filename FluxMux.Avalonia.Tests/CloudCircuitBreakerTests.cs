using System;
using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudCircuitBreakerTests : IDisposable
{
    private readonly string _path;

    public CloudCircuitBreakerTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "fluxmux-breaker-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Quota_failure_opens_breaker_for_hours()
    {
        CloudCircuitBreaker.RecordFailure(_path, "Gemini", "gemini-flash-lite-latest", "quota_exhausted");

        Assert.True(CloudCircuitBreaker.TryGetBlock(_path, "Gemini", "gemini-flash-lite-latest", out var block));
        Assert.Equal("quota_exhausted", block.Reason);
        Assert.True(block.OpenUntilUtc > DateTimeOffset.UtcNow.Add(CloudCircuitBreaker.QuotaOpenDuration - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Rate_limit_uses_retry_after_when_provided()
    {
        CloudCircuitBreaker.RecordFailure(_path, "Gemini", "gemini-flash-lite-latest", "rate_limited", 120);

        Assert.True(CloudCircuitBreaker.TryGetBlock(_path, "Gemini", "gemini-flash-lite-latest", out var block));
        Assert.Equal("rate_limited", block.Reason);
        Assert.InRange(block.RetryAfterSeconds, 110, 120);
    }

    [Fact]
    public void Success_clears_transient_breaker_but_not_quota()
    {
        CloudCircuitBreaker.RecordFailure(_path, "Gemini", "model-a", "rate_limited");
        CloudCircuitBreaker.RecordSuccess(_path, "Gemini", "model-a");
        Assert.False(CloudCircuitBreaker.TryGetBlock(_path, "Gemini", "model-a", out _));

        CloudCircuitBreaker.RecordFailure(_path, "Gemini", "model-b", "quota_exhausted");
        CloudCircuitBreaker.RecordSuccess(_path, "Gemini", "model-b");
        Assert.True(CloudCircuitBreaker.TryGetBlock(_path, "Gemini", "model-b", out _));
    }

    [Fact]
    public void Launch_clear_removes_open_breaker()
    {
        CloudCircuitBreaker.RecordFailure(_path, "Gemini", "gemini-flash-lite-latest", "quota_exhausted");
        CloudCircuitBreaker.Clear(_path, "Gemini", "gemini-flash-lite-latest");
        Assert.False(CloudCircuitBreaker.TryGetBlock(_path, "Gemini", "gemini-flash-lite-latest", out _));
    }

    [Fact]
    public void Auth_failures_do_not_open_breaker()
    {
        CloudCircuitBreaker.RecordFailure(_path, "Gemini", "gemini-flash-lite-latest", "auth_failed");
        Assert.False(CloudCircuitBreaker.TryGetBlock(_path, "Gemini", "gemini-flash-lite-latest", out _));
    }
}
