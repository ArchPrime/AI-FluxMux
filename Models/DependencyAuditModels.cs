using System.Collections.Generic;

namespace FluxMux.Avalonia.Models;

public sealed class DependencyAuditReport
{
    public string HardwareSummary { get; init; } = "Hardware telemetry unavailable.";

    public IReadOnlyList<DependencyMatrixRow> CoreDependencies { get; init; } = [];

    public IReadOnlyList<DependencyMatrixRow> ProviderAddons { get; init; } = [];

    public IReadOnlyList<InstallGuideRow> CoreInstallGuide { get; init; } = [];

    public IReadOnlyList<InstallGuideRow> SdkInstallGuide { get; init; } = [];

    public IReadOnlyList<ProviderRequirementRow> ProviderRequirements { get; init; } = [];

    public IReadOnlyList<PathStatusRow> HardRequiredPaths { get; init; } = [];

    public IReadOnlyList<PathStatusRow> RuntimePaths { get; init; } = [];

    public IReadOnlyList<LocalModelCapabilityRow> LocalModelCapabilities { get; init; } = [];
}

public sealed class DependencyMatrixRow
{
    public string Dependency { get; init; } = string.Empty;

    public string Minimum { get; init; } = string.Empty;

    public string Installed { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Guidance { get; init; } = string.Empty;

    public string RowBackground { get; set; } = string.Empty;
}

public sealed class InstallGuideRow
{
    public string Dependency { get; init; } = string.Empty;

    public string InstallOrUpdate { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Guidance { get; init; } = string.Empty;

    public string RowBackground { get; set; } = string.Empty;
}

public sealed class ProviderRequirementRow
{
    public string Provider { get; init; } = string.Empty;

    public string Sdk { get; init; } = string.Empty;

    public string RequiredFiles { get; init; } = string.Empty;

    public string RequiredFields { get; init; } = string.Empty;

    public string Guidance { get; init; } = string.Empty;

    public string RowBackground { get; set; } = string.Empty;
}

public sealed class PathStatusRow
{
    public string Component { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string ResolvedPath { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Guidance { get; init; } = string.Empty;

    public string RowBackground { get; set; } = string.Empty;
}

public sealed class LocalModelCapabilityRow
{
    public string Model { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string VisionSupport { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string Guidance { get; init; } = string.Empty;

    public string RowBackground { get; set; } = string.Empty;
}
