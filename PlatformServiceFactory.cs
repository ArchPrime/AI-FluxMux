using FluxMux.Avalonia.Abstractions;

namespace FluxMux.Avalonia;

/// <summary>
/// Returns the platform-appropriate implementation of each platform seam. This is the single
/// place where the app decides "which platform am I on, and which implementation do I use?"
///
/// <para>
/// PHASE 1 (current): The interfaces are defined but the Windows implementations have not yet
/// been moved behind them. Every factory method therefore returns <c>null</c>, which callers
/// treat as "use the existing static Windows code path." This means the running app's behavior
/// is completely unchanged — the interfaces are pure abstraction seams with no behavior yet.
/// </para>
///
/// <para>
/// PHASE 2 (next): Each <c>null</c> return will be replaced with a thin adapter that wraps the
/// existing Windows static class (e.g. <c>new WindowsGpuPreferenceService()</c> implementing
/// <see cref="IGpuPreferenceService"/>). At that point the app routes through the interfaces,
/// and a community member can add a <c>Linux/</c> or <c>MacOS/</c> folder with their own
/// implementations and register them here — without modifying the shared core.
/// </para>
///
/// <para>
/// To add a new platform: implement the five interfaces in a <c>Services/&lt;Platform&gt;/</c>
/// folder, then return those implementations from the matching factory method below (gated on
/// the platform check). Do NOT modify the shared core to add platform behavior.
/// </para>
/// </summary>
public static class PlatformServiceFactory
{
    /// <summary>
    /// Returns the GPU-affinity service for the current platform, or null to use the existing
    /// static Windows code path (Phase 1).
    /// </summary>
    public static IGpuPreferenceService? CreateGpuPreferenceService()
    {
        // Phase 1: no adapter yet — callers fall back to the static Windows code.
        // Phase 2: return new WindowsGpuPreferenceService() on Windows, or the
        // platform-appropriate implementation otherwise.
        return null;
    }

    /// <summary>
    /// Returns the display-topology service for the current platform, or null to use the
    /// existing static Windows code path (Phase 1).
    /// </summary>
    public static IDisplayTopologyService? CreateDisplayTopologyService()
    {
        // Phase 1: no adapter yet.
        return null;
    }

    /// <summary>
    /// Returns the process manager for the current platform, or null to use the existing
    /// static Windows code path (Phase 1).
    /// </summary>
    public static IProcessManager? CreateProcessManager()
    {
        // Phase 1: no adapter yet.
        return null;
    }

    /// <summary>
    /// Returns the update service for the current platform, or null to use the existing
    /// static Windows code path (Phase 1).
    /// </summary>
    public static IUpdateService? CreateUpdateService()
    {
        // Phase 1: no adapter yet.
        return null;
    }

    /// <summary>
    /// Returns the crash logger for the current platform, or null to use the existing
    /// static Windows code path (Phase 1).
    /// </summary>
    public static ICrashLogger? CreateCrashLogger()
    {
        // Phase 1: no adapter yet.
        return null;
    }
}