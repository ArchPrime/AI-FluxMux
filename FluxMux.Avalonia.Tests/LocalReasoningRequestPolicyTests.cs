using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalReasoningRequestPolicyTests
{
    [Theory]
    [InlineData("Off", "Off")]
    [InlineData("On", "Medium")]
    [InlineData("Auto", "Medium")]
    [InlineData("Low", "Low")]
    [InlineData("medium", "Medium")]
    [InlineData("XHigh", "XHigh")]
    [InlineData("high", "XHigh")]
    [InlineData("", "Off")]
    [InlineData(null, "Off")]
    public void FromProfile_maps_saved_profile_and_slot_levels(string? input, string expected)
        => Assert.Equal(expected, LocalReasoningRequestPolicy.FromProfile(input));

    [Fact]
    public void Effort_is_only_set_for_slot_levels()
    {
        Assert.Null(LocalReasoningRequestPolicy.Effort("Off"));
        Assert.Null(LocalReasoningRequestPolicy.Effort("On"));
        Assert.Equal("low", LocalReasoningRequestPolicy.Effort("Low"));
        Assert.Equal("medium", LocalReasoningRequestPolicy.Effort("Medium"));
        Assert.Equal("xhigh", LocalReasoningRequestPolicy.Effort("XHigh"));
    }
}
