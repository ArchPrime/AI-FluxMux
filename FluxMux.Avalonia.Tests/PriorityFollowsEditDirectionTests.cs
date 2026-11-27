using System.Collections.Generic;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// A manual edit moves the priority list the way it moved the settings (28 Nov): lowering Context
/// put "Context length" at the top, because the scores are absolute and Context was still near the
/// card's maximum, so it out-scored the current top whatever the edit did.
/// </summary>
public sealed class PriorityFollowsEditDirectionTests
{
    private static readonly string[] Current = ["Reasoning depth", "Stability", "Speed", "Fidelity", "Context length", "Reply length"];
    private static readonly string[] ContextFirst = ["Context length", "Reasoning depth", "Stability", "Speed", "Fidelity", "Reply length"];

    private static Dictionary<string, int> Scores(int context, int reasoning)
        => new()
        {
            ["Context length"] = context,
            ["Reasoning depth"] = reasoning,
            ["Stability"] = 3,
            ["Speed"] = 1,
            ["Fidelity"] = 2,
            ["Reply length"] = 0,
        };

    [Fact]
    public void Lowering_Context_does_not_lift_Context_length_even_while_it_still_scores_highest()
    {
        // Context still scores 18 after the edit (as before it); Reasoning depth 6.
        Assert.False(MainViewModel.ShouldAdoptInferredPriorityOrder(Current, ContextFirst, Scores(18, 6), margin: 3, scoresBefore: Scores(18, 6)));
        // Lowered a band: its score fell, so it certainly may not rise.
        Assert.False(MainViewModel.ShouldAdoptInferredPriorityOrder(Current, ContextFirst, Scores(14, 6), margin: 3, scoresBefore: Scores(18, 6)));
    }

    [Fact]
    public void Raising_Context_may_lift_it()
        => Assert.True(MainViewModel.ShouldAdoptInferredPriorityOrder(Current, ContextFirst, Scores(18, 6), margin: 3, scoresBefore: Scores(10, 6)));

    [Fact]
    public void Without_an_edit_the_old_rule_applies()
        => Assert.True(MainViewModel.ShouldAdoptInferredPriorityOrder(Current, ContextFirst, Scores(18, 6), margin: 3));
}
