using System;
using System.IO;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Development trees keep fluxmux_config.json under .vscode.
/// A public install (no workspace file) uses %AppData%\AI-FluxMux.
/// </summary>
public static class FluxMuxConfigPaths
{
    public const string ConfigFileName = "fluxmux_config.json";
    public const string PortRulesFileName = "port_forwarding_rules.json";
    public const string AppFolderName = "AI-FluxMux";
    public const string ProjectFileName = "FluxMux.Avalonia.csproj";

    public static string GetPublicConfigPath(string applicationDataDirectory)
        => Path.Combine(applicationDataDirectory, AppFolderName, ConfigFileName);

    public static string Resolve(
        string baseDirectory,
        string applicationDataDirectory,
        Func<string, bool> fileExists)
    {
        var current = baseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var workspaceConfig = Path.Combine(current, ".vscode", ConfigFileName);
            if (fileExists(workspaceConfig))
            {
                return workspaceConfig;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        var publicPath = GetPublicConfigPath(applicationDataDirectory);
        if (fileExists(publicPath))
        {
            return publicPath;
        }

        current = baseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var projectPath = Path.Combine(current, ProjectFileName);
            if (fileExists(projectPath))
            {
                var workspaceRoot = Directory.GetParent(current)?.FullName ?? current;
                return Path.Combine(workspaceRoot, ".vscode", ConfigFileName);
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return publicPath;
    }

    public static string ResolveSibling(
        string configPath,
        string fileName)
    {
        var directory = Path.GetDirectoryName(configPath);
        return string.IsNullOrWhiteSpace(directory)
            ? fileName
            : Path.Combine(directory, fileName);
    }

    public static string ResolvePortRulesPath(
        string baseDirectory,
        string applicationDataDirectory,
        Func<string, bool> fileExists)
        => ResolveSibling(
            Resolve(baseDirectory, applicationDataDirectory, fileExists),
            PortRulesFileName);
}