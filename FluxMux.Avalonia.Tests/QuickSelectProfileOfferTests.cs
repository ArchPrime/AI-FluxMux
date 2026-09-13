using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class QuickSelectProfileOfferTests
{
    [Fact]
    public void Validated_profile_stays_in_the_picker()
    {
        Assert.True(QuickSelectProfileOffer.KeepInSlotList(
            endpointValidated: true,
            hasEndpointWarning: false,
            assignedToSlot: false));
    }

    [Fact]
    public void Assigned_profile_that_needs_validate_again_stays_in_the_slot()
    {
        Assert.True(QuickSelectProfileOffer.KeepInSlotList(
            endpointValidated: false,
            hasEndpointWarning: true,
            assignedToSlot: true));
    }

    [Fact]
    public void Failed_validate_that_was_never_saved_to_a_slot_stays_out()
    {
        Assert.False(QuickSelectProfileOffer.KeepInSlotList(
            endpointValidated: false,
            hasEndpointWarning: true,
            assignedToSlot: false));
    }

    [Fact]
    public void Never_validated_profile_stays_out()
    {
        Assert.False(QuickSelectProfileOffer.KeepInSlotList(
            endpointValidated: false,
            hasEndpointWarning: false,
            assignedToSlot: true));
    }
}
