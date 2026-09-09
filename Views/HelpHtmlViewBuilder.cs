using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.Views;

public static class HelpHtmlViewBuilder
{
    private static readonly FontFamily CodeFont = new("Consolas, Courier New");
    private static Bitmap? _copyrightLogo;
    private static Cursor? _handCursor;
    private static readonly Dictionary<string, (DateTime Stamp, Bitmap Image)> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    // Reading measures. The window scales these through its own transform, so they stay
    // font-relative rather than pixel-tuned: prose is capped near 90 characters a line,
    // while tables and pictures may use the wider column.
    private const double BodyFontSize = 13;
    private const double BodyLineHeight = 19.5;
    private const double TableFontSize = 12;
    private const double ProseWidth = BodyFontSize * 46;
    private const double ColumnWidth = BodyFontSize * 68;

    // Space above a heading, which is what separates it from the topic that ended above.
    // A heading keeps far less space below it so it reads as attached to its own copy.
    private const double SectionGap = 52;
    private const double TopicGap = 40;
    private const double SubheadingGap = 16;

    // Paragraphs need a visible break between them or a long topic reads as one block.
    // List items sit closer together than paragraphs, so a list still reads as one thing.
    private const double ParagraphGap = 10;
    private const double ListItemGap = 4;

    // A section banner introduces the topic directly beneath it, so that one title
    // hugs the banner instead of opening the full between-topics gap.
    private const double TitleUnderSectionGap = 4;

    // The copyright logo is decoration beside the licence text, so it is bounded on both
    // edges. An unbounded one can widen the column it sits in and re-wrap the text.
    private const double LogoMaxEdge = 96;

    /// <summary>
    /// Colours read from the theme the Help tab is sitting in. The tab used to hard-code
    /// light greys, which left dark mode with a near-white table header. Brushes are looked
    /// up once against the panel rather than bound, because a link is an inline rather than
    /// a control; the window repopulates Help when the theme changes.
    /// </summary>
    private sealed class HelpTheme
    {
        public HelpTheme(StyledElement anchor)
        {
            var variant = VariantOf(anchor);
            Link = Find(anchor, variant, "FluxLinkForeground", "#0563C1");
            Heading = Find(anchor, variant, "FluxHeadingForeground", "#0F172A");
            Secondary = Find(anchor, variant, "FluxSecondaryForeground", "#334155");
            Border = Find(anchor, variant, "FluxMutedBorder", "#CBD5E1");
            Rule = Find(anchor, variant, "FluxCardBorder", "#D0D7DE");
            Muted = Find(anchor, variant, "FluxMutedBackground", "#F8FAFC");
            CodeFill = Find(anchor, variant, "FluxCodeFill", "#16000000");
            CalloutFill = Find(anchor, variant, "FluxTintAmberBackground", "#FFFBEB");
            CalloutEdge = Find(anchor, variant, "FluxTintAmberBorder", "#FDE68A");
        }

        public IBrush Link { get; }
        public IBrush Heading { get; }
        public IBrush Secondary { get; }
        public IBrush Border { get; }
        public IBrush Rule { get; }
        public IBrush Muted { get; }
        public IBrush CodeFill { get; }
        public IBrush CalloutFill { get; }
        public IBrush CalloutEdge { get; }

        /// <summary>
        /// The variant on screen. Asking for a resource without naming one resolves the
        /// light dictionary whenever the panel reports <see cref="ThemeVariant.Default"/>,
        /// which left dark mode with near-black headings and an invisible snippet tint.
        /// </summary>
        internal static ThemeVariant VariantOf(StyledElement anchor)
        {
            if (anchor.ActualThemeVariant is { } actual && actual != ThemeVariant.Default)
            {
                return actual;
            }

            var app = Application.Current?.ActualThemeVariant;
            return app is not null && app != ThemeVariant.Default ? app : ThemeVariant.Light;
        }

        private static IBrush Find(StyledElement anchor, ThemeVariant variant, string key, string fallback)
            => anchor.TryFindResource(key, variant, out var value) && value is IBrush brush
                ? brush
                : new SolidColorBrush(Color.Parse(fallback));
    }

    /// <summary>Everything a block needs to draw itself, so the walk keeps one parameter.</summary>
    private sealed record HelpRender(Action<string> OpenLink, string BaseDirectory, HelpTheme Theme);

    public static bool IsCopyrightHelpTopic(string? title)
        => !string.IsNullOrWhiteSpace(title)
           && title.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase);

    public static bool TrySplitProductTitle(string? title, out string name, out string version)
    {
        name = string.Empty;
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        const string product = "AI-FluxMux";
        if (!title.StartsWith(product, StringComparison.Ordinal))
        {
            return false;
        }

        name = product;
        version = title[product.Length..].Trim();
        return true;
    }

    public static void Populate(StackPanel panel, HelpDocument document, Action<string> openLink)
    {
        panel.Children.Clear();
        var render = new HelpRender(openLink, document.BaseDirectory, new HelpTheme(panel));
        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            panel.Children.Add(CreateProductTitle(document.Title, fontSize: 18));
        }

        foreach (var block in document.Preamble)
        {
            AddBlock(panel, block, render);
        }

        foreach (var topic in document.Topics)
        {
            foreach (var leadIn in topic.LeadIn)
            {
                AddBlock(panel, leadIn, render);
            }

            var topicPanel = new StackPanel();
            topicPanel.Classes.Add("helpTopic");
            topicPanel.Tag = topic.Title;
            var followsSection = topic.LeadIn.Count > 0
                && topic.LeadIn[^1] is HelpBannerBlock { Level: <= 1 };
            var titleBlock = new TextBlock
            {
                FontSize = BodyFontSize + 1.5,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, followsSection ? TitleUnderSectionGap : TopicGap, 0, 4),
                Foreground = render.Theme.Heading,
                Text = topic.Title,
                TextWrapping = TextWrapping.Wrap
            };
            Constrain(titleBlock, ProseWidth);
            var logo = IsCopyrightHelpTopic(topic.Title) ? TryGetCopyrightLogo() : null;
            if (logo is not null)
            {
                var body = new StackPanel();
                if (!string.IsNullOrWhiteSpace(document.Title))
                {
                    body.Children.Add(CreateProductTitle(document.Title, fontSize: 14));
                }
                body.Children.Add(titleBlock);
                foreach (var block in topic.Blocks)
                {
                    AddBlock(body, block, render);
                }

                var image = new Image { Source = logo, MaxHeight = 48 };
                image.Classes.Add("helpCopyrightLogo");
                LimitLogoHeightToText(image, body);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                Grid.SetColumn(image, 0);
                Grid.SetColumn(body, 1);
                row.Children.Add(image);
                row.Children.Add(body);
                topicPanel.Children.Add(row);
            }
            else
            {
                topicPanel.Children.Add(titleBlock);
                foreach (var block in topic.Blocks)
                {
                    AddBlock(topicPanel, block, render);
                }
            }

            panel.Children.Add(topicPanel);
        }
    }

    public static void ShowMessage(StackPanel panel, string message)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = message
        });
    }

    private static void AddBlock(Panel parent, HelpBlock block, HelpRender render)
    {
        switch (block)
        {
            case HelpBannerBlock banner:
                AddBanner(parent, banner, render.Theme);
                break;
            case HelpSeparatorBlock:
                parent.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 4) });
                break;
            case HelpPreBlock pre:
                parent.Children.Add(new Border
                {
                    Background = render.Theme.Muted,
                    BorderBrush = render.Theme.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(10, 7),
                    Margin = new Thickness(0, 2, 0, 8),
                    MaxWidth = ColumnWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        FontFamily = CodeFont,
                        FontSize = TableFontSize,
                        TextWrapping = TextWrapping.Wrap,
                        Text = pre.Text
                    }
                });
                break;
            case HelpListBlock list:
                // One container per list, so the gap between items stays tighter than
                // the gap between the list and the paragraphs around it.
                var listPanel = new StackPanel { Margin = new Thickness(0, 1, 0, ParagraphGap - ListItemGap) };
                for (var i = 0; i < list.Items.Count; i++)
                {
                    listPanel.Children.Add(CreateListItem(list, i, render));
                }

                parent.Children.Add(listPanel);
                break;
            case HelpParagraphBlock paragraph:
                var text = CreateInlineBlock(string.Empty, paragraph.Inlines, render);
                text.Margin = new Thickness(0, 0, 0, ParagraphGap);
                Constrain(text, ProseWidth);
                parent.Children.Add(text);
                break;
            case HelpTableBlock table:
                parent.Children.Add(CreateTable(table, render));
                break;
            case HelpImageBlock image:
                AddImage(parent, image, render);
                break;
            case HelpCalloutBlock callout:
                AddCallout(parent, callout, render);
                break;
        }
    }

    /// <summary>
    /// Gives the three heading levels distinct weight. Every heading used to render bold at
    /// body size, so a section banner and a subheading looked identical while scrolling.
    /// Space above a heading exceeds the space below it, so it groups with what it introduces.
    /// </summary>
    private static void AddBanner(Panel parent, HelpBannerBlock banner, HelpTheme theme)
    {
        var isSection = banner.Level <= 1;
        var heading = new TextBlock
        {
            FontSize = isSection ? BodyFontSize + 3 : BodyFontSize + 0.5,
            FontWeight = isSection ? FontWeight.Bold : FontWeight.SemiBold,
            Foreground = theme.Heading,
            Margin = new Thickness(0, isSection ? SectionGap : SubheadingGap, 0, isSection ? 3 : 1),
            Text = banner.Text,
            TextWrapping = TextWrapping.Wrap
        };
        Constrain(heading, ProseWidth);
        if (isSection)
        {
            heading.Classes.Add("helpSection");
        }

        parent.Children.Add(heading);
        if (!isSection)
        {
            return;
        }

        parent.Children.Add(new Border
        {
            Background = theme.Rule,
            Height = 1,
            Margin = new Thickness(0, 0, 0, 6),
            MaxWidth = ColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        });
    }

    /// <summary>
    /// Lays the marker beside the text rather than inside it, so a wrapped line aligns
    /// under the first word instead of under the bullet.
    /// </summary>
    private static Control CreateListItem(HelpListBlock list, int index, HelpRender render)
    {
        var row = new Grid
        {
            Margin = new Thickness(2, 0, 0, ListItemGap),
            MaxWidth = ProseWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        var marker = new TextBlock
        {
            FontSize = BodyFontSize,
            Foreground = render.Theme.Secondary,
            LineHeight = BodyLineHeight,
            Margin = new Thickness(0, 0, 7, 0),
            MinWidth = list.Ordered ? BodyFontSize * 1.5 : 0,
            Text = list.Ordered ? (index + 1) + "." : "•",
            TextAlignment = list.Ordered ? TextAlignment.Right : TextAlignment.Left
        };

        var content = CreateInlineBlock(string.Empty, list.Items[index], render);
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(content, 1);
        row.Children.Add(marker);
        row.Children.Add(content);
        return row;
    }

    /// <summary>
    /// Draws a Word Quote paragraph as a tinted box, for the short warnings an operator
    /// should not skim past.
    /// </summary>
    private static void AddCallout(Panel parent, HelpCalloutBlock callout, HelpRender render)
    {
        var body = new StackPanel();
        foreach (var block in callout.Blocks)
        {
            AddBlock(body, block, render);
        }

        parent.Children.Add(new Border
        {
            Background = render.Theme.CalloutFill,
            BorderBrush = render.Theme.CalloutEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 9, 12, 8),
            Margin = new Thickness(0, 6, 0, 10),
            MaxWidth = ProseWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = body
        });
    }

    /// <summary>
    /// Shows a picture saved beside Help.html. Word writes those into a companion folder,
    /// so the path is resolved against the copy of Help.html that actually loaded. A picture
    /// is never enlarged past its own size, and alt text becomes a caption.
    /// </summary>
    private static void AddImage(Panel parent, HelpImageBlock block, HelpRender render)
    {
        var bitmap = TryLoadImage(block.Source, render.BaseDirectory);
        if (bitmap is null)
        {
            return;
        }

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = Math.Min(bitmap.Size.Width, ColumnWidth),
            Margin = new Thickness(0, 6, 0, block.Alt.Length > 0 ? 2 : 10)
        };
        parent.Children.Add(image);
        if (block.Alt.Length == 0)
        {
            return;
        }

        parent.Children.Add(new TextBlock
        {
            FontSize = TableFontSize,
            FontStyle = FontStyle.Italic,
            Foreground = render.Theme.Secondary,
            Margin = new Thickness(0, 0, 0, 10),
            MaxWidth = ProseWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = block.Alt,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>
    /// Looks beside the Help.html that loaded, then beside the exe. An update saved to the
    /// per-user folder carries no pictures of its own, so it falls back to the shipped ones.
    /// </summary>
    private static Bitmap? TryLoadImage(string source, string baseDirectory)
    {
        return TryLoadImageFrom(source, baseDirectory)
            ?? TryLoadImageFrom(source, AppContext.BaseDirectory);
    }

    private static Bitmap? TryLoadImageFrom(string source, string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            // Stay inside that folder, so a hand-edited src cannot reach elsewhere
            // on the operator's PC.
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, source));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(full);
            if (ImageCache.TryGetValue(full, out var cached) && cached.Stamp == stamp)
            {
                return cached.Image;
            }

            var bitmap = new Bitmap(full);
            ImageCache[full] = (stamp, bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>One shared cursor for every linked paragraph on the page.</summary>
    private static Cursor? HandCursor()
    {
        if (_handCursor is not null)
        {
            return _handCursor;
        }

        try
        {
            _handCursor = new Cursor(StandardCursorType.Hand);
        }
        catch (Exception)
        {
            return null;
        }

        return _handCursor;
    }

    private static void Constrain(Control control, double width)
    {
        control.MaxWidth = width;
        control.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private static Control CreateTable(HelpTableBlock table, HelpRender render)
    {
        var columnCount = table.Rows.Count == 0
            ? 0
            : table.Rows.Max(row => row.Count);
        var grid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 12),
            MaxWidth = ColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                column == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star)));
        }

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = table.Rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var content = CreateInlineBlock(
                    string.Empty,
                    row[columnIndex],
                    render,
                    rowIndex == 0);
                content.SetValue(TextBlock.FontSizeProperty, TableFontSize);
                var cell = new Border
                {
                    Background = rowIndex == 0 ? render.Theme.Muted : Brushes.Transparent,
                    BorderBrush = render.Theme.Border,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(7, 5),
                    Child = content
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, columnIndex);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private static Control CreateInlineBlock(
        string prefix,
        IReadOnlyList<HelpInline> inlines,
        HelpRender render,
        bool forceBold = false)
    {
        if (!NeedsRichText(inlines, forceBold))
        {
            return new TextBlock
            {
                FontSize = BodyFontSize,
                LineHeight = BodyLineHeight,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = forceBold ? FontWeight.Bold : FontWeight.Normal,
                Text = prefix + JoinText(inlines)
            };
        }

        return CreateRichTextBlock(prefix, inlines, render, forceBold);
    }

    private static Control CreateRichTextBlock(
        string prefix,
        IReadOnlyList<HelpInline> inlines,
        HelpRender render,
        bool forceBold)
    {
        var textBlock = new TextBlock
        {
            FontSize = BodyFontSize,
            LineHeight = BodyLineHeight,
            TextWrapping = TextWrapping.Wrap
        };
        var inlineCollection = textBlock.Inlines
            ?? throw new InvalidOperationException("Help text inlines were not created.");
        var links = new List<(int Start, int Length, string Href)>();
        var position = 0;

        void AddText(string text, bool bold, bool italic, bool code, string? href)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (text == "\n")
            {
                inlineCollection.Add(new LineBreak());
                position += 1;
                return;
            }

            text = ProtectRunWhitespace(text);
            if (code)
            {
                // A run cannot carry padding, so thin spaces keep the tint off the glyphs.
                text = '\u2009' + text + '\u2009';
            }

            var run = new Run(text)
            {
                BaselineAlignment = BaselineAlignment.Baseline
            };
            if (bold || forceBold)
            {
                run.FontWeight = FontWeight.Bold;
            }

            if (italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (code)
            {
                // Consolas at body size reads almost like the prose around it, so the
                // tint is what actually marks a path or a literal as one.
                run.FontFamily = CodeFont;
                run.FontSize = BodyFontSize * 0.94;
                run.Background = render.Theme.CodeFill;
            }

            if (!string.IsNullOrWhiteSpace(href))
            {
                run.Foreground = render.Theme.Link;
                links.Add((position, text.Length, href));
            }

            inlineCollection.Add(run);
            position += text.Length;
        }

        if (prefix.Length > 0)
        {
            AddText(prefix, forceBold, italic: false, code: false, null);
        }

        foreach (var inline in inlines)
        {
            AddText(inline.Text, inline.Bold, inline.Italic, inline.Code, inline.Href);
        }

        if (links.Count > 0)
        {
            textBlock.Cursor = HandCursor();
            textBlock.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(textBlock).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                var hit = textBlock.TextLayout.HitTestPoint(e.GetPosition(textBlock));
                var index = hit.TextPosition;
                foreach (var link in links)
                {
                    if (index < link.Start || index >= link.Start + link.Length)
                    {
                        continue;
                    }

                    render.OpenLink(link.Href);
                    e.Handled = true;
                    return;
                }
            };
        }

        return textBlock;
    }

    private static bool NeedsRichText(IReadOnlyList<HelpInline> inlines, bool forceBold)
    {
        foreach (var inline in inlines)
        {
            if (inline.Href is not null
                || inline.Italic
                || inline.Code
                || (inline.Bold && !forceBold))
            {
                return true;
            }
        }

        return false;
    }

    private static string JoinText(IReadOnlyList<HelpInline> inlines)
    {
        var text = string.Empty;
        foreach (var inline in inlines)
        {
            text += inline.Text;
        }

        return text;
    }

    private static string ProtectRunWhitespace(string text)
    {
        if (text.Length == 0 || text == "\n")
        {
            return text;
        }

        if (text.Trim().Length == 0)
        {
            return "\u200B" + text + "\u200B";
        }

        if (char.IsWhiteSpace(text[0]))
        {
            text = "\u200B" + text;
        }

        if (char.IsWhiteSpace(text[^1]))
        {
            text += "\u200B";
        }

        return text;
    }

    private static Control CreateProductTitle(string title, double fontSize)
    {
        var textBlock = new TextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap
        };
        var inlines = textBlock.Inlines
            ?? throw new InvalidOperationException("Help text inlines were not created.");
        if (TrySplitProductTitle(title, out var name, out var version))
        {
            inlines.Add(new Run(name) { FontWeight = FontWeight.SemiBold });
            if (version.Length > 0)
            {
                inlines.Add(new Run("  "));
                inlines.Add(new Run(version) { FontWeight = FontWeight.Normal });
            }

            return textBlock;
        }

        textBlock.FontWeight = FontWeight.Bold;
        textBlock.Text = title;
        return textBlock;
    }

    /// <summary>
    /// Keeps the logo from towering over the copyright text beside it. The width is capped
    /// independently of the height: the logo sits in the Auto column next to the text, and
    /// a Uniform stretch makes a taller logo a wider one, so letting height drive width let
    /// each layout pass narrow the text, wrap it longer, and raise the height again until
    /// Avalonia reported an infinite layout loop.
    /// </summary>
    private static void LimitLogoHeightToText(Image image, Control text)
    {
        image.MaxWidth = LogoMaxEdge;

        void Sync(object? sender, EventArgs e)
        {
            var height = Math.Min(text.DesiredSize.Height, LogoMaxEdge);
            if (height > 1 && (double.IsNaN(image.MaxHeight) || Math.Abs(image.MaxHeight - height) > 0.5))
            {
                image.MaxHeight = height;
            }
        }

        text.LayoutUpdated += Sync;
    }

    private static Bitmap? TryGetCopyrightLogo()
    {
        if (_copyrightLogo is not null)
        {
            return _copyrightLogo;
        }

        try
        {
            _copyrightLogo = new Bitmap(
                AssetLoader.Open(new Uri("avares://FluxMux.Avalonia/Assets/Logos/AI-FluxMux Logo.png")));
            return _copyrightLogo;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
