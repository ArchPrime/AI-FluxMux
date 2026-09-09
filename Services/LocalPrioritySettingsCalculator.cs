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
        string? currentContext = null)
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
        var ctx = KeepHandTunedContext(
            speed == 0
                ? advice.ContextPriorityLower
                : context == 0
                    ? advice.ContextPriorityFirst
                    : context == 1
                        ? advice.ContextPrioritySecond
                        : context >= lastIndex - 1
                            ? advice.ContextPriorityLower
                            : advice.ContextSafeDefault,
            currentContext,
            imagesOn ? advice.ContextPriorityFirst : 0);

        var gpuMode = context == 0
            ? advice.OffloadModeAtMaxContext
            : speed == 0 && advice.OffloadMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase)
                ? "GPU only"
                : advice.OffloadMode;
        var gpuLayers = gpuMode.Equals("GPU only", StringComparison.OrdinalIgnoreCase)
            ? "999"
            : gpuMode.Equals("CPU only", StringComparison.OrdinalIgnoreCase)
                ? "0"
                : "Auto";
        var kv = ClampKvCacheTypeAtOrAboveMinimum(
            fidelity == 0 && advice.PreferFitEnabled == false
                ? "f16"
                : MinimumKvCacheType);
        var temp = fidelity == 0 || stability == 0 ? "0.2" : speed == 0 ? "0.4" : "0.3";
        var spec = fidelity == 0 ? "Disabled" : stability == 0 ? "ngram-simple" : specType;
        var reasoning = thinking == 0 ? "On" : "Off";
        var fit = speed == 0 && !advice.PreferFitEnabled ? "Disabled" : "Enabled";
        var cacheRam = advice.CacheRam;
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
            ["LocalVisionMaxImageEdge"] = visionMaxImageEdge
        };
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
