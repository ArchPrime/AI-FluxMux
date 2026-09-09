using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ProfileEndpointValidationStampTests
{
    [Fact]
    public void Failed_validate_stores_a_warning_and_clears_the_validated_stamp()
    {
        var profile = new JsonObject
        {
            [ProfileEndpointValidationStamp.ValidatedUtcKey] = "2026-01-01T00:00:00Z"
        };

        ProfileEndpointValidationStamp.Apply(
            profile,
            validated: false,
            warning: "This profile needs Validate to endpoint again.");

        Assert.Null(profile[ProfileEndpointValidationStamp.ValidatedUtcKey]);
        Assert.False(string.IsNullOrWhiteSpace(profile[ProfileEndpointValidationStamp.WarningKey]?.ToString()));
    }

    [Fact]
    public void Empty_warning_leaves_a_never_validated_profile_untouched()
    {
        var profile = new JsonObject();
        ProfileEndpointValidationStamp.Apply(profile, validated: false, warning: null);
        Assert.Null(profile[ProfileEndpointValidationStamp.ValidatedUtcKey]);
        Assert.Null(profile[ProfileEndpointValidationStamp.WarningKey]);
    }

    [Fact]
    public void Failed_validate_keeps_the_human_reason()
    {
        var profile = new JsonObject();
        ProfileEndpointValidationStamp.Apply(
            profile,
            validated: false,
            warning: "Gemini did not accept this model id.");

        Assert.Contains(
            "Gemini did not accept this model id",
            profile[ProfileEndpointValidationStamp.WarningKey]?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_validate_clears_the_warning()
    {
        var profile = new JsonObject
        {
            [ProfileEndpointValidationStamp.WarningKey] = "old failure"
        };

        ProfileEndpointValidationStamp.Apply(profile, validated: true, warning: null);

        Assert.Null(profile[ProfileEndpointValidationStamp.WarningKey]);
        Assert.False(string.IsNullOrWhiteSpace(profile[ProfileEndpointValidationStamp.ValidatedUtcKey]?.ToString()));
    }
}
