using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Title and optional stable id stashed on the Help topic panel so the index
/// can jump after a Word or feed update renames the Heading 2.
/// </summary>
public sealed class HelpTopicAnchor
{
    public required string Title { get; init; }
    public string Id { get; init; } = string.Empty;
}

public sealed class HelpTopicEntry
{
    public required string Title { get; init; }
    public string Id { get; init; } = string.Empty;
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
    /// The topic a Help.html cross-reference or a setting Help link points at.
    /// A <c>topic.*</c> id is tried first so a Word or feed update can rename the
    /// Heading 2 title. A link written as <c>href="#Topic title"</c> still matches
    /// on the visible Heading 2 text. Typing that address escapes the spaces and a
    /// Word bookmark drops the punctuation, so the fallback comparison keeps letters
    /// and digits only.
    /// </summary>
    public static HelpTopicEntry? FindByReference(IEnumerable<HelpTopicEntry> topics, string? reference)
    {
        var wanted = (reference ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            return null;
        }

        var candidates = topics.Where(topic => topic.IsTopic).ToList();
        var byId = candidates.FirstOrDefault(topic =>
            topic.Id.Length > 0
            && topic.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        var idKey = LettersAndDigits(wanted);
        if (idKey.Length > 0)
        {
            var byIdKey = candidates.FirstOrDefault(topic =>
                topic.Id.Length > 0 && LettersAndDigits(topic.Id) == idKey);
            if (byIdKey is not null)
            {
                return byIdKey;
            }
        }

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
