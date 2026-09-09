using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalMultiGpuLaunchTests
{
    private const string Help = "--main-gpu --split-mode --tensor-split";

    [Fact]
    public void BuildArgs_auto_balances_two_gpus()
    {
        var profile = new JsonObject { ["LocalMultiGpuMode"] = "Auto" };
        var inventory = new List<LocalGpuInventoryEntry>
        {
            new(0, 24, 20),
            new(1, 12, 8)
        };

        var args = LocalMultiGpuLaunch.BuildArgs(profile, Help, "GPU + CPU", useConservativeLocalLaunch: false, inventory);
        Assert.Equal(
            ["--main-gpu", "0", "--split-mode", "layer", "--tensor-split", "71.4,28.6"],
            args);
    }

    [Fact]
    public void BuildArgs_disabled_returns_empty_even_with_inventory()
    {
        var profile = new JsonObject { ["LocalMultiGpuMode"] = "Disabled" };
        var inventory = new List<LocalGpuInventoryEntry> { new(0, 24, 20), new(1, 12, 8) };
        var args = LocalMultiGpuLaunch.BuildArgs(profile, Help, "GPU + CPU", false, inventory);
        Assert.Empty(args);
    }

    [Fact]
    public void BuildArgs_manual_uses_profile_values()
    {
        var profile = new JsonObject
        {
            ["LocalMultiGpuMode"] = "Manual",
            ["LocalSplitMode"] = "row",
            ["LocalMainGpu"] = "1",
            ["LocalTensorSplit"] = "30,70"
        };

        var args = LocalMultiGpuLaunch.BuildArgs(profile, Help, "GPU only", false, []);
        Assert.Equal(["--split-mode", "row", "--tensor-split", "30,70", "--main-gpu", "1"], args);
    }
}
