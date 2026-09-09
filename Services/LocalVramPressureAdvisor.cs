using System;

namespace FluxMux.Avalonia.Services;

public readonly record struct LocalVramPressureAssessment(
    bool ShowWarning,
    string Message);

public static class LocalVramPressureAdvisor
{
    public const double HeadroomGiB = 0.75;

    private const string FreeVramStrategyNote =
        " You can also close other graphics-memory apps and browser tabs. Use the GPU & VRAM button in the Diagnostics panel below to see what else is currently using VRAM, and how much comes free when you close things.";

    public static double EffectiveFreeGiB(double freeGiB, double totalGiB, double reclaimableGiB)
    {
        var free = Math.Max(0, freeGiB);
        var reclaimable = Math.Max(0, reclaimableGiB);
        if (totalGiB <= 0)
        {
            return free + reclaimable;
        }

        return Math.Min(totalGiB, free + reclaimable);
    }

    public static LocalVramPressureAssessment Assess(
        double footprintGiB,
        double freeGiB,
        double totalGiB,
        string offloadMode,
        bool imagesOn,
        bool profileCurrentlyLoaded = false,
        double measuredGiB = 0,
        double reclaimableGiB = 0)
    {
        if (footprintGiB <= 0 || totalGiB <= 0)
        {
            return new LocalVramPressureAssessment(false, string.Empty);
        }

        if (profileCurrentlyLoaded
            && measuredGiB > 0
            && measuredGiB <= totalGiB - HeadroomGiB)
        {
            return new LocalVramPressureAssessment(false, string.Empty);
        }

        var effectiveFreeGiB = EffectiveFreeGiB(freeGiB, totalGiB, reclaimableGiB);
        var capacityGiB = profileCurrentlyLoaded ? totalGiB : effectiveFreeGiB;
        if (capacityGiB <= 0)
        {
            return new LocalVramPressureAssessment(false, string.Empty);
        }

        if (footprintGiB <= capacityGiB - HeadroomGiB)
        {
            return new LocalVramPressureAssessment(false, string.Empty);
        }

        var mode = NormalizeOffloadMode(offloadMode);
        var imagesNote = imagesOn
            ? " Images is on, so the mmproj projector adds VRAM for the whole session."
            : string.Empty;
        var capacityText = profileCurrentlyLoaded
            ? $"this local model profile needs about {footprintGiB:0.#} GiB but this graphics card has {totalGiB:0.#} GiB total"
            : reclaimableGiB > 0.05
                ? $"this local model profile needs about {footprintGiB:0.#} GiB but only about {effectiveFreeGiB:0.#} GiB will be free after the current llama-server unloads ({totalGiB:0.#} GiB total)"
                : $"this local model profile needs about {footprintGiB:0.#} GiB but only {freeGiB:0.#} GiB is free on the graphics card ({totalGiB:0.#} GiB total)";
        if (mode.Equals("CPU only", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalVramPressureAssessment(
                true,
                $"VRAM warning: this local model profile is set to CPU only, so llama-server runs mainly in system RAM, not GPU VRAM. "
                + $"The estimate is about {footprintGiB:0.#} GiB for Context and KV cache — replies can be much slower, and the PC may run low on memory if other apps are open."
                + imagesNote);
        }

        if (mode.Equals("GPU only", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalVramPressureAssessment(
                true,
                $"VRAM warning: {capacityText}. "
                + "GPU mode is GPU only, so llama-server cannot spill weights or KV cache to system RAM — launch may fail, or llama-server may exit with an out-of-memory error. "
                + "Try lower Context, turn Images off, or switch to GPU + CPU."
                + FreeVramStrategyNote
                + imagesNote);
        }

        return new LocalVramPressureAssessment(
            true,
            $"VRAM warning: {capacityText}. "
            + "With GPU + CPU (or partial GPU layers), llama-server may spill weights or KV cache to system RAM. Launch may still succeed, but replies can become much slower. "
            + "Try lower Context, turn Images off, or reduce GPU layers if speed matters."
            + FreeVramStrategyNote
            + imagesNote);
    }

    public static string FormatMeasuredFootnote(double measuredGiB, DateTimeOffset? measuredUtc)
    {
        if (measuredGiB <= 0)
        {
            return string.Empty;
        }

        var when = measuredUtc.HasValue
            ? measuredUtc.Value.ToLocalTime().ToString("g")
            : "last launch";
        return $"Measured {measuredGiB:0.#} GiB VRAM delta after load ({when}).";
    }

    private static string NormalizeOffloadMode(string offloadMode)
    {
        var text = (offloadMode ?? string.Empty).Trim();
        if (text.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase)
            || text.Equals("GPU only", StringComparison.OrdinalIgnoreCase)
            || text.Equals("CPU only", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return "GPU + CPU";
    }
}
