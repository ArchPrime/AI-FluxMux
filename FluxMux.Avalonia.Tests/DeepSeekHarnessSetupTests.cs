using System;
using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class DeepSeekHarnessSetupTests
{
    [Fact]
    public void BuildSettingsYaml_uses_fluxmux_port_and_context()
    {
        var yaml = DeepSeekHarnessSetup.BuildSettingsYaml(
            DeepSeekHarnessSetupOptions.Create(
                5001,
                131072,
                4096,
                imagesOn: true,
                reasoningOn: false,
                modelDisplayName: "Qwen3.8-27B-Q4_0.gguf"));

        Assert.Contains("baseURL: http://127.0.0.1:5001/v1", yaml, StringComparison.Ordinal);
        Assert.Contains("contextWindow: 131072", yaml, StringComparison.Ordinal);
        Assert.Contains("input: [text, image]", yaml, StringComparison.Ordinal);
        Assert.Contains("provider: ai-fluxmux", yaml, StringComparison.Ordinal);
        Assert.Contains("name: Qwen3.8-27B-Q4_0.gguf", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoningEfforts: false", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSettingsYaml_omits_reasoning_efforts_when_reasoning_on()
    {
        var yaml = DeepSeekHarnessSetup.BuildSettingsYaml(
            DeepSeekHarnessSetupOptions.Create(5001, 65536, 8192, reasoningOn: true));

        Assert.DoesNotContain("reasoningEfforts", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldAdvertiseImages_when_a_validated_vision_profile_exists()
    {
        Assert.True(DeepSeekHarnessSetup.YamlPackingFollowsLiveLocal(localAlive: true));
        Assert.False(DeepSeekHarnessSetup.YamlPackingFollowsLiveLocal(localAlive: false));
        Assert.True(DeepSeekHarnessSetup.ShouldAdvertiseImages(loadedProfileImagesOn: true, visionProfileAvailable: false));
        Assert.True(DeepSeekHarnessSetup.ShouldAdvertiseImages(loadedProfileImagesOn: false, visionProfileAvailable: true));
        Assert.False(DeepSeekHarnessSetup.ShouldAdvertiseImages(loadedProfileImagesOn: false, visionProfileAvailable: false));
        Assert.True(DeepSeekHarnessSetup.ShouldAdvertiseCapability(false, true));
        Assert.False(DeepSeekHarnessSetup.ShouldAdvertiseCapability(false, false));
        Assert.Equal(131072, DeepSeekHarnessSetup.AdvertiseNumericCapacity(8192, 131072));
        Assert.Equal(8192, DeepSeekHarnessSetup.AdvertiseNumericCapacity(8192, 0));
    }

    [Fact]
    public void EscapeYamlScalar_quotes_values_with_spaces()
    {
        Assert.Equal(
            "\"Gemini / flash (coding)\"",
            DeepSeekHarnessSetup.EscapeYamlScalar("Gemini / flash (coding)"));
    }

    [Fact]
    public void MergeProviderBlock_replaces_existing_ai_fluxmux_provider()
    {
        var existing = """
            llm-pi-ai:
              providers:
                ai-fluxmux:
                  baseURL: http://127.0.0.1:8080/v1
                  models:
                    - id: local
            agent-default-model:
              provider: ai-fluxmux
              model: local
            other: true
            """;

        var block = DeepSeekHarnessSetup.BuildSettingsYaml(
            DeepSeekHarnessSetupOptions.Create(5001, 65536, 8192, imagesOn: false));

        var merged = DeepSeekHarnessSetup.MergeProviderBlock(existing, block);

        Assert.Contains("baseURL: http://127.0.0.1:5001/v1", merged, StringComparison.Ordinal);
        Assert.Contains("contextWindow: 65536", merged, StringComparison.Ordinal);
        Assert.Contains("other: true", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("8080", merged, StringComparison.Ordinal);
        Assert.Equal(1, merged.Split("agent-default-model:", StringSplitOptions.None).Length - 1);
        var providersIndex = merged.IndexOf("  providers:", StringComparison.Ordinal);
        var providerIndex = merged.IndexOf("    ai-fluxmux:", StringComparison.Ordinal);
        Assert.True(providerIndex > providersIndex);
    }

    [Fact]
    public void BuildWebArguments_includes_port_and_no_open()
    {
        Assert.Equal("web --port 3080 --no-open", DeepSeekHarnessWebHost.BuildWebArguments(3080));
    }

    [Fact]
    public void ComposeArguments_prefixes_npx_launcher()
    {
        var args = DeepSeekHarnessWebHost.ComposeArguments("@deepseek-ai/dsh", 5001);
        Assert.Equal("@deepseek-ai/dsh web --port 5001 --no-open", args);
    }

    [Fact]
    public void ResolveNumericSetting_maps_auto_and_numbers()
    {
        Assert.Equal(128000, DeepSeekHarnessSetup.ResolveNumericSetting("Auto", 128000));
        Assert.Equal(128000, DeepSeekHarnessSetup.ResolveNumericSetting("Provider default", 128000));
        Assert.Equal(65536, DeepSeekHarnessSetup.ResolveNumericSetting("65536", 128000));
        Assert.Equal(4096, DeepSeekHarnessSetup.ResolveNumericSetting(string.Empty, 4096));
    }

    [Fact]
    public void BuildChatUrl_formats_localhost()
    {
        Assert.Equal("http://127.0.0.1:3080", DeepSeekHarnessSetup.BuildChatUrl(3080));
    }

    [Fact]
    public void TryParseWebAuthUrl_reads_the_token_url_dsh_web_prints()
    {
        Assert.True(DeepSeekHarnessSetup.TryParseWebAuthUrl(
            "dsh web: http://127.0.0.1:3080/?token=_Rxx2GlCMH60CsoNoXM6p7fg6rgTDvA6bjsCUT9d2_E",
            3080,
            out var url));
        Assert.Equal("http://127.0.0.1:3080/?token=_Rxx2GlCMH60CsoNoXM6p7fg6rgTDvA6bjsCUT9d2_E", url);
        Assert.False(DeepSeekHarnessSetup.TryParseWebAuthUrl(
            "dsh web: http://127.0.0.1:3080/?token=abc",
            3090,
            out _));
        Assert.False(DeepSeekHarnessSetup.TryParseWebAuthUrl("http://127.0.0.1:3080/", 3080, out _));
    }

    [Fact]
    public void IsWebUiListeningStatus_treats_auth_gate_as_the_ui_being_up()
    {
        Assert.True(DeepSeekHarnessSetup.IsWebUiListeningStatus(200));
        Assert.True(DeepSeekHarnessSetup.IsWebUiListeningStatus(401));
        Assert.True(DeepSeekHarnessSetup.IsWebUiListeningStatus(403));
        Assert.False(DeepSeekHarnessSetup.IsWebUiListeningStatus(500));
        Assert.False(DeepSeekHarnessSetup.IsWebUiListeningStatus(0));
        Assert.True(DeepSeekHarnessSetup.IsWebUiReadyToOpen(200, haveAuthUrl: false));
        Assert.False(DeepSeekHarnessSetup.IsWebUiReadyToOpen(401, haveAuthUrl: false));
        Assert.True(DeepSeekHarnessSetup.IsWebUiReadyToOpen(401, haveAuthUrl: true));
        Assert.True(DeepSeekHarnessSetup.NeedsFreshWebAuth(401, haveAuthUrl: false));
        Assert.False(DeepSeekHarnessSetup.NeedsFreshWebAuth(401, haveAuthUrl: true));
        Assert.False(DeepSeekHarnessSetup.NeedsFreshWebAuth(200, haveAuthUrl: false));
    }

    [Fact]
    public void MergeEnvFileContent_creates_or_updates_api_key_line()
    {
        Assert.Equal(
            "AI_FLUXMUX_API_KEY=sk-local" + Environment.NewLine,
            DeepSeekHarnessSetup.MergeEnvFileContent(null, DeepSeekHarnessSetup.ApiKeyEnvVar, DeepSeekHarnessSetup.DummyApiKey));

        var existing = "OTHER=1\nAI_FLUXMUX_API_KEY=old\n";
        var merged = DeepSeekHarnessSetup.MergeEnvFileContent(existing, DeepSeekHarnessSetup.ApiKeyEnvVar, DeepSeekHarnessSetup.DummyApiKey);
        Assert.Contains("AI_FLUXMUX_API_KEY=sk-local", merged, StringComparison.Ordinal);
        Assert.Contains("OTHER=1", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_FLUXMUX_API_KEY=old", merged, StringComparison.Ordinal);

        var append = "FOO=bar\n";
        var appended = DeepSeekHarnessSetup.MergeEnvFileContent(append, DeepSeekHarnessSetup.ApiKeyEnvVar, DeepSeekHarnessSetup.DummyApiKey);
        Assert.Contains("FOO=bar", appended, StringComparison.Ordinal);
        Assert.Contains("AI_FLUXMUX_API_KEY=sk-local", appended, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApiKeyEnvLine_matches_dummy_key()
    {
        Assert.Equal("AI_FLUXMUX_API_KEY=sk-local", DeepSeekHarnessSetup.BuildApiKeyEnvLine());
    }

    [Fact]
    public void LaunchLogIndicatesFluxMuxManaged_detects_fluxmux_start_line()
    {
        var logPath = DeepSeekHarnessWebHost.ResolveLaunchLogPath();
        var directory = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(directory);
        var backup = File.Exists(logPath) ? File.ReadAllText(logPath) : null;
        try
        {
            File.WriteAllText(
                logPath,
                "[2026-09-02T10:00:00Z] npx @deepseek-ai/dsh web --port 3080 --no-open" + Environment.NewLine);
            Assert.True(DeepSeekHarnessWebHost.LaunchLogIndicatesFluxMuxManaged(3080));
            Assert.False(DeepSeekHarnessWebHost.LaunchLogIndicatesFluxMuxManaged(3090));
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
            else
            {
                File.WriteAllText(logPath, backup);
            }
        }
    }

    [Fact]
    public void TryAdoptFluxMuxHarnessOnPort_requires_listening_port_not_http_probe_alone()
    {
        const int testPort = 43080;
        var pidPath = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
        var logPath = DeepSeekHarnessWebHost.ResolveLaunchLogPath();
        var directory = Path.GetDirectoryName(pidPath)!;
        Directory.CreateDirectory(directory);
        var pidBackup = File.Exists(pidPath) ? File.ReadAllText(pidPath) : null;
        var logBackup = File.Exists(logPath) ? File.ReadAllText(logPath) : null;
        try
        {
            DeepSeekHarnessWebHost.ClearPidRecord();
            File.WriteAllText(
                logPath,
                "[2026-09-02T10:00:00Z] npx @deepseek-ai/dsh web --port "
                + testPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " --no-open"
                + Environment.NewLine);
            Assert.False(DeepSeekHarnessWebHost.IsPortListening(testPort));
            Assert.False(DeepSeekHarnessWebHost.TryAdoptFluxMuxHarnessOnPort(testPort, uiReachable: true));
            Assert.False(DeepSeekHarnessWebHost.TryReadPidRecord(out _, out _));
        }
        finally
        {
            if (pidBackup is null)
            {
                DeepSeekHarnessWebHost.ClearPidRecord();
            }
            else
            {
                File.WriteAllText(pidPath, pidBackup);
            }

            if (logBackup is null)
            {
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
            else
            {
                File.WriteAllText(logPath, logBackup);
            }
        }
    }

    [Fact]
    public void HasFluxMuxHarnessRecordOnPort_clears_phantom_pid_zero_when_port_closed()
    {
        const int testPort = 43082;
        var path = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            DeepSeekHarnessWebHost.WritePidRecord(0, testPort);
            Assert.False(DeepSeekHarnessWebHost.IsPortListening(testPort));
            Assert.False(DeepSeekHarnessWebHost.HasFluxMuxHarnessRecordOnPort(testPort));
            Assert.False(DeepSeekHarnessWebHost.TryReadPidRecord(out _, out _));
        }
        finally
        {
            if (backup is null)
            {
                DeepSeekHarnessWebHost.ClearPidRecord();
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    [Fact]
    public void ReconcilePidRecordForPort_clears_stale_record_when_port_is_closed()
    {
        const int testPort = 43081;
        var path = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            DeepSeekHarnessWebHost.WritePidRecord(4242, testPort);
            Assert.True(DeepSeekHarnessWebHost.ReconcilePidRecordForPort(testPort));
            Assert.False(DeepSeekHarnessWebHost.TryReadPidRecord(out _, out _));
        }
        finally
        {
            if (backup is null)
            {
                DeepSeekHarnessWebHost.ClearPidRecord();
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    [Fact]
    public void BuildModelBaseUrl_formats_openai_path()
    {
        Assert.Equal("http://127.0.0.1:5001/v1", DeepSeekHarnessSetup.BuildModelBaseUrl(5001));
    }

    [Fact]
    public void IsListeningLineForPort_matches_exact_port_only()
    {
        Assert.True(DeepSeekHarnessWebHost.IsListeningLineForPort("  TCP    127.0.0.1:3080         0.0.0.0:0              LISTENING       1234", 3080));
        Assert.False(DeepSeekHarnessWebHost.IsListeningLineForPort("  TCP    127.0.0.1:30801        0.0.0.0:0              LISTENING       1234", 3080));
        Assert.False(DeepSeekHarnessWebHost.IsListeningLineForPort("  TCP    127.0.0.1:3080         0.0.0.0:0              ESTABLISHED     1234", 3080));
    }

    [Fact]
    public void TryReadPidRecord_round_trips_launcher_and_port()
    {
        var path = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            DeepSeekHarnessWebHost.WritePidRecord(4242, 3080);
            Assert.True(DeepSeekHarnessWebHost.TryReadPidRecord(out var pid, out var port));
            Assert.Equal(4242, pid);
            Assert.Equal(3080, port);
            DeepSeekHarnessWebHost.ClearPidRecord();
            Assert.False(DeepSeekHarnessWebHost.TryReadPidRecord(out _, out _));
        }
        finally
        {
            if (backup is null)
            {
                DeepSeekHarnessWebHost.ClearPidRecord();
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }
}