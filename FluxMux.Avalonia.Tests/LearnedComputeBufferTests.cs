using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The prompt-reading buffer learned from llama-server's own output (28 Nov). The log lines are the
/// operator's Inkling 2 Mini load that failed: 63 GB at Context 668,672 x micro-batch 512.
/// </summary>
public sealed class LearnedComputeBufferTests
{
    private const string FailedLoad =
        "=== llama-server launch log ===\n"
        + "Args: -m C:\\AI_Workbench\\Models\\Inkling\\Inkling-2-Mini-UD-Q4_K_M.gguf --port 5002 -c 668672 -n 16384 -b 1024 -ub 512 -ngl 999 -fa 1 -ctk q8_0 -ctv q8_0 --fit on --jinja\n"
        + "[stderr] 0.00.581.058 W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 999, abort\n"
        + "[stderr] 0.14.064.213 E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 64835.22 MiB on device 0: cudaMalloc failed: out of memory\n"
        + "[stderr] 0.14.064.225 E ggml_gallocr_reserve_n_impl: failed to allocate CUDA0 buffer of size 67984654720\n"
        + "[stderr] 0.14.064.227 E graph_reserve: failed to allocate compute buffers\n";

    [Fact]
    public void A_failed_compute_allocation_is_read_with_its_Context_micro_batch_and_KV_type()
    {
        var seen = LearnedComputeBuffer.Parse(FailedLoad);
        Assert.NotNull(seen);
        Assert.True(seen!.Failed);
        Assert.Equal(668672, seen.Context);
        Assert.Equal(512, seen.Ubatch);
        Assert.Equal("q8_0", seen.KvType);
        Assert.InRange(seen.BytesPerUbatchToken, 195, 202);
    }

    [Fact]
    public void A_swa_full_load_is_learned_apart_from_one_without()
    {
        // 15:07 the same day: --swa-full gave 85 bytes per micro-batch x Context token against 199 without.
        var seen = LearnedComputeBuffer.Parse(FailedLoad.Replace("--fit on", "--swa-full --fit on", StringComparison.Ordinal));
        Assert.Equal("q8_0" + LearnedComputeBuffer.SwaFullSuffix, seen!.KvType);
        Assert.Equal("q8_0", LearnedComputeBuffer.Parse(FailedLoad)!.KvType);
    }

    [Fact]
    public void A_successful_load_reads_the_reported_device_compute_buffers()
    {
        var log = "Args: -m D:\\m\\a.gguf -c 65536 -ub 512 -ctk q8_0\n"
                  + "llama_context:      CUDA0 compute buffer size =  6400.00 MiB\n"
                  + "llama_context:  CUDA_Host compute buffer size =   140.00 MiB\n";
        var seen = LearnedComputeBuffer.Parse(log);
        Assert.NotNull(seen);
        Assert.False(seen!.Failed);
        Assert.Equal(6400.0 * 1024 * 1024, seen.ComputeBytes);
    }

    [Fact]
    public void A_small_buffer_is_not_learned_and_drops_an_old_record()
    {
        var store = new JsonObject();
        var big = new LearnedComputeBuffer.Observation("x.gguf", 65536, 512, 6.0 * 1073741824, false, "q8_0");
        Assert.True(LearnedComputeBuffer.Record(store, big, DateTime.UtcNow));
        Assert.True(LearnedComputeBuffer.BytesPerUbatchToken(store, "x.gguf", "q8_0") > 0);
        // Per KV type: f16 has nothing learned.
        Assert.Equal(0, LearnedComputeBuffer.BytesPerUbatchToken(store, "x.gguf", "f16"));
        var small = big with { ComputeBytes = 0.5 * 1073741824 };
        Assert.True(LearnedComputeBuffer.Record(store, small, DateTime.UtcNow));
        Assert.Equal(0, LearnedComputeBuffer.BytesPerUbatchToken(store, "x.gguf", "q8_0"));
    }

    [Fact]
    public void A_cost_applies_only_to_the_llama_server_build_that_showed_it()
    {
        // 28 Nov: b12823 added Flash Attention for Inkling's bias; a build that loads fine prints no
        // buffer sizes, so the old build's cost must not carry over to it.
        var log = FailedLoad.Replace("Args:", "Server build: 111@20261123\nArgs:", StringComparison.Ordinal);
        var seen = LearnedComputeBuffer.Parse(log);
        Assert.Equal("111@20261123", seen!.ServerBuild);
        var store = new JsonObject();
        LearnedComputeBuffer.Record(store, seen, DateTime.UtcNow);
        Assert.True(LearnedComputeBuffer.BytesPerUbatchToken(store, seen.ModelPath, "q8_0", "111@20261123") > 0);
        Assert.Equal(0, LearnedComputeBuffer.BytesPerUbatchToken(store, seen.ModelPath, "q8_0", "222@20261128"));
        Assert.Empty(LearnedComputeBuffer.ByKvType(store, seen.ModelPath, "222@20261128"));
        Assert.Single(LearnedComputeBuffer.ByKvType(store, seen.ModelPath, "111@20261123"));
    }

    private static FluxMuxRuntimeService.LocalHardwareLaunchAdvice InklingOn32Gb(double learned)
        => new()
        {
            GpuTotalGb = 31.8,
            ModelFileGb = 15.5,
            NvidiaAvailable = true,
            ModelMaxCtx = 1048576,
            KvBytesPerToken = 16384,
            SlidingWindowPattern = true,
            KvComputeBudgetGb = 12.0,
            LearnedComputeBytesPerUbatchToken = learned > 0 ? new Dictionary<string, double> { ["q8_0"] = learned } : new Dictionary<string, double>(),
        };

    [Fact]
    public void With_Context_first_the_micro_batch_is_lowered_before_the_Context()
    {
        var (ctx, ub, batch) = LocalPrioritySettingsCalculator.FitToLearnedCompute(InklingOn32Gb(198.6), "668672", "512", "1024", "q8_0", contextOutranksSpeed: true);
        var c = int.Parse(ctx);
        var u = int.Parse(ub);
        Assert.Equal(LocalPrioritySettingsCalculator.SmallestFitUbatch, u);
        // KV plus compute must fit 92 % of the 12 GB budget.
        Assert.True(c * (16384 + 198.6 * u) <= 12.0 * 1073741824 * 0.92);
        Assert.InRange(c, 300_000, 450_000);
        Assert.Equal("1024", batch);
    }

    [Fact]
    public void With_Speed_first_the_micro_batch_is_kept_and_Context_lowered()
    {
        var (ctx, ub, _) = LocalPrioritySettingsCalculator.FitToLearnedCompute(InklingOn32Gb(198.6), "668672", "512", "1024", "q8_0", contextOutranksSpeed: false);
        Assert.Equal("512", ub);
        Assert.InRange(int.Parse(ctx), 80_000, 110_000);
    }

    [Fact]
    public void Nothing_learned_leaves_the_settings_alone()
        => Assert.Equal(("668672", "512", "1024"), LocalPrioritySettingsCalculator.FitToLearnedCompute(InklingOn32Gb(0), "668672", "512", "1024", "q8_0", true));
}
