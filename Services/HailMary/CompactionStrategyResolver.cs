using System.Collections.Generic;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>
/// The Hail Mary strategy the user selected in the picker. Stage 0 = WaitForCline (default);
/// Stage 1 adds ExternalRescue.
/// </summary>
public enum HailMaryStrategy
{
    WaitForCline,
    ExternalRescue,
}

/// <summary>
/// Maps a user-selected <see cref="HailMaryStrategy"/> to a concrete <see cref="ICompactionStrategy"/>.
/// This is the single seam future stages plug into: adding a new strategy = add an enum value +
/// register it here. Prior strategies are untouched.
/// </summary>
public static class CompactionStrategyResolver
{
    private static readonly Dictionary<HailMaryStrategy, ICompactionStrategy> Strategies = new()
    {
        [HailMaryStrategy.WaitForCline] = new WaitForClineStrategy(),
        [HailMaryStrategy.ExternalRescue] = new ExternalRescueStrategy(),
    };

    /// <summary>Resolves the strategy for the given choice. Falls back to WaitForCline if unknown.</summary>
    public static ICompactionStrategy Resolve(HailMaryStrategy choice)
    {
        return Strategies.TryGetValue(choice, out var s) ? s : Strategies[HailMaryStrategy.WaitForCline];
    }

    /// <summary>The default strategy (Stage 0 behavior).</summary>
    public static HailMaryStrategy Default => HailMaryStrategy.WaitForCline;
}

/// <summary>
/// The Hail Mary rescue "make headroom" preference. Controls how much free context the external
/// compaction leaves for resuming the chat. Higher headroom = smaller summary = less nuance
/// preserved but safer; lower headroom = bigger summary = more nuance but less safety margin.
/// </summary>
public enum HailMaryHeadroom
{
    /// <summary>Preserve more nuance; leave ~20% free context. Riskier if context is critically full.</summary>
    PreserveMore,

    /// <summary>Balanced (default); leave ~40% free context. The "just enough" default.</summary>
    Balanced,

    /// <summary>Leave more room; ~60% free context. Safest; drops nuance first.</summary>
    LeaveMoreRoom,
}

/// <summary>
/// Maps a <see cref="HailMaryHeadroom"/> preference to the target fraction of the context window
/// that should remain FREE after the compaction. This is the "make headroom" knob: it bounds how
/// large the rescue summary may be so there is always room to resume the chat.
/// </summary>
public static class HailMaryHeadroomPolicy
{
    /// <summary>The default headroom preference (Balanced).</summary>
    public static HailMaryHeadroom Default => HailMaryHeadroom.Balanced;

    /// <summary>
    /// Returns the target free-context fraction (0..1) for the given preference.
    /// PreserveMore = 0.20, Balanced = 0.40, LeaveMoreRoom = 0.60.
    /// </summary>
    public static double TargetFreeFraction(HailMaryHeadroom headroom) => headroom switch
    {
        HailMaryHeadroom.PreserveMore => 0.20,
        HailMaryHeadroom.LeaveMoreRoom => 0.60,
        _ => 0.40,
    };

    /// <summary>Human-readable label for the preference (for the picker).</summary>
    public static string DisplayName(HailMaryHeadroom headroom) => headroom switch
    {
        HailMaryHeadroom.PreserveMore => "Preserve more nuance (leave ~20% room)",
        HailMaryHeadroom.LeaveMoreRoom => "Leave more room (drop nuance first, ~60% free)",
        _ => "Balanced (~40% free)",
    };
}
