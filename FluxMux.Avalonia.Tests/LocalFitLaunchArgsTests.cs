using System.Collections.Generic;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalFitLaunchArgsTests
{
    [Fact]
    public void Append_modern_help_uses_fit_off_when_disabled()
    {
        var args = new List<string>();
        LocalFitLaunchArgs.Append(args, "Disabled", "-fit,  --fit [on|off]");
        Assert.Equal(["--fit", "off"], args);
    }

    [Fact]
    public void Append_modern_help_uses_fit_on_when_enabled()
    {
        var args = new List<string>();
        LocalFitLaunchArgs.Append(args, "Enabled", "-fit,  --fit [on|off]");
        Assert.Equal(["--fit", "on"], args);
    }

    [Fact]
    public void Append_legacy_help_uses_no_fit_when_disabled()
    {
        var args = new List<string>();
        LocalFitLaunchArgs.Append(args, "Disabled", "--no-fit disable fit");
        Assert.Equal(["--no-fit"], args);
    }

    [Fact]
    public void Append_auto_omits_fit_flags()
    {
        var args = new List<string>();
        LocalFitLaunchArgs.Append(args, "Auto", "-fit,  --fit [on|off]");
        Assert.Empty(args);
    }
}
