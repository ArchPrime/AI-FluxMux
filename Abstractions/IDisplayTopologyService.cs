using System.Collections.Generic;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.Abstractions;

/// <summary>
/// The result of detecting the display topology on the platform: which GPU drives each
/// monitor, the overall topology, and advice text. Mirrors the shape of
/// <see cref="DisplayTopologyService.TopologyResult"/> so the Windows implementation can
/// return it directly, but is defined here so other platforms can produce the same shape.
/// </summary>
public sealed record DisplayTopologyDetectionResult(
    bool Detected,
    DisplayTopology Topology,
    IReadOnlyList<MonitorPortRow> Monitors,
    string Advice,
    string TopologySummary);

/// <summary>
/// Abstraction over the platform's display-enumeration mechanism. On Windows this uses
/// P/Invoke <c>EnumDisplayDevices</c>; on Linux it would use xrandr / Wayland; on macOS
/// it would use CoreGraphics <c>CGGetActiveDisplayList</c>.
///
/// NOTE: The pure decision logic (classifying a topology, building advice text) is
/// platform-agnostic and lives in the shared <see cref="DisplayTopologyService"/> static
/// helpers (<c>ClassifyTopology</c>, <c>BuildAdvice</c>, <c>SummarizeTopology</c>). Only the
/// raw enumeration of "which GPU drives which monitor" is platform-specific and belongs
/// behind this interface.
/// </summary>
public interface IDisplayTopologyService
{
    /// <summary>True when the platform supports display-topology detection.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Enumerates the monitors and which GPU drives each, returning the detected topology.
    /// Must never throw: any platform failure degrades to a <see cref="Detected"/> = false
    /// / <see cref="DisplayTopology.Unknown"/> result so the UI can fall back gracefully.
    /// </summary>
    DisplayTopologyDetectionResult Detect();
}