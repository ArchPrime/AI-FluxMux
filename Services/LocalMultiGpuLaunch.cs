using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public readonly record struct LocalGpuInventoryEntry(int Index, double TotalGiB, double FreeGiB);

public static class LocalMultiGpuLaunch
{
    public static List<string> BuildArgs(
        JsonObject profile,
        string helpText,
        string offloadMode,
        bool useConservativeLocalLaunch,
        IReadOnlyList<LocalGpuInventoryEntry> inventory)
    {
        if (useConservativeLocalLaunch || offloadMode.Equals("CPU only", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!SupportsArg(helpText, "--main-gpu")
            || !SupportsArg(helpText, "--split-mode")
            || !SupportsArg(helpText, "--tensor-split"))
        {
            return [];
        }

        var mode = NormalizeMode(profile["LocalMultiGpuMode"]?.ToString());
        return mode switch
        {
            "Disabled" => [],
            "Manual" => BuildManualArgs(profile, helpText),
            _ => BuildAutoArgs(inventory, helpText)
        };
    }

    private static List<string> BuildManualArgs(JsonObject profile, string helpText)
    {
        var args = new List<string>();
        var splitMode = NormalizeToken(profile["LocalSplitMode"]?.ToString());
        if (!string.IsNullOrWhiteSpace(splitMode)
            && !splitMode.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && SupportsArg(helpText, "--split-mode"))
        {
            args.Add("--split-mode");
            args.Add(splitMode);
        }

        var tensorSplit = NormalizeToken(profile["LocalTensorSplit"]?.ToString());
        if (!string.IsNullOrWhiteSpace(tensorSplit)
            && !tensorSplit.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && SupportsArg(helpText, "--tensor-split"))
        {
            args.Add("--tensor-split");
            args.Add(tensorSplit);
        }

        var mainGpu = NormalizeToken(profile["LocalMainGpu"]?.ToString());
        if (!string.IsNullOrWhiteSpace(mainGpu)
            && !mainGpu.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(mainGpu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mainGpuIndex)
            && mainGpuIndex >= 0
            && SupportsArg(helpText, "--main-gpu"))
        {
            args.Add("--main-gpu");
            args.Add(mainGpuIndex.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }

    private static List<string> BuildAutoArgs(IReadOnlyList<LocalGpuInventoryEntry> inventory, string helpText)
    {
        if (inventory.Count < 2)
        {
            return [];
        }

        var totalFree = inventory.Sum(entry => entry.FreeGiB);
        if (totalFree <= 0.1)
        {
            return [];
        }

        var primary = inventory.OrderByDescending(entry => entry.FreeGiB).First();
        var weights = inventory
            .Select(entry => Math.Max(1.0, Math.Round(entry.FreeGiB / totalFree * 100.0, 1)))
            .Select(weight => weight.ToString(CultureInfo.InvariantCulture));

        return
        [
            "--main-gpu",
            primary.Index.ToString(CultureInfo.InvariantCulture),
            "--split-mode",
            "layer",
            "--tensor-split",
            string.Join(",", weights)
        ];
    }

    private static string NormalizeMode(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return "Auto";
    }

    private static string NormalizeToken(string? value) => (value ?? string.Empty).Trim();

    private static bool SupportsArg(string helpText, string arg)
        => !string.IsNullOrWhiteSpace(helpText)
            && helpText.Contains(arg, StringComparison.OrdinalIgnoreCase);
}
