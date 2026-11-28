using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The wizard follows what the model file and its template say rather than its name (28 Nov):
/// the maker's temperature from the GGUF, and reasoning levels when the template takes an effort.
/// </summary>
public sealed class WizardModelCapabilityTests
{
    private static FluxMuxRuntimeService.LocalHardwareLaunchAdvice Advice(double? temperature, params string[] effortLevels) => new()
    {
        GpuTotalGb = 31.8,
        ModelFileGb = 15.5,
        NvidiaAvailable = true,
        ModelMaxCtx = 262144,
        ContextPriorityFirst = 131072,
        ContextPrioritySecond = 98304,
        ContextSafeDefault = 65536,
        ContextPriorityLower = 32768,
        RecommendedTemperature = temperature,
        TemplateEffortLevels = effortLevels,
    };

    private static Dictionary<string, string> Wizard(FluxMuxRuntimeService.LocalHardwareLaunchAdvice advice, params string[] order)
        => LocalPrioritySettingsCalculator.Calculate(
            LocalPrioritySettingsCalculator.ResolveOrder(order),
            advice,
            "Auto",
            "ngram-simple",
            "Disabled",
            true,
            "Disabled",
            string.Empty,
            "1344",
            "8",
            "8");

    [Fact]
    public void The_makers_temperature_is_used_and_Fidelity_first_lowers_it()
    {
        // Inkling 2 Mini and Qwen 3.8 GGUFs both recommend 1.0.
        Assert.Equal("1", Wizard(Advice(1.0), "Context length", "Speed", "Fidelity", "Stability", "Reply length", "Reasoning depth")["LocalTemperature"]);
        Assert.Equal("0.6", Wizard(Advice(1.0), "Fidelity", "Context length", "Speed", "Stability", "Reply length", "Reasoning depth")["LocalTemperature"]);
        Assert.Equal("0.3", Wizard(Advice(null), "Speed", "Context length", "Fidelity", "Stability", "Reply length", "Reasoning depth")["LocalTemperature"]);
    }

    [Fact]
    public void Reasoning_depth_rank_picks_a_level_when_the_template_takes_one()
    {
        var levels = new[] { "none", "minimal", "low", "medium", "high", "max" };
        Assert.Equal("XHigh", Wizard(Advice(1.0, levels), "Reasoning depth", "Context length", "Speed", "Fidelity", "Stability", "Reply length")["LocalReasoning"]);
        Assert.Equal("Medium", Wizard(Advice(1.0, levels), "Context length", "Reasoning depth", "Speed", "Fidelity", "Stability", "Reply length")["LocalReasoning"]);
        Assert.Equal("Off", Wizard(Advice(1.0, levels), "Context length", "Speed", "Reasoning depth", "Fidelity", "Stability", "Reply length")["LocalReasoning"]);
        // No effort levels known: On or Off as before.
        Assert.Equal("On", Wizard(Advice(1.0), "Reasoning depth", "Context length", "Speed", "Fidelity", "Stability", "Reply length")["LocalReasoning"]);
    }

    [Fact]
    public void An_effort_the_template_does_not_list_becomes_its_nearest_level()
    {
        var inkling = LocalChatTemplateTraits.FromProps(new JsonObject
        {
            ["chat_template"] = "x",
            ["chat_template_caps"] = new JsonObject
            {
                ["supports_reasoning_effort"] = true,
                ["reasoning_effort_levels"] = new JsonArray("none", "minimal", "low", "medium", "high", "max"),
            },
        });
        Assert.Equal("max", inkling.NearestEffort("xhigh"));
        Assert.Equal("medium", inkling.NearestEffort("medium"));
        Assert.Equal("xhigh", LocalChatTemplateTraits.None.NearestEffort("xhigh"));

        var payload = new JsonObject { ["messages"] = new JsonArray() };
        LocalReasoningPayloadPolicy.ApplyToPayload(payload, "XHigh", null, inkling);
        Assert.Equal("max", payload["chat_template_kwargs"]!["reasoning_effort"]!.ToString());
    }
}
