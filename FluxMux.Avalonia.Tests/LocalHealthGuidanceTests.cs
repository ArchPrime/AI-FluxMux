using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalHealthGuidanceTests
{
    [Fact]
    public void Overflow_names_context_and_says_how_to_raise_it()
    {
        var text = LocalHealthGuidance.Build(
            ready: true,
            managedAlive: true,
            portOpen: true,
            probePort: 5002,
            publicPort: 5001,
            managedPid: 4242,
            overflow: "request tokens 42,000 exceeded context 8,192 by 33,808",
            stderrTail: null);

        Assert.Contains("Context is 8,192", text);
        Assert.Contains("about 42,000 tokens", text);
        Assert.Contains("raise Context", text);
        Assert.DoesNotContain("5002", text);
        Assert.DoesNotContain("PID", text);
        Assert.DoesNotContain("provider=", text);
    }

    [Fact]
    public void Dead_runner_tells_the_user_to_launch_again()
    {
        var text = LocalHealthGuidance.Build(
            ready: false,
            managedAlive: false,
            portOpen: false,
            probePort: 5002,
            publicPort: 5001,
            managedPid: null,
            overflow: null,
            stderrTail: "failed to allocate buffer");

        Assert.Contains("**Launch** the local model profile again", text);
        Assert.Contains("llama-server is not answering", text);
        Assert.Contains("Last llama-server output: failed to allocate buffer", text);
        Assert.DoesNotContain("5002", text);
    }

    [Fact]
    public void Stuck_start_says_wait_or_stop_and_relaunch()
    {
        var text = LocalHealthGuidance.Build(
            ready: false,
            managedAlive: true,
            portOpen: true,
            probePort: 5002,
            publicPort: 5001,
            managedPid: 99,
            overflow: null,
            stderrTail: null,
            localModel: "Qwen3.8-27B-Q4_K_M.gguf");

        Assert.Contains("llama-server is still starting", text);
        Assert.Contains("Qwen3.8-27B-Q4_K_M.gguf", text);
        Assert.Contains("**Stop** and **Launch** this model profile again", text);
        Assert.DoesNotContain("local runner", text);
        Assert.DoesNotContain("5002", text);
        Assert.DoesNotContain("PID", text);
    }

    [Fact]
    public void Public_gateway_miss_points_at_the_public_url()
    {
        var text = LocalHealthGuidance.BuildPublicGatewayMiss(
            "llama-server is up with Qwen3.8-27B-Q4_K_M.gguf.",
            publicPort: 5001);

        Assert.Contains("http://127.0.0.1:5001", text);
        Assert.Contains("AI-FluxMux public address", text);
        Assert.Contains("relaunch the local model profile so AI-FluxMux restarts that address", text);
    }

    [Fact]
    public void Harness_web_down_names_the_chat_page_not_port()
    {
        Assert.Equal(
            "Health needs attention: Harness web is not running.",
            LocalHealthGuidance.HarnessWebDownHeadline);
        var text = LocalHealthGuidance.BuildHarnessWebDown();
        Assert.Contains("Harness web is not running", text);
        Assert.Contains("**Harness web chat**", text);
        Assert.Contains("Failed to fetch", text);
        Assert.Contains("not Port", text);
        Assert.DoesNotContain("3080", text);
        Assert.DoesNotContain("llama-server", text);
    }

    [Fact]
    public void Profile_panel_warning_keeps_startup_exit_to_one_line()
    {
        var text = LocalHealthGuidance.FormatProfilePanelWarning(
            "llama-server exited during startup. Last llama-server output: g)\t0.00.086.071 1 srv\tinit: The Web UI is disabled\t0.00.086.073 1 srv\tinit: Use --ui/--no-ui (or de...");

        Assert.Equal(
            "llama-server exited during startup. Open Diagnostics for Last llama-server output.",
            text);
        Assert.DoesNotContain('\t', text);
        Assert.DoesNotContain("Web UI", text);
    }

    [Fact]
    public void Profile_panel_warning_strips_raw_llama_server_tail()
    {
        var text = LocalHealthGuidance.FormatProfilePanelWarning(
            "llama-server is still starting. Last llama-server output: failed to allocate buffer");

        Assert.StartsWith("llama-server is still starting.", text);
        Assert.Contains("Diagnostics", text);
        Assert.DoesNotContain("failed to allocate buffer", text);
    }

    [Fact]
    public void Profile_panel_warning_keeps_ui_disabled_init_line_out_of_the_form()
    {
        var text = LocalHealthGuidance.FormatProfilePanelWarning(
            "llama-server exited during startup. Last llama-server output: g)\r\n0.00.086.071 I srv          init: The UI is disabled\r\n0.00.086.073 I srv          init: Use --ui/--no-ui (or de...");

        Assert.Equal(
            "llama-server exited during startup. Open Diagnostics for Last llama-server output.",
            text);
        Assert.DoesNotContain("IJI", text);
        Assert.DoesNotContain("The UI is disabled", text);
    }

    [Theory]
    [InlineData("mmproj-Qwen3.8-27B-NVFP4-BF16.gguf")]
    [InlineData(@"C:\AI_Workbench\Models\Qwen\Qwen-Local\mmproj-Qwen3.8-27B-NVFP4-BF16.gguf")]
    [InlineData("Qwen3.8-27B.mmproj")]
    public void Vision_projector_filenames_are_not_local_models(string path)
    {
        Assert.True(LocalHealthGuidance.IsVisionProjectorFile(path));
        var warning = LocalHealthGuidance.FormatVisionProjectorAsModelWarning(path);
        Assert.Contains("vision projector", warning);
        Assert.Contains("Images", warning);
    }

    [Fact]
    public void Dense_gguf_is_not_a_vision_projector()
    {
        Assert.False(LocalHealthGuidance.IsVisionProjectorFile("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf"));
    }

    [Fact]
    public void Health_probe_uses_daemon_port_when_llama_server_is_not_managed()
    {
        Assert.Equal(5002, LocalHealthGuidance.ResolveLlamaHealthProbePort(
            managedAlive: false,
            managedLocalPort: -1,
            publicPort: 5001));
        Assert.Equal(5002, LocalHealthGuidance.ResolveLlamaHealthProbePort(
            managedAlive: true,
            managedLocalPort: 5002,
            publicPort: 5001));
        Assert.Equal(-1, LocalHealthGuidance.ResolveLlamaHealthProbePort(
            managedAlive: false,
            managedLocalPort: -1,
            publicPort: 65535));
    }

    [Fact]
    public void Parked_or_never_started_local_is_not_a_live_route()
    {
        Assert.True(LocalHealthGuidance.IsRouteNotStarted("Local route not started", "No local model is launched yet."));
        Assert.False(LocalHealthGuidance.IsRouteNotStarted("Local health check passed", "llama-server is up."));
    }

    [Fact]
    public void Ready_names_llama_server_and_the_local_model()
    {
        var text = LocalHealthGuidance.Build(
            ready: true,
            managedAlive: true,
            portOpen: true,
            probePort: 5002,
            publicPort: 5001,
            managedPid: 7,
            overflow: null,
            stderrTail: null,
            localModel: @"C:\AI_Workbench\Models\Qwen\Qwen-Local\Qwen3.8-27B-Q4_K_M.gguf");

        Assert.Contains("llama-server is up with Qwen3.8-27B-Q4_K_M.gguf", text);
        Assert.Contains("AI-FluxMux address http://127.0.0.1:5001", text);
        Assert.DoesNotContain("local runner", text);
    }
}
