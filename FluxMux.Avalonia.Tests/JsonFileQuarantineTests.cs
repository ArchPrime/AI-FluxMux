using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class JsonFileQuarantineTests
{
    [Fact]
    public void Corrupt_json_is_renamed_aside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-config-q-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "fluxmux_config.json");
        File.WriteAllText(path, "{ not json");
        var root = JsonFileQuarantine.ReadObjectOrEmpty(path, out var notice);
        Assert.IsType<JsonObject>(root);
        Assert.False(File.Exists(path));
        Assert.Contains("could not read fluxmux_config.json", notice, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".bak-", notice, System.StringComparison.Ordinal);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Missing_file_is_empty_without_notice()
    {
        var root = JsonFileQuarantine.ReadObjectOrEmpty(Path.Combine(Path.GetTempPath(), "no-such-fluxmux-config.json"), out var notice);
        Assert.Empty(root);
        Assert.Equal(string.Empty, notice);
    }
}