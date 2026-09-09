using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;

namespace FluxMux.Avalonia.Views;

public sealed class HelpTopicEntry
{
    public required string Title { get; init; }
    public Control? Target { get; init; }
    public string SearchText { get; init; } = string.Empty;

    /// <summary>Heading 1 banner this topic sits under, so the index can group long lists.</summary>
    public string Section { get; init; } = string.Empty;

    /// <summary>True for a banner row, which labels the topics beneath it and cannot be picked.</summary>
    public bool IsSection { get; init; }

    public bool IsTopic => !IsSection;
}

/// <summary>
/// Filtering and grouping for the Help tab index.
/// </summary>
public static class HelpTopicSearch
{
    /// <summary>
    /// The topic a Help.html cross-reference points at. A link is written as
    /// <c>href="#Topic title"</c> and matched on the visible Heading 2 text, because Word
    /// cannot be asked to keep a generated anchor id in step with the topic it names.
    /// Typing that address escapes the spaces and a Word bookmark drops the punctuation,
    /// so the fallback comparison keeps letters and digits only.
    /// </summary>
    public static HelpTopicEntry? FindByReference(IEnumerable<HelpTopicEntry> topics, string? reference)
    {
        var wanted = (reference ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            return null;
        }

        var candidates = topics.Where(topic => topic.IsTopic).ToList();
        var exact = candidates.FirstOrDefault(topic =>
            topic.Title.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var key = LettersAndDigits(wanted);
        if (key.Length == 0)
        {
            return null;
        }

        return candidates.FirstOrDefault(topic => LettersAndDigits(topic.Title) == key)
            ?? candidates.FirstOrDefault(topic =>
                LettersAndDigits(topic.Title).StartsWith(key, StringComparison.Ordinal));
    }

    private static string LettersAndDigits(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                text.Append(char.ToLowerInvariant(ch));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Puts a banner row above each run of topics so the index reads as a few short lists
    /// rather than one long one. Sections with nothing left after a search are dropped.
    /// </summary>
    public static List<HelpTopicEntry> GroupBySection(IEnumerable<HelpTopicEntry> topics)
    {
        var display = new List<HelpTopicEntry>();
        var section = string.Empty;
        foreach (var topic in topics)
        {
            if (topic.IsSection)
            {
                continue;
            }

            if (!string.Equals(topic.Section, section, StringComparison.Ordinal))
            {
                section = topic.Section;
                if (section.Length > 0)
                {
                    display.Add(new HelpTopicEntry
                    {
                        Title = section,
                        Section = section,
                        IsSection = true
                    });
                }
            }

            display.Add(topic);
        }

        return display;
    }

    /// <summary>Word-AND match. Empty query matches everything.</summary>
    public static bool Matches(string searchText, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (searchText.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
