using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ControlLabelMarkupTests
{
    [Fact]
    public void Mark_wraps_the_full_label()
    {
        Assert.Equal("**Continue waiting**", ControlLabelMarkup.Mark(RouteRecoveryPolicy.WaitLongerLabel));
    }

    [Fact]
    public void Parse_splits_bold_control_names_from_commentary()
    {
        var parts = ControlLabelMarkup.Parse(
            "Choose **Continue waiting** to keep this turn open.");

        Assert.Equal(3, parts.Count);
        Assert.Equal(("Choose ", false), parts[0]);
        Assert.Equal(("Continue waiting", true), parts[1]);
        Assert.Equal((" to keep this turn open.", false), parts[2]);
    }

    [Fact]
    public void Strip_removes_markers_for_the_Diagnostics_log()
    {
        Assert.Equal(
            "Click Health again.",
            ControlLabelMarkup.Strip("Click **Health** again."));
    }

    [Fact]
    public void ForClientApp_strips_markers_so_Cline_does_not_show_asterisks()
    {
        Assert.Equal(
            "raise Mill at omitted",
            ControlLabelMarkup.ForClientApp("raise **Mill at omitted**"));
        Assert.DoesNotContain("**", ControlLabelMarkup.ForClientApp(PortRulesPostMortem.MillBreakAdvice));
    }
}
