using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The load countdown follows recent launch times only if they survive a settings save. They
/// used to live in the main config, which FluxMuxConfigService.Save() rewrites from the copy the
/// window loaded at start-up, so every save put the history back to its start-up state.
/// </summary>
public sealed class LaunchTelemetryPersistenceTests
{
    [Fact]
    public void A_recorded_load_survives_a_settings_save_and_old_history_is_kept()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "fluxmux-telemetry-" + Path.GetRandomFileName());
        var configDir = Directory.CreateDirectory(Path.Combine(workspace, ".vscode")).FullName;
        var configPath = Path.Combine(configDir, "fluxmux_config.json");
        try
        {
            File.WriteAllText(configPath, new JsonObject
            {
                ["FluxMuxMeta"] = new JsonObject
                {
                    ["LocalLaunchTelemetry"] = new JsonObject
                    {
                        ["m.gguf::v"] = new JsonObject
                        {
                            ["samples"] = new JsonArray(40000),
                            ["sample_count"] = 1,
                            ["slowest_ms"] = 40000,
                        },
                    },
                },
            }.ToJsonString());

            // The window's copy, read at start-up.
            var configService = new FluxMuxConfigService(configPath);
            var startupCopy = configService.Load();

            new FluxMuxRuntimeService(configPath).RecordLocalLaunchTelemetry("m.gguf", "v", 12000, "ready");

            // A settings save rewrites the whole config from the start-up copy.
            configService.Save(startupCopy);

            var detail = new FluxMuxRuntimeService(configPath).GetLocalLaunchTelemetryDetailText("m.gguf", "v");
            Assert.Contains("2 launches", detail, StringComparison.Ordinal);
            Assert.Contains("last 12.0 s", detail, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(configDir, "local_launch_telemetry.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
            }
        }
    }
}
