using System;
using System.Collections.Generic;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Tests for <see cref="ContextFillStressResult.LimitBehavior"/> and related summary fields.
/// The stress test deliberately probes up to and past the context limit, so the result can have
/// zero levels (first level failed before producing a result), partial levels (stopped early or
/// aborted), or the full set. The summary must describe each case accurately.
/// </summary>
public sealed class ContextFillStressResultTests
{
    private static ContextFillStressResult Result(
        IReadOnlyList<ContextFillLevelResult> levels,
        bool stoppedEarly = false,
        bool aborted = false)
    {
        return new ContextFillStressResult
        {
            Model = "test.gguf",
            Variant = "default",
            ContextLimit = 393216,
            HardwareContext = "GPU 32 GB, RAM 64 GB, offload GPU only, NVIDIA True",
            Levels = levels,
            StartedUtc = new DateTime(2026, 10, 16, 12, 53, 36, DateTimeKind.Utc),
            CompletedUtc = new DateTime(2026, 10, 16, 12, 56, 51, DateTimeKind.Utc),
            StoppedEarly = stoppedEarly,
            Aborted = aborted
        };
    }

    private static ContextFillLevelResult Level(int fill, bool success, string status)
    {
        // Use fill * 1000 as the prompt token count so the LimitBehavior
        // (which now uses PromptTokens) produces meaningful output.
        var promptTokens = fill * 1000;
        return new ContextFillLevelResult(
            fill, promptTokens, 393216, 0, 0, 0, 0, success, status);
    }

    [Fact]
    public void LimitBehavior_with_no_levels_does_not_report_a_bogus_100_percent_failure()
    {
        // Regression: when zero levels completed (e.g. the very first level failed before
        // producing a result, or the test was aborted immediately), FirstOrDefault on an empty
        // list returned a default struct (IsSuccess=false, Status=""), which read as
        // "Failed at 100% fill ()". It must instead say plainly that no level completed.
        var result = Result(Array.Empty<ContextFillLevelResult>(), stoppedEarly: false, aborted: false);
        Assert.Equal("No fill level completed — no limit behavior observed.", result.LimitBehavior);
        Assert.Equal(0, result.HighestSuccessfulFillPercent);
    }

    [Fact]
    public void LimitBehavior_with_no_levels_and_aborted_still_reports_no_level_completed()
    {
        var result = Result(Array.Empty<ContextFillLevelResult>(), aborted: true);
        Assert.Equal("No fill level completed — no limit behavior observed.", result.LimitBehavior);
    }

    [Fact]
    public void LimitBehavior_stopped_early_reports_the_failing_level()
    {
        var levels = new List<ContextFillLevelResult>
        {
            Level(25, true, "success"),
            Level(50, true, "success"),
            Level(75, false, "HTTP 400: prompt too large")
        };
        var result = Result(levels, stoppedEarly: true);
        // 75 * 1000 = 75000 tokens → "75,000"
        Assert.Contains("Stopped at 75,000", result.LimitBehavior);
        Assert.Contains("HTTP 400: prompt too large", result.LimitBehavior);
        Assert.Equal(50, result.HighestSuccessfulFillPercent);
    }

    [Fact]
    public void LimitBehavior_when_100_succeeds_and_110_fails()
    {
        var levels = new List<ContextFillLevelResult>
        {
            Level(25, true, "success"),
            Level(50, true, "success"),
            Level(75, true, "success"),
            Level(90, true, "success"),
            Level(100, true, "success"),
            Level(110, false, "HTTP 400: context overflow")
        };
        var result = Result(levels);
        // 110 * 1000 = 110000 tokens → "110,000"
        // 100 * 1000 = 100000 tokens → "100,000"
        Assert.Contains("Failed at 110,000", result.LimitBehavior);
        Assert.Contains("100,000 succeeded", result.LimitBehavior);
    }

    [Fact]
    public void LimitBehavior_when_100_fails()
    {
        var levels = new List<ContextFillLevelResult>
        {
            Level(25, true, "success"),
            Level(50, true, "success"),
            Level(100, false, "HTTP 400: context overflow")
        };
        var result = Result(levels);
        // 100 * 1000 = 100000 tokens → "100,000" (N0 format)
        Assert.Contains("Failed at 100,000", result.LimitBehavior);
    }

    [Fact]
    public void LimitBehavior_when_110_succeeds_reports_no_failure()
    {
        var levels = new List<ContextFillLevelResult>
        {
            Level(100, true, "success"),
            Level(110, true, "success")
        };
        var result = Result(levels);
        // 110 * 1000 = 110000 tokens → "110,000" (N0 format)
        Assert.Contains("No failure observed up to 110,000", result.LimitBehavior);
    }

    [Fact]
    public void LimitBehavior_partial_levels_without_100_or_110_reports_failure()
    {
        // Aborted mid-run at 50%, so neither a 100% nor 110% level exists. The new logic
        // reports the actual failure level and the highest successful level.
        var levels = new List<ContextFillLevelResult>
        {
            Level(25, true, "success"),
            Level(50, false, "timed out")
        };
        var result = Result(levels, aborted: true);
        // 50 * 1000 = 50000 tokens → "50,000" (N0 format)
        // 25 * 1000 = 25000 tokens → "25,000" (N0 format)
        Assert.Contains("Failed at 50,000", result.LimitBehavior);
        Assert.Contains("25,000 succeeded", result.LimitBehavior);
    }

    [Fact]
    public void ComputeFillLevels_vram_only_262K_gives_4_steps_of_64K()
    {
        // Fixed 64K step. Levels: 64K, 128K, 192K, 256K. 256K (262144) >= 262144, so stop.
        var levels = ContextFillStressProbe.ComputeFillLevels(262144, 0);
        Assert.Equal(4, levels.Length);
        Assert.Equal(65536, levels[0]);
        Assert.Equal(131072, levels[1]);
        Assert.Equal(196608, levels[2]);
        Assert.Equal(262144, levels[3]);
    }

    [Fact]
    public void ComputeFillLevels_yarn_393K_gives_6_steps_of_64K_stopping_at_exact_limit()
    {
        // Fixed 64K step (same for every model, so results are comparable across profiles).
        // Levels: 64K, 128K, 192K, 256K, 320K, 384K. 384K (393216) == the 393216 limit, so we
        // stop there (stopping at an exact level is fine — no need to add a level beyond it).
        var levels = ContextFillStressProbe.ComputeFillLevels(393216, 262144);
        Assert.Equal(6, levels.Length);
        Assert.Equal(65536, levels[0]);
        Assert.Equal(131072, levels[1]);
        Assert.Equal(196608, levels[2]);
        Assert.Equal(262144, levels[3]);
        Assert.Equal(327680, levels[4]);
        Assert.Equal(393216, levels[5]);
    }

    [Fact]
    public void ComputeFillLevels_small_32K_gives_single_64K_step_beyond_limit()
    {
        // Fixed 64K step. The first level (64K = 65536) is already >= 32768, so only one level.
        var levels = ContextFillStressProbe.ComputeFillLevels(32768, 0);
        Assert.Equal(1, levels.Length);
        Assert.Equal(65536, levels[0]);
    }

    [Fact]
    public void ComputeFillLevels_zero_context_returns_default()
    {
        var levels = ContextFillStressProbe.ComputeFillLevels(0, 0);
        Assert.Equal(ContextFillStressProbe.DefaultFillLevels, levels);
    }
}