using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Views;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HelpHtmlViewBuilderTests
{
    [Fact]
    public void Heading_levels_are_drawn_at_different_sizes()
    {
        var panel = Render("""
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>What AI-FluxMux does</h2>
            <p>Intro.</p>
            <h1>Setting up</h1>
            <h2>Servers</h2>
            <p>Copy.</p>
            <h3>Fit and memory</h3>
            <p>More copy.</p>
            </body></html>
            """);

        var section = Headings(panel).Single(block => block.Text == "Setting up");
        var topic = Headings(panel).Single(block => block.Text == "Servers");
        var sub = Headings(panel).Single(block => block.Text == "Fit and memory");
        var body = Headings(panel).Single(block => block.Text == "Copy.");

        Assert.True(section.FontSize > topic.FontSize);
        Assert.True(topic.FontSize > sub.FontSize);
        Assert.True(sub.FontSize > body.FontSize);
    }

    [Fact]
    public void A_new_topic_opens_further_below_than_a_subheading()
    {
        var panel = Render("""
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>First</h2>
            <p>One.</p>
            <h3>Detail</h3>
            <p>Two.</p>
            <h2>Second</h2>
            <p>Three.</p>
            </body></html>
            """);

        var second = Headings(panel).Single(block => block.Text == "Second");
        var sub = Headings(panel).Single(block => block.Text == "Detail");
        var body = Headings(panel).Single(block => block.Text == "Three.");

        Assert.True(second.Margin.Top > sub.Margin.Top);
        Assert.True(second.Margin.Top > second.Margin.Bottom * 4);
        Assert.True(body.Margin.Top < second.Margin.Top);
    }

    [Fact]
    public void A_title_under_a_section_banner_stays_with_it()
    {
        var panel = Render("""
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>First</h2>
            <p>One.</p>
            <h1>Setting up</h1>
            <h2>Servers</h2>
            <p>Two.</p>
            <h2>Model profiles</h2>
            <p>Three.</p>
            </body></html>
            """);

        var underBanner = Headings(panel).Single(block => block.Text == "Servers");
        var betweenTopics = Headings(panel).Single(block => block.Text == "Model profiles");
        Assert.True(underBanner.Margin.Top < betweenTopics.Margin.Top);
    }

    [Fact]
    public void Prose_is_capped_narrower_than_a_table()
    {
        var panel = Render("""
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Servers</h2>
            <p>Copy that would otherwise run the whole width of the window.</p>
            <table><tr><th>Setting</th><th>Meaning</th></tr><tr><td>Port</td><td>5001</td></tr></table>
            </body></html>
            """);

        var paragraph = Headings(panel).Single(block => block.Text?.StartsWith("Copy that") == true);
        var table = Descendants(panel).OfType<Grid>().First(grid => grid.RowDefinitions.Count == 2);

        Assert.True(double.IsFinite(paragraph.MaxWidth));
        Assert.True(table.MaxWidth > paragraph.MaxWidth);
    }

    [Fact]
    public void A_bullet_sits_beside_its_text_so_wrapped_lines_line_up()
    {
        var panel = Render("""
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Servers</h2>
            <ul><li>First point.</li><li>Second point.</li></ul>
            </body></html>
            """);

        var row = Descendants(panel)
            .OfType<Grid>()
            .First(grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 0);
        var marker = row.Children.OfType<TextBlock>().First(block => block.Text == "•");
        var text = row.Children.OfType<TextBlock>().First(block => block.Text == "First point.");

        Assert.Equal(0, Grid.GetColumn(marker));
        Assert.Equal(1, Grid.GetColumn(text));
    }

    [Fact]
    public void Paragraphs_are_separated_more_than_the_items_of_one_list()
    {
        var panel = Render("""
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Servers</h2>
            <p>First paragraph.</p>
            <p>Second paragraph.</p>
            <ul><li>First point.</li><li>Second point.</li></ul>
            </body></html>
            """);

        var paragraph = Headings(panel).Single(block => block.Text == "First paragraph.");
        var row = Descendants(panel)
            .OfType<Grid>()
            .First(grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 0);

        Assert.True(paragraph.Margin.Bottom > row.Margin.Bottom);
    }

    [Fact]
    public void A_path_or_literal_is_tinted_so_it_does_not_read_as_prose()
    {
        var panel = new StackPanel();
        panel.Resources.Add("FluxCodeFill", new SolidColorBrush(Colors.Magenta));
        HelpHtmlViewBuilder.Populate(
            panel,
            HelpHtmlParser.Parse("""
                <html><body>
                <h1>AI-FluxMux v0.2</h1>
                <h2>Servers</h2>
                <p>Edit <code>fluxmux_config.json</code> by hand.</p>
                </body></html>
                """),
            _ => { });

        var runs = Descendants(panel)
            .OfType<TextBlock>()
            .SelectMany(block => block.Inlines ?? [])
            .OfType<Run>()
            .ToList();
        var code = runs.Single(item => item.Text?.Contains("fluxmux_config.json") == true);
        var prose = runs.Single(item => item.Text?.StartsWith("Edit") == true);

        var fill = Assert.IsType<SolidColorBrush>(code.Background);
        Assert.Equal(Colors.Magenta, fill.Color);
        Assert.Null(prose.Background);
    }

    [Fact]
    public void Dark_mode_reads_the_dark_dictionary_rather_than_the_light_one()
    {
        var panel = new StackPanel();
        panel.Resources.ThemeDictionaries[ThemeVariant.Light] =
            new ResourceDictionary { ["FluxHeadingForeground"] = new SolidColorBrush(Colors.Black) };
        panel.Resources.ThemeDictionaries[ThemeVariant.Dark] =
            new ResourceDictionary { ["FluxHeadingForeground"] = new SolidColorBrush(Colors.White) };
        // Asking for a resource without naming a variant reads the light dictionary even
        // here, where the panel itself reports dark, which is what faded the headings.
        _ = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = panel };
        Assert.Equal(ThemeVariant.Dark, panel.ActualThemeVariant);

        HelpHtmlViewBuilder.Populate(
            panel,
            HelpHtmlParser.Parse("""
                <html><body>
                <h1>AI-FluxMux v0.2</h1>
                <h2>Servers</h2>
                <p>Copy.</p>
                <h1>Cloud models</h1>
                <h2>Keys</h2>
                <p>More copy.</p>
                </body></html>
                """),
            _ => { });

        var banner = Headings(panel).First(block => block.Text == "Cloud models");
        var brush = Assert.IsType<SolidColorBrush>(banner.Foreground);
        Assert.Equal(Colors.White, brush.Color);
    }

    [Fact]
    public void The_snippet_tint_is_translucent_in_both_themes()
    {
        // An opaque tint disappeared in dark mode, because the value picked there was
        // close to the backdrop behind the Help tab. A translucent one cannot.
        var app = FindAppXaml();
        var fills = Regex.Matches(app, """FluxCodeFill"\s+Color="#([0-9A-Fa-f]{8})""");

        Assert.Equal(2, fills.Count);
        foreach (var fill in fills)
        {
            var alpha = byte.Parse(((Match)fill).Groups[1].Value[..2], System.Globalization.NumberStyles.HexNumber);
            Assert.InRange(alpha, (byte)1, (byte)254);
        }
    }

    [Fact]
    public void Link_colour_comes_from_the_theme_rather_than_a_fixed_grey()
    {
        var panel = new StackPanel();
        panel.Resources.Add("FluxLinkForeground", new SolidColorBrush(Colors.Magenta));
        HelpHtmlViewBuilder.Populate(
            panel,
            HelpHtmlParser.Parse("""
                <html><body>
                <h1>AI-FluxMux v0.2</h1>
                <h2>Servers</h2>
                <p>See <a href="https://example.com">the guide</a>.</p>
                </body></html>
                """),
            _ => { });

        var run = Descendants(panel)
            .OfType<TextBlock>()
            .SelectMany(block => block.Inlines ?? [])
            .OfType<Run>()
            .Single(item => item.Text == "the guide");

        var brush = Assert.IsType<SolidColorBrush>(run.Foreground);
        Assert.Equal(Colors.Magenta, brush.Color);
    }

    [Fact]
    public void A_table_header_is_tinted_from_the_theme()
    {
        var panel = new StackPanel();
        panel.Resources.Add("FluxMutedBackground", new SolidColorBrush(Colors.Magenta));
        HelpHtmlViewBuilder.Populate(
            panel,
            HelpHtmlParser.Parse("""
                <html><body>
                <h1>AI-FluxMux v0.2</h1>
                <h2>Servers</h2>
                <table><tr><th>Setting</th></tr><tr><td>Port</td></tr></table>
                </body></html>
                """),
            _ => { });

        var cells = Descendants(panel).OfType<Border>().ToList();
        var header = Assert.IsType<SolidColorBrush>(cells[0].Background);
        Assert.Equal(Colors.Magenta, header.Color);
        Assert.Equal(Brushes.Transparent, cells[1].Background);
    }

    private static string FindAppXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "App.axaml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        Assert.Fail("App.axaml should be somewhere above the test host.");
        return string.Empty;
    }

    [Fact]
    public void The_copyright_logo_cannot_widen_the_column_it_sits_in()
    {
        // Its height follows the text beside it, and a Uniform stretch turns height into
        // width. Without a width cap each layout pass narrows the text, wraps it longer,
        // and grows the logo again, which Avalonia ends as an infinite layout loop.
        var panel = Render("""
            <html><body>
            <h2>Copyright and licence</h2>
            <p>Copyright (c) 2026 by Paul King.</p>
            </body></html>
            """);

        var logo = Descendants(panel).OfType<Image>().SingleOrDefault();
        if (logo is null)
        {
            return; // The asset is not reachable in this test host.
        }

        Assert.False(double.IsNaN(logo.MaxWidth));
        Assert.False(double.IsInfinity(logo.MaxWidth));
        Assert.True(logo.MaxWidth <= 96);
    }

    private static StackPanel Render(string html)
    {
        var panel = new StackPanel();
        HelpHtmlViewBuilder.Populate(panel, HelpHtmlParser.Parse(html), _ => { });
        return panel;
    }

    private static System.Collections.Generic.IEnumerable<TextBlock> Headings(StackPanel panel)
        => Descendants(panel).OfType<TextBlock>();

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
    {
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                yield return child;
                foreach (var nested in Descendants(child))
                {
                    yield return nested;
                }
            }
        }
        else if (root is Border { Child: { } inner })
        {
            yield return inner;
            foreach (var nested in Descendants(inner))
            {
                yield return nested;
            }
        }
    }
}
