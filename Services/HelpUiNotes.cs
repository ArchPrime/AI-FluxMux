using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Standing UI notes loaded from Help.html Heading 2 "Interface notes".
/// Missing ids keep the XAML / ViewModel fallback.
/// </summary>
public static class HelpUiNotes
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, string> Notes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Values =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Token = new(@"\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static bool _loaded;

    public const string AddressValue = "Address";
    public const string PortValue = "Port";
    public const string ProfileContextValue = "ProfileContext";
    public const string HarnessChatUrlValue = "HarnessChatUrl";

    /// <summary>
    /// Every value a note may name. A note in Help.html that names anything else keeps
    /// showing the braces, so this list is what an operator writing Word can rely on.
    /// </summary>
    public static IReadOnlyList<string> ValueNames { get; } =
        [AddressValue, PortValue, ProfileContextValue, HarnessChatUrlValue];

    public static event EventHandler? Changed;

    public static void Publish(HelpDocument? document)
    {
        IReadOnlyDictionary<string, string> next = document?.UiNotes
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            Notes = next;
            _loaded = true;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void EnsureFromDisk()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }
        }

        var path = HelpHtmlFile.FindNewest();
        if (path is null)
        {
            return;
        }

        try
        {
            Publish(HelpHtmlParser.Load(path));
        }
        catch (Exception)
        {
            // Keep XAML fallbacks.
        }
    }

    /// <summary>
    /// A value a note can name, so the sentence around it can be written in Word while
    /// the value itself stays live. Word carries <c>{Address}</c>; this fills it in.
    /// </summary>
    public static void SetValue(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var text = value ?? string.Empty;
        lock (Gate)
        {
            if (Values.TryGetValue(name, out var current) && string.Equals(current, text, StringComparison.Ordinal))
            {
                return;
            }

            Values[name] = text;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string? Resolve(string? id, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            lock (Gate)
            {
                if (Notes.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    return Fill(text);
                }
            }
        }

        return Fill(fallback);
    }

    /// <summary>
    /// Replaces every <c>{Name}</c> that has a value. A name with no value is left as it
    /// was written, so a typo in Word shows up as itself rather than as a blank.
    /// </summary>
    public static string? Fill(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{'))
        {
            return text;
        }

        return Token.Replace(text, match =>
        {
            lock (Gate)
            {
                return Values.TryGetValue(match.Groups[1].Value, out var value)
                    ? value
                    : match.Value;
            }
        });
    }
}
