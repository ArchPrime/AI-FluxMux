using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Tests the generation-based, one-shot launch-supersede cancellation in FluxMuxGatewayHost.
/// The old design used a single shared CancellationTokenSource that was cancelled on every
/// supersede and never reset, so every subsequent local request inherited a dead token and
/// 502'd immediately — the "keeps generating errors" storm when an impatient user mashes
/// Launch/retry. The new design uses a monotonically increasing generation counter: a handler
/// only honors a supersede if the generation advanced AFTER it started.
/// </summary>
public sealed class LaunchSupersedeGenerationTests
{
    private static FluxMuxGatewayHost CreateHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);
        return new FluxMuxGatewayHost(
            P("state.json"),
            P("last_served.json"),
            P("recommend.json"),
            P("cloud_breaker.json"),
            P("local_reload.json"),
            P("log.txt"),
            P("context.jsonl"),
            P("last_route.json"));
    }

    [Fact]
    public void Initial_generation_is_zero_and_no_supersede_is_detected()
    {
        var host = CreateHost();
        Assert.Equal(0, host.CurrentSupersedeGeneration);
        // A request that starts at generation 0 has not been superseded.
        Assert.False(host.WasSupersededAfterStart(0));
    }

    [Fact]
    public void CancelInFlightHandlers_bumps_generation_and_detects_supersede_for_earlier_requests()
    {
        var host = CreateHost();
        int startGen = host.CurrentSupersedeGeneration; // 0

        host.CancelInFlightHandlers();

        // Generation advanced.
        Assert.Equal(startGen + 1, host.CurrentSupersedeGeneration);
        // A request that started BEFORE the supersede is now detected as superseded.
        Assert.True(host.WasSupersededAfterStart(startGen));
    }

    [Fact]
    public void Request_starting_after_supersede_is_not_cancelled()
    {
        var host = CreateHost();
        host.CancelInFlightHandlers(); // generation -> 1

        // A NEW request that starts after the supersede captures generation 1.
        int startGen = host.CurrentSupersedeGeneration; // 1
        Assert.False(host.WasSupersededAfterStart(startGen));
    }

    [Fact]
    public void Multiple_supersedes_are_cumulative_and_one_shot_per_request()
    {
        var host = CreateHost();
        int gen0 = host.CurrentSupersedeGeneration; // 0

        host.CancelInFlightHandlers(); // -> 1
        host.CancelInFlightHandlers(); // -> 2

        Assert.Equal(gen0 + 2, host.CurrentSupersedeGeneration);
        // A request that started at gen0 was superseded (by either).
        Assert.True(host.WasSupersededAfterStart(gen0));
        // A request that started at the latest generation (2) was NOT superseded.
        Assert.False(host.WasSupersededAfterStart(host.CurrentSupersedeGeneration));
    }

    [Fact]
    public void Supersede_is_thread_safe_under_concurrent_cancels()
    {
        var host = CreateHost();
        int before = host.CurrentSupersedeGeneration;
        const int cancels = 200;
        Parallel.For(0, cancels, _ => host.CancelInFlightHandlers());
        // Each cancel increments exactly once; no lost updates.
        Assert.Equal(before + cancels, host.CurrentSupersedeGeneration);
    }
}