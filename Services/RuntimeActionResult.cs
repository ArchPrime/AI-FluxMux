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

public sealed class VramProcessListItem
{
    public string MemoryLabel { get; init; } = string.Empty;

    public string NameLabel { get; init; } = string.Empty;

    public string CountLabel { get; init; } = string.Empty;

    public bool IsCaution { get; init; }
}

public sealed class DisplayAdapterListItem
{
    public string NameLabel { get; init; } = string.Empty;

    public string StatusLabel { get; init; } = string.Empty;

    public string NotesLabel { get; init; } = string.Empty;
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
