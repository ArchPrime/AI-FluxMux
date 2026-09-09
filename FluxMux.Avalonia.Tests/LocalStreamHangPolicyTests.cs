using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalStreamHangPolicyTests
{
    [Fact]
    public void Silent_llama_server_aborts_before_first_byte()
    {
        Assert.True(LocalStreamHangPolicy.ShouldAbort(
            gotUpstreamBytes: false,
            sinceCopyStart: TimeSpan.FromSeconds(25),
            sinceLastUpstreamByte: TimeSpan.FromSeconds(25),
            out var reason));
        Assert.Equal(LocalStreamHangPolicy.FirstByteReason, reason);
    }

    [Fact]
    public void Quiet_before_deadline_does_not_abort()
    {
        Assert.False(LocalStreamHangPolicy.ShouldAbort(
            gotUpstreamBytes: false,
            sinceCopyStart: TimeSpan.FromSeconds(24),
            sinceLastUpstreamByte: TimeSpan.FromSeconds(24),
            out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Mid_stream_silence_aborts_as_stall()
    {
        Assert.True(LocalStreamHangPolicy.ShouldAbort(
            gotUpstreamBytes: true,
            sinceCopyStart: TimeSpan.FromSeconds(60),
            sinceLastUpstreamByte: TimeSpan.FromSeconds(45),
            out var reason));
        Assert.Equal(LocalStreamHangPolicy.StallReason, reason);
    }

    [Fact]
    public void Flowing_tokens_do_not_abort()
    {
        Assert.False(LocalStreamHangPolicy.ShouldAbort(
            gotUpstreamBytes: true,
            sinceCopyStart: TimeSpan.FromMinutes(3),
            sinceLastUpstreamByte: TimeSpan.FromSeconds(1),
            out _));
    }

    [Fact]
    public void Continue_waiting_resets_the_quiet_deadline()
    {
        var clock = new LocalStreamHangClock();
        clock.ResetForWaitLonger();
        Assert.Equal(TimeSpan.FromSeconds(LocalStreamHangPolicy.WaitLongerSeconds), clock.FirstByteDeadline);
        Assert.False(LocalStreamHangPolicy.ShouldAbort(
            gotUpstreamBytes: false,
            sinceCopyStart: TimeSpan.FromSeconds(25),
            sinceLastUpstreamByte: TimeSpan.FromSeconds(25),
            clock.FirstByteDeadline,
            clock.StallDeadline,
            out _));
        Assert.True(LocalStreamHangPolicy.ShouldAbort(
            gotUpstreamBytes: false,
            sinceCopyStart: TimeSpan.FromSeconds(LocalStreamHangPolicy.WaitLongerSeconds),
            sinceLastUpstreamByte: TimeSpan.FromSeconds(LocalStreamHangPolicy.WaitLongerSeconds),
            clock.FirstByteDeadline,
            clock.StallDeadline,
            out var reason));
        Assert.Equal(LocalStreamHangPolicy.FirstByteReason, reason);
    }

    [Fact]
    public void Local_copy_timeout_covers_one_continue_waiting_cycle()
    {
        Assert.True(LocalStreamHangPolicy.MinCopyTimeoutSeconds
            >= LocalStreamHangPolicy.FirstByteSeconds
            + LocalStreamHangPolicy.DecisionSeconds
            + LocalStreamHangPolicy.WaitLongerSeconds
            + LocalStreamHangPolicy.DecisionSeconds);
        Assert.Equal(LocalStreamHangPolicy.MinCopyTimeoutSeconds, LocalStreamHangPolicy.ClampCopyTimeoutSeconds(30));
        Assert.Equal(LocalStreamHangPolicy.MaxCopyTimeoutSeconds, LocalStreamHangPolicy.ClampCopyTimeoutSeconds(10_000));
        Assert.True(LocalStreamHangPolicy.CopyTimeoutExtendSeconds > LocalStreamHangPolicy.WaitLongerSeconds);
    }
}
