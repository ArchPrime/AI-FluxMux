using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HtmlAgilityPack;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// One styled run of help copy. <see cref="Bold"/> marks a named AI-FluxMux control,
/// <see cref="Italic"/> marks emphasis, and <see cref="Code"/> marks a path, file name,
/// JSON key, or literal value the operator types.
/// </summary>
public readonly record struct HelpInline(
    string Text,
    string? Href,
    bool Bold,
    bool Italic = false,
    bool Code = false);

public abstract class HelpBlock;

public sealed class HelpParagraphBlock : HelpBlock
{
    public required IReadOnlyList<HelpInline> Inlines { get; init; }
}

public sealed class HelpListBlock : HelpBlock
{
    public required bool Ordered { get; init; }
    public required IReadOnlyList<IReadOnlyList<HelpInline>> Items { get; init; }
}

public sealed class HelpPreBlock : HelpBlock
{
    public required string Text { get; init; }
}

/// <summary>
/// A heading inside the flowing body. <see cref="Level"/> is 1 for a section banner
/// ("Setting up", "Appendices") and 3 for a subheading inside a topic, so the view can
/// give them different weight instead of rendering every heading at body size.
/// </summary>
public sealed class HelpBannerBlock : HelpBlock
{
    public required string Text { get; init; }
    public int Level { get; init; } = 3;
}

/// <summary>A picture beside the copy. <see cref="Source"/> is relative to Help.html.</summary>
public sealed class HelpImageBlock : HelpBlock
{
    public required string Source { get; init; }
    public string Alt { get; init; } = string.Empty;
}

/// <summary>A short aside the operator should not miss, drawn as a tinted box.</summary>
public sealed class HelpCalloutBlock : HelpBlock
{
    public required IReadOnlyList<HelpBlock> Blocks { get; init; }
}

public sealed class HelpSeparatorBlock : HelpBlock;

public sealed class HelpTableBlock : HelpBlock
{
    public required IReadOnlyList<IReadOnlyList<IReadOnlyList<HelpInline>>> Rows { get; init; }
}

public sealed class HelpTopic
{
    public required string Title { get; init; }

    /// <summary>
    /// Stable machine id from a hidden Heading 3 that starts with
    /// <see cref="HelpTopicIds.Prefix"/>. The Heading 2 title can change when
    /// Help.html is updated; UI jumps and in-Help links keep this id.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    public IReadOnlyList<HelpBlock> LeadIn { get; init; } = [];
    public IReadOnlyList<HelpBlock> Blocks { get; init; } = [];
}

/// <summary>Stable Help topic ids that settings can jump to after a copy update.</summary>
public static class HelpTopicIds
{
    public const string Prefix = "topic.";
    public const string PortRules = "topic.port_rules";
}

public sealed class HelpDocument
{
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<HelpBlock> Preamble { get; init; } = [];
    public IReadOnlyList<HelpTopic> Topics { get; init; } = [];
    public IReadOnlyDictionary<string, string> UiNotes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder the file came from, so relative pictures resolve against the copy in use.</summary>
    public string BaseDirectory { get; init; } = string.Empty;
}

/// <summary>
/// Reads Word "Web Page, Filtered" HTML. Heading 2 becomes a Help index topic.
/// Heading 2 "Interface notes" is omitted from that index; Heading 3 there is a UI note id.
/// A Heading 3 whose text is <c>topic.*</c> under a real topic is that topic's id and is not shown.
/// </summary>
public static class HelpHtmlParser
{
    /// <summary>Marks a list item in a flattened note, so the view can indent it.</summary>
    public const string NoteBullet = "\u2022";

    public static HelpDocument Load(string path)
    {
        var htmlDoc = new HtmlDocument();
        try
        {
            htmlDoc.DetectEncodingAndLoad(path);
        }
        catch (Exception)
        {
            htmlDoc.LoadHtml(File.ReadAllText(path));
        }

        var document = Parse(htmlDoc);
        return new HelpDocument
        {
            Title = document.Title,
            Preamble = document.Preamble,
            Topics = document.Topics,
            UiNotes = document.UiNotes,
            BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty
        };
    }

    public static HelpDocument Parse(string html)
    {
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);
        return Parse(htmlDoc);
    }

    public static HelpDocument Parse(HtmlDocument htmlDoc)
    {
        var title = string.Empty;
        var preamble = new List<HelpBlock>();
        var topics = new List<HelpTopic>();
        string? topicTitle = null;
        var topicId = string.Empty;
        var topicBlocks = new List<HelpBlock>();
        var topicLeadIn = new List<HelpBlock>();
        var pendingLeadIn = new List<HelpBlock>();
        var sawTitle = false;
        var inUiNotes = false;
        var uiNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? noteId = null;
        var noteBlocks = new List<HelpBlock>();

        // Word saves a list as ordinary paragraphs carrying an mso-list style and a
        // bullet character of its own. Gathered back into one list, they wrap with a
        // hanging indent instead of running under the bullet.
        var wordListItems = new List<IReadOnlyList<HelpInline>>();

        void FlushWordList()
        {
            if (wordListItems.Count == 0)
            {
                return;
            }

            var items = wordListItems;
            wordListItems = new List<IReadOnlyList<HelpInline>>();
            AddBlockCore(new HelpListBlock { Ordered = false, Items = items });
        }

        void FlushTopic()
        {
            FlushWordList();
            if (!string.IsNullOrWhiteSpace(topicTitle))
            {
                topics.Add(new HelpTopic
                {
                    Title = topicTitle,
                    Id = topicId,
                    LeadIn = topicLeadIn.ToList(),
                    Blocks = topicBlocks.ToList()
                });
            }

            topicTitle = null;
            topicId = string.Empty;
            topicBlocks = new List<HelpBlock>();
            topicLeadIn = new List<HelpBlock>();
        }

        void FlushNote()
        {
            FlushWordList();
            if (string.IsNullOrWhiteSpace(noteId))
            {
                noteBlocks.Clear();
                return;
            }

            var marked = FlattenNoteBlocks(noteBlocks);
            if (marked.Length > 0)
            {
                uiNotes[noteId] = marked;
            }

            noteId = null;
            noteBlocks.Clear();
        }

        void AddBlock(HelpBlock block)
        {
            FlushWordList();
            AddBlockCore(block);
        }

        void AddBlockCore(HelpBlock block)
        {
            if (inUiNotes)
            {
                if (noteId is not null)
                {
                    noteBlocks.Add(block);
                }

                return;
            }

            if (topicTitle is null)
            {
                if (topics.Count == 0)
                {
                    preamble.Add(block);
                    return;
                }

                pendingLeadIn.Add(block);
                return;
            }

            topicBlocks.Add(block);
        }

        var root = htmlDoc.DocumentNode.SelectSingleNode("//body") ?? htmlDoc.DocumentNode;
        Walk(root, node =>
        {
            var name = node.Name.ToLowerInvariant();
            if (name is "h1")
            {
                var text = VisibleText(node);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (!sawTitle)
                {
                    title = text;
                    sawTitle = true;
                    return;
                }

                FlushTopic();
                AddBlock(new HelpBannerBlock { Text = text, Level = 1 });
                return;
            }

            if (name is "h2")
            {
                var text = VisibleText(node);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (IsInterfaceNotesHeading(text))
                {
                    FlushTopic();
                    FlushNote();
                    inUiNotes = true;
                    topicTitle = null;
                    pendingLeadIn = new List<HelpBlock>();
                    return;
                }

                if (inUiNotes)
                {
                    FlushNote();
                    inUiNotes = false;
                }

                FlushTopic();
                topicLeadIn = pendingLeadIn;
                pendingLeadIn = new List<HelpBlock>();
                topicTitle = text;
                return;
            }

            if (name is "h3")
            {
                var text = VisibleText(node);
                if (inUiNotes)
                {
                    FlushNote();
                    if (TryParseUiNoteId(text, out var id))
                    {
                        noteId = id;
                    }

                    return;
                }

                if (TryParseTopicId(text, out var topicKey))
                {
                    if (string.IsNullOrWhiteSpace(topicId))
                    {
                        topicId = topicKey;
                    }

                    return;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    AddBlock(new HelpBannerBlock { Text = text, Level = 3 });
                }

                return;
            }

            if (name is "blockquote")
            {
                var inner = new List<HelpBlock>();
                Walk(node, child => ConsumeBlock(child, inner.Add));
                if (inner.Count > 0)
                {
                    AddBlock(new HelpCalloutBlock { Blocks = inner });
                }

                return;
            }

            if (name is "img")
            {
                var image = TryReadImage(node);
                if (image is not null)
                {
                    AddBlock(image);
                }

                return;
            }

            if (name is "hr")
            {
                AddBlock(new HelpSeparatorBlock());
                return;
            }

            if (name is "pre")
            {
                AddBlock(new HelpPreBlock { Text = TextWithBreaks(node).Trim('\r', '\n') });
                return;
            }

            if (name is "ul" or "ol")
            {
                var items = new List<IReadOnlyList<HelpInline>>();
                var itemNodes = node.SelectNodes("./li") ?? node.SelectNodes(".//li");
                if (itemNodes is not null)
                {
                    foreach (var item in itemNodes)
                    {
                        var inlines = CollectInlines(item);
                        if (inlines.Count > 0)
                        {
                            items.Add(inlines);
                        }
                    }
                }

                if (items.Count > 0)
                {
                    AddBlock(new HelpListBlock { Ordered = name == "ol", Items = items });
                }

                return;
            }

            if (name is "table")
            {
                var pre = TryTableAsPreformatted(node);
                if (pre is not null)
                {
                    AddBlock(pre);
                    return;
                }

                var rows = ParseTableRows(node);
                if (rows.Count > 0)
                {
                    AddBlock(new HelpTableBlock { Rows = rows });
                }

                return;
            }

            if (name is "p")
            {
                if (IsWordListParagraph(node))
                {
                    var item = WordListItemInlines(node);
                    if (item.Count > 0)
                    {
                        wordListItems.Add(item);
                        return;
                    }
                }

                ConsumeParagraph(node, AddBlock);
            }
        });

        FlushTopic();
        FlushNote();
        preamble.AddRange(pendingLeadIn);
        return new HelpDocument
        {
            Title = title,
            Preamble = preamble,
            Topics = topics,
            UiNotes = uiNotes
        };
    }

    public static bool IsInterfaceNotesHeading(string? title)
        => !string.IsNullOrWhiteSpace(title)
           && title.Trim().StartsWith("Interface notes", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseUiNoteId(string? heading, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(heading))
        {
            return false;
        }

        var text = heading.Trim();
        var end = 0;
        while (end < text.Length)
        {
            var ch = text[end];
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                break;
            }

            end++;
        }

        if (end == 0)
        {
            return false;
        }

        var token = text[..end];
        if (!token.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        id = token;
        return true;
    }

    /// <summary>
    /// A Heading 3 whose only job is a stable topic id, so a Word or feed update
    /// can rename the Heading 2 title without breaking a setting Help link.
    /// </summary>
    public static bool TryParseTopicId(string? heading, out string id)
    {
        if (!TryParseUiNoteId(heading, out id))
        {
            return false;
        }

        return id.StartsWith(HelpTopicIds.Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One note as a single string for <c>MarkedText</c>. Paragraphs and bullets keep a
    /// line each, because a note written as several short points in Word is unreadable
    /// once it is run together into one block of text.
    /// </summary>
    public static string FlattenNoteBlocks(IReadOnlyList<HelpBlock> blocks)
    {
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HelpParagraphBlock paragraph:
                    var paragraphText = FlattenInlines(paragraph.Inlines);
                    if (paragraphText.Length > 0)
                    {
                        parts.Add(paragraphText);
                    }

                    break;
                case HelpListBlock list:
                    foreach (var item in list.Items)
                    {
                        var itemText = FlattenInlines(item);
                        if (itemText.Length > 0)
                        {
                            parts.Add(NoteBullet + " " + itemText);
                        }
                    }

                    break;
            }
        }

        return string.Join("\n", parts);
    }

    private static string FlattenInlines(IReadOnlyList<HelpInline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            var text = inline.Text.Replace("\u200B", string.Empty).Replace('\u00a0', ' ');
            if (text.Length == 0)
            {
                continue;
            }

            if (inline.Bold)
            {
                builder.Append(ControlLabelMarkup.Mark(text));
            }
            else
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private static void Walk(HtmlNode node, Action<HtmlNode> onBlock)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Comment)
            {
                continue;
            }

            if (child.NodeType == HtmlNodeType.Text)
            {
                continue;
            }

            if (child.NodeType != HtmlNodeType.Element)
            {
                continue;
            }

            var name = child.Name.ToLowerInvariant();
            if (name is "style" or "script" or "meta" or "link" or "title" or "head")
            {
                continue;
            }

            if (name is "div" or "section" or "article" or "font" or "center")
            {
                Walk(child, onBlock);
                continue;
            }

            onBlock(child);
        }
    }

    private static void ConsumeBlock(HtmlNode node, Action<HelpBlock> addBlock)
    {
        var name = node.Name.ToLowerInvariant();
        if (name is "p")
        {
            ConsumeParagraph(node, addBlock);
            return;
        }

        if (name is "pre")
        {
            addBlock(new HelpPreBlock { Text = TextWithBreaks(node).Trim('\r', '\n') });
            return;
        }

        if (name is "ul" or "ol")
        {
            var items = new List<IReadOnlyList<HelpInline>>();
            foreach (var item in node.SelectNodes("./li") ?? Enumerable.Empty<HtmlNode>())
            {
                var inlines = CollectInlines(item);
                if (inlines.Count > 0)
                {
                    items.Add(inlines);
                }
            }

            if (items.Count > 0)
            {
                addBlock(new HelpListBlock { Ordered = name == "ol", Items = items });
            }

            return;
        }

        if (name is "table")
        {
            var rows = ParseTableRows(node);
            if (rows.Count > 0)
            {
                addBlock(new HelpTableBlock { Rows = rows });
            }

            return;
        }

        if (name is "img")
        {
            var image = TryReadImage(node);
            if (image is not null)
            {
                addBlock(image);
            }
        }
    }

    /// <summary>
    /// A paragraph Word wrote for a list item. Word keeps the list only in an
    /// <c>mso-list</c> style, so without this the item is prose beginning with a
    /// stray bullet character, and a wrapped line runs back under that bullet.
    /// </summary>
    private static bool IsWordListParagraph(HtmlNode node)
    {
        if (node.GetAttributeValue("style", string.Empty)
            .Contains("mso-list:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return node.GetAttributeValue("class", string.Empty)
            .Contains("MsoListParagraph", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The item without the bullet Word drew for itself, which it marks
    /// <c>mso-list:Ignore</c> along with the spacing that follows it.
    /// </summary>
    private static IReadOnlyList<HelpInline> WordListItemInlines(HtmlNode node)
    {
        var clone = HtmlNode.CreateNode(node.OuterHtml);
        var markers = clone.SelectNodes(".//span[contains(@style, 'mso-list:Ignore')]");
        if (markers is not null)
        {
            foreach (var marker in markers.ToList())
            {
                marker.Remove();
            }
        }

        return CollectInlines(clone);
    }

    private static void ConsumeParagraph(HtmlNode node, Action<HelpBlock> addBlock)
    {
        if (LooksPreformatted(node))
        {
            var text = TextWithBreaks(node).Trim();
            if (text.Length > 0)
            {
                addBlock(new HelpPreBlock { Text = text });
            }

            return;
        }

        // Word wraps an inserted picture in its own paragraph.
        foreach (var image in node.SelectNodes(".//img") ?? Enumerable.Empty<HtmlNode>())
        {
            var block = TryReadImage(image);
            if (block is not null)
            {
                addBlock(block);
            }
        }

        var inlines = CollectInlines(node);
        if (inlines.Count == 0)
        {
            return;
        }

        addBlock(new HelpParagraphBlock { Inlines = inlines });
    }

    /// <summary>
    /// Reads a picture reference. The path stays relative so the view can resolve it
    /// beside whichever Help.html actually loaded. Data URIs and remote images are ignored.
    /// </summary>
    private static HelpImageBlock? TryReadImage(HtmlNode node)
    {
        var src = Decode(node.GetAttributeValue("src", string.Empty)).Trim();
        if (src.Length == 0
            || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || src.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        return new HelpImageBlock
        {
            Source = src.Replace('/', Path.DirectorySeparatorChar),
            Alt = Decode(node.GetAttributeValue("alt", string.Empty)).Trim()
        };
    }

    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<HelpInline>>> ParseTableRows(HtmlNode table)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<HelpInline>>>();
        foreach (var rowNode in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = new List<IReadOnlyList<HelpInline>>();
            foreach (var cellNode in rowNode.SelectNodes("./td|./th") ?? Enumerable.Empty<HtmlNode>())
            {
                var inlines = CollectInlines(cellNode);
                if (inlines.Count > 0)
                {
                    cells.Add(inlines);
                }
            }

            if (cells.Count > 0)
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    private static HelpPreBlock? TryTableAsPreformatted(HtmlNode table)
    {
        var cells = table.SelectNodes(".//td|.//th");
        if (cells is null || cells.Count == 0)
        {
            return null;
        }

        if (cells.Count == 1 || LooksPreformatted(table))
        {
            var text = TextWithBreaks(cells[0]).Trim();
            if (text.Length > 0)
            {
                return new HelpPreBlock { Text = text };
            }
        }

        return null;
    }

    private static bool LooksPreformatted(HtmlNode node)
    {
        var style = node.GetAttributeValue("style", string.Empty)
                    + " "
                    + node.GetAttributeValue("class", string.Empty)
                    + " "
                    + node.GetAttributeValue("face", string.Empty);
        if (style.Contains("consolas", StringComparison.OrdinalIgnoreCase)
            || style.Contains("courier", StringComparison.OrdinalIgnoreCase)
            || style.Contains("monospace", StringComparison.OrdinalIgnoreCase)
            || style.Contains("code", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var brCount = node.SelectNodes(".//br")?.Count ?? 0;
        return brCount >= 3;
    }

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Code)
    {
        public static readonly InlineStyle None = default;

        public HelpInline Apply(string text, string? href)
            => new(text, href, Bold, Italic, Code);
    }

    private static List<HelpInline> CollectInlines(HtmlNode node)
    {
        var dest = new List<HelpInline>();
        CollectInlines(node, dest, InlineStyle.None);
        return NormalizeInlines(dest);
    }

    private static void CollectInlines(HtmlNode node, List<HelpInline> dest, InlineStyle style)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                // Word wraps its saved source mid-sentence. Those newlines are layout of
                // the file, not of the prose, so only a <br> below may break a line.
                var text = CollapseSpace(Decode(child.InnerText), preserveEdgeSpaces: true);
                if (text.Length > 0)
                {
                    dest.Add(style.Apply(text, null));
                }

                continue;
            }

            if (child.NodeType != HtmlNodeType.Element)
            {
                continue;
            }

            var name = child.Name.ToLowerInvariant();
            if (name is "script" or "style" or "o:p")
            {
                continue;
            }

            if (name is "br")
            {
                dest.Add(style.Apply("\n", null));
                continue;
            }

            var childStyle = ExtendStyle(style, child, name);
            if (name is "a")
            {
                var href = child.GetAttributeValue("href", string.Empty).Trim();
                var text = VisibleText(child);
                if (text.Length > 0)
                {
                    dest.Add(childStyle.Apply(text, string.IsNullOrWhiteSpace(href) ? null : href));
                }

                continue;
            }

            CollectInlines(child, dest, childStyle);
        }
    }

    /// <summary>
    /// Adds the styling an element contributes. Word "Web Page, Filtered" writes some of
    /// these as inline styles on a span rather than as semantic tags, so both are read.
    /// </summary>
    private static InlineStyle ExtendStyle(InlineStyle style, HtmlNode node, string name)
    {
        style = name switch
        {
            "b" or "strong" => style with { Bold = true },
            "i" or "em" or "cite" or "var" => style with { Italic = true },
            "code" or "kbd" or "samp" or "tt" => style with { Code = true },
            _ => style
        };

        var css = node.GetAttributeValue("style", string.Empty);
        if (css.Length == 0)
        {
            return style;
        }

        if (css.Contains("bold", StringComparison.OrdinalIgnoreCase))
        {
            style = style with { Bold = true };
        }

        if (css.Contains("italic", StringComparison.OrdinalIgnoreCase))
        {
            style = style with { Italic = true };
        }

        if (css.Contains("consolas", StringComparison.OrdinalIgnoreCase)
            || css.Contains("courier", StringComparison.OrdinalIgnoreCase)
            || css.Contains("monospace", StringComparison.OrdinalIgnoreCase))
        {
            style = style with { Code = true };
        }

        return style;
    }

    private static List<HelpInline> NormalizeInlines(List<HelpInline> inlines)
    {
        var result = new List<HelpInline>();
        foreach (var inline in inlines)
        {
            if (inline.Href is not null)
            {
                var linkText = CollapseSpace(inline.Text).Trim();
                if (linkText.Length > 0)
                {
                    result.Add(inline with { Text = linkText });
                }

                continue;
            }

            var text = inline.Text.Replace('\u00a0', ' ');
            if (text.Contains('\n', StringComparison.Ordinal))
            {
                result.Add(inline with { Text = text });
                continue;
            }

            text = CollapseSpace(text, preserveEdgeSpaces: true);
            if (text.Length > 0)
            {
                result.Add(inline with { Text = text });
            }
        }

        while (result.Count > 0 && result[0].Href is null && result[0].Text.Trim().Length == 0)
        {
            result.RemoveAt(0);
        }

        while (result.Count > 0 && result[^1].Href is null && result[^1].Text.Trim().Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return InsertVisibleSpacesAroundLinks(result);
    }

    private static List<HelpInline> InsertVisibleSpacesAroundLinks(List<HelpInline> inlines)
    {
        var visible = new List<HelpInline>();
        foreach (var inline in inlines)
        {
            if (inline.Href is null && inline.Text != "\n" && inline.Text.Trim().Length == 0)
            {
                continue;
            }

            visible.Add(inline);
        }

        var result = new List<HelpInline>();
        for (var i = 0; i < visible.Count; i++)
        {
            var current = visible[i];
            var text = current.Href is not null ? current.Text.Trim() : current.Text;
            if (current.Href is null)
            {
                if (i > 0 && visible[i - 1].Href is not null)
                {
                    text = text.TrimStart();
                }

                if (i + 1 < visible.Count && visible[i + 1].Href is not null)
                {
                    text = text.TrimEnd();
                }
            }

            // Avalonia trims leading/trailing whitespace on each Run, including U+00A0.
            // Keep the gap inside this Run and pin it with a zero-width character.
            if (i + 1 < visible.Count && NeedsVisibleSpaceBetween(current with { Text = text }, visible[i + 1]))
            {
                text += "\u00a0\u200B";
            }

            if (text.Length > 0)
            {
                result.Add(current with { Text = text });
            }
        }

        return result;
    }

    private static bool NeedsVisibleSpaceBetween(HelpInline previous, HelpInline next)
    {
        if (previous.Href is null && next.Href is null)
        {
            return false;
        }

        if (previous.Text == "\n" || next.Text == "\n")
        {
            return false;
        }

        if (previous.Href is not null
            && next.Text.Length > 0
            && next.Text[0] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']')
        {
            return false;
        }

        return true;
    }

    private static string CollapseSpace(string text, bool preserveEdgeSpaces = false)
    {
        var original = text.Replace('\u00a0', ' ');
        var keepLead = preserveEdgeSpaces && original.Length > 0 && char.IsWhiteSpace(original[0]);
        var keepTail = preserveEdgeSpaces && original.Length > 0 && char.IsWhiteSpace(original[^1]);
        var builder = new StringBuilder(original.Length);
        var gap = false;
        foreach (var ch in original)
        {
            if (char.IsWhiteSpace(ch))
            {
                gap = true;
                continue;
            }

            if (gap && builder.Length > 0)
            {
                builder.Append(' ');
            }

            gap = false;
            builder.Append(ch);
        }

        if (builder.Length == 0)
        {
            return keepLead || keepTail ? " " : string.Empty;
        }

        if (keepLead)
        {
            builder.Insert(0, ' ');
        }

        if (keepTail)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static string VisibleText(HtmlNode node)
    {
        return CollapseSpace(TextWithBreaks(node).Replace('\n', ' ')).Trim();
    }

    private static string TextWithBreaks(HtmlNode node)
    {
        var clone = HtmlNode.CreateNode(node.OuterHtml);
        var breaks = clone.SelectNodes(".//br");
        if (breaks is not null)
        {
            foreach (var br in breaks.ToList())
            {
                br.ParentNode?.ReplaceChild(HtmlTextNode.CreateNode("\n"), br);
            }
        }

        return Decode(clone.InnerText);
    }

    private static string Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return HtmlEntity.DeEntitize(text).Replace('\u00a0', ' ');
    }
}
