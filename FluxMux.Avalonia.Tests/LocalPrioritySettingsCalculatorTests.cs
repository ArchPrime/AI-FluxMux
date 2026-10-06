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

    private static FluxMuxRuntimeService.LocalHardwareLaunchAdvice AdviceWithNative(int nativeCtx)
    {
        return new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            GpuTotalGb = 32,
            ModelFileGb = 16,
            NvidiaAvailable = true,
            ContextPriorityFirst = Math.Min(nativeCtx, 65536),
            ContextPrioritySecond = Math.Min(nativeCtx, 49152),
            ContextSafeDefault = Math.Min(nativeCtx, 32768),
            ContextPriorityLower = Math.Min(nativeCtx, 16384),
            ModelMaxCtx = nativeCtx,
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
    }

    [Fact]
    public void Extend_context_off_keeps_native_ceiling_and_no_yarn()
    {
        var advice = AdviceWithNative(131072);
        // Context length first.
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: false);

        // Native ceiling: ContextPriorityFirst is capped at modelMaxCtx (131072), so 65536.
        Assert.Equal("65536", settings["OverrideContext"]);
        Assert.Equal("Auto", settings["LocalRopeScaling"]);
        Assert.Equal("Auto", settings["LocalRopeScale"]);
    }

    [Fact]
    public void Extend_context_on_lifts_context_past_native_and_sets_yarn()
    {
        var advice = AdviceWithNative(131072);
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true);

        // Extended ceiling = 131072 * 2 = 262144.
        Assert.Equal("262144", settings["OverrideContext"]);
        Assert.Equal("yarn", settings["LocalRopeScaling"]);
        // target/native = 2.0 -> smallest option >= 2.0 is "2".
        Assert.Equal("2", settings["LocalRopeScale"]);
        // Overflow must spill to RAM.
        Assert.Equal("GPU + CPU", settings["LocalGpuOffloadMode"]);
        Assert.Equal("Unlimited", settings["LocalCacheRam"]);
    }

    [Fact]
    public void Extend_context_on_with_yarn_disabled_still_extends_but_no_rope_scaling()
    {
        var advice = AdviceWithNative(131072);
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true,
            useYarn: false);

        // The context still extends past the native window (overflow spills to RAM)...
        Assert.Equal("262144", settings["OverrideContext"]);
        Assert.Equal("GPU + CPU", settings["LocalGpuOffloadMode"]);
        Assert.Equal("Unlimited", settings["LocalCacheRam"]);
        // ...but no YaRN rope scaling is applied.
        Assert.Equal("Auto", settings["LocalRopeScaling"]);
        Assert.Equal("Auto", settings["LocalRopeScale"]);
    }

    [Theory]
    [InlineData(131072, "q8_0", 524288)]   // 4x native, KV at floor -> rope bound
    [InlineData(131072, "q4_1", 262144)]   // KV below floor -> 2x native KV bound
    [InlineData(131072, "q6_k", 262144)]   // KV below floor -> 2x native KV bound
    [InlineData(0, "q8_0", 0)]             // no native window -> no bound
    public void Effective_useful_ceiling_bounded_by_rope_and_kv_fidelity(int native, string kv, int expected)
    {
        Assert.Equal(expected, LocalPrioritySettingsCalculator.EffectiveUsefulCeiling(native, kv));
    }

    [Fact]
    public void Extend_context_on_with_context_not_top_priority_still_uses_native_quality_pick()
    {
        var advice = AdviceWithNative(131072);
        // Speed first, Fidelity second, context third (rank 2) -> not a top priority, so no extension.
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Speed", "Fidelity", "Context length", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true);

        // Context is rank 2 (third of six), so it uses the lower window (capped at native) and no YaRN.
        Assert.Equal("16384", settings["OverrideContext"]);
        Assert.Equal("Auto", settings["LocalRopeScaling"]);
        Assert.Equal("Auto", settings["LocalRopeScale"]);
    }

    [Fact]
    public void Extend_context_scale_picks_smallest_option_reaching_target()
    {
        // Native 131072, extended 262144 -> ratio 2.0 -> "2".
        var advice = AdviceWithNative(131072);
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true);
        Assert.Equal("2", settings["LocalRopeScale"]);
    }

    [Fact]
    public void Extend_context_on_disables_fit_to_vram()
    {
        // When YaRN extends the context past the native window, the overflow is meant to live
        // in RAM, so Fit-to-VRAM (KV compression) should be disabled.
        var advice = AdviceWithNative(131072);
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true);
        Assert.Equal("Disabled", settings["LocalFit"]);
    }

    [Fact]
    public void Extend_context_off_keeps_fit_enabled_by_default()
    {
        // Without YaRN extension, Fit-to-VRAM follows the normal priority logic (enabled unless
        // Speed is first and the hardware doesn't prefer fit).
        var advice = AdviceWithNative(131072);
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: false);
        Assert.Equal("Enabled", settings["LocalFit"]);
    }

    [Fact]
    public void Yarn_target_from_option_maps_fixed_targets()
    {
        Assert.Equal(512 * 1024, LocalPrioritySettingsCalculator.YarnTargetFromOption("512K", 131072));
        Assert.Equal(768 * 1024, LocalPrioritySettingsCalculator.YarnTargetFromOption("768K", 131072));
        Assert.Equal(1024 * 1024, LocalPrioritySettingsCalculator.YarnTargetFromOption("1M", 131072));
        // Auto returns a large value (so the RAM bound is the limiting factor).
        Assert.True(LocalPrioritySettingsCalculator.YarnTargetFromOption("Auto", 131072) > 1024 * 1024);
        // Never below the native window.
        Assert.True(LocalPrioritySettingsCalculator.YarnTargetFromOption("512K", 600000) >= 600000);
    }

    [Fact]
    public void Ram_bounded_yarn_ceiling_uses_available_ram()
    {
        // 64 GB total RAM, 16 GB reserve -> 48 GB available.
        // q8_0 KV = 262144 bytes/token -> 48 GB / 262144 ≈ 196,608 overflow tokens.
        // VRAM window = max(native 131072, ContextPriorityFirst 65536) = 131072.
        // Ceiling ≈ 131072 + 196608 = 327680, snapped to 1024 boundary.
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            GpuTotalGb = 32,
            ModelFileGb = 16,
            NvidiaAvailable = true,
            ContextPriorityFirst = 65536,
            ModelMaxCtx = 131072,
            TotalRamGb = 64.0,
            KvBytesPerToken = 262144,
            OffloadMode = "GPU + CPU"
        };
        var ceiling = LocalPrioritySettingsCalculator.RamBoundedYarnCeiling(advice, 131072);
        // Should be well above the native window (extended) but bounded by RAM.
        Assert.True(ceiling > 131072, $"Expected extension above native, got {ceiling}");
        Assert.True(ceiling <= 1024 * 1024, $"Expected ceiling under 1M, got {ceiling}");
        // Should be a multiple of 1024.
        Assert.Equal(0, ceiling % 1024);
    }

    [Fact]
    public void Ram_bounded_yarn_ceiling_uses_effective_kv_when_provided()
    {
        // Same 64 GB RAM as the default test, but the effective KV bytes per token is
        // reduced (hybrid-KV + Flash Attention) so more tokens fit in the same RAM.
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            GpuTotalGb = 32,
            ModelFileGb = 16,
            NvidiaAvailable = true,
            ContextPriorityFirst = 65536,
            ModelMaxCtx = 131072,
            TotalRamGb = 64.0,
            KvBytesPerToken = 262144,
            OffloadMode = "GPU + CPU"
        };
        var defaultCeiling = LocalPrioritySettingsCalculator.RamBoundedYarnCeiling(advice, 131072);
        // Effective KV ~38% of default (hybrid-KV) -> more overflow tokens -> higher ceiling.
        var effectiveCeiling = LocalPrioritySettingsCalculator.RamBoundedYarnCeiling(advice, 131072, (long)(262144 * 0.38));
        Assert.True(effectiveCeiling > defaultCeiling,
            $"Expected effective-KV ceiling ({effectiveCeiling}) to exceed default ({defaultCeiling})");
        Assert.Equal(0, effectiveCeiling % 1024);
        // A larger effective KV (e.g. f16) should yield a lower ceiling than the default.
        var largeCeiling = LocalPrioritySettingsCalculator.RamBoundedYarnCeiling(advice, 131072, 524288);
        Assert.True(largeCeiling < defaultCeiling,
            $"Expected larger-KV ceiling ({largeCeiling}) to be below default ({defaultCeiling})");
    }

    [Fact]
    public void Extend_context_with_1m_target_reaches_higher_than_ram_bound()
    {
        // With a 1M target and ample RAM, the context should extend well past the native window.
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            GpuTotalGb = 32,
            ModelFileGb = 16,
            NvidiaAvailable = true,
            ContextPriorityFirst = 131072,
            ModelMaxCtx = 131072,
            TotalRamGb = 64.0,
            KvBytesPerToken = 262144,
            OffloadMode = "GPU + CPU",
            OffloadModeAtMaxContext = "GPU + CPU"
        };
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true,
            yarnMaxContext: "1M");
        var ctx = int.Parse(settings["OverrideContext"]);
        Assert.True(ctx > 131072, $"Expected context above native 131072, got {ctx}");
        Assert.Equal("yarn", settings["LocalRopeScaling"]);
        Assert.Equal("GPU + CPU", settings["LocalGpuOffloadMode"]);
        Assert.Equal("Disabled", settings["LocalFit"]);
    }

    [Fact]
    public void Extend_context_auto_is_bounded_by_ram()
    {
        // With Auto and limited RAM, the context should be bounded by the RAM estimate.
        var advice = new FluxMuxRuntimeService.LocalHardwareLaunchAdvice
        {
            GpuTotalGb = 32,
            ModelFileGb = 16,
            NvidiaAvailable = true,
            ContextPriorityFirst = 131072,
            ModelMaxCtx = 131072,
            TotalRamGb = 32.0, // 32 GB total, 16 GB reserve -> 16 GB available
            KvBytesPerToken = 262144,
            OffloadMode = "GPU + CPU"
        };
        var resolved = LocalPrioritySettingsCalculator.ResolveOrder(new[]
        {
            "Context length", "Speed", "Fidelity", "Reasoning depth", "Reply length", "Stability"
        });
        var settings = LocalPrioritySettingsCalculator.Calculate(
            resolved,
            advice,
            chatTemplate: "qwen",
            specType: "ngram-simple",
            currentSwaFull: "Disabled",
            autoCompress: true,
            visionEnabled: "Disabled",
            visionProjectorPath: string.Empty,
            visionMaxImageEdge: "1344",
            overrideThreads: "8",
            threadsBatch: "8",
            extendContextIntoRam: true,
            yarnMaxContext: "Auto");
        var ctx = int.Parse(settings["OverrideContext"]);
        // 16 GB / 262144 ≈ 65536 overflow tokens. Ceiling ≈ 131072 + 65536 = 196608.
        Assert.True(ctx > 131072, $"Expected extension, got {ctx}");
        Assert.True(ctx < 512 * 1024, $"Expected RAM-bounded context under 512K, got {ctx}");
    }
}
