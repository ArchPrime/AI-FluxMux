using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Renders <see cref="ControlLabelMarkup"/> markers as bold runs on a TextBlock,
/// or as a wrapping tooltip so named controls stand out from commentary.
/// </summary>
public static class MarkedText
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Source", typeof(MarkedText));

    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Tip", typeof(MarkedText));

    static MarkedText()
    {
        SourceProperty.Changed.AddClassHandler<TextBlock>(OnSourceChanged);
        TipProperty.Changed.AddClassHandler<Control>(OnTipChanged);
    }

    public static void SetSource(TextBlock element, string? value) => element.SetValue(SourceProperty, value);

    public static string? GetSource(TextBlock element) => element.GetValue(SourceProperty);

    public static void SetTip(Control element, string? value) => element.SetValue(TipProperty, value);

    public static string? GetTip(Control element) => element.GetValue(TipProperty);

    public static void ApplyTo(TextBlock block, string? source)
    {
        block.Text = null;
        block.Inlines?.Clear();
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        var inlines = block.Inlines
            ?? throw new InvalidOperationException("Text inlines were not created.");
        foreach (var (text, bold) in ControlLabelMarkup.Parse(source))
        {
            if (text.Length == 0)
            {
                continue;
            }

            var run = new Run(text);
            if (bold)
            {
                run.FontWeight = FontWeight.Bold;
            }

            inlines.Add(run);
        }
    }

    private static void OnSourceChanged(TextBlock block, AvaloniaPropertyChangedEventArgs e)
        => ApplyTo(block, e.NewValue as string);

    private static void OnTipChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text))
        {
            ToolTip.SetTip(control, null);
            return;
        }

        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        };
        ApplyTo(block, text);
        ToolTip.SetTip(control, block);
    }
}
