using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public static class LocalLaunchStatus
{
    public const string RouteReady = "Local route ready";
    public const string BackendReady = "llama-server ready";
    public const string WarmingUp = "Local route warming up";
    public const string StillInProgress = "Local launch still in progress";
}

public enum GpuKind
{
    Unknown,
    DiscreteVram,
    MotherboardRam
}

public sealed class VramProcessListItem
{
    public string MemoryLabel { get; init; } = string.Empty;

    public string NameLabel { get; init; } = string.Empty;

    public string RawName { get; init; } = string.Empty;

    /// <summary>
    /// The full executable path of the process (e.g. "C:\Program Files\Graphisoft\Archicad 30\node.exe").
    /// Used by the GPU toggle to write the registry for the correct executable, not just the
    /// process name (which could match multiple node.exe instances).
    /// </summary>
    public string FullPath { get; init; } = string.Empty;

    public string CountLabel { get; init; } = string.Empty;

    public bool IsCaution { get; init; }

    public GpuKind GpuKind { get; init; } = GpuKind.Unknown;

    public bool CanToggle { get; init; } = true;
}

public sealed class DisplayAdapterListItem
{
    public string NameLabel { get; init; } = string.Empty;

    public string StatusLabel { get; init; } = string.Empty;

    public string NotesLabel { get; init; } = string.Empty;
}

/// <summary>
/// Which GPU physically drives a monitor, as detected from the display device tree.
/// Used by the "Monitor ports in use" table and the topology advice block.
/// </summary>
public enum MonitorDriver
{
    Unknown,
    IntegratedGpu,
    DiscreteGpu
}

/// <summary>
/// A single physical monitor and which GPU drives it. Populated by
/// <see cref="DisplayTopologyService"/> from the Windows display device tree
/// (EnumDisplayDevices). <see cref="Port"/> and <see cref="Resolution"/> are
/// best-effort and may be empty when the OS does not expose them.
/// </summary>
public sealed class MonitorPortRow
{
    public int MonitorIndex { get; init; }

    public string MonitorName { get; init; } = string.Empty;

    public MonitorDriver DrivenBy { get; init; } = MonitorDriver.Unknown;

    public string DrivenByLabel { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;

    public string Resolution { get; init; } = string.Empty;

    public bool IsPrimary { get; init; }
}

/// <summary>
/// The detected arrangement of monitors across GPUs. Drives which advice text
/// and which switchability behaviour the Settings tab shows.
/// </summary>
public enum DisplayTopology
{
    Unknown,
    AllOnIgpu,
    AllOnDgpu,
    Hybrid
}

/// <summary>
/// A single app row in the "Apps &amp; GPU affinity" list on the Settings tab.
/// Shows both apps currently on the Discrete GPU and apps on the Motherboard GPU,
/// with a toggle to flip an app between the two. Apps that are pinned (e.g.
/// llama-server) have <see cref="CanToggle"/> set to false and the toggle is disabled.
/// </summary>
public sealed class GpuAppListItem
{
    public string AppName { get; init; } = string.Empty;

    /// <summary>
    /// The full executable path of the process (e.g. "C:\Program Files\Graphisoft\Archicad 30\node.exe").
    /// Used by the GPU toggle to write the registry for the correct executable, not just the
    /// process name (which could match multiple node.exe instances).
    /// </summary>
    public string FullPath { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string StatusLabel { get; init; } = string.Empty;

    public string MemoryLabel { get; init; } = "—";

    /// <summary>
    /// Which GPU the app is currently running on (based on the running process's actual GPU usage).
    /// This is the "current" state.
    /// </summary>
    public bool IsOnMotherboardGpu { get; init; }

    /// <summary>
    /// True when the registry has been set to a different GPU than the app is currently running on.
    /// The app will switch to the registry's GPU after it restarts. The toggle should show the
    /// *target* GPU (flipped from current) with a pending indicator.
    /// </summary>
    public bool HasPendingGpuChange { get; init; }

    /// <summary>
    /// Which GPU the registry is set to (the target after restart). Only meaningful when
    /// <see cref="HasPendingGpuChange"/> is true.
    /// </summary>
    public bool TargetIsMotherboardGpu { get; init; }

    /// <summary>
    /// Which GPU the app is actually running on right now (from the process's GpuKind).
    /// This is the "current" state, as opposed to <see cref="IsOnMotherboardGpu"/> which
    /// shows the target when a pending change exists. ToggleGpuAppAsync uses this to
    /// compute the flip target: clicking the toggle always moves the app to the opposite
    /// of its CURRENT GPU, so clicking a pending toggle reverses the pending change.
    /// </summary>
    public bool CurrentIsMotherboardGpu { get; init; }

    public bool CanToggle { get; init; } = true;
}

public sealed class RuntimeActionResult
{
    public bool IsSuccess { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public string AdvancedDetails { get; init; } = string.Empty;

    public IReadOnlyList<VramProcessListItem> VramProcessItems { get; init; } = [];

    public IReadOnlyList<DisplayAdapterListItem> DisplayAdapterItems { get; init; } = [];

    public int GpuCardCount { get; init; }

    public bool IsLocalRouteReady =>
        Status.Equals(LocalLaunchStatus.RouteReady, StringComparison.OrdinalIgnoreCase);

    public bool IsLocalRouteWarming =>
        Status is LocalLaunchStatus.WarmingUp or LocalLaunchStatus.StillInProgress;
}

public sealed class LocalGenerationTimingResult
{
    public bool IsSuccess { get; init; }

    public long LatencyMs { get; init; }

    public string Details { get; init; } = string.Empty;
}

public sealed class CloudModelCatalogResult
{
    public bool IsSuccess { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public IReadOnlyList<string> Models { get; init; } = [];

    public IReadOnlyDictionary<string, CloudCatalogVision.Traits> TraitsByModel { get; init; } =
        new Dictionary<string, CloudCatalogVision.Traits>(StringComparer.OrdinalIgnoreCase);
}
