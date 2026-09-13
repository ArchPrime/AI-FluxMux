using System;
using System.IO;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class PortRulesTelemetryTests
{
    [Fact]
    public void Omitted_28_of_32_formats_this_turn()
    {
        var rules = PortForwardingRules.Defaults with { RunawayOmittedResults = 32 };
        var snap = new PortRulesTelemetry { HasLocalTurn = true, Omitted = 28 };
        Assert.Equal("28 / 32 this turn", snap.MillAtOmittedLine(rules));
        Assert.Equal("28 / 32", PortRulesTelemetry.Fraction(28, 32));
    }

    [Fact]
    public void Off_rules_and_no_local_turn_omit_the_tally_line()
    {
        var snap = new PortRulesTelemetry { HasLocalTurn = true, Omitted = 28, ObserveOnly = 5, RapidStreak = 2 };
        var off = PortForwardingRules.Defaults with
        {
            MillAtOmittedEnabled = false,
            ObserveOnlyMillEnabled = false,
            RapidChurnEnabled = false,
            MaxPicturesEnabled = false,
            HangEnabled = false,
            Loading503Enabled = false,
            DiagnosticDumpEnabled = false,
            RepeatedCommandEnabled = false
        };
        Assert.Equal(string.Empty, snap.MillAtOmittedLine(off));
        Assert.Equal(string.Empty, snap.ObserveOnlyLine(off));
        Assert.Equal(string.Empty, snap.RapidChurnLine(off));
        Assert.Equal(string.Empty, snap.MaxPicturesLine(off));
        Assert.Equal(string.Empty, snap.HangLine(off));
        Assert.Equal(string.Empty, snap.Loading503Line(off));
        Assert.Equal(string.Empty, snap.DumpLine(off));
        Assert.Equal(string.Empty, snap.RepeatedCommandLine(off));
        Assert.Equal(string.Empty, PortRulesTelemetry.Empty.MillAtOmittedLine(PortForwardingRules.Defaults));
        Assert.Equal(
            string.Empty,
            snap.MillAtOmittedLine(PortForwardingRules.Defaults with { Enabled = false }));
        Assert.Equal(
            string.Empty,
            snap.CompactDiagnosticLine(PortForwardingRules.Defaults with { Enabled = false }));
    }

    [Fact]
    public void View_model_applies_live_tallies_without_writing_status()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-port-tally-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var store = new PortForwardingRulesStore(Path.Combine(dir, FluxMuxConfigPaths.PortRulesFileName));
        var vm = new PortForwardingRulesViewModel(store, _ => { });
        vm.Load();
        Assert.Equal(string.Empty, vm.StatusText);

        vm.ApplyTelemetry(new PortRulesTelemetry { HasLocalTurn = true, Omitted = 28 });
        Assert.Equal("28 / 32 this turn", vm.MillAtOmittedTally);
        Assert.Equal(string.Empty, vm.StatusText);

        vm.MillAtOmittedEnabled = false;
        Assert.Equal(string.Empty, vm.MillAtOmittedTally);

        vm.MillAtOmittedEnabled = true;
        vm.Enabled = false;
        Assert.Equal(string.Empty, vm.MillAtOmittedTally);
        Assert.Contains("without intervention", vm.StatusText, StringComparison.Ordinal);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Mill_and_dump_tips_do_not_say_HTTP_400()
    {
        var axaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "Views", "MainWindow.axaml"));
        Assert.Contains("derail a Client-app turn on local models", axaml, StringComparison.Ordinal);
        Assert.Contains("pause runaway looping", axaml, StringComparison.Ordinal);
        Assert.Contains("simply forward turns to llama-server without intervention", axaml, StringComparison.Ordinal);
        Assert.Contains("Cloud models do not use Port rules", axaml, StringComparison.Ordinal);
        Assert.Contains("End this Client-app turn when this many older tool results were omitted", axaml, StringComparison.Ordinal);
        Assert.Contains("End this Client-app turn when the Client app dumps a large diagnostic artifact", axaml, StringComparison.Ordinal);
        var millAt = axaml.IndexOf("Content=\"Mill at omitted\"", StringComparison.Ordinal);
        var dumpAt = axaml.IndexOf("Content=\"Diagnostic dump\"", StringComparison.Ordinal);
        Assert.True(millAt >= 0);
        Assert.True(dumpAt >= 0);
        AssertNoHttp400InNearbyTip(axaml, millAt);
        AssertNoHttp400InNearbyTip(axaml, dumpAt);
    }

    private static void AssertNoHttp400InNearbyTip(string axaml, int around)
    {
        var start = Math.Max(0, around - 400);
        var slice = axaml.Substring(start, Math.Min(900, axaml.Length - start));
        Assert.DoesNotContain("400", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("400s", slice, StringComparison.Ordinal);
    }

    private static string FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Views", "MainWindow.axaml")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
