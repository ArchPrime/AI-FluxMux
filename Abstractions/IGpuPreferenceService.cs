using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Abstractions;

/// <summary>
/// The result of reading the current GPU-affinity preference from the platform.
/// Platform-agnostic: the <see cref="Preference"/> value is the app's internal
/// preference code (see <see cref="GpuPreferenceCode"/>), not a platform-specific value.
/// </summary>
public sealed record GpuPreferenceReadResult(
    bool Supported,
    int Preference,
    string Label);

/// <summary>
/// The result of applying a GPU-affinity preference on the platform.
/// </summary>
public sealed record GpuPreferenceApplyResult(
    bool Succeeded,
    int Preference,
    string Label,
    int Rewritten,
    int Pinned,
    int Skipped,
    string? Error = null);

/// <summary>
/// Platform-agnostic GPU-affinity preference. The app uses these stable codes; each
/// platform maps them to its own mechanism (Windows Registry, nvidia-smi, etc.).
/// </summary>
public static class GpuPreferenceCode
{
    /// <summary>Let the OS decide (default / auto).</summary>
    public const int Auto = 0;

    /// <summary>Prefer the main / discrete graphics card.</summary>
    public const int MainGraphicsCard = 1;

    /// <summary>Prefer the integrated / motherboard graphics card.</summary>
    public const int Integrated = 2;

    /// <summary>Prefer the high-performance (discrete) graphics card.</summary>
    public const int HighPerformance = 3;

    /// <summary>Prefer the power-efficient (integrated) graphics card.</summary>
    public const int PowerSaver = 4;
}

/// <summary>
/// Abstraction over the platform's GPU-affinity mechanism. On Windows this is the
/// DirectX UserGpuPreferences Registry key; on Linux it would be nvidia-smi /
/// prime-select; on macOS it would be the system GPU settings. The app depends only on
/// this interface, so a community member can add a new platform by implementing it
/// without touching the core.
/// </summary>
public interface IGpuPreferenceService
{
    /// <summary>True when the platform supports reading/applying GPU affinity.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Reads the current GPU-affinity preference. Returns <see cref="GpuPreferenceReadResult.Supported"/>
    /// = false when the platform does not expose a preference (the app should fall back to
    /// its default).
    /// </summary>
    GpuPreferenceReadResult ReadCurrent();

    /// <summary>
    /// Applies a GPU-affinity preference for the whole system (or the app, depending on the
    /// platform's granularity). Returns a result describing what changed.
    /// </summary>
    GpuPreferenceApplyResult Apply(int preference, IReadOnlyList<string>? extraPins = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a GPU-affinity preference for a specific app by name. Returns the resulting
    /// preference code, or null when the platform does not support per-app affinity.
    /// </summary>
    int? ApplyForApp(string appName, int preference);

    /// <summary>
    /// Returns the per-app GPU-affinity preference for the given app, or null when unknown
    /// or unsupported.
    /// </summary>
    int? GetAppPreference(string appName);
}