using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalReasoningLaunchPolicyTests
{
    [Theory]
    [InlineData("On", "On")]
    [InlineData("on", "On")]
    [InlineData("Auto", "Auto")]
    [InlineData("AUTO", "Auto")]
    [InlineData("Off", "Off")]
    [InlineData("Low", "Low")]
    [InlineData("medium", "Medium")]
    [InlineData("XHigh", "XHigh")]
    [InlineData("xhigh", "XHigh")]
    [InlineData("High", "XHigh")]
    [InlineData("", "Off")]
    [InlineData(null, "Off")]
    public void NormalizeMode_maps_known_values(string? input, string expected)
        => Assert.Equal(expected, LocalReasoningLaunchPolicy.NormalizeMode(input));

    [Fact]
    public void IsOff_IsOn_IsAuto_follow_normalized_mode()
    {
        Assert.True(LocalReasoningLaunchPolicy.IsOff("off"));
        Assert.False(LocalReasoningLaunchPolicy.IsOn("off"));
        Assert.False(LocalReasoningLaunchPolicy.IsAuto("off"));

        Assert.True(LocalReasoningLaunchPolicy.IsOn("On"));
        Assert.True(LocalReasoningLaunchPolicy.IsAuto("auto"));
        Assert.False(LocalReasoningLaunchPolicy.IsOn("Medium"));
        Assert.True(LocalReasoningLaunchPolicy.IsThinkingEnabled("Medium"));
        Assert.True(LocalReasoningLaunchPolicy.IsThinkingEnabled("On"));
        Assert.False(LocalReasoningLaunchPolicy.IsThinkingEnabled("Off"));
    }

    [Fact]
    public void ProcessLaunchMode_is_auto_so_On_Off_can_change_without_reload()
        => Assert.Equal("Auto", LocalReasoningLaunchPolicy.ProcessLaunchMode());
}
