using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Marks control names in operator-facing copy so the UI can bold them.
/// Client-app error text must stay unmarked; Diagnostics strips markers.
/// </summary>
public static class ControlLabelMarkup
{
    public const string Marker = "**";

    public static string Mark(string label)
        => string.IsNullOrWhiteSpace(label) ? string.Empty : Marker + label.Trim() + Marker;

    public static string Strip(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var parts = Parse(source);
        if (parts.Count == 1 && !parts[0].Bold)
        {
            return parts[0].Text;
        }

        var length = 0;
        foreach (var part in parts)
        {
            length += part.Text.Length;
        }

        return string.Create(length, parts, static (span, list) =>
        {
            var offset = 0;
            foreach (var part in list)
            {
                part.Text.AsSpan().CopyTo(span[offset..]);
                offset += part.Text.Length;
            }
        });
    }

    public static List<(string Text, bool Bold)> Parse(string? source)
    {
        var parts = new List<(string Text, bool Bold)>();
        if (string.IsNullOrEmpty(source))
        {
            return parts;
        }

        var i = 0;
        while (i < source.Length)
        {
            var start = source.IndexOf(Marker, i, StringComparison.Ordinal);
            if (start < 0)
            {
                parts.Add((source[i..], false));
                break;
            }

            if (start > i)
            {
                parts.Add((source[i..start], false));
            }

            var end = source.IndexOf(Marker, start + Marker.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                parts.Add((source[start..], false));
                break;
            }

            var inner = source[(start + Marker.Length)..end];
            if (inner.Length > 0)
            {
                parts.Add((inner, true));
            }

            i = end + Marker.Length;
        }

        return parts;
    }
}
