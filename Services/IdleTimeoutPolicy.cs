using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Idle means no in-flight chat completion, no completed chat (or local launch),
/// and no recent use of the AI-FluxMux window for the configured minutes.
/// Health and /v1/models polls are not activity. A reply still being prepared is never idle.
/// </summary>
public static class IdleTimeoutPolicy
{
    public const int DefaultMinutes = 15;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 240;

    public static int ClampMinutes(int minutes)
    {
        if (minutes < MinMinutes)
        {
            return DefaultMinutes;
        }

        return Math.Clamp(minutes, MinMinutes, MaxMinutes);
    }

    public static bool ShouldUnloadLocal(
        bool enabled,
        bool localAlive,
        int inFlightChatCount,
        DateTime lastActivityUtc,
        DateTime nowUtc,
        int minutes)
    {
        if (!enabled || !localAlive || inFlightChatCount > 0)
        {
            return false;
        }

        if (lastActivityUtc == DateTime.MinValue)
        {
            return false;
        }

        var idleFor = TimeSpan.FromMinutes(ClampMinutes(minutes));
        return nowUtc - lastActivityUtc >= idleFor;
    }

    public static int? GetMinutesRemaining(DateTime lastActivityUtc, DateTime nowUtc, int minutes)
    {
        var limitMinutes = ClampMinutes(minutes);
        if (lastActivityUtc == DateTime.MinValue)
        {
            return limitMinutes;
        }

        var remaining = TimeSpan.FromMinutes(limitMinutes) - (nowUtc - lastActivityUtc);
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
    }
}
