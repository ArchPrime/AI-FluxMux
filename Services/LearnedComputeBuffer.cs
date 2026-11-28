using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// What a model's prompt-reading ("compute") buffer costs, learned from llama-server's own output
/// (28 Nov). With Flash Attention the buffer is a small fixed working set; for a model whose
/// attention llama.cpp runs without it (Inkling 2 Mini: relative positions, no RoPE) it grows with
/// micro-batch × Context — 63 GB at 512 × 668,672, so the load failed although the KV cache was
/// ~11 GB — and no estimate from the GGUF shows it. It depends on the KV cache type: users of the
/// same model report that a quantized K/V cache keeps llama.cpp on its dense relative-bias path
/// (~26 GiB at 262,144 × 512 with q8_0, Unsloth's GGUF discussion), so it is learned per model
/// file and KV type. A load that needed more than
/// <see cref="LearnAtBytes"/> teaches the cost per micro-batch × Context token for that model file;
/// the wizard then fits Context and micro-batch to the card (<see cref="LocalPrioritySettingsCalculator"/>).
/// A cost is also tied to the llama-server build that showed it (<see cref="ServerBuild"/>): llama.cpp
/// b12823 (27 Nov) added Flash Attention for Inkling's relative bias, which shrank the same buffer,
/// and a build that loads fine does not print its buffer sizes, so an old build's cost would never
/// be cleared. A cost from another build is ignored; a new build that still runs out relearns.
/// </summary>
public static class LearnedComputeBuffer
{
    /// <summary>Below this a compute buffer is the usual fixed working set, so nothing is learned (and an old record is dropped).</summary>
    public const double LearnAtBytes = 2.0 * 1024 * 1024 * 1024;

    /// <summary>The line the launch log carries the llama-server build on (<see cref="ServerBuild"/>).</summary>
    public const string ServerBuildLabel = "Server build: ";

    /// <summary>
    /// Identifies a llama-server build by its executable's size and last-write time; empty when the
    /// file cannot be read.
    /// </summary>
    public static string ServerBuild(string? serverPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
            {
                return string.Empty;
            }

            var info = new FileInfo(serverPath);
            return info.Length.ToString(CultureInfo.InvariantCulture) + "@" + info.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Added to the K cache type in the key for a load with --swa-full.</summary>
    public const string SwaFullSuffix = "+swa-full";

    /// <summary>The key part for a K cache type with SWA full on or off.</summary>
    public static string KvKey(string kvType, bool swaFull)
        => (string.IsNullOrWhiteSpace(kvType) ? "f16" : kvType.Trim().ToLowerInvariant()) + (swaFull ? SwaFullSuffix : string.Empty);

    public sealed record Observation(string ModelPath, int Context, int Ubatch, double ComputeBytes, bool Failed, string KvType = "f16", string ServerBuild = "")
    {
        public double BytesPerUbatchToken => Context > 0 && Ubatch > 0 ? ComputeBytes / ((double)Context * Ubatch) : 0;
    }

    private static readonly Regex ArgsLine = new(@"^Args:\s*(.*)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ServerBuildLine = new(@"^Server build:\s*(\S*)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ModelArg = new(@"(?:^|\s)-m\s+(?:""([^""]+)""|(\S+))", RegexOptions.Compiled);
    private static readonly Regex ContextArg = new(@"(?:^|\s)(?:-c|--ctx-size)\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex UbatchArg = new(@"(?:^|\s)(?:-ub|--ubatch-size)\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex KvTypeArg = new(@"(?:^|\s)(?:-ctk|--cache-type-k)\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex FailedAllocation = new(@"allocating\s+([\d.]+)\s+MiB\s+on device\s+\d+:\s*\w*\s*failed", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ComputeFailure = new(@"failed to allocate (CUDA\d+ |compute|\w+ compute)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceComputeBuffer = new(@"\b(?:CUDA|ROCm|Vulkan|SYCL|Metal)\d*\s+compute buffer size\s*=\s*([\d.]+)\s*MiB", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads the last launch in a llama-server output log: the model, Context and micro-batch from
    /// its Args line, and the compute buffer it failed to allocate or reported. Null when the log
    /// has no Args line or no compute buffer figure.
    /// </summary>
    public static Observation? Parse(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return null;
        }

        var args = ArgsLine.Matches(log);
        if (args.Count == 0)
        {
            return null;
        }

        var lastArgs = args[^1];
        var argText = lastArgs.Groups[1].Value;
        var tail = log[lastArgs.Index..];
        var model = ModelArg.Match(argText);
        var context = ContextArg.Match(argText);
        if (!model.Success || !context.Success)
        {
            return null;
        }

        var modelPath = model.Groups[1].Success ? model.Groups[1].Value : model.Groups[2].Value;
        var ctx = int.Parse(context.Groups[1].Value, CultureInfo.InvariantCulture);
        var ubMatch = UbatchArg.Match(argText);
        var ub = ubMatch.Success ? int.Parse(ubMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 512;
        var kvMatch = KvTypeArg.Match(argText);
        // SWA full changes the buffer too (the same Inkling load: 85 bytes per micro-batch x Context
        // token with it, 199 without), so it is part of the key: "q8_0" or "q8_0+swa-full".
        var kv = (kvMatch.Success ? kvMatch.Groups[1].Value.ToLowerInvariant() : "f16")
                 + (Regex.IsMatch(argText, @"(?:^|\s)--swa-full(?:\s|$)") ? SwaFullSuffix : string.Empty);

        var builds = ServerBuildLine.Matches(log[..lastArgs.Index]);
        var build = builds.Count > 0 ? builds[^1].Groups[1].Value.Trim() : string.Empty;

        // A failed compute-buffer allocation: the "allocating N MiB … failed" line just before
        // "failed to allocate compute buffers" (a KV-cache failure is not this buffer).
        var lines = tail.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var failed = FailedAllocation.Match(lines[i]);
            if (!failed.Success)
            {
                continue;
            }

            var next = string.Join("\n", lines.Skip(i + 1).Take(3));
            if (ComputeFailure.IsMatch(next) && next.Contains("compute", StringComparison.OrdinalIgnoreCase))
            {
                var mib = double.Parse(failed.Groups[1].Value, CultureInfo.InvariantCulture);
                return new Observation(modelPath, ctx, ub, mib * 1024 * 1024, Failed: true, kv, build);
            }
        }

        // A load that worked: the compute buffers it reported on the graphics devices.
        var reported = DeviceComputeBuffer.Matches(tail);
        if (reported.Count > 0)
        {
            var mib = reported.Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return new Observation(modelPath, ctx, ub, mib * 1024 * 1024, Failed: false, kv, build);
        }

        return null;
    }

    /// <summary>
    /// The key a cost is learned under: the model file's name and size (a re-download or another
    /// quant starts fresh) and the K cache type.
    /// </summary>
    public static string KeyFor(string modelPath, string kvType)
    {
        string file;
        try
        {
            var info = new FileInfo(modelPath);
            file = info.Name + "|" + (info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "?");
        }
        catch
        {
            file = Path.GetFileName(modelPath) + "|?";
        }

        return file + "|" + (string.IsNullOrWhiteSpace(kvType) ? "f16" : kvType.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Records <paramref name="seen"/> in <paramref name="store"/> (a JSON object keyed by <see cref="KeyFor"/>):
    /// learned when the buffer was over <see cref="LearnAtBytes"/>, dropped when a load showed it small.
    /// Returns true when the store changed.
    /// </summary>
    public static bool Record(JsonObject store, Observation seen, DateTime nowUtc)
    {
        var key = KeyFor(seen.ModelPath, seen.KvType);
        if (seen.ComputeBytes < LearnAtBytes)
        {
            return store.Remove(key);
        }

        store[key] = new JsonObject
        {
            ["bytesPerUbatchToken"] = Math.Round(seen.BytesPerUbatchToken, 2),
            ["context"] = seen.Context,
            ["ubatch"] = seen.Ubatch,
            ["computeMiB"] = Math.Round(seen.ComputeBytes / (1024 * 1024), 2),
            ["source"] = seen.Failed ? "out of memory" : "loaded",
            ["serverBuild"] = seen.ServerBuild,
            ["updatedUtc"] = nowUtc.ToString("o", CultureInfo.InvariantCulture),
        };
        return true;
    }

    /// <summary>
    /// The learned cost per micro-batch × Context token for <paramref name="modelPath"/> with K cache
    /// <paramref name="kvType"/> on llama-server build <paramref name="serverBuild"/>, or 0.
    /// </summary>
    public static double BytesPerUbatchToken(JsonObject? store, string modelPath, string kvType, string serverBuild = "")
        => store?[KeyFor(modelPath, kvType)] is JsonObject entry && SameBuild(entry, serverBuild)
           && entry["bytesPerUbatchToken"] is JsonValue value && value.TryGetValue<double>(out var bytes) && bytes > 0 ? bytes : 0;

    /// <summary>A cost applies only to the build that showed it; one saved without a build is from before builds were recorded.</summary>
    private static bool SameBuild(JsonNode? entry, string serverBuild)
        => (entry?["serverBuild"]?.ToString() ?? string.Empty).Equals(serverBuild ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every learned cost for <paramref name="modelPath"/> on llama-server build <paramref name="serverBuild"/>, by K cache type.</summary>
    public static System.Collections.Generic.Dictionary<string, double> ByKvType(JsonObject? store, string modelPath, string serverBuild = "")
    {
        var result = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (store is null)
        {
            return result;
        }

        var prefix = KeyFor(modelPath, "x");
        prefix = prefix[..(prefix.Length - 1)];
        foreach (var (key, node) in store)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && SameBuild(node, serverBuild)
                && node?["bytesPerUbatchToken"] is JsonValue value && value.TryGetValue<double>(out var bytes) && bytes > 0)
            {
                result[key[prefix.Length..]] = bytes;
            }
        }

        return result;
    }

    /// <summary>
    /// The largest Context whose KV cache and compute buffer fit <paramref name="budgetBytes"/> at
    /// micro-batch <paramref name="ubatch"/>, snapped down to 1,024; 0 when nothing fits.
    /// </summary>
    public static int FittingContext(double budgetBytes, double kvBytesPerToken, double computeBytesPerUbatchToken, int ubatch)
    {
        var perToken = Math.Max(0, kvBytesPerToken) + Math.Max(0, computeBytesPerUbatchToken) * Math.Max(1, ubatch);
        if (budgetBytes <= 0 || perToken <= 0)
        {
            return 0;
        }

        var tokens = budgetBytes / perToken;
        return tokens < 1024 ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(tokens / 1024.0) * 1024.0);
    }
}
