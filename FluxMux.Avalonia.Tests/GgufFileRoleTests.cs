using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// A GGUF's role comes from its contents (28 Nov): a drafter has no token embeddings of its own;
/// DSpark's has a Markov head. The wizard uses a drafter only with Speed above Context length.
/// </summary>
public sealed class GgufFileRoleTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluxmux-gguf-role-" + Guid.NewGuid().ToString("N"));

    public GgufFileRoleTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Temp folder; best-effort.
        }
    }

    private string Gguf(string name, string architecture, params string[] tensors)
    {
        var path = Path.Combine(_folder, name);
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }

        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write((ulong)tensors.Length);
        writer.Write(2UL);
        Str("general.architecture");
        writer.Write(8u);
        Str(architecture);
        Str(architecture + ".block_count");
        writer.Write(4u);
        writer.Write(3u);
        foreach (var tensor in tensors)
        {
            Str(tensor);
            writer.Write(2u);
            writer.Write(4096UL);
            writer.Write(256UL);
            writer.Write(8u);
            writer.Write(0UL);
        }

        return path;
    }

    private static readonly string[] ServerTypes = ["draft-mtp", "draft-eagle3", "draft-dflash", "draft-dspark", "ngram-simple"];

    [Fact]
    public void A_DSpark_drafter_is_found_by_its_contents_not_its_name()
    {
        var drafter = GgufFileRole.Read(Gguf("anything.gguf", "dflash", "blk.0.attn_q.weight", "markov_w1", "joint_proj"));
        Assert.True(drafter!.IsDrafter);
        Assert.Equal("draft-dspark", GgufFileRole.SpecTypeFor(drafter, ServerTypes));

        var eagle = GgufFileRole.Read(Gguf("e.gguf", "eagle3", "blk.0.attn_q.weight"));
        Assert.Equal("draft-eagle3", GgufFileRole.SpecTypeFor(eagle!, ServerTypes));
        Assert.Null(GgufFileRole.SpecTypeFor(eagle!, ["ngram-simple"]));
    }

    [Fact]
    public void Models_and_projectors_are_not_drafters_and_sidecars_leave_the_model_list()
    {
        var model = Gguf("Inkling.gguf", "inkling2_loop", "token_embd.weight", "blk.0.attn_q.weight");
        var projector = Gguf("vision.gguf", "clip", "v.patch_embd.weight");
        var drafter = Gguf("dspark-Inkling.gguf", "dflash", "markov_w1");
        Assert.False(GgufFileRole.Read(model)!.IsDrafter);
        Assert.False(GgufFileRole.IsSidecar(model));
        Assert.True(GgufFileRole.IsSidecar(projector));
        Assert.True(GgufFileRole.IsSidecar(drafter));
    }

    private static FluxMuxRuntimeService.LocalHardwareLaunchAdvice Advice() => new()
    {
        GpuTotalGb = 31.8,
        ModelFileGb = 15.5,
        NvidiaAvailable = true,
        ModelMaxCtx = 1048576,
        ContextPriorityFirst = 819200,
        ContextPrioritySecond = 524288,
        ContextSafeDefault = 262144,
        ContextPriorityLower = 131072,
        KvBytesPerToken = 16384,
        DrafterPath = @"C:\models\dspark.gguf",
        DrafterSpecType = "draft-dspark",
        DrafterBytes = 503889952,
        DrafterKvBytesPerToken = 4096,
    };

    private static Dictionary<string, string> Wizard(string spec, params string[] order)
        => LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(order),
            Advice(),
            "Auto",
            spec,
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8");

    [Fact]
    public void Speed_above_Context_uses_the_drafter_and_takes_its_memory_from_Context()
    {
        var speedFirst = Wizard("ngram-simple", "Speed", "Context length", "Fidelity", "Stability", "Reply length", "Reasoning depth");
        Assert.Equal("draft-dspark", speedFirst["LocalSpecType"]);
        // Speed first starts from the lower Context (131,072); the drafter's file, buffers and its
        // cache (4 KB/token at f16, run at the model's q8_0: 4096 x 8.5 / 16) come out of it.
        var fixedBytes = LocalPrioritySettingsCalculator.DrafterReserveBytes(503889952);
        var drafterKv = 4096 * 8.5 / 16;
        var expected = (int)(Math.Floor((131072.0 * 16384 - fixedBytes) / (16384 + drafterKv) / 1024) * 1024);
        Assert.Equal(expected.ToString(), speedFirst["OverrideContext"]);
        Assert.InRange(expected, 70_000, 80_000);

        var contextFirst = Wizard("ngram-simple", "Context length", "Speed", "Fidelity", "Stability", "Reply length", "Reasoning depth");
        Assert.Equal("ngram-simple", contextFirst["LocalSpecType"]);

        // Never over a deliberate Disabled or MTP.
        Assert.Equal("Disabled", Wizard("Disabled", "Speed", "Context length", "Fidelity", "Stability", "Reply length", "Reasoning depth")["LocalSpecType"]);
        Assert.Equal("draft-mtp", Wizard("draft-mtp", "Speed", "Context length", "Fidelity", "Stability", "Reply length", "Reasoning depth")["LocalSpecType"]);
    }
}
