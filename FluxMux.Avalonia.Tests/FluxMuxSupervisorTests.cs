using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public class FluxMuxSupervisorTests
{
    private static string CreateTempLogDir() { var dir = Path.Combine(Path.GetTempPath(), "fluxmux_supervisor_test_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }

    private static void WriteJson(string path, JsonObject obj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, obj.ToJsonString());
    }

    [Fact]
    public void ComputeScenario_HealthyState_ReturnsHealthyScenario()
    {
        var dir = CreateTempLogDir();
        try
        {
            WriteJson(Path.Combine(dir, "Proxy_Runtime_State.json"), new JsonObject
            {
                ["local_phase"] = "ready",
                ["local_context"] = "262144",
                ["local_vram_context"] = "163840",
                ["routing_enabled"] = false,
                ["endpoint_app"] = "Cline",
                ["local_gpu_offload"] = "GPU + CPU",
                ["cloud_routing_capacity"] = "Normal"
            });
            WriteJson(Path.Combine(dir, "Local_Runtime_State.json"), new JsonObject
            {
                ["phase"] = "ready",
                ["updated_utc"] = DateTime.UtcNow.AddMinutes(-5).ToString("o")
            });
            WriteJson(Path.Combine(dir, "Local_Context_Usage.json"), new JsonObject
            {
                ["prompt_tokens"] = "40989",
                ["context_limit"] = "262144"
            });

            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = supervisor.ComputeScenario();

            Assert.Equal(262144, scenario.HotContext);
            Assert.Equal(163840, scenario.VramContext);
            Assert.Equal(40989, scenario.PromptTokens);
            Assert.True(scenario.IsLocalReady);
            Assert.False(scenario.IsRoutingOn);
            Assert.True(scenario.IsClineLike);
            Assert.False(scenario.IsRamSpill);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EvaluateVerdict_ContextOverflow_ReturnsContextOverflow()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 262144, 163840, 262400, 525, 0.0, false);
            var verdict = supervisor.EvaluateVerdict(scenario);
            Assert.Equal(SupervisorVerdict.ContextOverflow, verdict);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EvaluateVerdict_HealthyState_ReturnsHealthy()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 262144, 163840, 40989, 82, 0.0, false);
            var verdict = supervisor.EvaluateVerdict(scenario);
            Assert.Equal(SupervisorVerdict.Healthy, verdict);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EvaluateVerdict_HighRam_ReturnsRamThrashRisk()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 262144, 163840, 40989, 82, 90.0, false);
            var verdict = supervisor.EvaluateVerdict(scenario);
            Assert.Equal(SupervisorVerdict.RamThrashRisk, verdict);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ComputeFillPercent_CorrectPercentage()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 262144, 163840, 131072, 0, 0.0, false);
            int fill = supervisor.ComputeFillPercent(scenario);
            Assert.Equal(50, fill);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ComputeFillPercent_ZeroContext_ReturnsZero()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 0, 0, 1000, 0, 0.0, false);
            int fill = supervisor.ComputeFillPercent(scenario);
            Assert.Equal(0, fill);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PredictPrefillSeconds_RamSpill_ReturnsClampedEstimate()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, true, true, false, false, false, true, false, "Normal", 393216, 163840, 200000, 0, 0.0, false);
            int estimate = supervisor.PredictPrefillSeconds(scenario, 200000, null);
            Assert.True(estimate >= 60);
            Assert.True(estimate <= 1800);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PredictPrefillSeconds_VramWarm_ReturnsLearnedEstimate()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 262144, 163840, 20000, 0, 0.0, false);
            int estimate = supervisor.PredictPrefillSeconds(scenario, 20000, null);
            Assert.True(estimate >= 5);
            Assert.True(estimate <= 1800);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PredictPrefillSeconds_ColdStart_NoData_ReturnsConservativeEstimate()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, true, false, false, true, false, "Normal", 262144, 163840, 50000, 0, 0.0, false);
            int estimate = supervisor.PredictPrefillSeconds(scenario, 50000, null);
            Assert.True(estimate >= 30);
            Assert.True(estimate <= 1800);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void PredictPrefillSeconds_ZeroTokens_ReturnsZero()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            var scenario = new ModelScenario(false, false, false, false, false, false, true, false, "Normal", 262144, 163840, 0, 0, 0.0, false);
            int estimate = supervisor.PredictPrefillSeconds(scenario, 0, null);
            Assert.Equal(0, estimate);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SetPrefillEstimate_UpdatesCurrentEstimate()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            Assert.Equal(0, supervisor.CurrentPrefillEstimateSeconds);
            supervisor.SetPrefillEstimate(120);
            Assert.Equal(120, supervisor.CurrentPrefillEstimateSeconds);
            supervisor.ClearPrefillEstimate();
            Assert.Equal(0, supervisor.CurrentPrefillEstimateSeconds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SetPrefillEstimate_ForwardsToGateway()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            int? gatewayValue = null;
            supervisor.SetGatewayPrefillEstimate = s => gatewayValue = s;
            supervisor.ClearGatewayPrefillEstimate = () => gatewayValue = 0;
            supervisor.SetPrefillEstimate(90);
            Assert.Equal(90, gatewayValue);
            supervisor.ClearPrefillEstimate();
            Assert.Equal(0, gatewayValue);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadLocalReloadRecommend_Pending_ReturnsSnapshot()
    {
        var dir = CreateTempLogDir();
        try
        {
            var path = Path.Combine(dir, "Proxy_Local_Reload_Recommend.json");
            File.WriteAllText(path, "{\"status\":\"pending\",\"reason\":\"Context near limit\",\"model\":\"test\",\"variant\":\"v1\"}");
            var supervisor = new FluxMuxSupervisor(dir);
            var snapshot = supervisor.ReadLocalReloadRecommend();
            Assert.NotNull(snapshot);
            Assert.Equal("pending", snapshot!.Status);
            Assert.Equal("Context near limit", snapshot.Reason);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadLocalReloadRecommend_Idle_ReturnsNull()
    {
        var dir = CreateTempLogDir();
        try
        {
            var path = Path.Combine(dir, "Proxy_Local_Reload_Recommend.json");
            File.WriteAllText(path, "{\"status\":\"idle\",\"reason\":\"\"}");
            var supervisor = new FluxMuxSupervisor(dir);
            var snapshot = supervisor.ReadLocalReloadRecommend();
            Assert.Null(snapshot);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnswerLocalReloadRecommend_Yes_WritesStatus()
    {
        var dir = CreateTempLogDir();
        try
        {
            var path = Path.Combine(dir, "Proxy_Local_Reload_Recommend.json");
            File.WriteAllText(path, "{\"status\":\"pending\",\"reason\":\"test\"}");
            var supervisor = new FluxMuxSupervisor(dir);
            supervisor.AnswerLocalReloadRecommend(true);
            var json = File.ReadAllText(path);
            Assert.Contains("\"status\": \"yes\"", json);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ClearLocalReloadRecommend_SetsIdle()
    {
        var dir = CreateTempLogDir();
        try
        {
            var path = Path.Combine(dir, "Proxy_Local_Reload_Recommend.json");
            File.WriteAllText(path, "{\"status\":\"pending\",\"reason\":\"test\"}");
            var supervisor = new FluxMuxSupervisor(dir);
            supervisor.ClearLocalReloadRecommend();
            var json = File.ReadAllText(path);
            Assert.Contains("\"status\": \"idle\"", json);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ShouldSuppressHangDialog_ReturnsTrue_WhenWaitLonger()
    {
        Assert.True(FluxMuxSupervisor.ShouldSuppressHangDialog(1, LocalStreamHangPolicy.FirstByteReason));
        Assert.False(FluxMuxSupervisor.ShouldSuppressHangDialog(0, LocalStreamHangPolicy.FirstByteReason));
        Assert.False(FluxMuxSupervisor.ShouldSuppressHangDialog(1, "stall"));
    }

    [Fact]
    public void StartRamWatchdog_CreatesTimer()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            supervisor.StartRamWatchdog();
            // No exception = success. The timer is internal.
            supervisor.StopRamWatchdog();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void LogLaunchMonitor_WritesToFile()
    {
        var dir = CreateTempLogDir();
        try
        {
            var supervisor = new FluxMuxSupervisor(dir);
            supervisor.LogLaunchMonitor("test message");
            var logPath = Path.Combine(dir, "launch_monitor.log");
            // Lines are written by a background writer (RollingLogFile), so wait for it.
            RollingLogFile.For(logPath).Flush(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(logPath));
            var content = File.ReadAllText(logPath);
            Assert.Contains("test message", content);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
