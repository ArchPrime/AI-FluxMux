using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Locates Help.html (or Help.htm) next to the exe, in the working directory, or in the
/// per-user folder. Word "Web Page, Filtered" may save either extension.
/// The per-user copy is where an update lands when the install folder is read-only,
/// which is what happens once AI-FluxMux is installed under Program Files.
/// </summary>
public static class HelpHtmlFile
{
    public static readonly string[] FileNames = ["Help.html", "Help.htm"];

    /// <summary>Writable folder that survives a read-only install folder.</summary>
    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        FluxMuxConfigPaths.AppFolderName);

    public static IReadOnlyList<string> CandidatePaths(
        string? baseDirectory = null,
        string? workingDirectory = null,
        string? userDirectory = null)
    {
        var dirs = new[]
        {
            baseDirectory ?? AppContext.BaseDirectory,
            workingDirectory ?? Directory.GetCurrentDirectory(),
            userDirectory ?? UserDirectory
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !seen.Add(Path.GetFullPath(dir)))
            {
                continue;
            }

            foreach (var name in FileNames)
            {
                paths.Add(Path.Combine(dir, name));
            }
        }

        return paths;
    }

    public static IReadOnlyList<string> ExistingPaths(
        string? baseDirectory = null,
        string? workingDirectory = null,
        string? userDirectory = null)
    {
        return CandidatePaths(baseDirectory, workingDirectory, userDirectory)
            .Where(File.Exists)
            .ToList();
    }

    public static string? FindNewest(
        string? baseDirectory = null,
        string? workingDirectory = null,
        string? userDirectory = null)
    {
        var baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var existing = ExistingPaths(baseDirectory, workingDirectory, userDirectory);
        if (existing.Count == 0)
        {
            return null;
        }

        string? bestPath = null;
        var bestUtc = DateTime.MinValue;
        var bestNextToApp = false;
        foreach (var path in existing)
        {
            DateTime utc;
            try
            {
                utc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                continue;
            }

            var nextToApp = IsUnderDirectory(path, baseDir);
            if (bestPath is null
                || utc > bestUtc
                || (utc == bestUtc && nextToApp && !bestNextToApp))
            {
                bestPath = path;
                bestUtc = utc;
                bestNextToApp = nextToApp;
            }
        }

        return bestPath;
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        return Path.GetFullPath(parent).Equals(directory, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHelpFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var fileName = Path.GetFileName(name);
        return FileNames.Any(candidate => fileName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
