using System;
using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Vendor-neutral GPU detection (llama-server --list-devices), the start-up write-access check,
/// and the rolling, dated log writer.
/// </summary>
public sealed class PortabilityAndLoggingTests
{
    [Fact]
    public void Parses_cuda_vulkan_and_rocm_devices()
    {
        const string output = """
            ggml_cuda_init: found 1 CUDA devices
            Available devices:
              CUDA0: NVIDIA GeForce RTX 5090 (32579 MiB, 24904 MiB free)
              Vulkan0: AMD Radeon RX 7900 XTX (24560 MiB, 23800 MiB free)
              ROCm1: AMD Instinct MI100 (32752.5 MiB, 32000 MiB free)
              CPU: AMD Ryzen 9 (65536 MiB, 40000 MiB free)
            """;

        var devices = LlamaDeviceList.Parse(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(new LlamaDevice("CUDA", 0, "NVIDIA GeForce RTX 5090", 32579, 24904), devices[0]);
        Assert.Equal("Vulkan", devices[1].Backend);
        Assert.Equal("AMD Radeon RX 7900 XTX", devices[1].Name);
        Assert.Equal(24560, devices[1].TotalMiB);
        Assert.Equal("ROCm", devices[2].Backend);
        Assert.Equal(1, devices[2].Index);
        Assert.Equal(32752.5, devices[2].TotalMiB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("error: unknown argument: --list-devices")]
    public void No_devices_from_empty_or_unsupported_output(string? output)
    {
        Assert.Empty(LlamaDeviceList.Parse(output));
    }

    [Theory]
    [InlineData(0x0000, "Untrusted")]
    [InlineData(0x1000, "Low")]
    [InlineData(0x2000, "Medium")]
    [InlineData(0x3000, "High")]
    [InlineData(0x4000, "System")]
    public void Integrity_levels_have_plain_names(int rid, string expected)
    {
        Assert.Equal(expected, StartupAccessCheck.DescribeIntegrity(rid));
    }

    [Fact]
    public void Writable_folder_passes_the_access_check()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-access-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(StartupAccessCheck.CanWrite(dir));
            Assert.Empty(Directory.GetFiles(dir)); // the probe cleans up after itself
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Fix_message_names_the_folder_and_the_command()
    {
        var text = StartupAccessCheck.BuildMessage(["Cannot write to C:\\x\\Logs."], @"C:\Apps\AI-FluxMux\");
        Assert.Contains("Cannot write to C:\\x\\Logs.", text);
        Assert.Contains("icacls \"C:\\Apps\\AI-FluxMux\" /setintegritylevel \"(OI)(CI)M\" /T /C /Q", text);
    }

    [Fact]
    public void Log_lines_carry_the_date()
    {
        Assert.Equal("[2026-11-23 20:48:05] ", RollingLogFile.Stamp(new DateTime(2026, 11, 23, 20, 48, 5)));
    }

    [Fact]
    public void Log_rotates_by_size_and_keeps_two_generations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "test.log");
            var log = RollingLogFile.CreateForTest(path, maxBytes: 200);
            for (var round = 0; round < 4; round++)
            {
                for (var i = 0; i < 10; i++)
                {
                    log.WriteLine("round " + round + " line " + i + " padding padding padding");
                }

                log.Flush(TimeSpan.FromSeconds(5));
            }

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.True(File.Exists(path + ".2"));
            Assert.False(File.Exists(path + ".3"));
            Assert.Contains("round 3", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
