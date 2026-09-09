using System.IO;
using System.Text;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CompiledXamlGuardTests
{
    [Fact]
    public void Launcher_assembly_contains_woven_avalonia_xaml()
    {
        Assert.True(CompiledXamlGuard.AssemblyContainsCompiledXaml(typeof(App).Assembly));
    }

    [Fact]
    public void Missing_file_is_not_treated_as_compiled_xaml()
    {
        Assert.False(CompiledXamlGuard.FileContainsCompiledXaml(Path.Combine(Path.GetTempPath(), "fluxmux-missing-xaml-check.dll")));
    }

    [Fact]
    public void Bytes_without_the_trampoline_marker_fail()
    {
        var bytes = Encoding.ASCII.GetBytes("!AvaloniaResources without populate");
        Assert.False(CompiledXamlGuard.ContainsAscii(bytes, CompiledXamlGuard.PopulateTrampolineMarker));
        Assert.True(CompiledXamlGuard.ContainsAscii(
            Encoding.ASCII.GetBytes("before " + CompiledXamlGuard.PopulateTrampolineMarker + " after"),
            CompiledXamlGuard.PopulateTrampolineMarker));
    }
}
