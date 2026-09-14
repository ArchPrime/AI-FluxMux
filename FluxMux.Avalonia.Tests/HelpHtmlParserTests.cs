using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Views;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HelpHtmlParserTests
{
    [Fact]
    public void Heading_2_becomes_index_topics()
    {
        const string html = """
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>What AI-FluxMux does</h2>
            <p>Router copy.</p>
            <h2>llama-server</h2>
            <p>Install it.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        Assert.Equal("AI-FluxMux v0.2", document.Title);
        Assert.Equal(["What AI-FluxMux does", "llama-server"], Titles(document));
    }

    [Fact]
    public void Extra_heading_1_is_a_banner_not_an_index_topic()
    {
        const string html = """
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>First</h2>
            <p>One.</p>
            <h1>Setting up</h1>
            <h2>Second</h2>
            <p>Two.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        Assert.Equal(["First", "Second"], Titles(document));
        Assert.Contains(document.Topics[1].LeadIn, block => block is HelpBannerBlock banner && banner.Text == "Setting up");
    }

    [Fact]
    public void Heading_levels_are_kept_so_the_view_can_size_them()
    {
        const string html = """
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
            """;

        var document = HelpHtmlParser.Parse(html);
        var section = document.Topics[1].LeadIn.OfType<HelpBannerBlock>().Single();
        Assert.Equal("Setting up", section.Text);
        Assert.Equal(1, section.Level);

        var sub = document.Topics[1].Blocks.OfType<HelpBannerBlock>().Single();
        Assert.Equal("Fit and memory", sub.Text);
        Assert.Equal(3, sub.Level);
    }

    [Fact]
    public void A_topic_id_heading_is_not_shown_and_survives_a_title_rename()
    {
        const string html = """
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Forwarding rules</h2>
            <h3>topic.port_rules</h3>
            <p>Watch the pack.</p>
            <h3>What Compact does</h3>
            <p>Shorten older turns.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var topic = Assert.Single(document.Topics);
        Assert.Equal("Forwarding rules", topic.Title);
        Assert.Equal(HelpTopicIds.PortRules, topic.Id);
        Assert.DoesNotContain(
            topic.Blocks,
            block => block is HelpBannerBlock banner && banner.Text.Contains("topic.port_rules", StringComparison.Ordinal));
        var sub = Assert.Single(topic.Blocks.OfType<HelpBannerBlock>());
        Assert.Equal("What Compact does", sub.Text);

        var entries = new[] { new HelpTopicEntry { Title = topic.Title, Id = topic.Id } };
        Assert.Equal(topic.Title, HelpTopicSearch.FindByReference(entries, HelpTopicIds.PortRules)?.Title);
    }

    [Fact]
    public void Quote_paragraph_becomes_a_callout()
    {
        const string html = """
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Servers</h2>
            <blockquote><p>Do not point the Client app at llama-server.</p></blockquote>
            <p>Ordinary copy.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var callout = document.Topics[0].Blocks.OfType<HelpCalloutBlock>().Single();
        var paragraph = Assert.IsType<HelpParagraphBlock>(callout.Blocks.Single());
        Assert.Equal("Do not point the Client app at llama-server.", VisibleText(paragraph.Inlines));
        Assert.Contains(document.Topics[0].Blocks, block => block is HelpParagraphBlock);
    }

    [Fact]
    public void Word_picture_becomes_an_image_with_its_caption()
    {
        const string html = """
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Servers</h2>
            <p class=MsoNormal><img width=600 height=320 src="Help_files/image001.png" alt="The Servers tab"></p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var image = document.Topics[0].Blocks.OfType<HelpImageBlock>().Single();
        Assert.Equal(Path.Combine("Help_files", "image001.png"), image.Source);
        Assert.Equal("The Servers tab", image.Alt);
    }

    [Fact]
    public void Remote_and_inline_pictures_are_ignored()
    {
        const string html = """
            <html><body>
            <h1>AI-FluxMux v0.2</h1>
            <h2>Servers</h2>
            <p><img src="https://example.com/shot.png"></p>
            <p><img src="data:image/png;base64,AAAA"></p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        Assert.Empty(document.Topics[0].Blocks.OfType<HelpImageBlock>());
    }

    [Fact]
    public void Word_span_inside_heading_2_still_indexes()
    {
        const string html = """
            <html><body>
            <div class=WordSection1>
            <h2><span style='font-family:Calibri'>Route requests</span></h2>
            <p class=MsoNormal>Ready models answer immediately.</p>
            </div>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        Assert.Equal(["Route requests"], Titles(document));
    }

    [Fact]
    public void Changing_heading_2_changes_the_index()
    {
        const string before = """
            <html><body>
            <h2>Old topic</h2><p>Keep.</p>
            <h2>Drop me</h2><p>Gone.</p>
            </body></html>
            """;
        const string after = """
            <html><body>
            <h2>Old topic</h2><p>Keep.</p>
            <h2>New topic</h2><p>Added.</p>
            </body></html>
            """;

        Assert.Equal(["Old topic", "Drop me"], Titles(HelpHtmlParser.Parse(before)));
        Assert.Equal(["Old topic", "New topic"], Titles(HelpHtmlParser.Parse(after)));
    }

    [Fact]
    public void Strong_marks_control_names_as_bold_inlines()
    {
        const string html = """
            <html><body>
            <h2>Route</h2>
            <p>Choose <strong>Continue waiting</strong> to keep this turn open.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var paragraph = Assert.IsType<HelpParagraphBlock>(Assert.Single(document.Topics).Blocks[0]);
        Assert.Contains(paragraph.Inlines, inline => inline.Bold && inline.Text == "Continue waiting");
        Assert.Contains(paragraph.Inlines, inline => !inline.Bold && inline.Text.Contains("Choose", StringComparison.Ordinal));
    }

    [Fact]
    public void Emphasis_and_monospace_parse_as_their_own_styles()
    {
        const string html = """
            <html><body>
            <h2>Route</h2>
            <p><em>Install it first.</em> Put the key in <code>fluxmux_secrets.json</code>, then click <strong>Launch</strong>.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var paragraph = Assert.IsType<HelpParagraphBlock>(Assert.Single(document.Topics).Blocks[0]);
        var lead = Assert.Single(paragraph.Inlines, inline => inline.Text.StartsWith("Install it first", StringComparison.Ordinal));
        Assert.True(lead.Italic);
        Assert.False(lead.Bold);
        Assert.False(lead.Code);

        var file = Assert.Single(paragraph.Inlines, inline => inline.Text == "fluxmux_secrets.json");
        Assert.True(file.Code);
        Assert.False(file.Bold);

        var control = Assert.Single(paragraph.Inlines, inline => inline.Text == "Launch");
        Assert.True(control.Bold);
        Assert.False(control.Italic);
    }

    [Fact]
    public void Word_character_formatting_maps_to_the_same_styles()
    {
        // "Web Page, Filtered" writes runs as spans with inline styles rather than semantic tags.
        const string html = """
            <html><body>
            <h2>Route</h2>
            <p><span style='font-style:italic'>Back up</span>
            <span style='font-family:"Consolas",serif'>fluxmux_config.json</span>
            <span style='font-weight:bold'>Save profile</span></p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var paragraph = Assert.IsType<HelpParagraphBlock>(Assert.Single(document.Topics).Blocks[0]);
        Assert.True(Assert.Single(paragraph.Inlines, inline => inline.Text.Trim() == "Back up").Italic);
        Assert.True(Assert.Single(paragraph.Inlines, inline => inline.Text.Trim() == "fluxmux_config.json").Code);
        Assert.True(Assert.Single(paragraph.Inlines, inline => inline.Text.Trim() == "Save profile").Bold);
    }

    [Fact]
    public void Shipped_help_html_keeps_bold_for_control_names_only()
    {
        // Bold means "a control the operator clicks". Emphasis and run-in lead-ins are
        // italic, so a bold run that closes with a colon or a full stop is the old
        // bold-as-emphasis habit creeping back. The routing note is the one carve-out:
        // that copy is rendered by MarkedText, which can only draw bold.
        string[] noteRunInLabels =
        [
            "Dynamic Model Routing.",
            "Instant Switching:",
            "Local-to-Local Delay:",
            "Smart Economy:"
        ];

        var html = File.ReadAllText(FindShippedHelp());
        var sentenceLike = System.Text.RegularExpressions.Regex
            .Matches(html, "<strong>(.*?)</strong>")
            .Select(match => match.Groups[1].Value)
            .Where(label => label.EndsWith(':') || label.EndsWith('.'))
            .Distinct()
            .Except(noteRunInLabels)
            .ToArray();

        Assert.Empty(sentenceLike);
        Assert.Contains("<em>", html, StringComparison.Ordinal);
        Assert.Contains("<code>", html, StringComparison.Ordinal);
        Assert.Contains("Never use bold for emphasis", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_help_html_explains_the_word_features_it_renders()
    {
        // A callout and a screenshot only reach the Help tab through the Quote style and
        // Alt Text, and a picture folder that travels with the file. If the prompt stops
        // saying so, the next editor cannot reach either.
        var html = File.ReadAllText(FindShippedHelp());
        Assert.True(IsBold(html, "Quote"), "The prompt should still name the Quote style.");
        Assert.True(IsBold(html, "Alt Text"), "The prompt should still name Alt Text.");
        Assert.Contains("companion folder", VisibleText(html), StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_name_is_read_as_bold_through_word_markup()
    {
        const string wordish =
            "<style>p {font-size:11.0pt;}</style>"
            + "<p><strong><span style='font-family:\"Calibri\",sans-serif'>Keep current model — end this\r\nturn</span></strong>"
            + " then plain <span lang=EN>Continue waiting</span> and a folder&nbsp;name</p>";

        Assert.True(IsBold(wordish, "Keep current model — end this turn"));
        Assert.False(IsBold(wordish, "Continue waiting"));
        Assert.Contains("then plain Continue waiting and a folder name", VisibleText(wordish), StringComparison.Ordinal);
        Assert.DoesNotContain("font-size", VisibleText(wordish), StringComparison.Ordinal);
    }

    [Fact]
    public void Links_and_lists_and_preformatted_yaml_parse()
    {
        const string html = """
            <html><body>
            <h2>llama-server</h2>
            <ul>
              <li>Get it from <a href="https://example.com/b">GitHub</a>.</li>
            </ul>
            <pre>baseURL: http://127.0.0.1:5001/v1
            model: local</pre>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var topic = Assert.Single(document.Topics);
        var list = Assert.IsType<HelpListBlock>(topic.Blocks[0]);
        Assert.Contains(list.Items[0], inline => inline.Href == "https://example.com/b");
        Assert.Equal(
            "Get it from GitHub.",
            VisibleText(list.Items[0]));
        var pre = Assert.IsType<HelpPreBlock>(topic.Blocks[1]);
        Assert.Contains("127.0.0.1:5001", pre.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Interface_notes_heading_is_not_an_index_topic()
    {
        const string html = """
            <html><body>
            <h2>What AI-FluxMux does</h2>
            <p>Router copy.</p>
            <h2>Interface notes</h2>
            <h3>quickselect.hot_routing — Dynamic Model Routing</h3>
            <p><strong>Instant Switching:</strong> hop in the same chat.</p>
            <h2>Copyright and licence</h2>
            <p>Copyright.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        Assert.Equal(["What AI-FluxMux does", "Copyright and licence"], Titles(document));
        Assert.Equal(
            "**Instant Switching:** hop in the same chat.",
            document.UiNotes["quickselect.hot_routing"]);
        Assert.True(HelpHtmlParser.TryParseUiNoteId("quickselect.hot_routing — title", out var id));
        Assert.Equal("quickselect.hot_routing", id);
        HelpUiNotes.Publish(document);
        Assert.Equal(
            "**Instant Switching:** hop in the same chat.",
            HelpUiNotes.Resolve("quickselect.hot_routing", "fallback"));
        Assert.Equal("fallback", HelpUiNotes.Resolve("no.such.note", "fallback"));
    }

    [Fact]
    public void A_note_written_as_several_points_keeps_a_line_each()
    {
        // Run together into one paragraph, a note of this shape reads as a wall of text.
        const string html = """
            <html><body>
            <h2>Interface notes</h2>
            <h3>servers.cline_hint</h3>
            <p>Settings first.</p>
            <ul><li>Base URL.</li><li>Model id.</li></ul>
            </body></html>
            """;

        var note = HelpHtmlParser.Parse(html).UiNotes["servers.cline_hint"];
        Assert.Equal("Settings first.\n• Base URL.\n• Model id.", note);
    }

    [Fact]
    public void A_note_can_name_a_live_value_the_app_fills_in()
    {
        HelpUiNotes.Publish(HelpHtmlParser.Parse("""
            <html><body>
            <h2>Interface notes</h2>
            <h3>servers.cline_hint</h3>
            <p>Base URL {Address}, and this profile is {ProfileContext}.</p>
            </body></html>
            """));

        HelpUiNotes.SetValue("Address", "http://127.0.0.1:5001");
        HelpUiNotes.SetValue("ProfileContext", "213056");

        Assert.Equal(
            "Base URL http://127.0.0.1:5001, and this profile is 213056.",
            HelpUiNotes.Resolve("servers.cline_hint", "fallback"));
    }

    [Fact]
    public void A_value_the_app_does_not_know_is_left_as_it_was_typed()
    {
        // A blank would look like a missing setting; the name shows the typo instead.
        HelpUiNotes.Publish(HelpHtmlParser.Parse("""
            <html><body>
            <h2>Interface notes</h2>
            <h3>servers.cline_hint</h3>
            <p>Port {Portt} is not a name.</p>
            </body></html>
            """));

        Assert.Equal("Port {Portt} is not a name.", HelpUiNotes.Resolve("servers.cline_hint", "fallback"));
    }

    [Fact]
    public void Spaces_around_links_are_kept()
    {
        const string html = """
            <html><body>
            <h2>Copyright and licence</h2>
            <p>Architecture Prime Ltd | <a href="https://www.prime.net.nz">https://www.prime.net.nz</a></p>
            <p>Licensing: <a href="https://polyformproject.org/licenses/noncommercial/1.0.0">PolyForm Noncommercial License 1.0.0</a> License applies.</p>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var blocks = document.Topics[0].Blocks.OfType<HelpParagraphBlock>().ToList();
        Assert.Equal(
            "Architecture Prime Ltd | https://www.prime.net.nz",
            VisibleText(blocks[0].Inlines));
        Assert.Contains('\u00a0', string.Concat(blocks[0].Inlines.Select(inline => inline.Text)));
        Assert.Equal(
            "Licensing: PolyForm Noncommercial License 1.0.0 License applies.",
            VisibleText(blocks[1].Inlines));
        Assert.Contains('\u00a0', string.Concat(blocks[1].Inlines.Select(inline => inline.Text)));
    }

    [Fact]
    public void Multi_column_table_parses_as_table_block()
    {
        const string html = """
            <html><body>
            <h2>Chart</h2>
            <table>
              <tr><th>Setting</th><th>What it does</th></tr>
              <tr><td>KV key</td><td>Keys link back to earlier turns.</td></tr>
              <tr><td>KV value</td><td>Values keep earlier content.</td></tr>
            </table>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var table = Assert.IsType<HelpTableBlock>(Assert.Single(Assert.Single(document.Topics).Blocks));
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].Count);
        Assert.Contains(table.Rows[1][0], inline => inline.Text == "KV key");
        Assert.Contains(table.Rows[1][1], inline => inline.Text.Contains("earlier turns", StringComparison.Ordinal));
    }

    [Fact]
    public void One_cell_table_is_preformatted()
    {
        const string html = """
            <html><body>
            <h2>Yaml</h2>
            <table><tr><td>llm-pi-ai:<br>  providers:</td></tr></table>
            </body></html>
            """;

        var document = HelpHtmlParser.Parse(html);
        var pre = Assert.IsType<HelpPreBlock>(Assert.Single(Assert.Single(document.Topics).Blocks));
        Assert.Contains("llm-pi-ai:", pre.Text, StringComparison.Ordinal);
        Assert.Contains("providers:", pre.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_help_html_indexes_heading_2_topics()
    {
        var path = FindShippedHelp();
        Assert.True(File.Exists(path), "Help.html should copy next to the test host.");
        var document = HelpHtmlParser.Load(path);
        Assert.Contains("What is AI-FluxMux?", Titles(document));
        Assert.Contains("1 Install AI-FluxMux", Titles(document));
        Assert.Contains("Choosing a Client app for local models", Titles(document));
        Assert.Contains("First-day path", Titles(document));
        Assert.Contains("Diagnostics", Titles(document));
        Assert.Contains("Ports", Titles(document));
        Assert.Contains("Port rules", Titles(document));
        Assert.Contains("Copyright and licence", Titles(document));
        Assert.DoesNotContain("Interface notes", Titles(document));
        Assert.True(document.UiNotes.ContainsKey("quickselect.hot_routing"));
        Assert.Contains("Dynamic Model Routing", document.UiNotes["quickselect.hot_routing"], StringComparison.Ordinal);
        Assert.True(HelpHtmlViewBuilder.IsCopyrightHelpTopic("Copyright and licence"));
        Assert.False(HelpHtmlViewBuilder.IsCopyrightHelpTopic("What is AI-FluxMux?"));
        Assert.True(HelpHtmlViewBuilder.TrySplitProductTitle("AI-FluxMux v0.2.1 beta", out var name, out var version));
        Assert.Equal("AI-FluxMux", name);
        Assert.Equal("v0.2.1 beta", version);
        var html = File.ReadAllText(path);
        Assert.True(IsBold(html, "Continue waiting"));
        Assert.True(IsBold(html, "Keep current model — end this turn"));
        Assert.True(IsBold(html, "Keep current model — this turn continues"));
        Assert.True(IsBold(html, "Keep current model profile"));
        Assert.DoesNotContain("keep / switch", VisibleText(html), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Prompt for any AI editing this file", VisibleText(html), StringComparison.Ordinal);
        Assert.Contains("topic.port_rules", VisibleText(html), StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_help_html_keeps_the_port_rules_topic_id_and_table()
    {
        // Settings jump by topic.port_rules, not the Heading 2 title, so a Word or
        // feed update can rename the topic and rewrite the table without breaking
        // the Servers links.
        var document = HelpHtmlParser.Load(FindShippedHelp());
        var topic = Assert.Single(document.Topics, item => item.Id == HelpTopicIds.PortRules);
        Assert.False(string.IsNullOrWhiteSpace(topic.Title));
        Assert.DoesNotContain(
            topic.Blocks,
            block => block is HelpBannerBlock banner && banner.Text.Contains("topic.port_rules", StringComparison.Ordinal));

        var table = Assert.Single(topic.Blocks.OfType<HelpTableBlock>());
        var cells = table.Rows
            .SelectMany(row => row)
            .Select(cell => string.Concat(cell.Select(inline => inline.Text)))
            .ToList();
        foreach (var rule in new[]
                 {
                     "Compact",
                     "Omit",
                     "Max pictures",
                     "Mill at omitted",
                     "Closed-loop lookback",
                     "Closed-loop mill ceiling",
                     "Observe-only mill",
                     "Rapid-churn",
                     "Repeated command",
                     "Diagnostic dump",
                     "Hang",
                     "503",
                     "Client-app max tokens",
                     "Stop strings"
                 })
        {
            Assert.Contains(cells, cell => cell.Contains(rule, StringComparison.Ordinal));
        }

        Assert.Contains(cells, cell => cell.Contains("What it watches", StringComparison.Ordinal));
        Assert.Contains(cells, cell => cell.Contains("When it treats the turn as a problem", StringComparison.Ordinal));
        Assert.Contains(cells, cell => cell.Contains("What FluxMux does", StringComparison.Ordinal));

        var copy = string.Join(' ', cells.Concat(
            topic.Blocks.OfType<HelpParagraphBlock>().Select(block =>
                string.Concat(block.Inlines.Select(inline => inline.Text)))));
        Assert.DoesNotContain("400", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_help_html_decodes_the_way_the_app_reads_it()
    {
        // Word saves this file as UTF-16 and declares charset=unicode, so an editor that
        // rewrites it without the byte-order mark leaves every character NUL-separated.
        // The Help tab then finds no topics at all, which is worth failing here instead.
        var text = File.ReadAllText(FindShippedHelp());
        Assert.StartsWith("<html", text.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', text);
    }

    [Fact]
    public void Shipped_help_html_gives_cline_and_harness_a_topic_each_and_links_to_them()
    {
        var document = HelpHtmlParser.Load(FindShippedHelp());
        Assert.Contains("Cline", Titles(document));
        Assert.Contains("DeepSeek Harness", Titles(document));

        // The summary of each Client app points at its own topic. Word rewrites the
        // address it was given — a saved file has "#DeepSeek_Harness" — so what has to
        // hold is that every reference still lands on a topic, not how it is spelled.
        var references = document.Topics
            .SelectMany(topic => topic.LeadIn.Concat(topic.Blocks))
            .SelectMany(Inlines)
            .Select(inline => inline.Href)
            .Where(href => href is not null && href.StartsWith('#'))
            .Select(href => href![1..])
            .ToList();

        Assert.NotEmpty(references);
        var topics = document.Topics
            .Select(topic => new HelpTopicEntry { Title = topic.Title, Id = topic.Id })
            .ToList();
        var reached = references
            .Select(reference => HelpTopicSearch.FindByReference(topics, reference))
            .ToList();

        Assert.DoesNotContain(null, reached);
        Assert.Contains("Cline", reached.Select(topic => topic!.Title));
        Assert.Contains("DeepSeek Harness", reached.Select(topic => topic!.Title));
    }

    [Fact]
    public void Shipped_help_html_notes_only_name_values_the_app_can_fill()
    {
        // The wording of a note belongs to whoever edits Word. What cannot vary is that
        // {Name} is a name the app knows: a typo would sit on screen showing its braces.
        var notes = HelpHtmlParser.Load(FindShippedHelp()).UiNotes;
        foreach (var (id, text) in notes)
        {
            foreach (Match token in Regex.Matches(text, @"\{([A-Za-z][A-Za-z0-9_]*)\}"))
            {
                Assert.True(
                    HelpUiNotes.ValueNames.Contains(token.Groups[1].Value, StringComparer.OrdinalIgnoreCase),
                    $"Note {id} names {token.Value}, which nothing fills in.");
            }

            // A value typed out goes stale the moment the operator changes Port.
            Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Word_wrapping_the_saved_file_does_not_break_a_line_on_screen()
    {
        // Word breaks its own source mid-sentence to keep the file narrow. Kept, those
        // newlines land in the middle of a paragraph on screen.
        var document = HelpHtmlParser.Parse(
            "<html><body><h2>Ports</h2><p>Point the Client app at\r\nPort, then click\n<strong>Launch</strong>\r\nonce.</p></body></html>");

        var paragraph = Assert.IsType<HelpParagraphBlock>(Assert.Single(document.Topics).Blocks[0]);
        Assert.DoesNotContain(paragraph.Inlines, inline => inline.Text.Contains('\n'));
        Assert.Equal(
            "Point the Client app at Port, then click Launch once.",
            string.Concat(paragraph.Inlines.Select(inline => inline.Text)));
    }

    [Fact]
    public void A_list_word_saved_as_paragraphs_is_read_back_as_a_list()
    {
        // This is how Word writes a bulleted list: ordinary paragraphs carrying an
        // mso-list style, with a bullet character it draws itself.
        const string html = """
            <html><body>
            <h2>Ports</h2>
            <p style='mso-list:l9 level1 lfo36'><span style='mso-list:Ignore'>&middot;<span>&nbsp;&nbsp;</span></span>Set Port.</p>
            <p style='mso-list:l9 level1 lfo36'><span style='mso-list:Ignore'>&middot;<span>&nbsp;&nbsp;</span></span>Then click <strong>Launch</strong>.</p>
            <p>After that, the model answers.</p>
            </body></html>
            """;

        var blocks = HelpHtmlParser.Parse(html).Topics[0].Blocks;
        var list = Assert.IsType<HelpListBlock>(blocks[0]);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("Set Port.", string.Concat(list.Items[0].Select(inline => inline.Text)));
        Assert.DoesNotContain(list.Items[1], inline => inline.Text.Contains('\u00b7'));
        Assert.Contains(list.Items[1], inline => inline is { Text: "Launch", Bold: true });

        // The paragraph after it must not be swallowed into the list.
        Assert.IsType<HelpParagraphBlock>(blocks[1]);
    }

    [Fact]
    public void A_note_bullet_is_marked_so_the_view_can_indent_it()
    {
        const string html = """
            <html><body>
            <h2>Interface notes</h2>
            <h3>servers.shared</h3>
            <p style='mso-list:l1 level1 lfo2'><span style='mso-list:Ignore'>&middot;<span>&nbsp;</span></span>One point.</p>
            <p style='mso-list:l1 level1 lfo2'><span style='mso-list:Ignore'>&middot;<span>&nbsp;</span></span>Another point.</p>
            </body></html>
            """;

        Assert.Equal(
            HelpHtmlParser.NoteBullet + " One point.\n" + HelpHtmlParser.NoteBullet + " Another point.",
            HelpHtmlParser.Parse(html).UiNotes["servers.shared"]);
    }

    [Fact]
    public void An_explicit_break_still_breaks_the_line()
    {
        var document = HelpHtmlParser.Parse(
            "<html><body><h2>Ports</h2><p>First line.<br>Second line.</p></body></html>");

        var paragraph = Assert.IsType<HelpParagraphBlock>(Assert.Single(document.Topics).Blocks[0]);
        Assert.Contains(paragraph.Inlines, inline => inline.Text == "\n");
    }

    [Fact]
    public void Shipped_help_html_carries_the_servers_notes_as_separate_points()
    {
        var document = HelpHtmlParser.Load(FindShippedHelp());
        foreach (var id in new[] { "servers.cline_hint", "servers.harness_summary", "servers.generic_hint" })
        {
            var note = document.UiNotes[id];
            Assert.Contains(HelpHtmlParser.NoteBullet, note, StringComparison.Ordinal);
            Assert.Contains('\n', note);
        }

        Assert.Contains(HelpUiNotes.AddressValue, document.UiNotes["servers.cline_hint"], StringComparison.Ordinal);
        Assert.Contains(
            HelpUiNotes.HarnessChatUrlValue,
            document.UiNotes["servers.harness_summary"],
            StringComparison.Ordinal);

        // A Client app of its own points at the adapters, which is the only way to have
        // AI-FluxMux write its settings.
        Assert.Contains(
            "Endpoint adapter",
            document.UiNotes["servers.generic_hint"],
            StringComparison.Ordinal);

        // The short note beside the buttons hands the detail to the appendix.
        Assert.True(document.UiNotes.ContainsKey("servers.harness_advanced"));
        Assert.Contains("Appendix E — Harness settings.yaml", Titles(document));

        // A note that sends the operator to an appendix must name it exactly as the
        // appendix is titled, so the words on the panel match the words in the index.
        var titles = Titles(document);
        foreach (var note in document.UiNotes.Values)
        {
            foreach (Match reference in Regex.Matches(note, @"Appendix [A-Z]"))
            {
                var title = Assert.Single(
                    titles,
                    t => t.StartsWith(reference.Value + " —", StringComparison.Ordinal));
                Assert.Contains(title, note, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Shipped_help_html_explains_windows_gpu_affinity_mode()
    {
        var document = HelpHtmlParser.Load(FindShippedHelp());
        var note = document.UiNotes["gpuvram.desktop_gpu"];
        var html = File.ReadAllText(FindShippedHelp());
        Assert.Contains("experimental", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("motherboard graphics ports", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("main graphics card", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart Windows", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Don't close this!", note, StringComparison.Ordinal);
        Assert.DoesNotContain("listed separately", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Cloud-only", note, StringComparison.Ordinal);
        Assert.Contains("Windows GPU affinity", note, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlLabelMarkup.Mark("Windows GPU affinity"), note, StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark("Motherboard GPU"), note, StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark("Apply"), note, StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark("Refresh VRAM"), note, StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark("Discrete GPU"), note, StringComparison.Ordinal);
        Assert.Contains("Apply " + ControlLabelMarkup.Mark("Discrete GPU") + " mode", note, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlLabelMarkup.Mark("Apply") + " " + ControlLabelMarkup.Mark("Discrete GPU"), note, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlLabelMarkup.Mark("Discrete GPU mode"), note, StringComparison.Ordinal);
        Assert.DoesNotContain("KVM", note, StringComparison.Ordinal);
        Assert.Contains("BIOS", note, StringComparison.Ordinal);
        Assert.Contains("Don't close this!", note, StringComparison.Ordinal);
        Assert.Contains("Local AI models benefit from as much VRAM as possible.", note, StringComparison.Ordinal);
        Assert.Contains(HelpHtmlParser.NoteBullet, note, StringComparison.Ordinal);
        Assert.Contains('\n', note);
        Assert.DoesNotContain("Desktop GPU", note, StringComparison.Ordinal);
        Assert.DoesNotContain("5090", note, StringComparison.Ordinal);
        Assert.DoesNotContain("Thunderbolt", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows GPU affinity", VisibleText(html), StringComparison.Ordinal);
        Assert.DoesNotContain("Windows GPU affinity mode", VisibleText(html), StringComparison.Ordinal);
        Assert.False(IsBold(html, "Windows GPU affinity"));
        Assert.True(IsBold(html, "Motherboard GPU"));
        Assert.True(IsBold(html, "Refresh VRAM"));
        Assert.True(IsBold(html, "Discrete GPU"));
        Assert.Contains("<strong>Discrete GPU</strong> mode", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Desktop GPU", VisibleText(html), StringComparison.Ordinal);
    }

    private static IEnumerable<HelpInline> Inlines(HelpBlock block) => block switch
    {
        HelpParagraphBlock paragraph => paragraph.Inlines,
        HelpListBlock list => list.Items.SelectMany(item => item),
        HelpCalloutBlock callout => callout.Blocks.SelectMany(Inlines),
        _ => []
    };

    private static string[] Titles(HelpDocument document)
        => document.Topics.Select(topic => topic.Title).ToArray();

    /// <summary>
    /// The words of the shipped file, without its markup. Saving Help.html from Word
    /// breaks lines mid-sentence, so a phrase has to be matched against the text rather
    /// than the file.
    /// </summary>
    private static string VisibleText(string html)
    {
        var text = Regex.Replace(html, "<style[^>]*>.*?</style>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = text
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace('\u00a0', ' ');
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    /// <summary>
    /// Whether the shipped file still shows <paramref name="phrase"/> as a control name.
    /// Word wraps every bold run in a span and may break the line inside the phrase, so
    /// matching the markup literally would fail on copy that is perfectly correct.
    /// </summary>
    private static bool IsBold(string html, string phrase)
    {
        const string gap = "(?:\\s|<[^>]+>)";
        var words = string.Join(gap + "+", phrase.Split(' ').Select(Regex.Escape));
        return Regex.IsMatch(html, "<(?:b|strong)\\b[^>]*>" + gap + "*" + words, RegexOptions.IgnoreCase);
    }

    private static string VisibleText(IEnumerable<HelpInline> inlines)
        => string.Concat(inlines.Select(inline => inline.Text))
            .Replace("\u200B", string.Empty)
            .Replace('\u00a0', ' ');

    private static string FindShippedHelp()
    {
        var nextToHost = Path.Combine(AppContext.BaseDirectory, "Help.html");
        if (File.Exists(nextToHost))
        {
            return nextToHost;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Help.html");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return nextToHost;
    }
}

public sealed class HelpHtmlFileTests
{
    /// <summary>Keeps this machine's real per-user Help.html out of a path test.</summary>
    private const string NoUserCopy = "";

    [Fact]
    public void Recognises_html_and_htm_names()
    {
        Assert.True(HelpHtmlFile.IsHelpFileName("Help.html"));
        Assert.True(HelpHtmlFile.IsHelpFileName("help.HTM"));
        Assert.True(HelpHtmlFile.IsHelpFileName(@"C:\tmp\Help.html"));
        Assert.False(HelpHtmlFile.IsHelpFileName("Help-draft.html"));
        Assert.False(HelpHtmlFile.IsHelpFileName("notes.html"));
    }

    [Fact]
    public void FindNewest_prefers_newer_working_copy_over_stale_exe_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxmux-help-" + Guid.NewGuid().ToString("n"));
        var exeDir = Path.Combine(root, "exe");
        var cwdDir = Path.Combine(root, "cwd");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(cwdDir);
        var exeHelp = Path.Combine(exeDir, "Help.html");
        var cwdHelp = Path.Combine(cwdDir, "Help.html");
        File.WriteAllText(exeHelp, "<h2>Exe</h2>");
        File.SetLastWriteTimeUtc(exeHelp, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(cwdHelp, "<h2>Newer cwd</h2>");
        File.SetLastWriteTimeUtc(cwdHelp, DateTime.UtcNow);

        try
        {
            var found = HelpHtmlFile.FindNewest(exeDir, cwdDir, NoUserCopy);
            Assert.Equal(cwdHelp, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindNewest_on_equal_time_prefers_the_copy_next_to_the_app()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxmux-help-" + Guid.NewGuid().ToString("n"));
        var exeDir = Path.Combine(root, "exe");
        var cwdDir = Path.Combine(root, "cwd");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(cwdDir);
        var exeHelp = Path.Combine(exeDir, "Help.html");
        var cwdHelp = Path.Combine(cwdDir, "Help.html");
        var stamp = DateTime.UtcNow.AddMinutes(-1);
        File.WriteAllText(exeHelp, "<h2>Exe</h2>");
        File.WriteAllText(cwdHelp, "<h2>Cwd</h2>");
        File.SetLastWriteTimeUtc(exeHelp, stamp);
        File.SetLastWriteTimeUtc(cwdHelp, stamp);

        try
        {
            var found = HelpHtmlFile.FindNewest(exeDir, cwdDir, NoUserCopy);
            Assert.Equal(exeHelp, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindNewest_without_exe_copy_uses_newest_elsewhere()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxmux-help-" + Guid.NewGuid().ToString("n"));
        var emptyExeDir = Path.Combine(root, "exe");
        var olderDir = Path.Combine(root, "a");
        var newerDir = Path.Combine(root, "b");
        Directory.CreateDirectory(emptyExeDir);
        Directory.CreateDirectory(olderDir);
        Directory.CreateDirectory(newerDir);
        var older = Path.Combine(olderDir, "Help.html");
        var newer = Path.Combine(newerDir, "Help.html");
        File.WriteAllText(older, "<h2>Old</h2>");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(newer, "<h2>New</h2>");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        try
        {
            var found = HelpHtmlFile.FindNewest(emptyExeDir, newerDir, NoUserCopy);
            Assert.Equal(newer, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindNewest_uses_the_per_user_copy_when_the_install_folder_is_read_only()
    {
        // An installed AI-FluxMux cannot write beside its own exe, so an updated Help.html
        // lands in the per-user folder and has to win on age like any other copy.
        var root = Path.Combine(Path.GetTempPath(), "fluxmux-help-" + Guid.NewGuid().ToString("n"));
        var exeDir = Path.Combine(root, "exe");
        var cwdDir = Path.Combine(root, "cwd");
        var userDir = Path.Combine(root, "user");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(cwdDir);
        Directory.CreateDirectory(userDir);
        var shipped = Path.Combine(exeDir, "Help.html");
        var updated = Path.Combine(userDir, "Help.html");
        File.WriteAllText(shipped, "<h2>Shipped</h2>");
        File.SetLastWriteTimeUtc(shipped, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(updated, "<h2>Updated</h2>");
        File.SetLastWriteTimeUtc(updated, DateTime.UtcNow);

        try
        {
            Assert.Equal(updated, HelpHtmlFile.FindNewest(exeDir, cwdDir, userDir));

            // A later reinstall ships a newer Help.html, which then wins again.
            File.SetLastWriteTimeUtc(shipped, DateTime.UtcNow.AddMinutes(5));
            Assert.Equal(shipped, HelpHtmlFile.FindNewest(exeDir, cwdDir, userDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
