using System;

namespace FluxMux.Avalonia.Services;

public static class DisplayAdapterLabeling
{
    public enum AdapterMemoryKind
    {
        Unknown,
        DedicatedVram,
        SharedSystemRam,
        NotApplicable
    }

    public static DisplayAdapterListItem ParseRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new DisplayAdapterListItem();
        }

        string name = ExtractAdapterName(line);
        return new DisplayAdapterListItem
        {
            NameLabel = name,
            StatusLabel = ExtractAdapterStatus(line),
            NotesLabel = FormatNotesColumn(name)
        };
    }

    public static string LabelLine(string line)
    {
        DisplayAdapterListItem row = ParseRow(line);
        if (string.IsNullOrWhiteSpace(row.NameLabel))
        {
            return line;
        }

        string statusSuffix = string.IsNullOrWhiteSpace(row.StatusLabel) ? string.Empty : $" — {row.StatusLabel}";
        if (string.IsNullOrWhiteSpace(row.NotesLabel))
        {
            return $"{row.NameLabel}{statusSuffix}";
        }

        string notes = row.NotesLabel.Replace(Environment.NewLine, "; ", StringComparison.Ordinal);
        return $"{row.NameLabel}{statusSuffix}  ({notes})";
    }

    public static AdapterMemoryKind ClassifyMemoryKind(string friendlyName)
    {
        string name = (friendlyName ?? string.Empty).ToLowerInvariant();
        if (name.Contains("basic display", StringComparison.Ordinal)
            || name.Contains("remote desktop", StringComparison.Ordinal)
            || name.Contains("virtual", StringComparison.Ordinal)
            || name.Contains("microsoft basic", StringComparison.Ordinal))
        {
            return AdapterMemoryKind.NotApplicable;
        }

        if (IsIntegratedGraphics(name))
        {
            return AdapterMemoryKind.SharedSystemRam;
        }

        if (IsDiscreteGraphics(name))
        {
            return AdapterMemoryKind.DedicatedVram;
        }

        return AdapterMemoryKind.Unknown;
    }

    public static string? ClassifyAdapterRole(string friendlyName)
    {
        return ClassifyMemoryKind(friendlyName) switch
        {
            AdapterMemoryKind.SharedSystemRam => "motherboard graphics chip",
            AdapterMemoryKind.DedicatedVram => "main graphics card",
            _ => null
        };
    }

    public static string FormatMemoryNote(AdapterMemoryKind kind)
        => kind switch
        {
            AdapterMemoryKind.DedicatedVram => "dedicated VRAM — local models can use this",
            AdapterMemoryKind.SharedSystemRam => "shared system RAM — not separate VRAM",
            AdapterMemoryKind.NotApplicable => "no dedicated VRAM",
            _ => string.Empty
        };

    public static string FormatNotesColumn(string friendlyName)
    {
        string? role = ClassifyAdapterRole(friendlyName);
        string memoryNote = FormatMemoryNote(ClassifyMemoryKind(friendlyName));
        if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(memoryNote))
        {
            return $"{role}: {memoryNote}";
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            return role;
        }

        return memoryNote;
    }

    private static string ExtractAdapterName(string line)
    {
        int sep = FindStatusSeparatorIndex(line);
        return sep >= 0 ? line[..sep].Trim() : line.Trim();
    }

    private static string ExtractAdapterStatus(string line)
    {
        int sep = FindStatusSeparatorIndex(line);
        if (sep < 0)
        {
            return string.Empty;
        }

        string tail = line[(sep + 3)..].Trim();
        int notesStart = tail.IndexOf("  (", StringComparison.Ordinal);
        return notesStart >= 0 ? tail[..notesStart].Trim() : tail;
    }

    private static int FindStatusSeparatorIndex(string line)
    {
        int sep = line.IndexOf(" — ", StringComparison.Ordinal);
        if (sep < 0)
        {
            sep = line.LastIndexOf(" - ", StringComparison.Ordinal);
        }

        return sep;
    }

    private static bool IsIntegratedGraphics(string nameLower)
    {
        if (nameLower.Contains("uhd", StringComparison.Ordinal)
            || nameLower.Contains("iris", StringComparison.Ordinal)
            || nameLower.Contains("hd graphics", StringComparison.Ordinal)
            || nameLower.Contains("adreno", StringComparison.Ordinal)
            || nameLower.Contains("qualcomm", StringComparison.Ordinal))
        {
            return true;
        }

        if (nameLower.Contains("intel", StringComparison.Ordinal)
            && nameLower.Contains("graphics", StringComparison.Ordinal)
            && !ContainsDiscreteIntelArc(nameLower))
        {
            return true;
        }

        if (nameLower.Contains("amd", StringComparison.Ordinal) && nameLower.Contains("graphics", StringComparison.Ordinal))
        {
            return true;
        }

        return nameLower.Contains("radeon", StringComparison.Ordinal)
            && !nameLower.Contains("radeon rx", StringComparison.Ordinal)
            && !nameLower.Contains("radeon pro", StringComparison.Ordinal);
    }

    private static bool IsDiscreteGraphics(string nameLower)
    {
        if (ContainsDiscreteIntelArc(nameLower))
        {
            return true;
        }

        if (nameLower.Contains("nvidia", StringComparison.Ordinal)
            || nameLower.Contains("geforce", StringComparison.Ordinal)
            || nameLower.Contains("quadro", StringComparison.Ordinal)
            || nameLower.Contains("rtx ", StringComparison.Ordinal)
            || nameLower.Contains("gtx ", StringComparison.Ordinal))
        {
            return true;
        }

        if (nameLower.Contains("radeon rx", StringComparison.Ordinal)
            || nameLower.Contains("radeon pro", StringComparison.Ordinal)
            || nameLower.Contains("rx 5", StringComparison.Ordinal)
            || nameLower.Contains("rx 6", StringComparison.Ordinal)
            || nameLower.Contains("rx 7", StringComparison.Ordinal)
            || nameLower.Contains("rx 9", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsDiscreteIntelArc(string nameLower)
    {
        string[] discrete = ["a310", "a380", "a580", "a750", "a770", "b580", "b570"];
        if (!nameLower.Contains("arc", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string token in discrete)
        {
            if (nameLower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
