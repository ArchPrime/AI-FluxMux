using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Detects which GPU physically drives each monitor by walking the Windows display
/// device tree (EnumDisplayDevices). Top-level display devices are the adapters
/// (e.g. "Intel(R) Graphics", "NVIDIA GeForce RTX 5090"); their child devices are
/// the attached monitors. A monitor's driving GPU is the adapter it hangs under.
///
/// This is the reliable path on Windows 10/11: QueryDisplayConfig's P/Invoke is
/// brittle (struct layout / E_INVALIDARG across OS builds), whereas EnumDisplayDevices
/// is stable and gives exactly the adapter-per-monitor mapping we need.
///
/// The pure decision logic (<see cref="ClassifyTopology"/>, <see cref="BuildAdvice"/>)
/// is separated from the P/Invoke so it can be unit-tested without touching the OS.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class DisplayTopologyService
{
    public const string IgpuLabel = "Motherboard GPU (iGPU)";
    public const string DgpuLabel = "Discrete GPU";
    public const string UnknownLabel = "Unknown";

    /// <summary>
    /// Result of a topology scan: the detected arrangement, the per-monitor table,
    /// and the advice text to show. <see cref="Detected"/> is false when the display
    /// device tree could not be read (in which case everything degrades to Unknown).
    /// </summary>
    public sealed class TopologyResult
    {
        public bool Detected { get; init; }

        public DisplayTopology Topology { get; init; } = DisplayTopology.Unknown;

        public IReadOnlyList<MonitorPortRow> Monitors { get; init; } = [];

        public string Advice { get; init; } = string.Empty;

        public string TopologySummary { get; init; } = string.Empty;
    }

    /// <summary>
    /// Walks the display device tree and returns the detected topology, per-monitor
    /// rows, and advice text. Never throws: any P/Invoke or parsing failure degrades
    /// to a Detected=false / Unknown result so the UI can fall back gracefully.
    /// </summary>
    public static TopologyResult Detect()
    {
        try
        {
            var adapters = EnumerateAdapters();
            var monitors = new List<MonitorPortRow>();
            int monitorIndex = 0;

            foreach (var adapter in adapters)
            {
                var driver = ClassifyDriver(adapter.AdapterName);
                var childMonitors = EnumerateMonitorsForAdapter(adapter.DeviceName);
                foreach (var mon in childMonitors)
                {
                    monitorIndex++;
                    monitors.Add(new MonitorPortRow
                    {
                        MonitorIndex = monitorIndex,
                        MonitorName = mon.Name,
                        DrivenBy = driver,
                        DrivenByLabel = LabelForDriver(driver),
                        Port = mon.Port,
                        Resolution = mon.Resolution,
                        IsPrimary = mon.IsPrimary
                    });
                }
            }

            var topology = ClassifyTopology(monitors);
            return new TopologyResult
            {
                Detected = true,
                Topology = topology,
                Monitors = monitors,
                Advice = BuildAdvice(topology, monitors),
                TopologySummary = SummarizeTopology(topology, monitors)
            };
        }
        catch
        {
            return new TopologyResult
            {
                Detected = false,
                Topology = DisplayTopology.Unknown,
                Monitors = [],
                Advice = BuildAdvice(DisplayTopology.Unknown, []),
                TopologySummary = string.Empty
            };
        }
    }
    // ------------------------------------------------------------------
    // Pure decision logic (testable without P/Invoke)
    // ------------------------------------------------------------------

    /// <summary>
    /// Classifies a GPU's friendly name as iGPU or dGPU, reusing the existing
    /// DisplayAdapterLabeling heuristics so the classification stays consistent
    /// with the rest of the app. Returns Unknown when the name cannot be classified.
    /// </summary>
    public static MonitorDriver ClassifyDriver(string? adapterName)
    {
        var kind = DisplayAdapterLabeling.ClassifyMemoryKind(adapterName ?? string.Empty);
        return kind switch
        {
            DisplayAdapterLabeling.AdapterMemoryKind.SharedSystemRam => MonitorDriver.IntegratedGpu,
            DisplayAdapterLabeling.AdapterMemoryKind.DedicatedVram => MonitorDriver.DiscreteGpu,
            _ => MonitorDriver.Unknown
        };
    }

    public static string LabelForDriver(MonitorDriver driver) => driver switch
    {
        MonitorDriver.IntegratedGpu => IgpuLabel,
        MonitorDriver.DiscreteGpu => DgpuLabel,
        _ => UnknownLabel
    };

    /// <summary>
    /// Determines the overall topology from the per-monitor driving GPUs.
    /// - No monitors (or all Unknown)  -> Unknown
    /// - All on iGPU                    -> AllOnIgpu
    /// - All on dGPU                    -> AllOnDgpu
    /// - Mix of iGPU and dGPU           -> Hybrid
    /// </summary>
    public static DisplayTopology ClassifyTopology(IEnumerable<MonitorPortRow> monitors)
    {
        bool anyIgpu = false;
        bool anyDgpu = false;
        int known = 0;

        foreach (var mon in monitors)
        {
            switch (mon.DrivenBy)
            {
                case MonitorDriver.IntegratedGpu:
                    anyIgpu = true;
                    known++;
                    break;
                case MonitorDriver.DiscreteGpu:
                    anyDgpu = true;
                    known++;
                    break;
                default:
                    break;
            }
        }

        if (known == 0)
        {
            return DisplayTopology.Unknown;
        }

        if (anyIgpu && anyDgpu)
        {
            return DisplayTopology.Hybrid;
        }

        return anyDgpu ? DisplayTopology.AllOnDgpu : DisplayTopology.AllOnIgpu;
    }
    /// <summary>
    /// Builds the advice block text for the detected topology. The copy is kept
    /// accurate to how Windows hybrid graphics actually works:
    /// - AllOnIgpu: full software control (per-app + desktop) via the registry.
    /// - AllOnDgpu: app-level toggles still work, but the desktop stays on the dGPU
    ///   while a monitor uses its port, so there is an irreducible compositing overhead.
    /// - Hybrid: the dGPU-port monitor keeps an irreducible compositing overhead; the
    ///   iGPU-port monitor is free. Good for "one monitor always full-perf".
    /// - Unknown: we could not detect which ports the monitors use.
    /// </summary>
    public static string BuildAdvice(DisplayTopology topology, IEnumerable<MonitorPortRow> monitors)
    {
        var list = monitors?.ToList() ?? [];
        return topology switch
        {
            DisplayTopology.AllOnIgpu => BuildAllOnIgpuAdvice(),
            DisplayTopology.AllOnDgpu => BuildAllOnDgpuAdvice(),
            DisplayTopology.Hybrid => BuildHybrid(list),
            _ => BuildUnknown()
        };
    }

    /// <summary>
    /// The "all monitors on the iGPU" advice, exposed so the "Other options" view can
    /// describe this arrangement as an alternative to the currently-detected topology.
    /// </summary>
    public static string BuildAllOnIgpuAdvice()
    {
        return "Your displays are driven by the integrated GPU (iGPU). This is the most flexible "
            + "option, as it lets Windows Registry settings control graphics routing on a per-app "
            + "basis. If you toggle apps and desktop affinity to the iGPU, most of your discrete "
            + "GPU's VRAM is then free for local models. If you toggle apps and desktop affinity to "
            + "the Discrete GPU, they will operate with nearly full accelerated performance, but "
            + "leave less VRAM for local AI models. There is often a small loss in performance when "
            + "a Discrete-GPU app's output is handed off to the iGPU to reach a monitor on an iGPU "
            + "port. Best results apply when using iGPU ports of the highest specification your "
            + "monitor(s) will support (such as Thunderbolt or DisplayPort), and high-bandwidth "
            + "short cables.";
    }

    /// <summary>
    /// The "all monitors on the dGPU" advice, exposed so the "Other options" view can
    /// describe this arrangement as an alternative to the currently-detected topology.
    /// </summary>
    public static string BuildAllOnDgpuAdvice()
    {
        return "All your displays are plugged into your discrete GPU ports. This is the least "
            + "flexible option: app-level toggles still work — you can move a browser or game to the "
            + "iGPU to free discrete-GPU VRAM (that app renders on the iGPU and its output is handed to "
            + "the Discrete GPU for display). What you cannot do is move the desktop itself off the "
            + "Discrete GPU while a monitor uses its port, so there is an irreducible compositing "
            + "overhead. The desktop and any apps that stay on the Discrete GPU run in its VRAM and "
            + "maximise graphical performance, but leave less VRAM free for local models. To free up "
            + "VRAM you can close some running apps or move eligible apps to the iGPU, but the desktop's "
            + "share of the Discrete GPU's VRAM cannot be removed without moving a monitor to an iGPU port.";
    }

    /// <summary>
    /// A generic "hybrid / mixed" arrangement description (no per-monitor sentences), used by
    /// the "Other options" view when the current topology is not hybrid.
    /// </summary>
    public static string BuildHybridAdviceGeneric()
    {
        return "Some monitors are on the Discrete GPU and some on the iGPU. "
            + "The Discrete-GPU monitor still needs VRAM for compositing. This offers a fair "
            + "amount of flexibility, for when you want apps or games on at least one monitor to "
            + "always run with full graphical performance, without messing around unplugging and "
            + "replugging monitor cables. You can toggle many things to use the iGPU, but there "
            + "will always be a higher irreducible VRAM overhead this way.";
    }

    private static string BuildHybrid(IReadOnlyList<MonitorPortRow> monitors)
    {
        var sentences = new List<string>();
        foreach (var mon in monitors)
        {
            sentences.Add($"Monitor {mon.MonitorIndex} is on the {mon.DrivenByLabel}.");
        }

        var monitorLines = string.Join(" ", sentences);
        return monitorLines
            + " The Discrete-GPU monitor still needs VRAM for compositing. This offers a fair "
            + "amount of flexibility, for when you want apps or games on at least one monitor to "
            + "always run with full graphical performance, without messing around unplugging and "
            + "replugging monitor cables. You can toggle many things to use the iGPU, but there "
            + "will always be a higher irreducible VRAM overhead this way.";
    }

    private static string BuildUnknown()
    {
        return "Could not detect which ports your monitors use. You can still switch app affinity "
            + "manually below.";
    }

    /// <summary>
    /// A short one-line summary of the detected topology, e.g. "2 monitors, both on the iGPU".
    /// </summary>
    public static string SummarizeTopology(DisplayTopology topology, IReadOnlyList<MonitorPortRow> monitors)
    {
        if (monitors.Count == 0)
        {
            return string.Empty;
        }

        int igpu = 0;
        int dgpu = 0;
        foreach (var mon in monitors)
        {
            if (mon.DrivenBy == MonitorDriver.IntegratedGpu) igpu++;
            else if (mon.DrivenBy == MonitorDriver.DiscreteGpu) dgpu++;
        }

        return topology switch
        {
            DisplayTopology.AllOnIgpu => $"{monitors.Count} monitor(s), all on the iGPU",
            DisplayTopology.AllOnDgpu => $"{monitors.Count} monitor(s), all on the Discrete GPU",
            DisplayTopology.Hybrid => $"{monitors.Count} monitor(s): {igpu} on the iGPU, {dgpu} on the Discrete GPU",
            _ => $"{monitors.Count} monitor(s), topology not detected"
        };
    }
    // ------------------------------------------------------------------
    // P/Invoke enumeration (isolated so the pure logic above stays testable)
    // ------------------------------------------------------------------

    private sealed class AdapterInfo
    {
        public string AdapterName = string.Empty;
        public string DeviceName = string.Empty;
    }

    private sealed class MonitorInfo
    {
        public string Name = string.Empty;
        public string Port = string.Empty;
        public string Resolution = string.Empty;
        public bool IsPrimary;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DataIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Local;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint dwIndex, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    // DISPLAY_DEVICE_STATE_CONNECTED: the monitor is actually attached.
    private const uint DISPLAY_DEVICE_STATE_CONNECTED = 0x00000002;

    private static List<AdapterInfo> EnumerateAdapters()
    {
        var adapters = new List<AdapterInfo>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0))
            {
                break;
            }

            adapters.Add(new AdapterInfo
            {
                AdapterName = dd.DeviceString ?? string.Empty,
                DeviceName = dd.DeviceName ?? string.Empty
            });
        }

        return adapters;
    }

    private static List<MonitorInfo> EnumerateMonitorsForAdapter(string adapterName)
    {
        var monitors = new List<MonitorInfo>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(adapterName, i, ref dd, 0))
            {
                break;
            }

            // Only count monitors that are actually connected (state has the CONNECTED bit).
            if ((dd.StateFlags & DISPLAY_DEVICE_STATE_CONNECTED) == 0)
            {
                continue;
            }

            monitors.Add(new MonitorInfo
            {
                Name = dd.DeviceString ?? string.Empty,
                Port = string.Empty,
                Resolution = string.Empty,
                IsPrimary = false
            });
        }

        return monitors;
    }
}