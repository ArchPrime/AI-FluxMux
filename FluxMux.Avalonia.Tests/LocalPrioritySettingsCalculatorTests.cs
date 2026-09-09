using System;
using System.Collections.Generic;
using System.Linq;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalPrioritySettingsCalculatorTests
{
    private static readonly FluxMuxRuntimeService.LocalHardwareLaunchAdvice Advice = new()
    {
        GpuTotalGb = 32,
        ModelFileGb = 16,
        NvidiaAvailable = true,
        ContextPriorityFirst = 65536,
        ContextPrioritySecond = 49152,
        ContextSafeDefault = 32768,
        ContextPriorityLower = 16384,
        OffloadMode = "GPU only",
        OffloadModeAtMaxContext = "GPU + CPU",
        GpuLayers = "Auto",
        BatchSize = "512",
        UbatchSize = "128",
        FlashAttention = "Enabled",
        PreferFitEnabled = true,
        SpecType = "ngram-simple",
        EnableSwaFull = false,
        MaxTokens = "4096",
        CacheRam = "8192"
    };

    public static IEnumerable<object[]> AllGoalOrders()
    {
        foreach (var order in Permute(LocalPrioritySettingsCalculator.GoalNames))
        {
            yield return [order];
        }
    }

    [Theory]
    [MemberData(nameof(AllGoalOrders))]
    public void Every_goal_order_maps_to_the_canonical_hardware_and_model_settings(string[] order)
    {
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(order);
        Assert.Equal(6, resolved.Count);
        Assert.Equal(order, resolved.ToArray());

        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            Advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8");

        int Rank(string name) => Array.FindIndex(order, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        var lastIndex = order.Length - 1;
        var speed = Rank("Speed");
        var fidelity = Rank("Fidelity");
        var thinking = Rank("Reasoning depth");
        var context = Rank("Context length");
        var reply = Rank("Reply length");
        var stability = Rank("Stability");

        var expectedCtx = speed == 0
            ? Advice.ContextPriorityLower
            : context == 0
                ? Advice.ContextPriorityFirst
                : context == 1
                    ? Advice.ContextPrioritySecond
                    : context >= lastIndex - 1
                        ? Advice.ContextPriorityLower
                        : Advice.ContextSafeDefault;
        Assert.Equal(expectedCtx.ToString(), settings["OverrideContext"]);

        var expectedGpu = context == 0 ? Advice.OffloadModeAtMaxContext : "GPU only";
        Assert.Equal(expectedGpu, settings["LocalGpuOffloadMode"]);
        Assert.Equal(expectedGpu == "GPU only" ? "999" : "Auto", settings["GpuLayers"]);

        Assert.Equal("q8_0", settings["LocalKvCacheTypeK"]);
        Assert.Equal("q8_0", settings["LocalKvCacheTypeV"]);
        Assert.False(LocalPrioritySettingsCalculator.IsBelowMinimumKvCacheType(settings["LocalKvCacheTypeK"]));
        Assert.Equal(fidelity == 0 || stability == 0 ? "0.2" : speed == 0 ? "0.4" : "0.3", settings["LocalTemperature"]);
        Assert.Equal(fidelity == 0 ? "Disabled" : "ngram-simple", settings["LocalSpecType"]);
        Assert.Equal(thinking == 0 ? "On" : "Off", settings["LocalReasoning"]);
        Assert.Equal("Enabled", settings["LocalFit"]);
        Assert.Equal(
            reply == 0 ? "16384" : reply == 1 ? "8192" : reply == 2 ? "4096" : "2048",
            settings["OverrideMaxTokens"]);
        Assert.Equal("Disabled", settings["LocalVisionEnabled"]);
        Assert.Equal("Enabled", settings["LocalUnbanTokensMode"]);
        Assert.Equal("Disabled", settings["LocalMultiUserMode"]);
        Assert.Equal("Enabled", settings["LocalFlashAttention"]);

        var expectedBatch = context == 0
            ? LocalPrioritySettingsCalculator.SnapWizardBatchDown(Advice.BatchSize)
            : speed == 0 && context > 1
                ? LocalPrioritySettingsCalculator.ScaleWizardBatchUp(Advice.BatchSize)
                : Advice.BatchSize;
        Assert.Equal(expectedBatch, settings["LocalBatchSize"]);
        Assert.Equal(LocalPrioritySettingsCalculator.DeriveUbatch(expectedBatch), settings["LocalUbatchSize"]);
    }

    [Fact]
    public void Fidelity_first_on_no_fit_hardware_uses_full_precision_kv()
    {
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            ContextPriorityFirst = 8192,
            ContextPrioritySecond = 4096,
            ContextSafeDefault = 4096,
            ContextPriorityLower = 2048,
            OffloadMode = "GPU + CPU",
            OffloadModeAtMaxContext = "CPU only",
            FlashAttention = "Enabled",
            PreferFitEnabled = false,
            SpecType = "ngram-simple",
            BatchSize = "512",
            CacheRam = "4096"
        };
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Fidelity", "Speed", "Stability", "Context Capacity", "Reply length", "Thinking"]),
            advice,
            "Auto",
            "ngram-simple",
            "Disabled",
            true,
            "Enabled",
            @"C:\models\mmproj.gguf",
            "1344",
            "8",
            "8");
        Assert.Equal("f16", settings["LocalKvCacheTypeK"]);
        Assert.Equal("f16", settings["LocalKvCacheTypeV"]);
        Assert.False(LocalPrioritySettingsCalculator.IsBelowMinimumKvCacheType(settings["LocalKvCacheTypeK"]));
        Assert.Equal("Disabled", settings["LocalSpecType"]);
        Assert.Equal("Enabled", settings["LocalVisionEnabled"]);
        Assert.Equal("0.2", settings["LocalTemperature"]);
    }

    [Fact]
    public void ClampKvCacheType_raises_types_below_q8_to_minimum()
    {
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum("q4_1"));
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum("q5_1"));
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum("q6_k"));
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum("Auto"));
        Assert.Equal("q8_0", LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum("q8_0"));
        Assert.Equal("f16", LocalPrioritySettingsCalculator.ClampKvCacheTypeAtOrAboveMinimum("f16"));
    }

    [Fact]
    public void Context_first_priority_still_keeps_kv_at_or_above_q8()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(
                ["Context length", "Speed", "Stability", "Fidelity", "Reply length", "Reasoning depth"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8");
        Assert.Equal(LocalPrioritySettingsCalculator.MinimumKvCacheType, settings["LocalKvCacheTypeK"]);
        Assert.Equal(LocalPrioritySettingsCalculator.MinimumKvCacheType, settings["LocalKvCacheTypeV"]);
    }

    [Fact]
    public void Context_last_uses_the_lower_window_not_the_safe_default()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Speed", "Stability", "Fidelity", "Reply length", "Thinking", "Context Capacity"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8");
        Assert.Equal(Advice.ContextPriorityLower.ToString(), settings["OverrideContext"]);
    }

    [Fact]
    public void ResolveOrder_maps_legacy_goal_names()
    {
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(
            ["Thinking", "Context Capacity", "Speed", "Stability", "Fidelity", "Reply length"]);
        Assert.Equal("Reasoning depth", resolved[0]);
        Assert.Equal("Context length", resolved[1]);
        Assert.Equal("Reasoning depth", LocalPrioritySettingsCalculator.CanonicalizeGoalName("Deep Reasoning"));
        Assert.Equal("Context length", LocalPrioritySettingsCalculator.CanonicalizeGoalName("Long Context"));
        Assert.Equal("Fidelity", LocalPrioritySettingsCalculator.CanonicalizeGoalName("Thinking + Fidelity"));
    }

    [Fact]
    public void Saved_higher_max_tokens_is_kept_when_reply_length_is_not_first()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Speed", "Stability", "Fidelity", "Context Capacity", "Thinking", "Reply length"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8",
            currentMaxTokens: "32768");
        Assert.Equal("32768", settings["OverrideMaxTokens"]);
    }

    [Fact]
    public void Reply_length_first_still_raises_a_lower_saved_max_tokens()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Reply length", "Speed", "Stability", "Fidelity", "Context Capacity", "Thinking"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8",
            currentMaxTokens: "4096");
        Assert.Equal("16384", settings["OverrideMaxTokens"]);
    }

    [Fact]
    public void Images_on_replaces_a_text_only_context_above_the_vision_ceiling()
    {
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            ContextPriorityFirst = 163840,
            ContextPrioritySecond = 131072,
            ContextSafeDefault = 98304,
            ContextPriorityLower = 54272,
            OffloadMode = "GPU only",
            OffloadModeAtMaxContext = "GPU + CPU",
            FlashAttention = "Enabled",
            PreferFitEnabled = true,
            SpecType = "ngram-simple",
            BatchSize = "512",
            CacheRam = "8192"
        };
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(
                ["Context length", "Speed", "Stability", "Fidelity", "Reply length", "Reasoning depth"]),
            advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Enabled",
            string.Empty,
            "1344",
            "8",
            "8",
            currentContext: "213056");
        Assert.Equal("163840", settings["OverrideContext"]);
    }

    [Fact]
    public void Saved_higher_context_is_kept_when_context_is_not_first()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Speed", "Stability", "Fidelity", "Reply length", "Thinking", "Context Capacity"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8",
            currentMaxTokens: "4096",
            currentContext: "163840");
        Assert.Equal("163840", settings["OverrideContext"]);
    }

    [Fact]
    public void Editor_max_context_is_not_kept_as_a_hand_tuned_value()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Context length", "Speed", "Stability", "Fidelity", "Reply length", "Reasoning depth"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Enabled",
            string.Empty,
            "1344",
            "8",
            "8",
            currentContext: DeepSeekHarnessSetup.MaxLocalContextWindow.ToString());
        Assert.Equal(Advice.ContextPriorityFirst.ToString(), settings["OverrideContext"]);
    }

    [Fact]
    public void Context_first_still_raises_a_lower_saved_context()
    {
        var settings = LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(["Context length", "Speed", "Stability", "Fidelity", "Reply length", "Reasoning depth"]),
            Advice,
            "qwen",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8",
            currentContext: "32768");
        Assert.Equal(Advice.ContextPriorityFirst.ToString(), settings["OverrideContext"]);
    }

    [Fact]
    public void Preserve_hand_tuned_reply_settings_keeps_style_and_a_higher_max_tokens()
    {
        var wizard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverrideMaxTokens"] = "4096",
            ["OverrideContext"] = "32768",
            ["LocalTemperature"] = "0.4",
            ["LocalReasoning"] = "On",
            ["LocalChatTemplate"] = "qwen",
            ["LocalChatParser"] = "Jinja",
            ["LocalUnbanTokensMode"] = "Enabled",
            ["AutoCompressEnabled"] = "True"
        };
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OverrideMaxTokens"] = "16384",
            ["OverrideContext"] = "163840",
            ["LocalTemperature"] = "0.15",
            ["LocalReasoning"] = "Off",
            ["LocalChatTemplate"] = "Auto",
            ["LocalChatParser"] = "Skip parsing",
            ["LocalUnbanTokensMode"] = "Disabled",
            ["AutoCompressEnabled"] = "False"
        };

        LocalPrioritySettingsCalculator.PreserveHandTunedReplySettings(wizard, current);

        Assert.Equal("16384", wizard["OverrideMaxTokens"]);
        Assert.Equal("163840", wizard["OverrideContext"]);

        wizard["OverrideContext"] = "32768";
        current["OverrideContext"] = DeepSeekHarnessSetup.MaxLocalContextWindow.ToString();
        LocalPrioritySettingsCalculator.PreserveHandTunedReplySettings(wizard, current);
        Assert.Equal("32768", wizard["OverrideContext"]);
        Assert.Equal("0.15", wizard["LocalTemperature"]);
        Assert.Equal("Off", wizard["LocalReasoning"]);
        Assert.Equal("Auto", wizard["LocalChatTemplate"]);
        Assert.Equal("Skip parsing", wizard["LocalChatParser"]);
        Assert.Equal("Disabled", wizard["LocalUnbanTokensMode"]);
        Assert.Equal("False", wizard["AutoCompressEnabled"]);
    }

    private static IEnumerable<string[]> Permute(string[] items)
    {
        var data = (string[])items.Clone();
        foreach (var perm in Permute(data, 0))
        {
            yield return perm;
        }
    }

    private static IEnumerable<string[]> Permute(string[] items, int start)
    {
        if (start == items.Length)
        {
            yield return (string[])items.Clone();
            yield break;
        }

        for (var i = start; i < items.Length; i++)
        {
            (items[start], items[i]) = (items[i], items[start]);
            foreach (var perm in Permute(items, start + 1))
            {
                yield return perm;
            }

            (items[start], items[i]) = (items[i], items[start]);
        }
    }
}
