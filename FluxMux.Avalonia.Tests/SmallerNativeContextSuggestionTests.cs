using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// The launch-failure dialog's "Use a smaller context" button for profiles WITHOUT YaRN.
/// It used to offer a YaRN target (512K/768K/1M) and switch YaRN on; it must now offer a
/// genuinely smaller native Context, or nothing.
/// </summary>
public sealed class SmallerNativeContextSuggestionTests
{
    [Fact]
    public void Uses_safe_default_when_it_is_smaller_than_current()
    {
        Assert.Equal(131072, MainViewModel.SuggestSmallerNativeContext(213056, 131072));
    }

    [Fact]
    public void Falls_back_to_half_when_safe_default_is_not_smaller()
    {
        // 213056 / 2 = 106528, snapped down to a 4096 multiple = 106496.
        Assert.Equal(106496, MainViewModel.SuggestSmallerNativeContext(213056, 262144));
        Assert.Equal(106496, MainViewModel.SuggestSmallerNativeContext(213056, 0));
    }

    [Fact]
    public void Never_suggests_more_than_current()
    {
        for (int ctx = 4097; ctx < 300000; ctx += 7919)
        {
            int s = MainViewModel.SuggestSmallerNativeContext(ctx, 0);
            Assert.True(s == 0 || s < ctx, $"ctx={ctx} suggested={s}");
        }
    }

    [Fact]
    public void Returns_zero_when_already_at_floor()
    {
        Assert.Equal(0, MainViewModel.SuggestSmallerNativeContext(4096, 0));
        Assert.Equal(0, MainViewModel.SuggestSmallerNativeContext(2048, 1024));
    }
}
