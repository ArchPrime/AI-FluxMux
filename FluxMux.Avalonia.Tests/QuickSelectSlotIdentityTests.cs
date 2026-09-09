using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class QuickSelectSlotIdentityTests
{
    [Fact]
    public void Empty_cloud_variant_matches_defaults_on_a_saved_slot()
    {
        var option = new ValidatedQuickSelectProfileOption
        {
            RouteType = "Cloud",
            Provider = "FluxMux Stub",
            Model = "stub-cloud",
            Variant = "(defaults)",
            DisplayName = "Cloud | FluxMux Stub | stub-cloud (default)"
        };

        Assert.True(option.MatchesSavedAssignment(
            "Cloud",
            "FluxMux Stub",
            "stub-cloud",
            string.Empty,
            string.Empty,
            "(defaults)"));
        Assert.Equal("Cloud::FluxMux Stub::stub-cloud::(defaults)", option.Key);
    }

    [Fact]
    public void A_different_cloud_model_is_not_the_saved_assignment()
    {
        var option = new ValidatedQuickSelectProfileOption
        {
            RouteType = "Cloud",
            Provider = "FluxMux Stub",
            Model = "stub-cloud",
            Variant = "(defaults)",
            DisplayName = "Cloud | FluxMux Stub | stub-cloud (default)"
        };

        Assert.False(option.MatchesSavedAssignment(
            "Cloud",
            "xAI",
            "grok-4",
            "(defaults)",
            string.Empty,
            "(defaults)"));
    }
}
