using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public sealed class LocalEndpointCandidate
{
    public int Port { get; init; }

    public string OwnerName { get; init; } = string.Empty;

    public int OwnerPid { get; init; }

    public string Detail { get; init; } = string.Empty;
}

public sealed class InstalledLocalServerCandidate
{
    public string Name { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;
}

public sealed class LocalEndpointDiscoveryResult
{
    public bool Found { get; init; }

    public bool Compatible { get; init; }

    public int Port { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public IReadOnlyList<LocalEndpointCandidate> CompatibleServers { get; init; } = Array.Empty<LocalEndpointCandidate>();

    public IReadOnlyList<InstalledLocalServerCandidate> InstalledServers { get; init; } = Array.Empty<InstalledLocalServerCandidate>();
}
