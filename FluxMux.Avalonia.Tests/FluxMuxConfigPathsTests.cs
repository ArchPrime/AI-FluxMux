using System.Collections.Generic;
using System.IO;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class FluxMuxConfigPathsTests
{
    [Fact]
    public void Workspace_config_wins_over_appdata()
    {
        var workspace = Path.Combine(@"C:\dev\Workspace", ".vscode", FluxMuxConfigPaths.ConfigFileName);
        var appData = FluxMuxConfigPaths.GetPublicConfigPath(@"C:\Users\demo\AppData\Roaming");
        var exists = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { workspace, appData };
        var resolved = FluxMuxConfigPaths.Resolve(
            @"C:\dev\Workspace\FluxMux.Avalonia\bin\Debug\net10.0",
            @"C:\Users\demo\AppData\Roaming",
            exists.Contains);
        Assert.Equal(workspace, resolved);
    }

    [Fact]
    public void Public_install_uses_appdata_when_no_workspace_file()
    {
        var appData = FluxMuxConfigPaths.GetPublicConfigPath(@"C:\Users\demo\AppData\Roaming");
        var resolved = FluxMuxConfigPaths.Resolve(
            @"C:\Program Files\AI-FluxMux",
            @"C:\Users\demo\AppData\Roaming",
            _ => false);
        Assert.Equal(appData, resolved);
    }

    [Fact]
    public void Dev_tree_without_file_still_targets_workspace_vscode()
    {
        var project = Path.Combine(@"C:\dev\Workspace\FluxMux.Avalonia", FluxMuxConfigPaths.ProjectFileName);
        var expected = Path.Combine(@"C:\dev\Workspace", ".vscode", FluxMuxConfigPaths.ConfigFileName);
        var resolved = FluxMuxConfigPaths.Resolve(
            @"C:\dev\Workspace\FluxMux.Avalonia\bin\Debug\net10.0",
            @"C:\Users\demo\AppData\Roaming",
            path => string.Equals(path, project, System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, resolved);
    }
}