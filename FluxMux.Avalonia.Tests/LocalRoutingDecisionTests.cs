using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalRoutingDecisionTests
{
    [Fact]
    public void Overlay_uses_the_only_attached_style_when_there_is_just_one()
    {
        var state = OverlayState(("solo", 0.5, 1024));
        var payload = new JsonObject
        {
            ["model"] = "local",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Hello" }
            }
        };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "local");
        Assert.Equal("solo", overlay?["variant"]?.ToString());
    }

    [Fact]
    public void Overlay_prefers_the_longest_matching_variant_name_in_the_requested_model()
    {
        var state = OverlayState(("vision", 0.3, 2048), ("vision test 2", 0.2, 4096));
        var payload = new JsonObject { ["model"] = "local", ["temperature"] = 0.9 };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "local (vision test 2)");
        Assert.Equal("vision test 2", overlay?["variant"]?.ToString());
    }

    [Fact]
    public void Reload_picks_a_reasoning_profile_when_the_hot_profile_has_reasoning_off()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("qwen.gguf", "thinking", vision: false, reasoning: true, context: 16384, sameGguf: true)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: false,
            needThinking: true,
            neededContext: 0,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.Equal("thinking", candidate?["variant"]?.ToString());
    }

    [Fact]
    public void Reload_skips_profiles_without_reasoning_when_the_turn_needs_thinking()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "fast", vision: false, reasoning: false, context: 131072, sameGguf: true)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: false,
            needThinking: true,
            neededContext: 8000,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.Null(candidate);
    }

    [Fact]
    public void Reload_offer_surfaces_a_thinking_handoff_when_reasoning_is_off_on_the_hot_profile()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("qwen.gguf", "thinking", vision: false, reasoning: true, context: 16384, sameGguf: true)
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: true,
            hotContext: 16384,
            turnTokens: 2000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.NotNull(offer);
        Assert.Equal("thinking", offer.Candidate?["variant"]?.ToString());
    }

    [Fact]
    public void Overlay_picks_the_requested_variant_even_when_the_live_model_name_was_rewritten()
    {
        var state = OverlayState(("(defaults)", 0.3, 2048), ("vision test 2", 0.2, 4096));
        var payload = new JsonObject
        {
            ["model"] = "Qwen3.8-27B-Q4_K_M.gguf",
            ["temperature"] = 0.9,
            ["max_tokens"] = 128
        };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "Qwen3.8-27B-Q4_K_M.gguf (vision test 2)");
        LocalRequestOverlayRouting.Apply(payload, overlay);

        Assert.Equal("vision test 2", overlay?["variant"]?.ToString());
        Assert.Equal(0.2, payload["temperature"]!.GetValue<double>());
        Assert.Equal(4096, payload["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Overlay_uses_a_cooler_temperature_for_code_shaped_prompts()
    {
        var state = OverlayState(("coding", 0.15, 2048), ("chatty", 0.8, 2048));
        var payload = new JsonObject
        {
            ["model"] = "local.gguf",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Please refactor this class and add a unit test." }
            }
        };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "local.gguf");
        Assert.Equal("coding", overlay?["variant"]?.ToString());
    }

    [Fact]
    public void Overlay_uses_a_cooler_temperature_for_tool_call_turns()
    {
        var state = OverlayState(("tools", 0.1, 4096), ("chatty", 0.9, 2048));
        var payload = new JsonObject
        {
            ["model"] = "local.gguf",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.Empty,
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "call_1",
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "run_command", ["arguments"] = "{}" }
                        }
                    }
                }
            }
        };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "local.gguf");
        Assert.Equal("tools", overlay?["variant"]?.ToString());
    }

    [Fact]
    public void Overlay_picks_the_larger_reply_budget_for_a_long_prompt()
    {
        var state = OverlayState(("short", 0.3, 2048), ("long", 0.3, 8192));
        var payload = new JsonObject
        {
            ["model"] = "local",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = new string('x', 4000) }
            }
        };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "local");
        Assert.Equal("long", overlay?["variant"]?.ToString());
    }

    [Fact]
    public void Overlay_falls_back_to_the_last_attached_style()
    {
        var state = OverlayState(("coding", 0.15, 2048), ("chatty", 0.8, 8192));
        var payload = new JsonObject
        {
            ["model"] = "local",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "What is in this folder?" }
            }
        };

        var overlay = LocalRequestOverlayRouting.Pick(state, payload, "local");
        Assert.Equal("chatty", overlay?["variant"]?.ToString());
    }

    [Fact]
    public void Overlay_max_tokens_is_clamped_so_a_long_reconnect_fits_the_loaded_context()
    {
        var payload = new JsonObject
        {
            ["model"] = "local",
            ["max_tokens"] = 128,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = new string('x', 8000) }
            }
        };
        var overlay = OverlayState(("long", 0.3, 16384))["local_overlays"]![0]!.AsObject();
        LocalRequestOverlayRouting.Apply(payload, overlay);

        Assert.Equal(16384, payload["max_tokens"]!.GetValue<int>());
        var clamped = LocalRequestOverlayRouting.ClampMaxTokensToContext(payload, hotContext: 4096);
        Assert.True(clamped < 16384);
        Assert.True(clamped >= 1);
        Assert.Equal(clamped, payload["max_tokens"]!.GetValue<int>());
        Assert.False(LocalRequestOverlayRouting.PromptExceedsContext(payload, 4096));
    }

    [Fact]
    public void Prompt_that_already_fills_context_is_detected_before_llama_is_called()
    {
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = new string('x', 20000) }
            }
        };

        Assert.True(LocalRequestOverlayRouting.PromptExceedsContext(payload, hotContext: 2048));
    }

    [Fact]
    public void Launch_fingerprint_ignores_temperature_and_reply_length()
    {
        var coding = SampleLaunchSettings(temperature: "0.15", maxTokens: "2048", vision: "Disabled");
        var chatty = SampleLaunchSettings(temperature: "0.8", maxTokens: "8192", vision: "Disabled");
        var vision = SampleLaunchSettings(temperature: "0.15", maxTokens: "2048", vision: "Enabled");

        Assert.Equal(LocalLaunchFingerprint.From(coding), LocalLaunchFingerprint.From(chatty));
        Assert.NotEqual(LocalLaunchFingerprint.From(coding), LocalLaunchFingerprint.From(vision));
    }

    [Fact]
    public void Launch_fingerprint_ignores_projector_path_when_images_are_off()
    {
        var textOnly = SampleLaunchSettings(temperature: "0.3", maxTokens: "2048", vision: "Disabled");
        var textOnlyWithProjector = SampleLaunchSettings(temperature: "0.3", maxTokens: "2048", vision: "Disabled");
        textOnlyWithProjector["LocalVisionProjectorPath"] = @"C:\models\mmproj-F16.gguf";
        textOnlyWithProjector["LocalVisionMaxImageEdge"] = "4096";
        var imagesOn = SampleLaunchSettings(temperature: "0.3", maxTokens: "2048", vision: "Enabled");
        imagesOn["LocalVisionProjectorPath"] = @"C:\models\mmproj-F16.gguf";

        Assert.Equal(LocalLaunchFingerprint.From(textOnly), LocalLaunchFingerprint.From(textOnlyWithProjector));
        Assert.NotEqual(LocalLaunchFingerprint.From(textOnlyWithProjector), LocalLaunchFingerprint.From(imagesOn));
    }

    [Fact]
    public void Reload_is_not_offered_when_the_hot_profile_already_covers_the_turn()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "chatty", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("other.gguf", "vision", vision: true, reasoning: true, context: 131072, sameGguf: false)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: false,
            needThinking: false,
            neededContext: 0,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.Null(candidate);
    }

    [Fact]
    public void Reload_picks_the_same_GGUF_vision_profile_when_the_hot_profile_cannot_see_images()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("qwen.gguf", "vision test 2", vision: true, reasoning: false, context: 54272, sameGguf: true),
                Profile("other.gguf", "vision", vision: true, reasoning: false, context: 8192, sameGguf: false)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: true,
            needThinking: false,
            neededContext: 0,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.Equal("vision test 2", candidate?["variant"]?.ToString());
    }

    [Fact]
    public void Reload_skips_text_only_profiles_when_the_turn_has_a_picture()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "fast", vision: false, reasoning: true, context: 131072, sameGguf: true)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: true,
            needThinking: true,
            neededContext: 8000,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.Null(candidate);
    }

    [Fact]
    public void Reload_prefers_the_same_GGUF_when_it_already_covers_the_context_miss()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen3.8-q8.gguf", "long ctx", vision: false, reasoning: false, context: 65536, sameGguf: true),
                Profile("qwen3.6-q4.gguf", "thinking long", vision: false, reasoning: true, context: 131072, sameGguf: false)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: false,
            needThinking: false,
            neededContext: 42000,
            hotModel: "qwen3.8-q8.gguf",
            hotVariant: "(defaults)");

        Assert.Equal("long ctx", candidate?["variant"]?.ToString());
    }

    [Fact]
    public void Reload_picks_a_different_GGUF_only_when_no_same_file_profile_covers_the_miss()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen3.8-q8.gguf", "chatty", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("qwen3.6-q4.gguf", "long ctx", vision: false, reasoning: true, context: 131072, sameGguf: false)
            }
        };

        var candidate = LocalReloadRouting.PickCandidate(
            state,
            needVision: false,
            needThinking: false,
            neededContext: 42000,
            hotModel: "qwen3.8-q8.gguf",
            hotVariant: "(defaults)");

        Assert.Equal("qwen3.6-q4.gguf", candidate?["model"]?.ToString());
    }

    [Fact]
    public void Reload_prompt_pairs_the_loaded_context_deficiency_with_the_suggested_context()
    {
        var candidate = Profile("qwen3.6-q4.gguf", "long ctx", vision: false, reasoning: true, context: 131072, sameGguf: false);
        candidate["displayName"] = "Local | qwen3.6-q4.gguf: long ctx";

        var prompt = LocalReloadRouting.BuildOfferReason(
            hotModel: "qwen3.8-q8.gguf",
            hotVariant: "(defaults)",
            hotVision: false,
            hotReasoning: "Off",
            hotContext: 8192,
            needVision: false,
            needThinking: false,
            neededContext: 42000,
            candidate);

        Assert.StartsWith("This chat turn cannot continue:", prompt, StringComparison.Ordinal);
        Assert.Contains("Context is too small", prompt);
        Assert.Contains("alternative model with a larger Context (131,072)", prompt);
        Assert.Contains("long ctx · qwen3.6-q4.gguf", prompt);
        Assert.Contains("Do you want to switch to this model?", prompt);
        Assert.DoesNotContain("needs about", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Currently loaded:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Local |", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_prompt_blames_the_ready_cloud_when_that_was_the_last_reply()
    {
        var candidate = Profile("qwen.gguf", "max context", vision: false, reasoning: false, context: 213056, sameGguf: true);
        var prompt = LocalReloadRouting.BuildOfferReason(
            hotModel: "qwen.gguf",
            hotVariant: "teeny",
            hotVision: false,
            hotReasoning: "Off",
            hotContext: 1024,
            needVision: false,
            needThinking: false,
            neededContext: 8000,
            candidate,
            lastServedKind: "cloud",
            lastServedCloudLabel: "Gemini / gemini-flash");

        Assert.Contains("the last reply came from Gemini / gemini-flash", prompt, StringComparison.Ordinal);
        Assert.Contains("That cloud model's Context cannot hold this turn", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("loaded model profile's Context is too small", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("stub", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reload_prompt_pairs_images_off_with_images_on_for_the_same_file()
    {
        var candidate = Profile("qwen.gguf", "vision test 2", vision: true, reasoning: false, context: 54272, sameGguf: true);

        var prompt = LocalReloadRouting.BuildOfferReason(
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)",
            hotVision: false,
            hotReasoning: "Off",
            hotContext: 16384,
            needVision: true,
            needThinking: false,
            neededContext: 0,
            candidate);

        Assert.Contains("the request included a picture, and **Images** is off", prompt);
        Assert.Contains("alternative model with Images on (vision test 2 · qwen.gguf)", prompt);
        Assert.Contains("Do you want to switch to this model?", prompt);
        Assert.Equal(1, CountOccurrences(prompt, "vision test 2"));
        Assert.DoesNotContain("Currently loaded:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("needs about", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Local |", prompt, StringComparison.Ordinal);
        Assert.Contains("reconnecting", prompt, StringComparison.Ordinal);

        var harnessPrompt = LocalReloadRouting.BuildOfferReason(
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)",
            hotVision: false,
            hotReasoning: "Off",
            hotContext: 16384,
            needVision: true,
            needThinking: false,
            neededContext: 0,
            candidate,
            harnessNeedsRelaunch: true);
        Assert.Contains(RouteRecoveryPolicy.HarnessRelaunchFromNewSlot, harnessPrompt);
        Assert.Contains("reconnecting", harnessPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_prompt_warns_when_the_suggested_pack_context_is_too_small()
    {
        var candidate = Profile("qwen.gguf", "vision test 2", vision: true, reasoning: false, context: 54272, sameGguf: true);

        var prompt = LocalReloadRouting.BuildOfferReason(
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)",
            hotVision: false,
            hotReasoning: "Off",
            hotContext: 131072,
            needVision: true,
            needThinking: false,
            neededContext: 80000,
            candidate,
            destinationTight: true);

        Assert.Contains("That profile's Context is smaller than this chat", prompt);
        Assert.Contains("Compact can shorten older turns first", prompt);
        Assert.DoesNotContain("Load the suggested model profile?", prompt);
    }

    [Fact]
    public void Near_limit_offer_picks_a_same_file_pack_with_more_context()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("qwen.gguf", "long ctx", vision: false, reasoning: false, context: 131072, sameGguf: true),
                Profile("other.gguf", "huge", vision: false, reasoning: false, context: 262144, sameGguf: false)
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: false,
            hotContext: 16384,
            turnTokens: 15000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.NotNull(offer);
        Assert.True(offer.NearLimit);
        Assert.False(offer.DestinationTight);
        Assert.True(offer.CompactRecommended);
        Assert.Equal("long ctx", offer.Candidate?["variant"]?.ToString());
    }

    [Fact]
    public void Near_limit_offer_stays_on_the_current_file_instead_of_jumping_ggufs()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 16384, sameGguf: true),
                Profile("other.gguf", "huge", vision: false, reasoning: false, context: 262144, sameGguf: false)
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: false,
            hotContext: 16384,
            turnTokens: 15000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.NotNull(offer);
        Assert.Null(offer.Candidate);
        Assert.True(offer.CompactRecommended);
        Assert.False(offer.BetterChance);
    }

    [Fact]
    public void Filling_offer_suggests_a_faster_local_that_still_covers_the_turn()
    {
        var state = new JsonObject
        {
            ["local_gpu_offload"] = "GPU + CPU",
            ["local_ttft_ms"] = 0,
            ["local_reload_pool"] = new JsonArray
            {
                Profile("Qwen3.8-27B-Q4_K_M.gguf", "vision", vision: true, reasoning: false, context: 163840, sameGguf: false, gpuOffload: "GPU only"),
                Profile("Qwen3.8-27B-Q8_0.gguf", "(defaults)", vision: false, reasoning: false, context: 60416, sameGguf: true, gpuOffload: "GPU only")
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: false,
            hotContext: 131072,
            turnTokens: 124000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "Qwen3.8-27B-Q8_0.gguf",
            hotVariant: "vision+max tokens 16K + no compact");

        Assert.NotNull(offer);
        Assert.True(offer.BetterChance);
        Assert.Equal("Qwen3.8-27B-Q4_K_M.gguf", offer.Candidate?["model"]?.ToString());
        Assert.Equal("vision", offer.Candidate?["variant"]?.ToString());
        Assert.True(offer.CompactRecommended);
    }

    [Fact]
    public void Filling_offer_does_not_suggest_a_heavier_local_when_the_loaded_one_is_already_faster()
    {
        var state = new JsonObject
        {
            ["local_gpu_offload"] = "GPU only",
            ["local_reload_pool"] = new JsonArray
            {
                Profile("Qwen3.8-27B-Q8_0.gguf", "vision", vision: true, reasoning: false, context: 131072, sameGguf: false, gpuOffload: "GPU only")
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: false,
            hotContext: 131072,
            turnTokens: 124000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "Qwen3.8-27B-Q4_K_M.gguf",
            hotVariant: "vision");

        Assert.NotNull(offer);
        Assert.Null(offer.Candidate);
        Assert.False(offer.BetterChance);
        Assert.True(offer.CompactRecommended);
    }

    [Fact]
    public void Reload_prompt_explains_a_faster_local_for_a_packed_turn()
    {
        var candidate = Profile("Qwen3.8-27B-Q4_K_M.gguf", "vision", vision: true, reasoning: false, context: 131072, sameGguf: false, gpuOffload: "GPU only");

        var prompt = LocalReloadRouting.BuildOfferReason(
            hotModel: "Qwen3.8-27B-Q8_0.gguf",
            hotVariant: "vision+max tokens 16K + no compact",
            hotVision: true,
            hotReasoning: "Off",
            hotContext: 131072,
            needVision: false,
            needThinking: false,
            neededContext: 124000,
            candidate,
            nearLimit: true,
            betterChance: true);

        Assert.Contains("a better chance to finish this turn", prompt);
        Assert.Contains("vision \u00b7 Qwen3.8-27B-Q4_K_M.gguf", prompt);
        Assert.Contains("Do you want to switch to this model?", prompt);
        Assert.DoesNotContain("needs about", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Vision_handoff_marks_a_smaller_destination_context_as_tight()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 131072, sameGguf: true),
                Profile("qwen.gguf", "vision test 2", vision: true, reasoning: false, context: 54272, sameGguf: true)
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: true,
            needThinking: false,
            hotContext: 131072,
            turnTokens: 80000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)");

        Assert.NotNull(offer);
        Assert.Equal("vision test 2", offer.Candidate?["variant"]?.ToString());
        Assert.True(offer.DestinationTight);
        Assert.True(offer.CompactRecommended);
    }

    [Fact]
    public void Picture_turn_does_not_suggest_a_same_context_or_images_off_profile()
    {
        var state = new JsonObject
        {
            ["local_gpu_offload"] = "GPU only",
            ["local_reload_pool"] = new JsonArray
            {
                Profile("Qwen3.8-27B-Q4_K_M.gguf", "vision", vision: true, reasoning: false, context: 131072, sameGguf: false, gpuOffload: "GPU only"),
                Profile("Qwen3.8-27B-Q4_K_M.gguf", "(defaults)", vision: false, reasoning: false, context: 131072, sameGguf: false, gpuOffload: "GPU only")
            }
        };

        var first = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: false,
            hotContext: 131072,
            turnTokens: 120000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            hotVariant: "Vision",
            payloadHasImage: true);

        Assert.NotNull(first);
        Assert.Null(first.Candidate);
        Assert.True(first.CompactRecommended);

        var afterQ4Vision = LocalReloadRouting.DecideOffer(
            state,
            needVision: false,
            needThinking: false,
            hotContext: 131072,
            turnTokens: 120000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "Qwen3.8-27B-Q4_K_M.gguf",
            hotVariant: "vision",
            payloadHasImage: true);

        Assert.NotNull(afterQ4Vision);
        Assert.Null(afterQ4Vision.Candidate);
        Assert.False(afterQ4Vision.BetterChance);
        Assert.False(LocalReloadRouting.LocalCannotCoverTurn(afterQ4Vision, needVision: false, promptExceeds: false));
    }

    [Fact]
    public void Picture_turn_with_no_images_on_profile_cannot_be_covered_locally()
    {
        var state = new JsonObject
        {
            ["local_reload_pool"] = new JsonArray
            {
                Profile("qwen.gguf", "(defaults)", vision: false, reasoning: false, context: 131072, sameGguf: true)
            }
        };

        var offer = LocalReloadRouting.DecideOffer(
            state,
            needVision: true,
            needThinking: false,
            hotContext: 131072,
            turnTokens: 3000,
            promptExceeds: false,
            compactAlreadyOn: false,
            hotModel: "qwen.gguf",
            hotVariant: "(defaults)",
            payloadHasImage: true);

        Assert.True(LocalReloadRouting.LocalCannotCoverTurn(offer, needVision: true, promptExceeds: false));
    }

    [Fact]
    public void Attribute_summary_includes_vision_state()
    {
        var settings = new JsonObject
        {
            ["LocalVisionEnabled"] = "Enabled",
            ["LocalReasoning"] = "Off",
            ["OverrideContext"] = "54272",
            ["LocalGpuOffloadMode"] = "GPU only",
            ["LocalTemperature"] = "0.3",
            ["OverrideMaxTokens"] = "2048"
        };

        var text = LocalProfileAttributeSummary.FormatLocal(settings);
        Assert.Contains("Images on", text);
        Assert.Contains("Reasoning off", text);
        Assert.Contains("ctx 54,272", text);
    }

    [Fact]
    public void Leftover_picture_clear_does_not_retract_a_context_reload_offer()
    {
        Assert.False(LocalReloadRouting.ShouldClearLeftoverVisionMiss(
            pending: true,
            storedNeedVision: false,
            currentNeedVision: false,
            currentNeedThinking: false,
            currentPromptExceeds: false));
        Assert.True(LocalReloadRouting.ShouldClearLeftoverVisionMiss(
            pending: true,
            storedNeedVision: true,
            currentNeedVision: false,
            currentNeedThinking: false,
            currentPromptExceeds: false));
        Assert.False(LocalReloadRouting.ShouldClearLeftoverVisionMiss(
            pending: false,
            storedNeedVision: true,
            currentNeedVision: false,
            currentNeedThinking: false,
            currentPromptExceeds: false));
        Assert.False(LocalReloadRouting.ShouldClearLeftoverVisionMiss(
            pending: true,
            storedNeedVision: true,
            currentNeedVision: true,
            currentNeedThinking: false,
            currentPromptExceeds: false));
    }

    private static JsonObject OverlayState(params (string Variant, double Temperature, int MaxTokens)[] overlays)
    {
        var array = new JsonArray();
        foreach (var overlay in overlays)
        {
            array.Add(new JsonObject
            {
                ["variant"] = overlay.Variant,
                ["temperature"] = overlay.Temperature,
                ["max_tokens"] = overlay.MaxTokens
            });
        }

        return new JsonObject { ["local_overlays"] = array };
    }

    private static JsonObject Profile(
        string model,
        string variant,
        bool vision,
        bool reasoning,
        int context,
        bool sameGguf,
        string gpuOffload = "",
        int ttftMs = 0)
        => new()
        {
            ["model"] = model,
            ["variant"] = variant,
            ["displayName"] = model + " " + variant,
            ["vision"] = vision ? "Enabled" : "Disabled",
            ["reasoning"] = reasoning ? "On" : "Off",
            ["context"] = context,
            ["sameGguf"] = sameGguf,
            ["gpuOffload"] = gpuOffload,
            ["ttftMs"] = ttftMs
        };

    private static JsonObject SampleLaunchSettings(string temperature, string maxTokens, string vision)
        => new()
        {
            ["OverrideContext"] = "60416",
            ["OverrideThreads"] = "12",
            ["LocalThreadsBatch"] = "4",
            ["LocalGpuOffloadMode"] = "GPU only",
            ["GpuLayers"] = "999",
            ["LocalFlashAttention"] = "Enabled",
            ["LocalKvCacheTypeK"] = "q8_0",
            ["LocalKvCacheTypeV"] = "q8_0",
            ["LocalChatTemplate"] = "Auto",
            ["LocalMultiUserMode"] = "Disabled",
            ["LocalUnbanTokensMode"] = "Disabled",
            ["LocalBatchSize"] = "2048",
            ["LocalUbatchSize"] = "512",
            ["LocalSpecType"] = "draft-mtp",
            ["LocalCacheReuse"] = "256",
            ["LocalCacheRam"] = "Unlimited",
            ["LocalFit"] = "Disabled",
            ["LocalSwaFull"] = "Enabled",
            ["LocalReasoning"] = "Off",
            ["LocalChatParser"] = "Jinja",
            ["LocalVisionEnabled"] = vision,
            ["LocalVisionProjectorPath"] = "",
            ["LocalVisionMaxImageEdge"] = "1344",
            ["LocalTemperature"] = temperature,
            ["OverrideMaxTokens"] = maxTokens
        };

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
