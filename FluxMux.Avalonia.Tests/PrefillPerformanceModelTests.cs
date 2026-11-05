using System;
using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class PrefillPerformanceModelTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux_prefill_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "model.json");
    }

    [Fact]
    public void No_data_uses_conservative_default_speed()
    {
        var model = new PrefillPerformanceModel(TempPath());
        Assert.False(model.HasData);
        Assert.Equal(PrefillPerformanceModel.DefaultTokensPerSec, model.CurrentSpeedTokensPerSec);
        // 10000 tokens at 500 tok/s = 20 seconds.
        Assert.Equal(20, model.EstimateSeconds(10000, isCold: true));
    }

    [Fact]
    public void Unknown_token_count_returns_zero()
    {
        var model = new PrefillPerformanceModel(TempPath());
        Assert.Equal(0, model.EstimateSeconds(0, isCold: true));
        Assert.Equal(0, model.EstimateSeconds(-5, isCold: true));
    }

    [Fact]
    public void Single_sample_sets_speed_from_observation()
    {
        var model = new PrefillPerformanceModel(TempPath());
        // 10000 tokens took 20000 ms => 500 tok/s.
        model.RecordSample(10000, 20000, isCold: true);
        Assert.True(model.HasData);
        Assert.Equal(500.0, model.CurrentSpeedTokensPerSec, 0);
        Assert.Equal(500.0, model.LastMeasuredSpeedTokensPerSec, 0);
        // Estimate should now be ~20s for 10000 tokens (overhead ~0 with a single point).
        Assert.Equal(20, model.EstimateSeconds(10000, isCold: true));
    }

    [Fact]
    public void Multiple_samples_fit_a_slope()
    {
        var model = new PrefillPerformanceModel(TempPath());
        // Simulate a hardware that prefills at ~1000 tok/s with ~1000 ms fixed overhead:
        //   ms = 1000 + tokens / 1000 * 1000 = 1000 + tokens
        model.RecordSample(5000, 6000, isCold: true);   // 1000 + 5000
        model.RecordSample(10000, 11000, isCold: true); // 1000 + 10000
        model.RecordSample(15000, 16000, isCold: true); // 1000 + 15000
        Assert.True(model.HasData);
        // Fitted speed should be close to 1000 tok/s.
        Assert.InRange(model.CurrentSpeedTokensPerSec, 900.0, 1100.0);
        // Estimate for 10000 tokens should be close to 11s.
        var estimate = model.EstimateSeconds(10000, isCold: true);
        Assert.InRange(estimate, 10, 12);
    }

    [Fact]
    public void Small_prompts_update_last_speed_but_not_the_fit()
    {
        var model = new PrefillPerformanceModel(TempPath());
        // A small prompt (< MinFitTokens) should update the last-measured speed but not fit.
        model.RecordSample(200, 1000, isCold: true); // 200 tok/s implied, but too small to fit
        Assert.False(model.HasData); // no fit yet
        Assert.Equal(200.0, model.LastMeasuredSpeedTokensPerSec, 0);
        // Estimate still uses the conservative default.
        Assert.Equal(20, model.EstimateSeconds(10000, isCold: true));
    }

    [Fact]
    public void State_persists_across_instances()
    {
        var path = TempPath();
        var model1 = new PrefillPerformanceModel(path);
        model1.RecordSample(10000, 20000, isCold: true);
        var fittedSpeed = model1.CurrentSpeedTokensPerSec;

        var model2 = new PrefillPerformanceModel(path);
        Assert.True(model2.HasData);
        Assert.Equal(fittedSpeed, model2.CurrentSpeedTokensPerSec, 0);
        Assert.Equal(20, model2.EstimateSeconds(10000, isCold: true));
    }

    [Fact]
    public void Outlier_cold_load_sample_is_rejected()
    {
        var model = new PrefillPerformanceModel(TempPath());
        // One cold-load outlier (model still loading from disk: ~190k tokens in ~1900s =>
        // ~100 tok/s implied, very slow) followed by normal samples (~190k tokens in ~190s =>
        // ~1000 tok/s implied, a realistic cold-prefill speed). The outlier (10x slower than
        // the median) must be rejected by the OutlierSpeedRatio (3x) logic and must not drag
        // the fitted speed down to the clamp floor.
        //
        // NOTE: The implied speeds here are realistic for a 27B model on consumer hardware
        // (cold prefill ~500-1000 tok/s). The previous test used ~190000 tok/s "normal"
        // samples, which are implausible and would be rejected by the MaxPlausibleSpeedTokensPerSec
        // cap (5000 tok/s) that guards against warm-cache delta prefills.
        model.RecordSample(188850, 1900000, isCold: true); // cold-load outlier (very slow: ~100 tok/s)
        model.RecordSample(189234, 189000, isCold: true); // normal (~1001 tok/s)
        model.RecordSample(190170, 190000, isCold: true); // normal (~1001 tok/s)
        model.RecordSample(193546, 193000, isCold: true); // normal (~1003 tok/s)
        model.RecordSample(195995, 195000, isCold: true); // normal (~1005 tok/s)
        Assert.True(model.HasData);
        // The fitted speed should be close to the normal ~1000 tok/s, NOT the ~100 the
        // outlier would imply. Allow a wide band but it must be far above the outlier's speed.
        Assert.True(model.ColdSpeedTokensPerSec > 800,
            $"Expected fitted speed well above the outlier, got {model.ColdSpeedTokensPerSec}");
    }

    [Fact]
    public void Cold_and_warm_models_are_fitted_separately()
    {
        var model = new PrefillPerformanceModel(TempPath());
        // Cold samples: ~500 tok/s (first turn after launch, includes model loading).
        model.RecordSample(10000, 20000, isCold: true);
        model.RecordSample(20000, 40000, isCold: true);
        // Warm samples: ~2000 tok/s (subsequent turns, model already loaded).
        model.RecordSample(10000, 5000, isCold: false);
        model.RecordSample(20000, 10000, isCold: false);

        Assert.True(model.HasData);
        // The cold model should be fitted to ~500 tok/s.
        Assert.InRange(model.ColdSpeedTokensPerSec, 400.0, 600.0);
        // The warm model should be fitted to ~2000 tok/s.
        Assert.InRange(model.WarmSpeedTokensPerSec, 1500.0, 2500.0);
        // The cold estimate should be slower than the warm estimate for the same prompt.
        var coldEstimate = model.EstimateSeconds(10000, isCold: true);
        var warmEstimate = model.EstimateSeconds(10000, isCold: false);
        Assert.True(coldEstimate > warmEstimate,
            $"Expected cold estimate ({coldEstimate}s) to be slower than warm estimate ({warmEstimate}s)");
    }

    [Fact]
    public void Warm_estimate_falls_back_to_cold_when_no_warm_data()
    {
        var model = new PrefillPerformanceModel(TempPath());
        // Only cold samples.
        model.RecordSample(10000, 20000, isCold: true);
        model.RecordSample(20000, 40000, isCold: true);

        // The warm estimate should fall back to the cold model.
        var coldEstimate = model.EstimateSeconds(10000, isCold: true);
        var warmEstimate = model.EstimateSeconds(10000, isCold: false);
        Assert.Equal(coldEstimate, warmEstimate);
    }

    [Fact]
    public void BuildKey_sanitizes_model_and_variant()
    {
        // The "/" in the model name and the "|" separator are both replaced with "_" so the
        // key is filesystem-safe.
        var key = PrefillPerformanceModel.BuildKey("Qwen/Qwen2.5-7B", "Q4_K_M");
        Assert.Equal("Qwen_Qwen2.5-7B_Q4_K_M", key);
    }

    [Fact]
    public void FormatSpeed_formats_and_handles_zero()
    {
        Assert.Equal("750 tok/s", PrefillPerformanceModel.FormatSpeed(750.4));
        Assert.Equal(string.Empty, PrefillPerformanceModel.FormatSpeed(0));
    }
}
