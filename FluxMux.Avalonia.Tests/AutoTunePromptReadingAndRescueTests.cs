using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.ContextEngine;
using FluxMux.Avalonia.ViewModels;
using Xunit;
using Kind = FluxMux.Avalonia.ViewModels.MainViewModel.TuneTrialKind;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Speed AutoTune judging Micro-batch on prompt reading, the llama-server path self-repair,
/// and the bounded essence carried through a Hail Mary middle-cut.
/// </summary>
public sealed class AutoTunePromptReadingAndRescueTests
{
    private static MainViewModel.TuneTrialResult Result(string label, Kind kind, double gen, double? prompt, params (string Key, string Value)[] changes)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LocalSpecDraftNMax"] = "2",
            ["LocalUbatchSize"] = "256",
            ["LocalBatchSize"] = "1024",
        };
        foreach (var (key, value) in changes)
        {
            settings[key] = value;
        }

        return new MainViewModel.TuneTrialResult(new MainViewModel.LocalMeasuredTuneTrial(label, settings, label, kind), gen, prompt);
    }

    [Fact]
    public void Combines_best_draft_length_and_best_micro_batch()
    {
        var results = new[]
        {
            Result("baseline", Kind.Baseline, 135, 3937),
            Result("Micro-batch 512", Kind.PromptReading, 134, 5196, ("LocalUbatchSize", "512")),
            Result("Micro-batch 1024", Kind.PromptReading, 133, 5577, ("LocalUbatchSize", "1024")),
            Result("Draft length 3", Kind.Generation, 152, 3900, ("LocalSpecDraftNMax", "3")),
            Result("Draft length 4", Kind.Generation, 145, 3900, ("LocalSpecDraftNMax", "4")),
        };

        var combined = MainViewModel.CombineTuneResults(results)!.Value;

        Assert.Equal("3", combined.Settings["LocalSpecDraftNMax"]);
        Assert.Equal("1024", combined.Settings["LocalUbatchSize"]);
        Assert.Equal(new[] { "Draft length 3", "Micro-batch 1024" }, combined.Chosen);
    }

    [Fact]
    public void Micro_batch_that_slows_generation_is_rejected()
    {
        var results = new[]
        {
            Result("baseline", Kind.Baseline, 135, 3937),
            Result("Micro-batch 1024", Kind.PromptReading, 120, 5577, ("LocalUbatchSize", "1024")), // -11% generation
        };

        var combined = MainViewModel.CombineTuneResults(results)!.Value;

        Assert.Equal("256", combined.Settings["LocalUbatchSize"]);
        Assert.Empty(combined.Chosen);
        Assert.Null(combined.BestPrompt);
    }

    [Fact]
    public void Noise_level_gains_keep_the_baseline()
    {
        var results = new[]
        {
            Result("baseline", Kind.Baseline, 135, 3937),
            Result("Micro-batch 512", Kind.PromptReading, 135, 3990, ("LocalUbatchSize", "512")), // +1.3%
            Result("Draft length 3", Kind.Generation, 136, 3937, ("LocalSpecDraftNMax", "3")),   // +0.7%
        };

        var combined = MainViewModel.CombineTuneResults(results)!.Value;

        Assert.Empty(combined.Chosen);
        Assert.Contains("no trial was clearly faster", MainViewModel.BuildCombinedTuneSummary(combined.Baseline, null, null));
    }

    [Fact]
    public void No_measured_baseline_returns_null()
    {
        Assert.Null(MainViewModel.CombineTuneResults([Result("Draft length 3", Kind.Generation, 152, null)]));
    }

    [Fact]
    public void Summary_reports_both_stages()
    {
        var baseline = Result("baseline", Kind.Baseline, 135, 3937);
        var text = MainViewModel.BuildCombinedTuneSummary(
            baseline,
            Result("Draft length 3", Kind.Generation, 152, null),
            Result("Micro-batch 1024", Kind.PromptReading, 133, 5577));
        Assert.Contains("Generation: 135 tok/s → 152 tok/s with Draft length 3.", text);
        Assert.Contains("Prompt reading: 3,937 tok/s → 5,577 tok/s with Micro-batch 1024.", text);
    }

    [Theory]
    [InlineData(256, new[] { 512, 1024 })]
    [InlineData(512, new[] { 1024 })]
    [InlineData(1024, new[] { 512 })]
    public void Micro_batch_trials_skip_the_current_size(int current, int[] expected)
    {
        Assert.Equal(expected, MainViewModel.MicroBatchTrialValues(current));
    }

    [Fact]
    public void Path_repair_finds_the_moved_build_before_backups()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxmux-llama-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The 23 Nov layout: new build one level down, old build in a stamped backup.
            var moved = Path.Combine(root, "llama-server", "llama-server.exe");
            var backup = Path.Combine(root, "llama-server-backup", "llama-server-20261123-021658", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllText(moved, "x");
            File.WriteAllText(backup, "x");
            var missingConfigured = Path.Combine(root, "llama-server.exe");

            var found = FluxMux.Avalonia.Services.FluxMuxRuntimeService.NearbyLlamaServerCandidates(missingConfigured, Path.Combine(root, "nowhere"));

            Assert.Equal(new[] { moved, backup }, found);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Rescue_block_includes_decisions_and_dead_ends_and_stays_bounded()
    {
        var log = new EssenceLog(new FakeContextIo(), "/tmp/essence.json");
        log.UpdateCurrent("Speed up prompt reading", "benchmarked ub sizes", "commit", "");
        log.RecordDecision("Micro-batch = Batch, capped at 1024");
        log.RecordDeadEnd("ub 2048 was no faster");
        for (var i = 0; i < 200; i++)
        {
            log.RecordDecision("filler decision number " + i + " " + new string('x', 200));
        }

        var block = log.Load().ToRescueBlock();

        Assert.Contains("Key decisions:", block);
        Assert.Contains("Dead ends (do not retry):", block);
        Assert.Contains("ub 2048 was no faster", block);
        Assert.True(block.Length <= 3_001, "length " + block.Length);
    }
}
