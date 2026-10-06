using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Assesses the risk of a local model launch overflowing into system RAM and triggering
/// Windows paging (which can cause "device failure" messages, massive slowdowns, and reboots).
/// This is a *systemic* risk warning, distinct from the VRAM pressure warning (which is about
/// speed). It works regardless of whether YaRN / "Extend into RAM" is on or off.
/// </summary>
public readonly record struct LocalRamOverflowAssessment(
    bool ShowWarning,
    string Message);

public static class LocalRamOverflowAdvisor
{
    /// <summary>
    /// Minimum RAM (GiB) to leave free for the OS + other processes. If the model + KV cache
    /// footprint exceeds (totalRam - reserve), the OS will start paging to the SSD, which can
    /// cause "device failure" messages, massive slowdowns, and reboots.
    /// </summary>
    public const double ReserveGiB = 8.0;

    /// <summary>
    /// Assesses whether a local model launch is likely to overflow into system RAM.
    /// </summary>
    /// <param name="modelFootprintGiB">Estimated model + KV cache footprint in GiB.</param>
    /// <param name="totalRamGiB">Total system RAM in GiB.</param>
    /// <param name="offloadMode">The GPU offload mode (CPU only / GPU only / GPU + CPU).</param>
    /// <param name="contextTokens">The actual context window (tokens) being launched.</param>
    /// <returns>A warning if the footprint is likely to overflow into RAM, otherwise no warning.</returns>
    public static LocalRamOverflowAssessment Assess(
        double modelFootprintGiB,
        double totalRamGiB,
        string offloadMode,
        int contextTokens)
    {
        if (modelFootprintGiB <= 0 || totalRamGiB <= 0)
        {
            return new LocalRamOverflowAssessment(false, string.Empty);
        }

        // GPU-only mode: the model + KV must fit in VRAM, not RAM. No RAM overflow risk.
        if (offloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalRamOverflowAssessment(false, string.Empty);
        }

        // CPU-only or GPU+CPU: the model + KV can spill into system RAM.
        double reserveGiB = Math.Min(ReserveGiB, totalRamGiB * 0.25);
        double availableRamGiB = Math.Max(0.0, totalRamGiB - reserveGiB);

        if (modelFootprintGiB <= availableRamGiB)
        {
            return new LocalRamOverflowAssessment(false, string.Empty);
        }

        double overflowGiB = modelFootprintGiB - availableRamGiB;
        var modeNote = offloadMode.Equals("CPU only", StringComparison.OrdinalIgnoreCase)
            ? "The model runs entirely in system RAM."
            : "With GPU + CPU, the model weights and/or KV cache spill into system RAM.";

        return new LocalRamOverflowAssessment(
            true,
            $"⚠ RAM overflow risk: this local model profile needs about {modelFootprintGiB:0.#} GiB, "
            + $"but only about {availableRamGiB:0.#} GiB of system RAM is available after reserving "
            + $"{reserveGiB:0.#} GiB for the OS and other apps ({totalRamGiB:0.#} GiB total). "
            + $"{modeNote} The overflow (~{overflowGiB:0.#} GiB) will be paged to the SSD, which can cause "
            + "massive slowdowns, 'device failure' messages in Windows, and even a reboot. "
            + "Try a smaller model, lower the Context, or switch to GPU only if your VRAM can hold it.");
    }
}