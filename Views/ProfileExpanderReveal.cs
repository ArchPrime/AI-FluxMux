using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluxMux.Avalonia.ViewModels;

namespace FluxMux.Avalonia.Views;

internal static class ProfileExpanderReveal
{
    // Leave ModelProfilesScroll where it is unless the target header is fully
    // off-screen. Validate, Save, and expanding a visible row must not jump.
    private const double HeaderTopInset = 12;
    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan ContentFadeDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ContentFadeDelay = TimeSpan.FromMilliseconds(70);

    private static readonly TimeSpan ContentCollapseDuration = TimeSpan.FromMilliseconds(200);

    private static int _scrollGeneration;
    private static int _contentGeneration;
    private static readonly HashSet<Expander> _collapseCommitAllowed = new();
    private static readonly HashSet<Expander> _collapseInProgress = new();

    public static void Schedule(Control host, CloudVariantTreeItemViewModel item, bool focusNameBox)
    {
        void Run()
        {
            if (!item.IsExpanded)
            {
                return;
            }

            Reveal(host, item, focusNameBox);
        }

        Dispatcher.UIThread.Post(Run, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Run, DispatcherPriority.Background);
    }

    public static void Reveal(Control host, CloudVariantTreeItemViewModel item, bool focusNameBox)
    {
        if (!item.IsExpanded)
        {
            return;
        }

        var expander = FindExpanderForItem(host, item);
        if (expander is null)
        {
            return;
        }

        var header = FindProfileHeader(expander);
        var scroll = host.FindControl<ScrollViewer>("ModelProfilesScroll") ?? FindScrollingAncestor(expander);
        if (host.DataContext is MainViewModel { SuppressProfileExpanderReveal: true })
        {
            _scrollGeneration++;
            return;
        }

        if (scroll is not null && header is not null
            && TryComputeOffscreenScrollTarget(scroll, header) is { } targetY)
        {
            _ = AnimateScrollOffsetAsync(scroll, targetY, ScrollDuration);
        }
        else
        {
            _scrollGeneration++;
        }

        var content = ResolveExpanderContent(expander);
        if (content is not null && content.Opacity >= 0.99)
        {
            return;
        }

        _ = AnimateContentRevealAsync(expander, ContentFadeDelay, ContentFadeDuration);

        if (!focusNameBox || header is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var nameBox = header.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(box => box.Classes.Contains("profileName") && box.IsEnabled);
            nameBox?.Focus();
            nameBox?.SelectAll();
        }, DispatcherPriority.Input);
    }

    public static bool TryDeferCollapse(Expander expander)
    {
        if (expander.DataContext is CloudVariantTreeItemViewModel { IsExpanded: false })
        {
            return false;
        }

        if (_collapseCommitAllowed.Remove(expander) || _collapseInProgress.Contains(expander))
        {
            return false;
        }

        _ = CollapseAsync(expander);
        return true;
    }

    private static async Task CollapseAsync(Expander expander)
    {
        if (!_collapseInProgress.Add(expander))
        {
            return;
        }

        try
        {
            await AnimateContentCollapseAsync(expander, ContentCollapseDuration);
            _collapseCommitAllowed.Add(expander);
            if (expander.DataContext is CloudVariantTreeItemViewModel item)
            {
                item.IsExpanded = false;
            }
            else
            {
                expander.IsExpanded = false;
            }
        }
        finally
        {
            _collapseInProgress.Remove(expander);
        }
    }

    private static Expander? FindExpanderForItem(Control host, CloudVariantTreeItemViewModel item)
    {
        var listNames = string.IsNullOrWhiteSpace(item.Provider)
            ? new[] { "LocalVariantList", "CloudVariantList" }
            : new[] { "CloudVariantList", "LocalVariantList" };
        foreach (var listName in listNames)
        {
            var list = host.FindControl<ListBox>(listName);
            if (list is null)
            {
                continue;
            }

            for (var index = 0; index < list.ItemCount; index++)
            {
                if (list.ContainerFromIndex(index) is not Control container)
                {
                    continue;
                }

                var expander = container.GetVisualDescendants().OfType<Expander>().FirstOrDefault();
                var context = expander?.DataContext ?? container.DataContext;
                if (!ReferenceEquals(context, item))
                {
                    continue;
                }

                return expander ?? container as Expander;
            }
        }

        return null;
    }

    private static Border? FindProfileHeader(Expander expander)
        => expander.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("profileHeaderChrome"));

    private static ScrollViewer? FindScrollingAncestor(Control start)
    {
        ScrollViewer? last = null;
        for (Visual? visual = start; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not ScrollViewer candidate)
            {
                continue;
            }

            last = candidate;
            if (candidate.Extent.Height > candidate.Viewport.Height + 1)
            {
                return candidate;
            }
        }

        return last;
    }

    private static double? TryComputeOffscreenScrollTarget(ScrollViewer scroll, Control header)
    {
        var headerPoint = header.TranslatePoint(new Point(0, 0), scroll);
        if (headerPoint is not { } origin)
        {
            return null;
        }

        var headerHeight = Math.Max(header.Bounds.Height, 1);
        if (!IsHeaderOffscreen(origin.Y, headerHeight, scroll.Viewport.Height))
        {
            return null;
        }

        var delta = origin.Y < 0
            ? origin.Y - HeaderTopInset
            : origin.Y + headerHeight - scroll.Viewport.Height + HeaderTopInset;
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        return Math.Clamp(scroll.Offset.Y + delta, 0, maxY);
    }

    private static bool IsHeaderOffscreen(double headerTop, double headerHeight, double viewportHeight)
    {
        var headerBottom = headerTop + headerHeight;
        return headerBottom <= 0 || headerTop >= viewportHeight;
    }

    private static async Task AnimateScrollOffsetAsync(ScrollViewer scroll, double targetY, TimeSpan duration)
    {
        var generation = ++_scrollGeneration;
        var startY = scroll.Offset.Y;
        if (Math.Abs(startY - targetY) < 0.5)
        {
            return;
        }

        var startedUtc = DateTime.UtcNow;
        while (DateTime.UtcNow - startedUtc < duration)
        {
            if (generation != _scrollGeneration)
            {
                return;
            }

            var progress = (DateTime.UtcNow - startedUtc).TotalMilliseconds / duration.TotalMilliseconds;
            var eased = EaseOutCubic(Math.Clamp(progress, 0, 1));
            scroll.Offset = new Vector(scroll.Offset.X, startY + (targetY - startY) * eased);
            await Task.Delay(16);
        }

        if (generation == _scrollGeneration)
        {
            scroll.Offset = new Vector(scroll.Offset.X, targetY);
        }
    }

    private static async Task AnimateContentRevealAsync(Expander expander, TimeSpan delay, TimeSpan duration)
    {
        var generation = ++_contentGeneration;
        await Task.Delay(delay);
        if (generation != _contentGeneration)
        {
            return;
        }

        var content = ResolveExpanderContent(expander);
        if (content is null)
        {
            return;
        }

        content.Opacity = 0;
        content.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        content.RenderTransform = new TranslateTransform(0, -8);

        var startedUtc = DateTime.UtcNow;
        while (DateTime.UtcNow - startedUtc < duration)
        {
            if (generation != _contentGeneration)
            {
                return;
            }

            var progress = (DateTime.UtcNow - startedUtc).TotalMilliseconds / duration.TotalMilliseconds;
            var eased = EaseOutCubic(Math.Clamp(progress, 0, 1));
            content.Opacity = eased;
            if (content.RenderTransform is TranslateTransform translate)
            {
                translate.Y = -8 * (1 - eased);
            }

            await Task.Delay(16);
        }

        if (generation != _contentGeneration)
        {
            return;
        }

        content.Opacity = 1;
        content.RenderTransform = null;
    }

    private static async Task AnimateContentCollapseAsync(Expander expander, TimeSpan duration)
    {
        var generation = ++_contentGeneration;
        var content = ResolveExpanderContent(expander);
        if (content is null)
        {
            return;
        }

        content.Opacity = 1;
        content.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        var translate = content.RenderTransform as TranslateTransform ?? new TranslateTransform();
        translate.Y = 0;
        content.RenderTransform = translate;

        var startedUtc = DateTime.UtcNow;
        while (DateTime.UtcNow - startedUtc < duration)
        {
            if (generation != _contentGeneration)
            {
                return;
            }

            var progress = (DateTime.UtcNow - startedUtc).TotalMilliseconds / duration.TotalMilliseconds;
            var eased = EaseInCubic(Math.Clamp(progress, 0, 1));
            content.Opacity = 1 - eased;
            translate.Y = -8 * eased;
            await Task.Delay(16);
        }

        if (generation != _contentGeneration)
        {
            return;
        }

        content.Opacity = 0;
        translate.Y = -8;
    }

    private static Control? ResolveExpanderContent(Expander expander)
    {
        if (expander.Content is Control direct)
        {
            return direct;
        }

        return expander.GetVisualDescendants()
            .OfType<ContentControl>()
            .LastOrDefault();
    }

    private static double EaseOutCubic(double t)
        => 1 - Math.Pow(1 - t, 3);

    private static double EaseInCubic(double t)
        => t * t * t;
}
