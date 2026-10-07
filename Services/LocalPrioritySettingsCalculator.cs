using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluxMux.Avalonia.Services;

public static class LocalPrioritySettingsCalculator
{
    /// <summary>
    /// Lowest KV cache type the priorities wizard / AutoTune may choose.
    /// Below q8_0, fidelity loss usually outweighs any Context gain.
    /// </summary>
    public const string MinimumKvCacheType = "q8_0";

    public static readonly string[] GoalNames =
    [
        "Stability",
        "Speed",
        "Fidelity",
        "Context length",
        "Reply length",
        "Reasoning depth"
    ];

    public static string CanonicalizeGoalName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals("Thinking + Fidelity", StringComparison.OrdinalIgnoreCase))
        {
            return "Fidelity";
        }

        if (text.Equals("Thinking", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Deep Reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return "Reasoning depth";
        }

        if (text.Equals("Context Capacity", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Long Context", StringComparison.OrdinalIgnoreCase))
        {
            return "Context length";
        }

        return text;
    }

    public static IReadOnlyList<string> ResolveOrder(IEnumerable<string> priorityOrder)
    {
        var resolved = (priorityOrder ?? Array.Empty<string>())
            .Select(CanonicalizeGoalName)
            .Where(value => GoalNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in GoalNames)
        {
            if (!resolved.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(fallback);
            }
        }

        return resolved;
    }

    public static Dictionary<string, string> Calculate(
        IReadOnlyList<string> resolved,
        FluxMuxRuntimeService.LocalHardwareLaunchAdvice advice,
        string chatTemplate,
        string specType,
        string currentSwaFull,
        bool autoCompress,
        string visionEnabled,
        string visionProjectorPath,
        string visionMaxImageEdge,
        string overrideThreads,
        string threadsBatch,
        string? currentMaxTokens = null,
        string? currentContext = null,
        bool extendContextIntoRam = false,
        string yarnMaxContext = "Auto",
        long? effectiveKvBytesPerToken = null,
        string? kvCacheChoice = null,
        bool useYarn = true)
    {
        int Rank(string name)
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                if (resolved[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return resolved.Count;
        }

        var speed = Rank("Speed");
        var fidelity = Rank("Fidelity");
        var thinking = Rank("Reasoning depth");
        var context = Rank("Context length");
        var reply = Rank("Reply length");
        var stability = Rank("Stability");
        var autoTemplate = string.IsNullOrWhiteSpace(chatTemplate) ? "Auto" : chatTemplate;
        if (string.IsNullOrWhiteSpace(specType) || specType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            specType = advice.SpecType;
        }

        var lastIndex = Math.Max(0, resolved.Count - 1);
        var imagesOn = visionEnabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        int nativeCeiling = advice.ModelMaxCtx;
        // YaRN extension: the context may exceed the native window. The target is bounded by:
        //   1. The user's "Max YaRN context" choice (Auto / 512K / 768K / 1M).
        //   2. How much RAM is available for the KV overflow (Auto mode).
        //   3. YaRN's practical ceiling (~1M tokens).
        int extendedCeiling = 0;
        if (extendContextIntoRam && nativeCeiling > 0)
        {
            int userTarget = YarnTargetFromOption(yarnMaxContext, nativeCeiling);
            int ramBounded = RamBoundedYarnCeiling(advice, nativeCeiling, effectiveKvBytesPerToken);
            int yarnPracticalCeiling = 1048576; // ~1M, YaRN's practical limit
            extendedCeiling = Math.Min(userTarget, Math.Min(ramBounded, yarnPracticalCeiling));
            // Never extend below the native window.
            if (extendedCeiling < nativeCeiling)
            {
                extendedCeiling = nativeCeiling;
            }
        }

        int BaseContextPick()
        {
            return speed == 0
                ? advice.ContextPriorityLower
                : context == 0
                    ? advice.ContextPriorityFirst
                    : context == 1
                        ? advice.ContextPrioritySecond
                        : context >= lastIndex - 1
                            ? advice.ContextPriorityLower
                            : advice.ContextSafeDefault;
        }

        int wizardContext = BaseContextPick();
        if (extendContextIntoRam && extendedCeiling > 0 && context <= 1)
        {
            // Context is a top priority and the user opted into extension: aim for the extended ceiling.
            wizardContext = Math.Max(wizardContext, extendedCeiling);
        }

        var ctx = KeepHandTunedContext(
            wizardContext,
            currentContext,
            imagesOn ? advice.ContextPriorityFirst : 0);

        var gpuMode = context == 0
            ? advice.OffloadModeAtMaxContext
            : speed == 0 && advice.OffloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase)
                ? "GPU only"
                : advice.OffloadMode;
        // YaRN extension needs the overflow to live in ordinary memory, so force GPU + CPU when extending.
        if (extendContextIntoRam && extendedCeiling > 0
            && !gpuMode.Equals("GPU + CPU", StringComparison.OrdinalIgnoreCase)
            && !gpuMode.Equals("CPU only", StringComparison.OrdinalIgnoreCase))
        {
            gpuMode = "GPU + CPU";
        }
        // When extending, ensure the KV overflow can actually spill to RAM.
        var cacheRam = advice.CacheRam;
        if (extendContextIntoRam && extendedCeiling > 0
            && !cacheRam.Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
        {
            cacheRam = "Unlimited";
        }
        var gpuLayers = gpuMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase)
            ? "999"
            : gpuMode.Equals("CPU only", StringComparison.OrdinalIgnoreCase)
                ? "0"
                : "Auto";
        var kv = ClampKvCacheTypeAtOrAboveMinimum(
            kvCacheChoice is not null
                ? kvCacheChoice
                : fidelity == 0 && advice.PreferFitEnabled == false
                    ? "f16"
                    : MinimumKvCacheType);
        var temp = fidelity == 0 || stability == 0 ? "0.2" : speed == 0 ? "0.4" : "0.3";
        var spec = fidelity == 0 ? "Disabled" : stability == 0 ? "ngram-simple" : specType;
        var reasoning = thinking == 0 ? "On" : "Off";
        // When YaRN extends the context past the native window, the overflow is meant to live
        // in ordinary memory. Fit-to-VRAM (KV compression to squeeze more onto the card) is
        // counterproductive there — it adds compression overhead for no benefit, since the
        // extra context is deliberately spilling to RAM.
        var extending = extendContextIntoRam && extendedCeiling > 0 && nativeCeiling > 0;
        var fit = extending
            ? "Disabled"
            : speed == 0 && !advice.PreferFitEnabled ? "Disabled" : "Enabled";
        var batch = context == 0
            ? SnapWizardBatchDown(advice.BatchSize)
            : speed == 0 && context > 1 ? ScaleWizardBatchUp(advice.BatchSize) : advice.BatchSize;
        var ubatch = DeriveUbatch(batch);
        var maxTokens = KeepHigherMaxTokens(
            reply == 0 ? 16384
            : reply == 1 ? 8192
            : reply == 2 ? 4096
            : 2048,
            currentMaxTokens);

        // YaRN rope scaling: only set when actually extending past the native window AND the
        // user has YaRN enabled. When YaRN is off, the context may still exceed the native
        // window (the overflow spills to RAM) but no rope scaling is applied, so the far
        // context relies on the model's native positional encoding only.
        string ropeScaling = "Auto";
        string ropeScale = "Auto";
        if (useYarn && extendContextIntoRam && extendedCeiling > 0 && nativeCeiling > 0)
        {
            var target = int.TryParse(ctx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
            if (target > nativeCeiling)
            {
                var ratio = (double)target / nativeCeiling;
                double[] options = { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 8.0 };
                string chosen = options.Length.ToString(CultureInfo.InvariantCulture); // fallback
                foreach (var opt in options)
                {
                    if (opt >= ratio - 1e-6)
                    {
                        chosen = opt.ToString(CultureInfo.InvariantCulture);
                        break;
                    }
                }

                ropeScaling = "yarn";
                ropeScale = chosen;
            }
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverrideContext"] = ctx,
            ["LocalTemperature"] = temp,
            ["LocalKvCacheTypeK"] = kv,
            ["LocalKvCacheTypeV"] = kv,
            ["LocalFlashAttention"] = advice.FlashAttention,
            ["LocalGpuOffloadMode"] = gpuMode,
            ["OverrideThreads"] = overrideThreads,
            ["LocalThreadsBatch"] = threadsBatch,
            ["LocalChatTemplate"] = autoTemplate,
            ["LocalMultiUserMode"] = "Disabled",
            ["LocalUnbanTokensMode"] = autoTemplate.Equals("qwen", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled",
            ["AutoCompressEnabled"] = autoCompress.ToString(),
            ["GpuLayers"] = gpuLayers,
            ["OverrideMaxTokens"] = maxTokens,
            ["LocalBatchSize"] = batch,
            ["LocalUbatchSize"] = ubatch,
            ["LocalSpecType"] = spec,
            ["LocalCacheReuse"] = "256",
            ["LocalCacheRam"] = cacheRam,
            ["LocalFit"] = fit,
            ["LocalSwaFull"] = advice.EnableSwaFull ? "Enabled" : currentSwaFull,
            ["LocalReasoning"] = reasoning,
            ["LocalChatParser"] = "Jinja",
            ["LocalVisionEnabled"] = visionEnabled,
            ["LocalVisionProjectorPath"] = visionProjectorPath,
            ["LocalVisionMaxImageEdge"] = visionMaxImageEdge,
            ["LocalRopeScaling"] = ropeScaling,
            ["LocalRopeScale"] = ropeScale
        };
    }

    /// <summary>
    /// Maps the "Max YaRN context" option to a target context. Auto returns a large value
    /// (so the RAM bound is the limiting factor); the fixed options return their token counts.
    /// </summary>
    public static int YarnTargetFromOption(string option, int nativeCeiling)
    {
        int target = (option ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "512K" => 512 * 1024,
            "768K" => 768 * 1024,
            "1M" => 1024 * 1024,
            _ => int.MaxValue // Auto: let the RAM bound decide
        };
        return Math.Max(target, nativeCeiling);
    }

    /// <summary>
    /// Computes how far YaRN can extend the context given the available RAM. The overflow
    /// (context above the VRAM-resident window) lives in system RAM, so the target is bounded
    /// by how many tokens of KV fit in the available RAM. A conservative reserve is subtracted
    /// to leave headroom for the OS and other processes.
    /// </summary>
    public static int RamBoundedYarnCeiling(FluxMuxRuntimeService.LocalHardwareLaunchAdvice advice, int nativeCeiling, long? effectiveKvBytesPerToken = null)
    {
        double totalRamGb = advice.TotalRamGb;
        if (totalRamGb <= 0.0)
        {
            // No RAM info: fall back to a modest 2x extension.
            return nativeCeiling * 2;
        }

        // Conservative reserve: 8 GB for the OS + other processes (or 25% of total, whichever is smaller).
        // This leaves more room for the KV cache overflow when extending context into RAM.
        double reserveGb = Math.Min(8.0, totalRamGb * 0.25);
        double availableGb = Math.Max(0.0, totalRamGb - reserveGb);

        // Use the effective KV bytes per token if provided (from the profile's actual KV type,
        // adjusted for hybrid-KV and Flash Attention); otherwise fall back to the advice default.
        long kvBytesPerToken = effectiveKvBytesPerToken ?? Math.Max(1L, advice.KvBytesPerToken);
        double availableBytes = availableGb * 1073741824.0;
        int overflowTokens = (int)(availableBytes / kvBytesPerToken);

        // The VRAM-resident window is ContextPriorityFirst (what fits on the card).
        int vramWindow = Math.Max(nativeCeiling, advice.ContextPriorityFirst);
        int ceiling = vramWindow + overflowTokens;

        // Snap down to a clean 1024 boundary.
        ceiling = (int)(Math.Floor(ceiling / 1024.0) * 1024.0);
        return Math.Max(ceiling, nativeCeiling);
    }

    /// <summary>
    /// The "effective useful ceiling" for a YaRN extension: the context length beyond which the
    /// extension stops paying for itself. Two penalties kick in as the target grows past the
    /// model's native window:
    ///   1. RoPE extrapolation degrades. Past ~4x the native window the positional encoding is
    ///      heavily interpolated (not native), so retrieval quality on far context drops off.
    ///   2. KV fidelity degrades. If the best KV type that still fits is coarser than q8_0, the
    ///      far tokens are quantised harder and the model "remembers the topic but loses the
    ///      details".
    /// Returns the smaller of the two ceilings (or 0 when no meaningful bound applies). This is a
    /// warning heuristic, not a hard limit — the RAM/1M ceilings still allow going further.
    /// </summary>
    public static int EffectiveUsefulCeiling(int nativeCeiling, string bestFittingKvType)
    {
        if (nativeCeiling <= 0)
        {
            return 0;
        }

        // RoPE extrapolation bound: 4x the native window.
        int ropeBound = nativeCeiling * 4;

        // KV fidelity bound: if the best fitting type is already below the q8_0 floor, the far
        // context is quantised harder, so treat 2x native as the useful ceiling.
        int kvBound = IsBelowMinimumKvCacheType(bestFittingKvType) ? nativeCeiling * 2 : int.MaxValue;

        return Math.Min(ropeBound, kvBound);
    }

    /// <summary>
    /// Maps a KV cache type to its bytes-per-token (K+V combined).
    /// </summary>
    public static double KvBytesPerTokenForType(string kvType)
    {
        static double Resolve(string type) => type.ToLowerInvariant() switch
        {
            "q4_1" => 64 * 1024d,
            "q5_1" => 80 * 1024d,
            "q6_k" => 96 * 1024d,
            "q8_0" => 128 * 1024d,
            _ => 192 * 1024d // f16
        };

        return Resolve(kvType) * 2; // K + V
    }

    /// <summary>
    /// Estimates the KV cache size in GB for a given context length and KV type.
    /// </summary>
    public static double EstimateKvCacheGb(int contextTokens, string kvType)
    {
        if (contextTokens <= 0)
        {
            return 0.0;
        }

        double bytesPerToken = KvBytesPerTokenForType(kvType);
        double totalBytes = contextTokens * bytesPerToken;
        return totalBytes / 1073741824.0; // to GB
    }

    /// <summary>
    /// Estimates the KV cache size in GB for a given context length, using an explicit
    /// bytes-per-token value (e.g. the effective KV bytes per token that accounts for
    /// hybrid-KV and Flash Attention). Use this when the bytes-per-token has already been
    /// adjusted for the model's characteristics, rather than deriving it from the KV type.
    /// </summary>
    public static double EstimateKvCacheGb(int contextTokens, double bytesPerToken)
    {
        if (contextTokens <= 0 || bytesPerToken <= 0)
        {
            return 0.0;
        }

        double totalBytes = contextTokens * bytesPerToken;
        return totalBytes / 1073741824.0; // to GB
    }

    /// <summary>
    /// Returns true when <paramref name="kvType"/> is coarser than <see cref="MinimumKvCacheType"/> (q8_0).
    /// Unknown or empty values are treated as below the floor so callers raise them to q8_0.
    /// </summary>
    public static bool IsBelowMinimumKvCacheType(string? kvType)
    {
        var text = (kvType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.ToLowerInvariant() switch
        {
            "f16" or "f32" or "bf16" or "q8_0" or "q8_1" or "q8_k" => false,
            _ => true
        };
    }

    public static string ClampKvCacheTypeAtOrAboveMinimum(string? kvType)
        => IsBelowMinimumKvCacheType(kvType) ? MinimumKvCacheType : kvType!.Trim();

    public static string KeepHigherInteger(int wizardValue, string? currentValue)
    {
        if (int.TryParse(currentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)
            && current > wizardValue)
        {
            return current.ToString(CultureInfo.InvariantCulture);
        }

        return wizardValue.ToString(CultureInfo.InvariantCulture);
    }

    public static string KeepHigherMaxTokens(int wizardMaxTokens, string? currentMaxTokens)
        => KeepHigherInteger(wizardMaxTokens, currentMaxTokens);

    /// <summary>
    /// Keeps a working hand-set Context above the wizard pick, but not the editor max
    /// and not a text-only window after Images was turned on.
    /// <paramref name="adviceCeiling"/> is Context-first when Images are on; 0 means no Images cap.
    /// </summary>
    public static string KeepHandTunedContext(int wizardValue, string? currentValue, int adviceCeiling = 0)
    {
        if (!int.TryParse(currentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)
            || current <= 0)
        {
            return wizardValue.ToString(CultureInfo.InvariantCulture);
        }

        if (current >= DeepSeekHarnessSetup.MaxLocalContextWindow)
        {
            return wizardValue.ToString(CultureInfo.InvariantCulture);
        }

        if (adviceCeiling > 0 && current > adviceCeiling)
        {
            current = adviceCeiling;
        }

        return KeepHigherInteger(wizardValue, current.ToString(CultureInfo.InvariantCulture));
    }

    public static void PreserveHandTunedReplySettings(
        IDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> current,
        int contextCeiling = 0)
    {
        if (current.TryGetValue("OverrideMaxTokens", out var currentMax)
            && settings.TryGetValue("OverrideMaxTokens", out var wizardMax)
            && int.TryParse(wizardMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wizard))
        {
            settings["OverrideMaxTokens"] = KeepHigherInteger(wizard, currentMax);
        }

        if (current.TryGetValue("OverrideContext", out var currentContext)
            && settings.TryGetValue("OverrideContext", out var wizardContext)
            && int.TryParse(wizardContext, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wizardCtx))
        {
            settings["OverrideContext"] = KeepHandTunedContext(wizardCtx, currentContext, contextCeiling);
        }

        KeepIfPresent(settings, current, "LocalTemperature");
        KeepIfPresent(settings, current, "LocalReasoning");
        KeepIfPresent(settings, current, "LocalChatTemplate");
        KeepIfPresent(settings, current, "LocalChatParser");
        KeepIfPresent(settings, current, "LocalUnbanTokensMode");
        KeepIfPresent(settings, current, "AutoCompressEnabled");
        KeepIfPresent(settings, current, "LocalVisionEnabled");
        KeepIfPresent(settings, current, "LocalVisionProjectorPath");
        KeepIfPresent(settings, current, "LocalVisionMaxImageEdge");
        KeepIfPresent(settings, current, "LocalMultiGpuMode");
        KeepIfPresent(settings, current, "LocalSplitMode");
        KeepIfPresent(settings, current, "LocalMainGpu");
        KeepIfPresent(settings, current, "LocalTensorSplit");
    }

    private static void KeepIfPresent(
        IDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> current,
        string key)
    {
        if (current.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            settings[key] = value;
        }
    }

    public static string SnapWizardBatchDown(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 64)
        {
            return value;
        }

        return Math.Max(64, parsed / 2).ToString(CultureInfo.InvariantCulture);
    }

    public static string ScaleWizardBatchUp(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return value;
        }

        return Math.Min(2048, parsed * 2).ToString(CultureInfo.InvariantCulture);
    }

    public static string DeriveUbatch(string batch)
    {
        if (!int.TryParse(batch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return "128";
        }

        return Math.Max(64, parsed / 4).ToString(CultureInfo.InvariantCulture);
    }
}
