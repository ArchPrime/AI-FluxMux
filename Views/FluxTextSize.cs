using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// The app's text sizes for controls built in code. The values live once, in App.axaml
/// (<c>FluxFontSize*</c> resources), so code-built text follows the same scale as the views and a
/// change there applies everywhere — no control is tuned to a pixel size of its own.
/// </summary>
public static class FluxTextSize
{
    public const string Tiny = "FluxFontSizeTiny";
    public const string Small = "FluxFontSizeSmall";
    public const string Caption = "FluxFontSizeCaption";
    public const string Body = "FluxFontSizeBody";
    public const string Emphasis = "FluxFontSizeEmphasis";
    public const string Heading = "FluxFontSizeHeading";
    public const string Title = "FluxFontSizeTitle";

    /// <summary>Current value of a text-size resource; <paramref name="fallback"/> when the app
    /// resources are not loaded (unit tests build views without an Application).</summary>
    public static double Get(string key, double fallback)
        => Application.Current is { } app
           && app.TryFindResource(key, app.ActualThemeVariant, out var value)
           && value is double size
            ? size
            : fallback;

    /// <summary>Binds a control's font size to a text-size resource, so it follows later changes.</summary>
    public static T WithSize<T>(this T control, string key) where T : Control
    {
        control[!TextElement.FontSizeProperty] = control.GetResourceObservable(key).ToBinding();
        return control;
    }
}
