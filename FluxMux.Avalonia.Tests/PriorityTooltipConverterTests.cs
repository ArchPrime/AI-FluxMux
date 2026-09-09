using System;
using System.Globalization;
using System.Linq;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class PriorityTooltipConverterTests
{
    [Theory]
    [InlineData("Stability")]
    [InlineData("Speed")]
    [InlineData("Fidelity")]
    [InlineData("Context length")]
    [InlineData("Reply length")]
    [InlineData("Reasoning depth")]
    public void Local_tooltips_mention_vram_or_memory(string goal)
    {
        var tip = Tip(goal, "local");
        Assert.True(
            ContainsAny(tip, "VRAM", "out-of-memory", "memory"),
            goal + ": " + tip);
    }

    [Theory]
    [InlineData("Stability")]
    [InlineData("Speed")]
    [InlineData("Context length")]
    [InlineData("Reply length")]
    [InlineData("Reasoning depth")]
    [InlineData("Token Cost Economy")]
    public void Cloud_tooltips_mention_token_cost(string goal)
    {
        var tip = Tip(goal, "cloud");
        Assert.True(
            ContainsAny(tip, "token", "credit", "bill", "cheaper", "pay"),
            goal + ": " + tip);
    }

    [Fact]
    public void Legacy_goal_names_still_resolve_to_the_new_tooltips()
    {
        Assert.Contains("VRAM", Tip("Context Capacity", "local"), StringComparison.Ordinal);
        Assert.Contains("token", Tip("Long Context", "cloud"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VRAM", Tip("Thinking", "local"), StringComparison.Ordinal);
        Assert.Contains("token", Tip("Deep Reasoning", "cloud"), StringComparison.OrdinalIgnoreCase);
    }

    private static string Tip(string goal, string route)
        => PriorityTooltipConverter.Instance.Convert(goal, typeof(string), route, CultureInfo.InvariantCulture)?.ToString()
           ?? string.Empty;

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
}
