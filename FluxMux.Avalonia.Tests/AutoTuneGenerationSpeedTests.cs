using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// AutoTune now scores trials by generation speed (tok/s) and, for MTP models, tries draft
/// lengths instead of swapping to ngram-simple. The new profile-editor fields must also count as
/// launch-critical, so changing them asks for a relaunch.
/// </summary>
public sealed class AutoTuneGenerationSpeedTests
{
    [Theory]
    [InlineData(null, new[] { "3", "4" })]
    [InlineData("Auto", new[] { "3", "4" })]
    [InlineData("2", new[] { "3", "4" })]
    [InlineData("3", new[] { "2", "4" })]
    [InlineData("4", new[] { "2", "3" })]
    [InlineData("6", new[] { "2", "3", "4" })]
    public void Draft_length_trials_skip_the_current_value(string? current, string[] expected)
    {
        Assert.Equal(expected, MainViewModel.DraftLengthTrialValues(current));
    }

    [Fact]
    public void Small_differences_are_treated_as_noise()
    {
        Assert.False(MainViewModel.IsMeaningfulSpeedGain(135.0, 136.5)); // ~1%
        Assert.True(MainViewModel.IsMeaningfulSpeedGain(135.0, 152.0));  // ~12.6%
        Assert.False(MainViewModel.IsMeaningfulSpeedGain(0, 152.0));
    }

    [Fact]
    public void Summary_reports_percentage_and_rates_for_a_real_win()
    {
        var text = MainViewModel.BuildLocalTuneResultSummary(152.0, "Draft length 3", 135.0, extraTrialsRan: true);
        Assert.Contains("13% faster", text);
        Assert.Contains("135 tok/s", text);
        Assert.Contains("152 tok/s", text);
        Assert.Contains("Draft length 3", text);
    }

    [Fact]
    public void Summary_keeps_usual_settings_when_gain_is_noise()
    {
        var text = MainViewModel.BuildLocalTuneResultSummary(136.0, "Draft length 4", 135.0, extraTrialsRan: true);
        Assert.Contains("did not generate clearly faster", text);
    }

    [Fact]
    public void Change_list_uses_page_labels_in_page_order()
    {
        var before = new Dictionary<string, string>
        {
            ["LocalSpecDraftNMax"] = "3",
            ["LocalKvCacheTypeK"] = "q4_0",
            ["OverrideContext"] = "393216",
            ["LocalExtendContextIntoRam"] = "True",
            ["LocalBatchSize"] = "1024"
        };
        var after = new Dictionary<string, string>(before)
        {
            ["LocalSpecDraftNMax"] = "4",
            ["LocalKvCacheTypeK"] = "q8_0",
            ["OverrideContext"] = "163840",
            ["LocalExtendContextIntoRam"] = "False"
        };

        Assert.Equal(
            "Extend context into RAM (YaRN) On → Off; Context 393216 → 163840; KV key q4_0 → q8_0; Draft length 3 → 4",
            MainViewModel.BuildLabelledChangeList(before, after));
        Assert.Equal(string.Empty, MainViewModel.BuildLabelledChangeList(before, before));
    }

    [Fact]
    public void Restoring_pre_autotune_settings_keeps_current_validation_state()
    {
        var live = new JsonObject { ["EndpointValidatedUtc"] = "2026-11-23T05:07:44Z", ["LocalSpecDraftNMax"] = "4" };
        var oldCopy = new JsonObject
        {
            ["EndpointValidatedUtc"] = "2026-10-01T00:00:00Z",
            ["EndpointWarning"] = "llama-server exited during startup.",
            ["LocalSpecDraftNMax"] = "3"
        };

        MainViewModel.CarryCurrentValidationState(live, oldCopy);

        Assert.Equal("2026-11-23T05:07:44Z", oldCopy["EndpointValidatedUtc"]?.ToString());
        Assert.Null(oldCopy["EndpointWarning"]);                       // stale ⚠ not revived
        Assert.Equal("3", oldCopy["LocalSpecDraftNMax"]?.ToString());  // settings still restored
    }

    [Theory]
    [InlineData("256", "256")]
    [InlineData("512", "512")]
    [InlineData("1024", "1024")]   // was 256 (batch ÷ 4): measured ~40% slower prompt reading
    [InlineData("2048", "1024")]   // capped: 2048 measured no faster than 1024
    [InlineData("32", "64")]
    [InlineData("not a number", "512")]
    public void Micro_batch_follows_batch_up_to_the_cap(string batch, string expected)
    {
        Assert.Equal(expected, LocalPrioritySettingsCalculator.DeriveUbatch(batch));
    }

    [Fact]
    public void Every_labelled_key_is_unique()
    {
        var keys = MainViewModel.LocalDetailedSettingLabels.Select(entry => entry.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Same_tune_settings_compares_every_key()
    {
        var a = new Dictionary<string, string> { ["LocalSpecDraftNMax"] = "3", ["LocalBatchSize"] = "1024" };
        var same = new Dictionary<string, string> { ["LocalBatchSize"] = "1024", ["LocalSpecDraftNMax"] = "3" };
        var other = new Dictionary<string, string> { ["LocalSpecDraftNMax"] = "4", ["LocalBatchSize"] = "1024" };
        Assert.True(MainViewModel.SameTuneSettings(a, same));
        Assert.False(MainViewModel.SameTuneSettings(a, other));
    }

    [Theory]
    [InlineData("Fidelity")]
    [InlineData("Stability")]
    [InlineData("Speed")]
    public void Priority_order_never_changes_the_speculative_method(string first)
    {
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            ContextPriorityFirst = 131072,
            ContextPrioritySecond = 65536,
            ContextSafeDefault = 65536,
            ContextPriorityLower = 32768,
            OffloadMode = "GPU only",
            OffloadModeAtMaxContext = "GPU only",
            FlashAttention = "Enabled",
            PreferFitEnabled = true,
            SpecType = "ngram-simple",
            BatchSize = "1024",
            CacheRam = "8192"
        };
        var order = new List<string> { first };
        order.AddRange(new[] { "Fidelity", "Stability", "Speed", "Context length", "Reply length", "Reasoning depth" }
            .Where(name => name != first));
        foreach (var chosen in new[] { "draft-mtp", "Disabled", "ngram-simple" })
        {
            var settings = LocalPrioritySettingsCalculator.Calculate(
                LocalPrioritySettingsCalculator.ResolveOrder(order),
                advice, "Auto", chosen, "Disabled", true, "Disabled", string.Empty, "1344", "8", "4");
            Assert.Equal(chosen, settings["LocalSpecType"]);
        }
    }

    [Theory]
    [InlineData("LocalSpecDraftNMax", "3")]
    [InlineData("LocalTopK", "20")]
    [InlineData("LocalTopP", "0.95")]
    [InlineData("LocalMinP", "0")]
    [InlineData("LocalPresencePenalty", "1.5")]
    [InlineData("LocalReasoningBudget", "2048")]
    public void New_launch_flags_change_the_launch_fingerprint(string key, string value)
    {
        var baseline = new JsonObject { ["OverrideContext"] = "131072", ["LocalSpecType"] = "draft-mtp" };
        var changed = (JsonObject)baseline.DeepClone();
        changed[key] = value;

        Assert.NotEqual(LocalLaunchFingerprint.From(baseline), LocalLaunchFingerprint.From(changed));
        Assert.NotEqual(ProfileSettingsFingerprint.Local(baseline), ProfileSettingsFingerprint.Local(changed));
    }
}
