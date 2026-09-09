using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class IdleTimeoutPolicyTests
{
    [Fact]
    public void Long_in_flight_chat_is_never_idle()
    {
        var started = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = started.AddHours(3);
        Assert.False(IdleTimeoutPolicy.ShouldUnloadLocal(
            enabled: true,
            localAlive: true,
            inFlightChatCount: 1,
            lastActivityUtc: started,
            nowUtc: now,
            minutes: 15));
    }

    [Fact]
    public void Unload_after_quiet_period_with_no_in_flight_chat()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = last.AddMinutes(15);
        Assert.True(IdleTimeoutPolicy.ShouldUnloadLocal(
            enabled: true,
            localAlive: true,
            inFlightChatCount: 0,
            lastActivityUtc: last,
            nowUtc: now,
            minutes: 15));
    }

    [Fact]
    public void Health_style_quiet_under_threshold_does_not_unload()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = last.AddMinutes(14);
        Assert.False(IdleTimeoutPolicy.ShouldUnloadLocal(
            enabled: true,
            localAlive: true,
            inFlightChatCount: 0,
            lastActivityUtc: last,
            nowUtc: now,
            minutes: 15));
    }

    [Fact]
    public void Disabled_or_no_local_model_does_not_unload()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = last.AddHours(1);
        Assert.False(IdleTimeoutPolicy.ShouldUnloadLocal(false, true, 0, last, now, 15));
        Assert.False(IdleTimeoutPolicy.ShouldUnloadLocal(true, false, 0, last, now, 15));
    }

    [Fact]
    public void Minutes_remaining_counts_down_from_last_activity()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(15, IdleTimeoutPolicy.GetMinutesRemaining(last, last.AddMinutes(0), 15));
        Assert.Equal(1, IdleTimeoutPolicy.GetMinutesRemaining(last, last.AddMinutes(14), 15));
        Assert.Equal(0, IdleTimeoutPolicy.GetMinutesRemaining(last, last.AddMinutes(15), 15));
    }
}
