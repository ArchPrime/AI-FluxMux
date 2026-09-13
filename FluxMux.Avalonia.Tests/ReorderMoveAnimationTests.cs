using FluxMux.Avalonia.Views;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ReorderMoveAnimationTests
{
    [Fact]
    public void Leading_edge_into_the_first_row_secures_it()
    {
        Assert.True(ReorderMoveAnimation.ShouldDisplaceByOverlap(
            neighborTop: 0,
            neighborBottom: 200,
            ghostTop: 160,
            ghostBottom: 360,
            holeTop: 210,
            holeBottom: 410,
            movingUp: true));
    }

    [Fact]
    public void After_taking_the_first_row_the_old_slot_does_not_bounce_back()
    {
        Assert.False(ReorderMoveAnimation.ShouldDisplaceByOverlap(
            neighborTop: 210,
            neighborBottom: 410,
            ghostTop: 160,
            ghostBottom: 360,
            holeTop: 0,
            holeBottom: 200,
            movingUp: true));
    }

    [Fact]
    public void Leading_edge_into_the_last_row_secures_it()
    {
        Assert.True(ReorderMoveAnimation.ShouldDisplaceByOverlap(
            neighborTop: 210,
            neighborBottom: 410,
            ghostTop: 50,
            ghostBottom: 250,
            holeTop: 0,
            holeBottom: 200,
            movingUp: false));
    }

    [Fact]
    public void A_one_pixel_kiss_on_the_neighbour_does_not_displace_it()
    {
        Assert.False(ReorderMoveAnimation.ShouldDisplaceByOverlap(
            neighborTop: 0,
            neighborBottom: 200,
            ghostTop: 199,
            ghostBottom: 399,
            holeTop: 210,
            holeBottom: 410,
            movingUp: true));
    }

    [Fact]
    public void Sitting_in_the_hole_does_not_displace_a_neighbor()
    {
        Assert.False(ReorderMoveAnimation.ShouldDisplaceByOverlap(
            neighborTop: 0,
            neighborBottom: 40,
            ghostTop: 42,
            ghostBottom: 82,
            holeTop: 42,
            holeBottom: 82,
            movingUp: true));
    }
}
