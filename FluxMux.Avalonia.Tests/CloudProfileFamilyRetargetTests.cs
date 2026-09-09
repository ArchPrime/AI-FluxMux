using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudProfileFamilyRetargetTests
{
    [Fact]
    public void Rewrite_moves_the_family_and_clears_validate_stamps()
    {
        var profiles = new JsonObject
        {
            ["Copilot GitHub::old-id::(defaults)"] = new JsonObject
            {
                ["EndpointValidatedUtc"] = "2026-01-01T00:00:00Z",
                ["EndpointWarning"] = "failed",
                ["CloudMaxTokens"] = 2048
            },
            ["Copilot GitHub::old-id::fast"] = new JsonObject
            {
                ["CloudMaxTokens"] = 512
            },
            ["Gemini::keep::(defaults)"] = new JsonObject()
        };

        Assert.True(CloudProfileFamilyRetarget.TryRewriteProfiles(
            profiles,
            "Copilot GitHub",
            "old-id",
            "new-id",
            out var moved,
            out var error));

        Assert.Equal(2, moved);
        Assert.Equal(string.Empty, error);
        Assert.Null(profiles["Copilot GitHub::old-id::(defaults)"]);
        Assert.NotNull(profiles["Copilot GitHub::new-id::(defaults)"]);
        Assert.NotNull(profiles["Copilot GitHub::new-id::fast"]);
        Assert.NotNull(profiles["Gemini::keep::(defaults)"]);
        Assert.Null(profiles["Copilot GitHub::new-id::(defaults)"]?["EndpointValidatedUtc"]);
        Assert.Null(profiles["Copilot GitHub::new-id::(defaults)"]?["EndpointWarning"]);
        Assert.Equal(2048, profiles["Copilot GitHub::new-id::(defaults)"]?["CloudMaxTokens"]?.GetValue<int>());
    }

    [Fact]
    public void Rewrite_refuses_when_the_destination_family_already_exists()
    {
        var profiles = new JsonObject
        {
            ["Copilot GitHub::old-id::(defaults)"] = new JsonObject(),
            ["Copilot GitHub::new-id::(defaults)"] = new JsonObject()
        };

        Assert.False(CloudProfileFamilyRetarget.TryRewriteProfiles(
            profiles,
            "Copilot GitHub",
            "old-id",
            "new-id",
            out _,
            out var error));

        Assert.Contains("already exists", error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(profiles["Copilot GitHub::old-id::(defaults)"]);
    }

    [Fact]
    public void Route_slots_that_pointed_at_the_old_id_follow_the_new_id()
    {
        var slots = new JsonArray
        {
            new JsonObject
            {
                ["routeType"] = "cloud",
                ["provider"] = "Copilot GitHub",
                ["cloudModel"] = "old-id",
                ["validatedDefaultProfile"] = true,
                ["endpointValidatedUtc"] = "2026-01-01T00:00:00Z"
            },
            new JsonObject
            {
                ["routeType"] = "local",
                ["provider"] = string.Empty,
                ["cloudModel"] = string.Empty
            }
        };

        Assert.Equal(1, CloudProfileFamilyRetarget.RewriteRouteSlots(slots, "Copilot GitHub", "old-id", "new-id"));
        Assert.Equal("Copilot GitHub", slots[0]?["provider"]?.ToString());
        Assert.Equal("new-id", slots[0]?["cloudModel"]?.ToString());
        Assert.Equal(false, slots[0]?["validatedDefaultProfile"]?.GetValue<bool>());
        Assert.Null(slots[0]?["endpointValidatedUtc"]);
    }

    [Fact]
    public void Route_slots_follow_a_cross_provider_retarget()
    {
        var slots = new JsonArray
        {
            new JsonObject
            {
                ["routeType"] = "cloud",
                ["provider"] = "Copilot GitHub",
                ["cloudModel"] = "gemini-3.6-flash"
            }
        };

        Assert.Equal(1, CloudProfileFamilyRetarget.RewriteRouteSlots(
            slots,
            "Copilot GitHub",
            "gemini-3.6-flash",
            "gemini-flash-lite-latest",
            "Gemini"));
        Assert.Equal("Gemini", slots[0]?["provider"]?.ToString());
        Assert.Equal("gemini-flash-lite-latest", slots[0]?["cloudModel"]?.ToString());
    }

    [Fact]
    public void Rewrite_can_give_an_empty_model_id_a_real_id()
    {
        var profiles = new JsonObject
        {
            ["Copilot GitHub:::(defaults)"] = new JsonObject
            {
                ["CloudMaxTokens"] = 1024
            }
        };

        Assert.True(CloudProfileFamilyRetarget.TryRewriteProfiles(
            profiles,
            "Copilot GitHub",
            "",
            "gpt-4.1",
            out var moved,
            out var error));

        Assert.Equal(1, moved);
        Assert.Equal(string.Empty, error);
        Assert.Null(profiles["Copilot GitHub:::(defaults)"]);
        Assert.Equal(1024, profiles["Copilot GitHub::gpt-4.1::(defaults)"]?["CloudMaxTokens"]?.GetValue<int>());
    }

    [Fact]
    public void Parse_accepts_a_missing_model_id_between_double_colons()
    {
        Assert.True(CloudProfileFamilyRetarget.TryParseKey(
            "Copilot GitHub:::(defaults)",
            out var provider,
            out var model,
            out var variant));
        Assert.Equal("Copilot GitHub", provider);
        Assert.Equal(string.Empty, model);
        Assert.Equal("(defaults)", variant);
    }

    [Fact]
    public void Parse_and_rewrite_a_profile_that_lost_both_provider_and_model()
    {
        Assert.True(CloudProfileFamilyRetarget.TryParseKey(
            "::::(defaults)",
            out var provider,
            out var model,
            out var variant));
        Assert.Equal(string.Empty, provider);
        Assert.Equal(string.Empty, model);
        Assert.Equal("(defaults)", variant);

        var profiles = new JsonObject
        {
            ["::::(defaults)"] = new JsonObject { ["CloudMaxTokens"] = 2048 }
        };

        Assert.True(CloudProfileFamilyRetarget.TryRewriteProfiles(
            profiles,
            "",
            "",
            "Gemini",
            "gemini-flash-lite-latest",
            out var moved,
            out var error));
        Assert.Equal(1, moved);
        Assert.Equal(string.Empty, error);
        Assert.Null(profiles["::::(defaults)"]);
        Assert.Equal(2048, profiles["Gemini::gemini-flash-lite-latest::(defaults)"]?["CloudMaxTokens"]?.GetValue<int>());
    }
}
