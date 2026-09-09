using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class FluxMuxCloudCallLimitsTests
{
    [Fact]
    public void Low_stays_on_local_when_a_local_model_is_ready()
    {
        Assert.True(FluxMuxCloudCallLimits.StayOnLocalWhenLow(
            localReady: true,
            userText: "Read these files and continue the refactor.",
            toolContinuation: false));
    }

    [Fact]
    public void Low_stays_on_local_for_a_long_or_design_shaped_prompt()
    {
        Assert.True(FluxMuxCloudCallLimits.StayOnLocalWhenLow(
            localReady: true,
            userText: new string('x', 8000) + " architecture review and system design",
            toolContinuation: false));
    }

    [Fact]
    public void Low_stays_on_local_for_cline_tool_turns()
    {
        Assert.True(FluxMuxCloudCallLimits.StayOnLocalWhenLow(
            localReady: true,
            userText: "use gemini for this",
            toolContinuation: true));
    }

    [Fact]
    public void Low_allows_cloud_only_when_the_latest_prompt_asks_for_it_and_local_is_not_in_a_tool_turn()
    {
        Assert.False(FluxMuxCloudCallLimits.StayOnLocalWhenLow(
            localReady: true,
            userText: "Please use gemini for this answer.",
            toolContinuation: false));
    }

    [Fact]
    public void Low_uses_cloud_when_local_is_down()
    {
        Assert.False(FluxMuxCloudCallLimits.StayOnLocalWhenLow(
            localReady: false,
            userText: "hello",
            toolContinuation: false));
    }
}
