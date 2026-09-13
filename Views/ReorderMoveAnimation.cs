using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Slides list rows from their last layout position to the new one.
/// Implicit Offset animation is not used: that treats a new arrange as a
/// move from the top of the list, and it breaks drag hit-testing.
/// </summary>
public sealed class ReorderMoveAnimation : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<ReorderMoveAnimation, ItemsControl, bool>("Enable");

    private static readonly AttachedProperty<Subscription?> SubscriptionProperty =
        AvaloniaProperty.RegisterAttached<ReorderMoveAnimation, ItemsControl, Subscription?>("Subscription");

    public static bool GetEnable(ItemsControl element) => element.GetValue(EnableProperty);

    public static void SetEnable(ItemsControl element, bool value) => element.SetValue(EnableProperty, value);

    public static void BeginFollow(ItemsControl list, object draggedItem, PointerEventArgs e)
        => list.GetValue(SubscriptionProperty)?.BeginFollow(draggedItem, e);

    public static void Follow(ItemsControl list, object draggedItem, PointerEventArgs e)
        => list.GetValue(SubscriptionProperty)?.Follow(draggedItem, e);

    public static void EndFollow(ItemsControl list, object? draggedItem)
        => list.GetValue(SubscriptionProperty)?.EndFollow(draggedItem);

    public static object? GetDisplacementTarget(ItemsControl list, object draggedItem)
        => list.GetValue(SubscriptionProperty)?.GetDisplacementTarget(draggedItem);

    /// <summary>
    /// The leading edge of the ghost takes a neighbour's place. Tall
    /// cards bounce back if the rule is "most of the ghost" — after a
    /// swap the ghost still covers the old slot. Direction is from the
    /// press, so the new hole under the ghost does not reverse it.
    /// </summary>
    public static bool ShouldDisplaceByOverlap(
        double neighborTop,
        double neighborBottom,
        double ghostTop,
        double ghostBottom,
        double holeTop,
        double holeBottom,
        bool movingUp)
    {
        if (neighborBottom <= neighborTop)
        {
            return false;
        }

        var edge = Math.Max(8, (neighborBottom - neighborTop) * 0.05);
        if (movingUp)
        {
            if (neighborBottom > holeTop + 1)
            {
                return false;
            }

            return ghostTop <= neighborBottom - edge && ghostBottom > neighborTop;
        }

        if (neighborTop < holeBottom - 1)
        {
            return false;
        }

        return ghostBottom >= neighborTop + edge && ghostTop < neighborBottom;
    }

    static ReorderMoveAnimation()
    {
        EnableProperty.Changed.AddClassHandler<ItemsControl>(OnEnableChanged);
    }

    private static void OnEnableChanged(ItemsControl list, AvaloniaPropertyChangedEventArgs args)
    {
        Detach(list);
        if (args.NewValue is true)
        {
            Attach(list);
        }
    }

    private static void Attach(ItemsControl list)
    {
        var subscription = new Subscription(list);
        list.SetValue(SubscriptionProperty, subscription);
        list.AttachedToVisualTree += subscription.OnAttached;
        list.DetachedFromVisualTree += subscription.OnDetached;
        list.PropertyChanged += subscription.OnListPropertyChanged;
        subscription.HookItems();
        if (list.IsAttachedToVisualTree())
        {
            subscription.CaptureAfterLayout();
        }
    }

    private static void Detach(ItemsControl list)
    {
        var subscription = list.GetValue(SubscriptionProperty);
        if (subscription is null)
        {
            return;
        }

        list.AttachedToVisualTree -= subscription.OnAttached;
        list.DetachedFromVisualTree -= subscription.OnDetached;
        list.PropertyChanged -= subscription.OnListPropertyChanged;
        subscription.UnhookItems();
        list.SetValue(SubscriptionProperty, null);
    }

    private sealed class Subscription
    {
        private readonly ItemsControl _list;
        private readonly Dictionary<object, double> _tops = new();
        private INotifyCollectionChanged? _items;
        private bool _awaitingLayout;
        private object? _followedItem;
        private double _grabOffsetY;
        private double _ghostHomeX;
        private double _pressPointerY;
        private double _grabOffsetInPanel;
        private double _lastPointerInPanelY;
        private double _dragOriginCenterY;
        private Visual? _itemPanel;
        private Canvas? _ghostLayer;
        private Border? _ghost;
        private const double CapturePad = 2;

        public Subscription(ItemsControl list)
        {
            _list = list;
        }

        public void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
            => HookItems();

        public void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
            => UnhookItems();

        public void OnListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ItemsControl.ItemsSourceProperty)
            {
                HookItems();
            }
        }

        public void HookItems()
        {
            UnhookItems();
            if (_list.Items is INotifyCollectionChanged items)
            {
                _items = items;
                _items.CollectionChanged += OnItemsChanged;
            }

            CaptureAfterLayout();
        }

        public void UnhookItems()
        {
            if (_items is not null)
            {
                _items.CollectionChanged -= OnItemsChanged;
                _items = null;
            }
        }

        public void CaptureAfterLayout()
        {
            if (_awaitingLayout)
            {
                return;
            }

            _awaitingLayout = true;
            _list.LayoutUpdated += OnLayoutUpdated;
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            _list.LayoutUpdated -= OnLayoutUpdated;
            _awaitingLayout = false;
            CaptureTops();
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Move)
            {
                CaptureAfterLayout();
                return;
            }

            var previous = new Dictionary<object, double>(_tops);
            _list.UpdateLayout();
            if (_followedItem is not null)
            {
                HideHole();
            }
            else
            {
                Play(previous);
            }

            CaptureTops();
        }

        private void CaptureTops()
        {
            _tops.Clear();
            for (var index = 0; index < _list.ItemCount; index++)
            {
                if (_list.ContainerFromIndex(index) is not Control container)
                {
                    continue;
                }

                var key = ItemKey(index, container);
                if (key is not null)
                {
                    _tops[key] = container.Bounds.Y;
                }
            }
        }

        private void Play(IReadOnlyDictionary<object, double> previous)
        {
            if (previous.Count == 0)
            {
                return;
            }

            for (var index = 0; index < _list.ItemCount; index++)
            {
                if (_list.ContainerFromIndex(index) is not Control container)
                {
                    continue;
                }

                var key = ItemKey(index, container);
                if (key is null
                    || Equals(key, _followedItem)
                    || !previous.TryGetValue(key, out var oldY))
                {
                    continue;
                }

                var delta = oldY - container.Bounds.Y;
                if (Math.Abs(delta) < 0.5)
                {
                    continue;
                }

                SlideFrom(container, delta);
            }
        }

        private object? ItemKey(int index, Control container)
            => _list.ItemFromContainer(container)
               ?? container.DataContext
               ?? (index < _list.ItemCount ? _list.Items[index] : null);

        public void BeginFollow(object draggedItem, PointerEventArgs e)
        {
            EndFollow(null);
            _followedItem = draggedItem;
            if (ContainerFor(draggedItem) is not { } container)
            {
                return;
            }

            _ghostLayer = FindGhostLayer(_list);
            if (_ghostLayer is null)
            {
                return;
            }

            var origin = container.TranslatePoint(new Point(-CapturePad, -CapturePad), _ghostLayer)
                ?? new Point(container.Bounds.X - CapturePad, container.Bounds.Y - CapturePad);
            var pointer = e.GetPosition(_ghostLayer);
            _grabOffsetY = pointer.Y - origin.Y;
            _ghostHomeX = origin.X;
            _itemPanel = container.Parent as Visual;
            if (_itemPanel is not null)
            {
                _pressPointerY = e.GetPosition(_itemPanel).Y;
                _grabOffsetInPanel = _pressPointerY - container.Bounds.Y;
                _lastPointerInPanelY = _pressPointerY;
            }
            _ghost = CreateGhost(container, _ghostLayer);
            _ghostLayer.Children.Add(_ghost);
            HideHole();
            PositionGhost(pointer.Y - _grabOffsetY);
            _dragOriginCenterY = RectInLayer(container).Center.Y;
        }

        public void Follow(object draggedItem, PointerEventArgs e)
        {
            _followedItem = draggedItem;
            HideHole();
            if (_ghostLayer is null || _ghost is null)
            {
                return;
            }

            PositionGhost(e.GetPosition(_ghostLayer).Y - _grabOffsetY);
            var panel = _itemPanel ?? _list.ItemsPanelRoot;
            if (panel is not null)
            {
                _itemPanel = panel;
                _lastPointerInPanelY = e.GetPosition(panel).Y;
            }
        }

        public object? GetDisplacementTarget(object draggedItem)
        {
            if (_ghost is null || _ghostLayer is null)
            {
                return null;
            }

            var draggedIndex = -1;
            Control? hole = null;
            for (var index = 0; index < _list.ItemCount; index++)
            {
                if (_list.ContainerFromIndex(index) is not Control container)
                {
                    continue;
                }

                if (!Equals(ItemKey(index, container), draggedItem))
                {
                    continue;
                }

                draggedIndex = index;
                hole = container;
                break;
            }

            if (draggedIndex < 0 || hole is null)
            {
                return null;
            }

            var ghostRect = GhostRectInLayer();
            var holeRect = RectInLayer(hole);
            var movingUp = ghostRect.Center.Y < _dragOriginCenterY - 1;
            object? target = null;
            var bestScore = 0.0;
            for (var index = 0; index < _list.ItemCount; index++)
            {
                if (index == draggedIndex)
                {
                    continue;
                }

                TryNeighbor(index, holeRect, ghostRect, movingUp, ref target, ref bestScore);
            }

            return target;
        }

        private void TryNeighbor(
            int neighborIndex,
            Rect holeRect,
            Rect ghostRect,
            bool movingUp,
            ref object? target,
            ref double bestScore)
        {
            if (neighborIndex < 0
                || neighborIndex >= _list.ItemCount
                || _list.ContainerFromIndex(neighborIndex) is not Control neighbor)
            {
                return;
            }

            var neighborRect = RectInLayer(neighbor);
            var ghostTop = ghostRect.Y + CapturePad;
            var ghostBottom = ghostRect.Y + ghostRect.Height - CapturePad;
            if (!ShouldDisplaceByOverlap(
                    neighborRect.Top,
                    neighborRect.Bottom,
                    ghostTop,
                    ghostBottom,
                    holeRect.Top,
                    holeRect.Bottom,
                    movingUp))
            {
                return;
            }

            var overlap = ghostRect.Intersect(neighborRect);
            var score = overlap.Height > 0 ? overlap.Height : 1;
            if (score <= bestScore)
            {
                return;
            }

            bestScore = score;
            target = ItemKey(neighborIndex, neighbor);
        }

        private Rect GhostRectInLayer()
        {
            if (_ghost is null)
            {
                return default;
            }

            var width = _ghost.Bounds.Width > 1 ? _ghost.Bounds.Width : _ghost.Width;
            var height = _ghost.Bounds.Height > 1 ? _ghost.Bounds.Height : _ghost.Height;
            return new Rect(Canvas.GetLeft(_ghost), Canvas.GetTop(_ghost), width, height);
        }

        private Rect RectInLayer(Control container)
        {
            var layer = _ghostLayer;
            if (layer is null)
            {
                return container.Bounds;
            }

            var topLeft = container.TranslatePoint(new Point(0, 0), layer) ?? default;
            var bottomRight = container.TranslatePoint(
                    new Point(container.Bounds.Width, container.Bounds.Height),
                    layer)
                ?? new Point(topLeft.X + container.Bounds.Width, topLeft.Y + container.Bounds.Height);
            return new Rect(topLeft, bottomRight);
        }

        public void EndFollow(object? draggedItem)
        {
            _followedItem = null;
            if (_ghost is not null && _ghostLayer is not null)
            {
                _ghostLayer.Children.Remove(_ghost);
            }

            _ghost = null;
            _ghostLayer = null;
            RestoreAllContainers();
        }

        private void HideHole()
        {
            RestoreAllContainers();
            if (_followedItem is null || ContainerFor(_followedItem) is not { } container)
            {
                return;
            }

            container.Transitions = null;
            container.RenderTransform = TransformOperations.Identity;
            container.Opacity = 0;
            container.IsHitTestVisible = false;
        }

        private void RestoreAllContainers()
        {
            for (var index = 0; index < _list.ItemCount; index++)
            {
                if (_list.ContainerFromIndex(index) is not Control container)
                {
                    continue;
                }

                container.Opacity = 1;
                container.IsHitTestVisible = true;
            }
        }

        private void PositionGhost(double top)
        {
            if (_ghost is null)
            {
                return;
            }

            Canvas.SetLeft(_ghost, _ghostHomeX);
            Canvas.SetTop(_ghost, top);
        }

        private Border CreateGhost(Control container, Visual layer)
        {
            var localWidth = Math.Max(container.Bounds.Width, 1);
            var localHeight = Math.Max(container.Bounds.Height, 1);
            var innerWidth = localWidth + CapturePad * 2;
            var innerHeight = localHeight + CapturePad * 2;
            var display = SizeIn(container, layer, CapturePad);
            var backing = OpaqueRowBrush(container);
            var bitmap = CaptureOpaque(container, innerWidth, innerHeight, backing);
            // Never display the snapshot smaller than its pixel grid — that
            // squashes the bottom 1px stroke to a thinner line.
            var scaleX = display.Width / innerWidth;
            var scaleY = display.Height / innerHeight;

            var ghost = new Border
            {
                Width = bitmap.PixelSize.Width * scaleX,
                Height = bitmap.PixelSize.Height * scaleY,
                Background = backing,
                ClipToBounds = false,
                Opacity = 1,
                IsHitTestVisible = false,
                Child = new Image
                {
                    Source = bitmap,
                    Opacity = 1,
                    Stretch = Stretch.Fill
                }
            };
            RenderOptions.SetBitmapInterpolationMode(ghost, BitmapInterpolationMode.None);
            return ghost;
        }

        private static RenderTargetBitmap CaptureOpaque(
            Control container,
            double innerWidth,
            double innerHeight,
            IBrush backing)
        {
            var pixels = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(innerWidth)),
                Math.Max(1, (int)Math.Ceiling(innerHeight)));
            var dpi = new Vector(96, 96);
            var flat = new RenderTargetBitmap(pixels, dpi);
            using (var context = flat.CreateDrawingContext())
            {
                context.DrawRectangle(backing, null, new Rect(0, 0, pixels.Width, pixels.Height));
                using (context.PushTransform(Matrix.CreateTranslation(CapturePad, CapturePad)))
                {
                    RenderForGhost(context, container, isRoot: true);
                    PunchGroupBoxHeaderGap(context, container, backing);
                    StrokeGroupBoxBottom(context, container);
                }
            }

            return flat;
        }

        /// <summary>
        /// Same tree walk as a snapshot, but without ClipToBounds or
        /// OpacityMask. ClipToBounds shears the 1px corner stroke; a GroupBox
        /// VisualBrush mask disappears in a bitmap and takes the side borders
        /// with it. The header gap is painted afterwards.
        /// </summary>
        private static void RenderForGhost(DrawingContext context, Visual visual, bool isRoot)
        {
            if (!visual.IsVisible || visual.Opacity <= 0)
            {
                return;
            }

            var transform = isRoot
                ? Matrix.Identity
                : Matrix.CreateTranslation(visual.Bounds.Position);
            if (visual.RenderTransform?.Value is { } render)
            {
                var origin = visual.RenderTransformOrigin.ToPixels(visual.Bounds.Size);
                var offset = Matrix.CreateTranslation(origin);
                transform = (-offset) * render * offset * transform;
            }

            using (context.PushTransform(transform))
            using (context.PushOpacity(visual.Opacity))
            using (visual.Clip is { } clip
                ? context.PushGeometryClip(clip)
                : default(DrawingContext.PushedState?))
            {
                visual.Render(context);
                foreach (var child in visual.GetVisualChildren().OrderBy(item => item.ZIndex))
                {
                    RenderForGhost(context, child, isRoot: false);
                }
            }
        }

        private static void PunchGroupBoxHeaderGap(
            DrawingContext context,
            Control container,
            IBrush backing)
        {
            var group = container as GroupBox
                ?? container.GetVisualDescendants().OfType<GroupBox>().FirstOrDefault();
            if (group is null || FindGroupBoxHeader(group) is not { } header)
            {
                return;
            }

            var chrome = HeaderChrome(group, header);
            var headerOrigin = chrome.TranslatePoint(new Point(0, 0), container);
            if (headerOrigin is null)
            {
                return;
            }

            // Live GroupBox only masks the top stroke behind the header, not
            // the whole title block. The break is the header chrome plus about
            // one space past each end of the title.
            var frame = FindGroupBoxFrame(group);
            var frameOrigin = frame?.TranslatePoint(new Point(0, 0), container) ?? headerOrigin;
            var stroke = frame is not null
                ? Math.Max(1, frame.BorderThickness.Top)
                : 1;
            var space = MeasureHeaderSpace(header);
            var gap = new Rect(
                headerOrigin.Value.X - space,
                frameOrigin.Value.Y - stroke,
                chrome.Bounds.Width + space * 2,
                stroke * 3);
            if (gap.Width < 1 || gap.Height < 1)
            {
                return;
            }

            context.DrawRectangle(ColorBehind(group, backing), null, gap);
            var drawAt = header.TranslatePoint(new Point(0, 0), container) ?? headerOrigin.Value;
            using (context.PushTransform(Matrix.CreateTranslation(drawAt.X, drawAt.Y)))
            {
                RenderForGhost(context, header, isRoot: true);
            }
        }

        private static void StrokeGroupBoxBottom(DrawingContext context, Control container)
        {
            var group = container as GroupBox
                ?? container.GetVisualDescendants().OfType<GroupBox>().FirstOrDefault();
            if (group is null || FindGroupBoxFrame(group) is not { } frame
                || frame.BorderBrush is not { } brush)
            {
                return;
            }

            var origin = frame.TranslatePoint(new Point(0, 0), container);
            if (origin is null)
            {
                return;
            }

            var thickness = Math.Max(1, frame.BorderThickness.Bottom);
            var y = origin.Value.Y + frame.Bounds.Height - thickness / 2;
            context.DrawLine(
                new Pen(brush, thickness),
                new Point(origin.Value.X, y),
                new Point(origin.Value.X + frame.Bounds.Width, y));
        }

        private static Border? FindGroupBoxFrame(GroupBox group)
        {
            Border? best = null;
            foreach (var border in group.GetVisualDescendants().OfType<Border>())
            {
                if (border.BorderThickness == default || border.BorderBrush is null)
                {
                    continue;
                }

                if (border.Bounds.Width < group.Bounds.Width * 0.85)
                {
                    continue;
                }

                if (best is null || border.Bounds.Height > best.Bounds.Height)
                {
                    best = border;
                }
            }

            return best;
        }

        private static Control? FindGroupBoxHeader(GroupBox group)
        {
            foreach (var presenter in group.GetVisualDescendants().OfType<ContentPresenter>())
            {
                if (presenter.Name?.Contains("Header", StringComparison.OrdinalIgnoreCase) == true
                    || Equals(presenter.Content, group.Header))
                {
                    return presenter;
                }
            }

            return group.Header as Control;
        }

        private static Control HeaderChrome(GroupBox group, Control header)
        {
            for (var parent = header.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
            {
                if (parent is ContentPresenter presenter)
                {
                    return presenter;
                }

                if (Equals(parent, group))
                {
                    break;
                }
            }

            return header;
        }

        private static double MeasureHeaderSpace(Control header)
        {
            var text = header as TextBlock
                ?? header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            if (text is null)
            {
                return 4;
            }

            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            var pair = new FormattedText(
                "x x",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                text.FontSize,
                Brushes.Black);
            var tight = new FormattedText(
                "xx",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                text.FontSize,
                Brushes.Black);
            return Math.Max(1, pair.Width - tight.Width);
        }

        private static IBrush ColorBehind(Visual visual, IBrush fallback)
        {
            for (var parent = visual.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
            {
                if (TryOpaqueBackground(parent) is { } brush)
                {
                    return ToOpaque(brush);
                }
            }

            return fallback;
        }

        private IBrush OpaqueRowBrush(Control container)
        {
            var variant = ThemeVariantOf(_list);
            if (FindPaintedBackground(container) is { } painted
                && !IsWrongThemeFill(painted.Color, variant))
            {
                return ToOpaque(painted);
            }

            var preferred = _list is ListBox ? "FluxListItemBackground" : "FluxCardBackground";
            foreach (var key in new[]
                     {
                         preferred,
                         "FluxListItemBackground",
                         "FluxCardBackground",
                         "FluxMutedBackground",
                         "FluxPaneBackground"
                     })
            {
                if (ResolveBrush(key, variant) is ISolidColorBrush found
                    && !IsWrongThemeFill(found.Color, variant))
                {
                    return ToOpaque(found);
                }
            }

            return new SolidColorBrush(
                variant == ThemeVariant.Dark
                    ? Color.FromRgb(0x15, 0x20, 0x33)
                    : Color.FromRgb(0xF8, 0xFA, 0xFC));
        }

        private static ISolidColorBrush? FindPaintedBackground(Control container)
        {
            var minWidth = Math.Max(1, container.Bounds.Width * 0.6);
            foreach (var visual in container.GetVisualDescendants())
            {
                if (visual is not Control painted
                    || painted is Button or ComboBox or TextBox or TextBlock
                    || painted.Bounds.Width < minWidth)
                {
                    continue;
                }

                if (TryOpaqueBackground(painted) is { } child)
                {
                    return child;
                }
            }

            if (TryOpaqueBackground(container) is { } own)
            {
                return own;
            }

            for (var parent = container.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
            {
                if (TryOpaqueBackground(parent) is { } ancestor)
                {
                    return ancestor;
                }
            }

            return null;
        }

        private static ISolidColorBrush? TryOpaqueBackground(Visual visual)
        {
            var background = visual switch
            {
                ContentPresenter presenter => presenter.Background,
                Border border => border.Background,
                Panel panel => panel.Background,
                TemplatedControl templated => templated.Background,
                _ => null
            };
            return background is ISolidColorBrush solid && solid.Color.A == 255
                ? solid
                : null;
        }

        private static bool IsWrongThemeFill(Color color, ThemeVariant variant)
        {
            var light = color.R > 240 && color.G > 240 && color.B > 240;
            var dark = color.R < 40 && color.G < 40 && color.B < 40;
            return variant == ThemeVariant.Dark ? light : dark;
        }

        private static SolidColorBrush ToOpaque(ISolidColorBrush solid)
            => new(Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B));

        private static ThemeVariant ThemeVariantOf(StyledElement anchor)
        {
            if (anchor.ActualThemeVariant is { } actual && actual != ThemeVariant.Default)
            {
                return actual;
            }

            var app = Application.Current?.ActualThemeVariant;
            return app is not null && app != ThemeVariant.Default ? app : ThemeVariant.Light;
        }

        private static Size SizeIn(Control container, Visual layer, double pad)
        {
            var topLeft = container.TranslatePoint(new Point(-pad, -pad), layer)
                ?? new Point(-pad, -pad);
            var bottomRight = container.TranslatePoint(
                new Point(container.Bounds.Width + pad, container.Bounds.Height + pad),
                layer)
                ?? new Point(container.Bounds.Width + pad, container.Bounds.Height + pad);
            return new Size(
                Math.Max(1, Math.Abs(bottomRight.X - topLeft.X)),
                Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y)));
        }

        private IBrush? ResolveBrush(string key, ThemeVariant variant)
        {
            if (_list.TryFindResource(key, variant, out var value) && value is IBrush brush)
            {
                return brush;
            }

            return Application.Current?.TryFindResource(key, variant, out var appValue) == true
                   && appValue is IBrush appBrush
                ? appBrush
                : null;
        }

        private static Canvas? FindGhostLayer(Visual from)
        {
            return TopLevel.GetTopLevel(from) is Control root
                ? root.FindControl<Canvas>("DragGhostLayer")
                : null;
        }

        private Control? ContainerFor(object item)
        {
            for (var index = 0; index < _list.ItemCount; index++)
            {
                if (_list.ContainerFromIndex(index) is not Control container)
                {
                    continue;
                }

                var key = ItemKey(index, container);
                if (key is not null && Equals(key, item))
                {
                    return container;
                }
            }

            return null;
        }

        private static void SlideFrom(Control item, double deltaY)
        {
            item.Transitions = null;
            item.RenderTransform = TransformOperations.Parse(
                "translateY(" + deltaY.ToString("0.###", CultureInfo.InvariantCulture) + "px)");

            Dispatcher.UIThread.Post(() =>
            {
                item.Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(180),
                        Easing = new CubicEaseOut()
                    }
                };
                item.RenderTransform = TransformOperations.Identity;
            }, DispatcherPriority.Render);
        }
    }
}
