using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Abstractions;

/// <summary>
/// A process observed on the platform, with enough detail for the app to reason about it
/// (name, PID, and optionally the GPU memory it is using).
/// </summary>
public sealed record ObservedProcess(
    int Pid,
    string Name,
    double GpuMemoryGiB = 0);

/// <summary>
/// The owner of a TCP port, when the platform can resolve it.
/// </summary>
public sealed record PortOwner(
    int Pid,
    string Name);

/// <summary>
/// Abstraction over the platform's process-management mechanisms: enumerating processes,
/// resolving which process owns a TCP port, and killing a process tree. On Windows these
/// use <c>Process.GetProcesses</c>, <c>netstat -ano</c>, and <c>Process.Kill</c>; on
/// Linux/macOS they would use <c>ps</c>, <c>lsof</c>/<c>ss</c>, and <c>kill</c>.
///
/// This is the largest of the five platform seams because process management is scattered
/// across the runtime service today. The goal is to gather all of it behind this interface
/// so a new platform implements it once.
/// </summary>
public interface IProcessManager
{
    /// <summary>
    /// Returns true when a process with the given name is currently running.
    /// </summary>
    bool IsProcessRunning(string processName);

    /// <summary>
    /// Enumerates all running processes. Used for GPU-memory attribution and the
    /// "Apps &amp; GPU affinity" list.
    /// </summary>
    IReadOnlyList<ObservedProcess> GetProcesses();

    /// <summary>
    /// Resolves which process (if any) is listening on the given TCP port on loopback.
    /// Returns null when the port is free or the owner cannot be resolved.
    /// </summary>
    PortOwner? GetPortOwner(int port);

    /// <summary>
    /// Kills the process with the given PID and its entire process tree. Best-effort:
    /// returns true when the process was signalled, false otherwise.
    /// </summary>
    bool KillProcessTree(int pid);

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> milliseconds for the given TCP port on
    /// loopback to close (i.e. no longer accepting connections). Returns true when the
    /// port closed within the timeout.
    /// </summary>
    Task<bool> WaitForPortToCloseAsync(int port, int timeoutMs, CancellationToken cancellationToken = default);
}