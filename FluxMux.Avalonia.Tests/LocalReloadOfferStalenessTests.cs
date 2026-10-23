using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalReloadOfferStalenessTests
{
    [Fact]
    public void Offer_is_stale_when_needed_context_is_well_below_the_window()
    {
        // 20% of a 32K window: the chat has shrunk, so a pending overflow offer is stale.
        Assert.True(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 6553, hotContext: 32768));
        // 50%: still comfortably below the 70% staleness threshold.
        Assert.True(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 16384, hotContext: 32768));
    }

    [Fact]
    public void Offer_is_not_stale_when_needed_context_is_near_the_window()
    {
        // 85%: a genuinely near-limit offer must NOT be dropped.
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 27852, hotContext: 32768));
        // 95%: clearly not stale.
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 31129, hotContext: 32768));
        // 100%: not stale.
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 32768, hotContext: 32768));
    }

    [Fact]
    public void Offer_is_not_stale_at_the_seventy_percent_boundary()
    {
        // Use exact multiples so the 70% boundary is unambiguous. 70% of 10000 is 7000.
        // Exactly 70% is NOT stale (the rule is strictly less than 70%).
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 7000, hotContext: 10000));
        // Just under 70% is stale.
        Assert.True(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 6999, hotContext: 10000));
    }

    [Fact]
    public void Offer_is_not_stale_when_numbers_are_missing()
    {
        // Older offer files may not carry neededContext / hotContext; those keep showing.
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 0, hotContext: 32768));
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 6553, hotContext: 0));
        Assert.False(FluxMuxRuntimeService.IsLocalReloadOfferStale(neededContext: 0, hotContext: 0));
    }
}