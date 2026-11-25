using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The operator requires AI-FluxMux to work at any DPI, display scaling and text size, without
/// brittle hard-coded sizes (25 Nov). These guard the parts that were fixed: text sizes come from
/// the shared FluxFontSize* scale in App.axaml, and no view text is painted a fixed black that
/// disappears in dark mode.
/// </summary>
public sealed class LayoutScalingRulesTests
{
    // The test host may run outside the source tree (side output), so find the views from this
    // file's own location: the tests folder sits inside the project folder.
    private static string ProjectRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static string[] ViewFiles()
    {
        var root = ProjectRoot();
        return new[] { "Views", "Controls" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(root, folder), "*.axaml"))
            .Append(Path.Combine(root, "App.axaml"))
            .ToArray();
    }

    [Fact]
    public void View_text_sizes_come_from_the_shared_scale()
    {
        var offenders = ViewFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"FontSize=""\d|Property=""FontSize"" Value=""\d")
                .Select(match => Path.GetFileName(file) + ": " + match.Value))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_shared_scale_defines_every_size_the_views_use()
    {
        var app = File.ReadAllText(Path.Combine(ProjectRoot(), "App.axaml"));
        var used = ViewFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"DynamicResource (FluxFontSize\w+)")
                .Select(match => match.Groups[1].Value))
            .Distinct();
        foreach (var key in used)
        {
            Assert.Contains("x:Key=\"" + key + "\"", app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_view_text_is_painted_fixed_black()
    {
        var offenders = ViewFiles()
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"Foreground=""#(000000|000|FF000000)"""))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }
}
