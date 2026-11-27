using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// "Extend context into RAM" adds nothing once Context fits the model's native window (27 Nov): a
/// variant copied from a YaRN profile kept the tick after its Context was lowered to 151,232 on a
/// 262,144-token model. Save profile unticks it then; while editing only a note says so.
/// </summary>
public sealed class YarnAddsNothingTests
{
    [Theory]
    [InlineData(true, 151232, 262144, true)]
    [InlineData(true, 262144, 262144, true)]
    [InlineData(true, 351232, 262144, false)]
    [InlineData(false, 151232, 262144, false)]
    [InlineData(true, 151232, 0, false)]
    public void Yarn_adds_nothing_only_when_ticked_and_Context_fits_the_known_native_window(bool ticked, int context, int native, bool expected)
        => Assert.Equal(expected, LocalPrioritySettingsCalculator.YarnAddsNothing(ticked, context, native));
}
