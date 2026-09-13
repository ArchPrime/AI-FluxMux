using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class FluxMuxGatewayRoutingTests
{
    [Fact]
    public void Hold_routes_local_when_cloud_is_not_ready()
    {
        var state = DualReadyState(cloudReady: false, capacity: "Hold");
        var payload = ChatPayload("Please use gemini for this answer.");

        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Low_routes_local_when_cloud_is_not_ready_even_if_the_prompt_asks_for_cloud()
    {
        var state = DualReadyState(cloudReady: false, capacity: "Low");
        var payload = ChatPayload("Please use claude for this answer.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Hold_routes_local_when_both_targets_are_ready()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Hold");
        var payload = ChatPayload("Summarize this folder.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Low_routes_local_for_a_normal_prompt_when_both_targets_are_ready()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Low");
        var payload = ChatPayload("Continue the refactor and add a unit test.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Low_requests_cloud_consent_when_the_prompt_asks_for_cloud_and_cloud_is_ready()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Low");
        var payload = ChatPayload("Please use gemini for this answer.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Normal_routing_prefers_local_when_it_was_launched_and_the_prompt_is_not_explicit_cloud()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        var payload = ChatPayload("What files are in this folder?");

        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Only_local_ready_skips_capacity_rules_and_routes_local()
    {
        var state = new JsonObject
        {
            ["routing_enabled"] = true,
            ["mode"] = "local",
            ["local_phase"] = "ready",
            ["cloud_phase"] = "idle",
            ["cloud_routing_capacity"] = "Low"
        };
        var payload = ChatPayload("Please use gpt for this answer.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Routing_disabled_follows_mode_without_reload_offer()
    {
        var state = new JsonObject
        {
            ["routing_enabled"] = false,
            ["mode"] = "cloud",
            ["local_phase"] = "ready",
            ["cloud_phase"] = "ready",
            ["cloud_routing_capacity"] = "Normal"
        };
        var payload = ChatPayload("hello");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Cloud, route.Kind);
        Assert.False(route.OfferReload);
    }

    [Fact]
    public void Only_cloud_ready_routes_cloud()
    {
        var state = new JsonObject
        {
            ["routing_enabled"] = true,
            ["mode"] = "local",
            ["local_phase"] = "idle",
            ["cloud_phase"] = "ready",
            ["cloud_routing_capacity"] = "Hold"
        };
        var payload = ChatPayload("hello");

        Assert.Equal(FluxMuxGatewayRouting.Cloud, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Neither_target_ready_falls_back_to_mode()
    {
        var state = new JsonObject
        {
            ["routing_enabled"] = true,
            ["mode"] = "cloud",
            ["local_phase"] = "idle",
            ["cloud_phase"] = "idle",
            ["cloud_routing_capacity"] = "Normal"
        };
        var payload = ChatPayload("hello");

        Assert.Equal(FluxMuxGatewayRouting.Cloud, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Normal_routes_cloud_when_prompt_explicitly_asks_for_cloud()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        var payload = ChatPayload("Please use claude for this answer.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.Equal(FluxMuxGatewayRouting.ExplicitCloudConsentReason, route.ConsentReason);
    }

    [Fact]
    public void Normal_routes_cloud_for_a_very_large_prompt_when_local_was_not_launched()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "cloud";
        state["local_phase"] = "idle";
        var payload = ChatPayload(new string('x', 50000));
        payload["model"] = "gpt-4.1";

        Assert.Equal(FluxMuxGatewayRouting.Cloud, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Low_routes_cloud_when_local_is_down_and_cloud_is_ready()
    {
        var state = new JsonObject
        {
            ["routing_enabled"] = true,
            ["mode"] = "local",
            ["local_phase"] = "idle",
            ["cloud_phase"] = "ready",
            ["cloud_routing_capacity"] = "Low"
        };
        var payload = ChatPayload("hello");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Cloud, route.Kind);
        Assert.False(route.OfferReload);
    }

    [Fact]
    public void Hold_stays_local_for_tool_continuation_even_on_normal_capacity()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "local";
        var payload = ChatPayload("continue");
        payload["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "continue" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject { ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "run" } }
                }
            },
            new JsonObject { ["role"] = "tool", ["content"] = "ok", ["tool_call_id"] = "call_1" }
        };

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Normal_routes_local_when_requested_model_matches_local_preferred()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["local_preferred_model"] = "qwen-27b-q4.gguf";
        var payload = ChatPayload("hello");
        payload["model"] = "qwen-27b-q4.gguf";

        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Normal_routes_cloud_when_requested_model_matches_cloud_preferred_and_local_was_not_launched()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "cloud";
        state["local_phase"] = "idle";
        state["cloud_preferred_model"] = "gpt-4.1";
        var payload = ChatPayload("hello");
        payload["model"] = "gpt-4.1";

        Assert.Equal(FluxMuxGatewayRouting.Cloud, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Force_cloud_validates_the_cloud_profile_even_when_local_is_launched()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Low");
        var payload = ChatPayload("Reply with the single word OK.");
        payload["model"] = "gemini-flash-lite-latest";

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload, FluxMuxGatewayRouting.Cloud);
        Assert.Equal(FluxMuxGatewayRouting.Cloud, route.Kind);
        Assert.False(route.OfferReload);
    }

    [Fact]
    public void Force_cloud_stays_on_cloud_even_when_that_leg_is_not_ready()
    {
        var state = DualReadyState(cloudReady: false, capacity: "Normal");
        var payload = ChatPayload("Reply with the single word OK.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload, FluxMuxGatewayRouting.Cloud);
        Assert.Equal(FluxMuxGatewayRouting.Cloud, route.Kind);
        Assert.False(route.OfferReload);
    }

    [Fact]
    public void Normal_keeps_local_when_launched_local_even_if_cloud_model_is_requested()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["cloud_preferred_model"] = "gpt-4.1";
        var payload = ChatPayload("hello");
        payload["model"] = "gpt-4.1";

        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Harness_endpoint_offers_local_reload_prompt_when_routing_is_on()
    {
        var state = HarnessDualReadyState();
        var payload = ChatPayload("Continue the refactor and add a unit test.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Prompted_routing_offers_local_reload_without_nominating_an_Endpoint_app()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        var payload = ChatPayload("Continue the refactor and add a unit test.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Harness_prompts_for_cloud_when_the_prompt_asks_for_cloud()
    {
        var state = HarnessDualReadyState();
        var payload = ChatPayload("Please use gemini for this answer.");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.True(route.OfferReload);
        Assert.Equal(FluxMuxGatewayRouting.ExplicitCloudConsentReason, route.ConsentReason);
    }

    [Fact]
    public void Harness_prompts_for_the_running_cloud_when_a_large_turn_would_spill_to_ram()
    {
        var state = HarnessRamSpillState();
        var payload = ChatPayload(new string('x', 50000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.True(route.OfferReload);
        Assert.Equal(FluxMuxGatewayRouting.CapacityCloudConsentReason, route.ConsentReason);
        Assert.Contains("spill into RAM", FluxMuxGatewayRouting.CapacityCloudConsentReason, System.StringComparison.Ordinal);
        Assert.Contains("ready-loaded cloud model", FluxMuxGatewayRouting.CapacityCloudConsentReason, System.StringComparison.Ordinal);
        Assert.DoesNotContain("still running", FluxMuxGatewayRouting.CapacityCloudConsentReason, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("low percent", FluxMuxGatewayRouting.CapacityCloudConsentReason, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turn is over", FluxMuxGatewayRouting.CapacityCloudConsentReason, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filling", FluxMuxGatewayRouting.CapacityCloudConsentReason, System.StringComparison.OrdinalIgnoreCase);
        var annotated = FluxMuxGatewayRouting.AnnotateConsentReason(
            FluxMuxGatewayRouting.CapacityCloudConsentReason,
            state,
            payload);
        Assert.Equal(FluxMuxGatewayRouting.CapacityCloudConsentReason, annotated);
        Assert.DoesNotContain("AI-FluxMux measured the messages", annotated, System.StringComparison.Ordinal);
        Assert.False(FluxMuxGatewayRouting.ConsentTimesOutToError(annotated));
        Assert.True(FluxMuxGatewayRouting.ConsentTimesOutToError(
            FluxMuxGatewayRouting.FillingCloudConsentReason + " extra"));
    }

    [Fact]
    public void Harness_stays_local_for_a_large_turn_when_the_local_model_is_gpu_only()
    {
        var state = HarnessDualReadyState();
        state["local_gpu_offload"] = "GPU only";
        state["local_context"] = 131072;
        var payload = ChatPayload(new string('x', 50000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
        Assert.True(FluxMuxGatewayRouting.PromptLooksTooLargeForLocal(state, payload));
        Assert.False(FluxMuxGatewayRouting.ShouldOfferRamSpillCloudConsent(state, payload));
    }

    [Fact]
    public void Harness_prompts_for_cloud_on_a_ram_spill_turn_even_when_context_is_set_to_the_maximum()
    {
        var state = HarnessRamSpillState();
        state["local_context"] = 131072;
        var payload = ChatPayload(new string('x', 50000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Harness_prompts_for_cloud_on_a_ram_spill_or_filling_turn_even_when_history_has_tool_calls()
    {
        var state = HarnessRamSpillState();
        state["local_context"] = 131072;
        var large = ChatPayloadWithToolHistory(new string('x', 50000));
        var largeRoute = FluxMuxGatewayRouting.DecideRoute(state, large);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, largeRoute.Kind);
        Assert.Equal(FluxMuxGatewayRouting.CapacityCloudConsentReason, largeRoute.ConsentReason);

        state["local_overlays"] = new JsonArray
        {
            new JsonObject { ["max_tokens"] = 16384 }
        };
        var filling = ChatPayloadWithToolHistory(new string('n', 270000));
        Assert.True(FluxMuxGatewayRouting.PromptFillsLocalContext(state, filling));
        var fillingRoute = FluxMuxGatewayRouting.DecideRoute(state, filling);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, fillingRoute.Kind);
        Assert.Equal(FluxMuxGatewayRouting.FillingCloudConsentReason, fillingRoute.ConsentReason);
        Assert.True(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            filling));
    }

    [Fact]
    public void Harness_prompts_for_the_running_cloud_when_loaded_context_is_filling()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 4096;
        var payload = ChatPayload(new string('n', 14000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.True(route.OfferReload);
        Assert.Equal(FluxMuxGatewayRouting.FillingCloudConsentReason, route.ConsentReason);
        Assert.True(FluxMuxGatewayRouting.ConsentTimesOutToError(route.ConsentReason));
        var filling = FluxMuxGatewayRouting.FormatFillingBlockedMessage(state, payload);
        Assert.StartsWith(FluxMuxGatewayRouting.LocalFillingBlockedMessage, filling, System.StringComparison.Ordinal);
        Assert.Contains("too large for the current local model's Context", filling, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.RaiseContextAdvice, filling, System.StringComparison.Ordinal);
        Assert.StartsWith("This chat turn cannot continue:", filling, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, filling, System.StringComparison.Ordinal);
        Assert.Contains("reconnecting", filling, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", filling, System.StringComparison.Ordinal);
        Assert.DoesNotContain("AI-FluxMux measured the messages", FluxMuxGatewayRouting.LocalFillingBlockedMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("llama-server was not asked", FluxMuxGatewayRouting.LocalFillingBlockedMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Filling_400_after_a_cloud_reply_names_the_cloud_not_the_local()
    {
        var state = HarnessDualReadyState();
        var payload = ChatPayload(new string('n', 14000));
        var filling = FluxMuxGatewayRouting.FormatFillingBlockedMessage(
            state,
            payload,
            lastServedKind: "cloud",
            lastServedCloudLabel: "Gemini / gemini-flash");
        Assert.Contains("the last reply came from Gemini / gemini-flash", filling, System.StringComparison.Ordinal);
        Assert.Contains("That cloud model's Context cannot hold this turn", filling, System.StringComparison.Ordinal);
        Assert.DoesNotContain("current local model's Context", filling, System.StringComparison.Ordinal);
        Assert.DoesNotContain("stub", filling, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnecting", filling, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", filling, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_error_body_is_a_plain_sentence_not_a_typed_object()
    {
        var body = FluxMuxGatewayRouting.EndpointErrorBody(
            FluxMuxGatewayRouting.LocalVisionUnavailableMessage);
        var json = body.ToJsonString();
        var plain = ControlLabelMarkup.ForClientApp(FluxMuxGatewayRouting.LocalVisionUnavailableMessage);
        Assert.Equal(plain, body["message"]?.ToString());
        Assert.Equal(body["message"]?.ToString(), body["error"]?.ToString());
        Assert.DoesNotContain("**", body["message"]?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"code\"", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("local_context_filling", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Loaded_local_that_cannot_take_the_turn_offers_the_same_cloud_hop_as_filling()
    {
        var state = HarnessDualReadyState();
        state["local_vision"] = "Disabled";
        state["local_context"] = 131072;
        var payload = ChatPayloadWithImage("look at this");

        Assert.True(FluxMuxGatewayRouting.LoadedLocalCannotTakeTurn(state, payload));
        Assert.True(FluxMuxGatewayRouting.ShouldOfferCloudHopBecauseLocalCannotTakeTurn(state, payload));
        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.Equal(FluxMuxGatewayRouting.LocalCannotCloudConsentReason, route.ConsentReason);
        Assert.True(FluxMuxGatewayRouting.ConsentTimesOutToError(route.ConsentReason));
        Assert.Contains("picture", FluxMuxGatewayRouting.FormatBlockedLocalTurnMessage(state, payload), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("too large for the current local", FluxMuxGatewayRouting.FormatBlockedLocalTurnMessage(state, payload), StringComparison.Ordinal);
    }

    [Fact]
    public void Picture_turn_the_loaded_local_cannot_take_still_offers_cloud_during_a_tool_loop()
    {
        var state = HarnessDualReadyState();
        state["local_vision"] = "Disabled";
        state["local_context"] = 131072;
        var payload = ChatPayloadWithToolHistory("look at this");
        var messages = (JsonArray)payload["messages"]!;
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "look at this" },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,abc" }
                }
            }
        });

        Assert.True(FluxMuxGatewayRouting.LoadedLocalCannotTakeTurn(state, payload));
        Assert.True(FluxMuxGatewayRouting.ShouldOfferCloudHopBecauseLocalCannotTakeTurn(state, payload));
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, FluxMuxGatewayRouting.DecideRoute(state, payload).Kind);
    }

    [Fact]
    public void Images_on_picture_turn_stays_local_when_it_fits()
    {
        var state = HarnessDualReadyState();
        state["local_vision"] = "Enabled";
        state["local_context"] = 131072;
        var payload = ChatPayloadWithImage("look at this");

        Assert.False(FluxMuxGatewayRouting.LoadedLocalCannotTakeTurn(state, payload));
        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
    }

    [Fact]
    public void Text_follow_up_after_a_picture_still_fits_the_loaded_local_for_switch_back()
    {
        var state = HarnessDualReadyState();
        state["local_vision"] = "Disabled";
        state["local_context"] = 131072;
        var payload = ChatPayloadWithImage("look at this");
        var messages = (JsonArray)payload["messages"]!;
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ack" });
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "write a script that counts from 1 to 10 in steps of 0.3"
        });

        Assert.False(LocalChatPayloadSignals.LatestUserTurnHasImage(payload));
        Assert.False(FluxMuxGatewayRouting.LoadedLocalCannotTakeTurn(state, payload));
        Assert.True(FluxMuxGatewayRouting.LoadedLocalCouldTakeLatestUserTurn(state, payload));
        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.DecideRoute(state, payload).Kind);
        Assert.DoesNotContain(
            "Images is off",
            FluxMuxGatewayRouting.FormatBlockedLocalTurnMessage(state, payload),
            StringComparison.Ordinal);
        Assert.True(CloudReturnToLocalPolicy.ShouldOffer(
            inApprovedCloudWindow: true,
            localReady: true,
            localWouldHaveSufficed: FluxMuxGatewayRouting.LoadedLocalCouldTakeLatestUserTurn(state, payload),
            returnArmed: true,
            streakAfterThisTurn: 1,
            harnessEndpoint: true,
            alreadyPendingReturn: false));
    }

    [Fact]
    public void New_picture_follow_up_does_not_count_as_a_local_sufficient_turn()
    {
        var state = HarnessDualReadyState();
        state["local_vision"] = "Disabled";
        state["local_context"] = 131072;
        var payload = ChatPayloadWithImage("another picture");

        Assert.False(FluxMuxGatewayRouting.LoadedLocalCouldTakeLatestUserTurn(state, payload));
    }

    [Fact]
    public void Cloud_picture_block_uses_the_ready_cloud_label_from_state()
    {
        var state = HarnessDualReadyState();
        state["provider"] = "Lab Cloud";
        state["cloud_preferred_model"] = "lab-model";
        state["cloud_vision"] = "Disabled";
        state["endpoint_app"] = FluxMuxGatewayRouting.HarnessEndpointApp;
        Assert.False(FluxMuxGatewayRouting.CloudTakesImages(state));
        state["cloud_vision"] = "Enabled";
        Assert.True(FluxMuxGatewayRouting.CloudTakesImages(state));
        state["cloud_vision"] = "Disabled";
        var message = FluxMuxGatewayRouting.FormatCloudVisionUnavailableMessage(state);
        Assert.Contains("Lab Cloud / lab-model", message, StringComparison.Ordinal);
        Assert.Contains("cannot take pictures", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Images is off", message, StringComparison.Ordinal);
        Assert.DoesNotContain("loaded model profile", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hold_cloud_usage_does_not_offer_a_hop_when_the_loaded_local_cannot_take_the_turn()
    {
        var state = HarnessDualReadyState();
        state["cloud_routing_capacity"] = "Hold";
        state["local_vision"] = "Disabled";
        var payload = ChatPayloadWithImage("look at this");

        Assert.False(FluxMuxGatewayRouting.ShouldOfferCloudHopBecauseLocalCannotTakeTurn(state, payload));
        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.DecideRoute(state, payload).Kind);
    }

    [Fact]
    public void Picture_on_text_only_load_uses_a_short_endpoint_error()
    {
        Assert.StartsWith(
            PortRulesPostMortem.ChatTurnCannotContinue,
            FluxMuxGatewayRouting.LocalVisionUnavailableMessage,
            System.StringComparison.Ordinal);
        Assert.Contains("**Images**", FluxMuxGatewayRouting.LocalVisionUnavailableMessage, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.EnableImagesAdvice, FluxMuxGatewayRouting.LocalVisionUnavailableMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Load this local", FluxMuxGatewayRouting.LocalVisionUnavailableMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Validate", FluxMuxGatewayRouting.LocalVisionUnavailableMessage, System.StringComparison.Ordinal);
        var either = FluxMuxGatewayRouting.FormatLocalVisionUnavailableMessage(
            new System.Text.Json.Nodes.JsonObject { ["endpoint_app"] = FluxMuxGatewayRouting.HarnessEndpointApp });
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, either, System.StringComparison.Ordinal);
        Assert.Contains("reconnecting", either, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", either, System.StringComparison.Ordinal);
        var cline = FluxMuxGatewayRouting.FormatLocalVisionUnavailableMessage(
            new System.Text.Json.Nodes.JsonObject { ["endpoint_app"] = "Cline" });
        Assert.Equal(either, cline);
    }

    [Fact]
    public void Filling_local_turn_is_blocked_unless_the_operator_chose_stay_local()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 4096;
        var payload = ChatPayload(new string('n', 14000));

        Assert.True(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.True(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload));
        Assert.True(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.ConsentTimeout,
            state,
            payload));
        Assert.False(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload,
            cloudRecommendStatus: "no"));
        Assert.False(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload,
            forceRoute: FluxMuxGatewayRouting.Local));
    }

    [Fact]
    public void Half_full_Harness_bar_with_16k_max_tokens_is_large_not_filling()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 131072;
        state["local_overlays"] = new JsonArray
        {
            new JsonObject { ["variant"] = "vision", ["max_tokens"] = 16384 }
        };
        var payload = ChatPayload(new string('n', 190000));
        payload["max_tokens"] = 16384;

        Assert.False(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.True(FluxMuxGatewayRouting.PromptLooksTooLargeForLocal(state, payload));
        Assert.False(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload));
    }

    [Fact]
    public void Packed_turn_counts_max_tokens_toward_filling_so_q8_131k_fails_early()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 131072;
        state["local_overlays"] = new JsonArray
        {
            new JsonObject { ["variant"] = "vision", ["max_tokens"] = 16384 }
        };
        var payload = ChatPayload(new string('n', 428000));

        Assert.True(FluxMuxGatewayRouting.PromptFillsLocalContext(state, payload));
        Assert.True(FluxMuxGatewayRouting.ShouldBlockLocalForward(
            FluxMuxGatewayRouting.Local,
            state,
            payload));
        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.Equal(FluxMuxGatewayRouting.FillingCloudConsentReason, route.ConsentReason);
    }

    [Fact]
    public void Hold_filling_turn_stays_classified_local_but_must_not_forward()
    {
        var state = HarnessDualReadyState();
        state["cloud_routing_capacity"] = "Hold";
        state["local_context"] = 4096;
        var payload = ChatPayload(new string('n', 14000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
        Assert.True(FluxMuxGatewayRouting.ShouldBlockLocalForward(route.Kind, state, payload));
    }

    [Fact]
    public void Harness_stays_local_for_a_small_turn_when_both_targets_are_ready()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 131072;
        var payload = ChatPayload("hello");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Forced_local_probe_does_not_offer_a_model_switch()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 1024;
        var payload = ChatPayload("Reply with the single word ready.");

        var route = FluxMuxGatewayRouting.DecideRoute(
            state,
            payload,
            forceRoute: FluxMuxGatewayRouting.Local);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.False(route.OfferReload);
        Assert.Null(route.ConsentReason);
        Assert.False(FluxMuxGatewayRouting.AllowsOperatorRouting(FluxMuxGatewayRouting.Local));
        Assert.True(FluxMuxGatewayRouting.AllowsOperatorRouting(null));
    }

    [Fact]
    public void Forced_local_probe_stays_local_even_when_llama_server_is_still_warming()
    {
        var state = HarnessDualReadyState();
        state["local_phase"] = "warming";
        var payload = ChatPayload("Reply with exactly: OK");

        var route = FluxMuxGatewayRouting.DecideRoute(
            state,
            payload,
            forceRoute: FluxMuxGatewayRouting.Local);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.False(route.OfferReload);
    }

    [Fact]
    public void Approved_cloud_window_overrides_a_later_local_classification()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(FluxMuxGatewayRouting.ShouldHonorApprovedCloudWindow(
            cloudOk: true,
            forceRoute: null,
            recommendStatus: "yes",
            allowUntilUnix: now + 60,
            nowUnix: now,
            approvedThisProcess: true));
        Assert.False(FluxMuxGatewayRouting.ShouldHonorApprovedCloudWindow(
            cloudOk: true,
            forceRoute: null,
            recommendStatus: "yes",
            allowUntilUnix: now + 60,
            nowUnix: now,
            approvedThisProcess: false));
        Assert.False(FluxMuxGatewayRouting.ShouldHonorApprovedCloudWindow(
            cloudOk: true,
            forceRoute: FluxMuxGatewayRouting.Local,
            recommendStatus: "yes",
            allowUntilUnix: now + 60,
            nowUnix: now,
            approvedThisProcess: true));
        Assert.False(FluxMuxGatewayRouting.ShouldHonorApprovedCloudWindow(
            cloudOk: true,
            forceRoute: null,
            recommendStatus: "no",
            allowUntilUnix: now + 60,
            nowUnix: now));
        Assert.True(FluxMuxGatewayRouting.ShouldHonorApprovedCloudWindow(
            cloudOk: true,
            forceRoute: null,
            recommendStatus: "pending",
            allowUntilUnix: now + 60,
            nowUnix: now,
            approvedThisProcess: true,
            recommendSource: CloudReturnToLocalPolicy.Source));
    }

    [Fact]
    public void Harness_stays_local_for_a_small_tool_continuation_when_both_targets_are_ready()
    {
        var state = HarnessDualReadyState();
        state["local_context"] = 131072;
        var payload = ChatPayloadWithToolHistory("hello");

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
        Assert.False(FluxMuxGatewayRouting.ShouldBlockLocalForward(route.Kind, state, payload));
    }

    [Fact]
    public void Harness_hold_does_not_prompt_cloud_when_the_turn_would_spill_to_ram()
    {
        var state = HarnessRamSpillState();
        state["cloud_routing_capacity"] = "Hold";
        var payload = ChatPayload(new string('x', 50000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.Local, route.Kind);
        Assert.True(route.OfferReload);
    }

    [Fact]
    public void Harness_endpoint_routes_cloud_when_only_cloud_is_ready()
    {
        var state = new JsonObject
        {
            ["routing_enabled"] = true,
            ["endpoint_app"] = FluxMuxGatewayRouting.HarnessEndpointApp,
            ["mode"] = "local",
            ["local_phase"] = "idle",
            ["cloud_phase"] = "ready",
            ["cloud_routing_capacity"] = "Normal"
        };
        var payload = ChatPayload("hello");

        Assert.Equal(FluxMuxGatewayRouting.Cloud, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Normal_routing_prefers_local_when_dual_hot_mode_is_route()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "route";
        var payload = ChatPayload("What files are in this folder?");

        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Normal_routes_cloud_when_loaded_context_is_filling()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "route";
        state["local_context"] = 4096;
        var payload = ChatPayload(new string('n', 14000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.Equal(FluxMuxGatewayRouting.FillingCloudConsentReason, route.ConsentReason);
    }

    [Fact]
    public void Normal_stays_local_for_a_very_large_prompt_when_the_local_model_is_gpu_only()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "route";
        state["local_gpu_offload"] = "GPU only";
        var payload = ChatPayload(new string('x', 50000));

        Assert.Equal(FluxMuxGatewayRouting.Local, FluxMuxGatewayRouting.ClassifyRoute(state, payload));
    }

    [Fact]
    public void Normal_offers_cloud_consent_when_a_large_turn_would_spill_to_ram()
    {
        var state = DualReadyState(cloudReady: true, capacity: "Normal");
        state["mode"] = "route";
        state["local_gpu_offload"] = "GPU + CPU";
        var payload = ChatPayload(new string('x', 50000));

        var route = FluxMuxGatewayRouting.DecideRoute(state, payload);
        Assert.Equal(FluxMuxGatewayRouting.CloudConsent, route.Kind);
        Assert.True(route.OfferReload);
        Assert.Equal(FluxMuxGatewayRouting.CapacityCloudConsentReason, route.ConsentReason);
    }

    private static JsonObject HarnessRamSpillState()
    {
        var state = HarnessDualReadyState();
        state["local_gpu_offload"] = "GPU + CPU";
        return state;
    }

    private static JsonObject HarnessDualReadyState()
        => new()
        {
            ["routing_enabled"] = true,
            ["endpoint_app"] = FluxMuxGatewayRouting.HarnessEndpointApp,
            ["mode"] = "route",
            ["local_phase"] = "ready",
            ["cloud_phase"] = "ready",
            ["cloud_routing_capacity"] = "Normal",
            ["local_preferred_model"] = "local.gguf",
            ["cloud_preferred_model"] = "gpt-4.1"
        };

    private static JsonObject DualReadyState(bool cloudReady, string capacity)
        => new()
        {
            ["routing_enabled"] = true,
            ["mode"] = "route",
            ["local_phase"] = "ready",
            ["cloud_phase"] = cloudReady ? "ready" : "idle",
            ["cloud_routing_capacity"] = capacity,
            ["local_preferred_model"] = "local.gguf",
            ["cloud_preferred_model"] = "gpt-4.1"
        };

    private static JsonObject ChatPayload(string userText)
        => new()
        {
            ["model"] = "local",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userText }
            }
        };

    private static JsonObject ChatPayloadWithImage(string userText)
        => new()
        {
            ["model"] = "local",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = userText },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,abc" }
                        }
                    }
                }
            }
        };

    private static JsonObject ChatPayloadWithToolHistory(string userText)
    {
        var payload = ChatPayload(userText);
        var messages = (JsonArray)payload["messages"]!;
        messages.Insert(0, new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "call_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "bash", ["arguments"] = "{}" }
                }
            }
        });
        messages.Insert(1, new JsonObject
        {
            ["role"] = "tool",
            ["content"] = "ok",
            ["tool_call_id"] = "call_1"
        });
        return payload;
    }
}
