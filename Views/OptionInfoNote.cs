using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Standing commentary beside an option. <see cref="OptionInfoDismissalStore.HideLabel"/>
/// tucks it away; <see cref="OptionInfoDismissalStore.MoreInfoLabel"/> brings it back.
/// </summary>
public sealed class OptionInfoNote : UserControl
{
    public static readonly StyledProperty<string> InfoIdProperty =
        AvaloniaProperty.Register<OptionInfoNote, string>(nameof(InfoId), string.Empty);

    public static readonly StyledProperty<string?> NoteTextProperty =
        AvaloniaProperty.Register<OptionInfoNote, string?>(nameof(NoteText));

    public static readonly StyledProperty<bool> StartDismissedProperty =
        AvaloniaProperty.Register<OptionInfoNote, bool>(nameof(StartDismissed));

    private const string BulletMarker = HelpHtmlParser.NoteBullet;

    private readonly StackPanel _body;
    private readonly Button _hide;
    private readonly Button _more;
    private readonly Grid _expanded;
    private OptionInfoDismissalStore? _store;

    static OptionInfoNote()
    {
        InfoIdProperty.Changed.AddClassHandler<OptionInfoNote>((note, _) => note.Refresh());
        NoteTextProperty.Changed.AddClassHandler<OptionInfoNote>((note, _) => note.Refresh());
        StartDismissedProperty.Changed.AddClassHandler<OptionInfoNote>((note, _) => note.Refresh());
    }

    public OptionInfoNote()
    {
        _body = new StackPanel { Spacing = 3 };
        _hide = CreateChromeButton(OptionInfoDismissalStore.HideLabel);
        _hide.Click += (_, _) => Store.Hide(InfoId);
        _more = CreateChromeButton(OptionInfoDismissalStore.MoreInfoLabel);
        _more.Click += (_, _) => Store.Show(InfoId);
        _more.HorizontalAlignment = HorizontalAlignment.Left;

        _expanded = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };
        _expanded.Children.Add(_body);
        Grid.SetColumn(_hide, 1);
        _hide.VerticalAlignment = VerticalAlignment.Top;
        _expanded.Children.Add(_hide);

        Content = new StackPanel
        {
            Spacing = 4,
            Children = { _expanded, _more }
        };

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    public string InfoId
    {
        get => GetValue(InfoIdProperty);
        set => SetValue(InfoIdProperty, value);
    }

    public string? NoteText
    {
        get => GetValue(NoteTextProperty);
        set => SetValue(NoteTextProperty, value);
    }

    public bool StartDismissed
    {
        get => GetValue(StartDismissedProperty);
        set => SetValue(StartDismissedProperty, value);
    }

    private OptionInfoDismissalStore Store => _store ?? OptionInfoDismissalStore.Current;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _store = OptionInfoDismissalStore.Current;
        _store.Changed += OnStoreChanged;
        HelpUiNotes.Changed += OnNotesChanged;
        HelpUiNotes.EnsureFromDisk();
        Refresh();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HelpUiNotes.Changed -= OnNotesChanged;
        if (_store is not null)
        {
            _store.Changed -= OnStoreChanged;
            _store = null;
        }
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Refresh();

    private void OnNotesChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var fallback = NoteText;
        if (string.IsNullOrWhiteSpace(fallback))
        {
            IsVisible = false;
            return;
        }

        var text = HelpUiNotes.Resolve(InfoId, fallback);
        if (string.IsNullOrWhiteSpace(text))
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        Populate(text);
        var expanded = Store.IsNoteVisible(InfoId, StartDismissed);
        _expanded.IsVisible = expanded;
        _more.IsVisible = !expanded;
    }

    /// <summary>
    /// One line per paragraph or bullet. A bullet is laid out as marker and text so a
    /// wrapped line lines up with the words above it rather than with the bullet.
    /// </summary>
    private void Populate(string text)
    {
        _body.Children.Clear();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith(BulletMarker, StringComparison.Ordinal))
            {
                _body.Children.Add(BulletRow(trimmed[BulletMarker.Length..].TrimStart()));
                continue;
            }

            _body.Children.Add(Line(trimmed));
        }
    }

    private static Grid BulletRow(string text)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 5
        };

        var marker = Line(BulletMarker);
        marker.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(marker);

        var body = Line(text);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private static TextBlock Line(string text)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.82
        };
        MarkedText.ApplyTo(block, text);
        return block;
    }

    private static Button CreateChromeButton(string label)
        => new()
        {
            Content = label,
            Classes = { "optionInfo" }
        };
}
