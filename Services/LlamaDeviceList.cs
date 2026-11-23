using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>One compute device as llama-server reports it in <c>--list-devices</c>.</summary>
public sealed record LlamaDevice(string Backend, int Index, string Name, double TotalMiB, double FreeMiB);

/// <summary>
/// Parses <c>llama-server --list-devices</c>, the vendor-neutral way to learn which GPUs
/// llama-server can use and how much memory each has. Hardware advice used to read VRAM only
/// from <c>nvidia-smi</c>, so on an AMD or Intel card (or a Vulkan / ROCm / SYCL build) the GPU
/// size came back as 0 GB and AutoTune advised as if there were no graphics card at all. The
/// device list also matches exactly what llama-server will use, whatever the vendor.
///
/// Format (llama.cpp b12xxx):
/// <code>
/// Available devices:
///   CUDA0: NVIDIA GeForce RTX 5090 (32579 MiB, 24904 MiB free)
///   Vulkan0: AMD Radeon RX 7900 XTX (24560 MiB, 23800 MiB free)
/// </code>
/// </summary>
public static class LlamaDeviceList
{
    private static readonly Regex DeviceLine = new(
        @"^\s*(?<backend>[A-Za-z]+?)(?<index>\d+):\s*(?<name>.+?)\s*\((?<total>\d+(?:\.\d+)?)\s*MiB,\s*(?<free>\d+(?:\.\d+)?)\s*MiB free\)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// GPU devices in <paramref name="output"/>. CPU entries, if a build lists them, are skipped
    /// because system RAM is accounted for separately.
    /// </summary>
    public static IReadOnlyList<LlamaDevice> Parse(string? output)
    {
        var devices = new List<LlamaDevice>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return devices;
        }

        foreach (Match match in DeviceLine.Matches(output))
        {
            var backend = match.Groups["backend"].Value;
            if (backend.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            devices.Add(new LlamaDevice(
                backend,
                int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value.Trim(),
                double.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["free"].Value, CultureInfo.InvariantCulture)));
        }

        return devices;
    }
}
