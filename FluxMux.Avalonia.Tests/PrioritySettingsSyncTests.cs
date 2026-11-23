using System.Collections.Generic;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Two-way sync between the priority list and the detailed settings: the q8_0 KV floor follows
/// the order, and a manual edit is described with its trade-off and its effect on the order.
/// </summary>
public sealed class PrioritySettingsSyncTests
{
    private static readonly string[] ContextFirst = ["Context length", "Stability", "Reply length", "Speed", "Fidelity", "Reasoning depth"];
    private static readonly string[] FidelityFirst = ["Fidelity", "Context length", "Speed", "Stability", "Reply length", "Reasoning depth"];

    [Fact]
    public void Kv_floor_applies_only_when_fidelity_outranks_context()
    {
        Assert.False(LocalPrioritySettingsCalculator.KvMinimumApplies(ContextFirst));
        Assert.True(LocalPrioritySettingsCalculator.KvMinimumApplies(FidelityFirst));
    }

    [Fact]
    public void Context_first_keeps_low_precision_kv_and_fidelity_first_raises_it()
    {
        Assert.Equal("q4_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeForPriorities("q4_0", ContextFirst));
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeForPriorities("q4_0", FidelityFirst));
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeForPriorities("", ContextFirst));
    }

    [Fact]
    public void Manual_change_line_names_the_change_the_trade_off_and_the_move()
    {
        var before = new Dictionary<string, string> { ["LocalKvCacheTypeK"] = "q8_0", ["OverrideContext"] = "163840" };
        var after = new Dictionary<string, string> { ["LocalKvCacheTypeK"] = "q4_0", ["OverrideContext"] = "163840" };

        var text = MainViewModel.BuildManualChangeConsequence(before, after, FidelityFirst, ContextFirst);

        Assert.StartsWith("KV key q8_0 → q4_0.", text);
        Assert.Contains("Lower KV precision fits roughly twice the Context", text);
        Assert.Contains("Priorities: Context length moved up to 1st", text);
    }

    [Fact]
    public void Unchanged_order_is_said_plainly()
    {
        var before = new Dictionary<string, string> { ["LocalTemperature"] = "0.3" };
        var after = new Dictionary<string, string> { ["LocalTemperature"] = "0.4" };

        var text = MainViewModel.BuildManualChangeConsequence(before, after, ContextFirst, ContextFirst);

        Assert.Contains("Temperature 0.3 → 0.4.", text);
        Assert.EndsWith("Priority order unchanged.", text);
    }

    [Fact]
    public void Every_trade_off_key_is_a_labelled_setting()
    {
        var labelled = new HashSet<string>();
        foreach (var (key, _) in MainViewModel.LocalDetailedSettingLabels)
        {
            labelled.Add(key);
        }

        foreach (var key in MainViewModel.LocalSettingTradeOffs.Keys)
        {
            Assert.Contains(key, labelled);
        }
    }
}
