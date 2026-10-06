using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class FluxMuxConfigServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fluxmux-config-svc-" + Guid.NewGuid().ToString("n"));

    public FluxMuxConfigServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Save_writes_json_to_config_path()
    {
        var configPath = Path.Combine(_root, "fluxmux_config.json");
        var service = new FluxMuxConfigService(configPath);
        var doc = new FluxMuxConfigDocument(new JsonObject { ["WindowX"] = 120 });

        service.Save(doc);

        Assert.True(File.Exists(configPath));
        var text = File.ReadAllText(configPath);
        Assert.Contains("\"WindowX\"", text, StringComparison.Ordinal);
        Assert.Contains("120", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_writes_a_rolling_prev_backup_of_the_previous_good_config()
    {
        var configPath = Path.Combine(_root, "fluxmux_config.json");
        var service = new FluxMuxConfigService(configPath);

        // First save: no previous config yet, so no .prev is created.
        service.Save(new FluxMuxConfigDocument(new JsonObject { ["WindowX"] = 1 }));
        Assert.False(File.Exists(configPath + ".prev"));

        // Second save: the previous good config must be preserved as .prev.
        service.Save(new FluxMuxConfigDocument(new JsonObject { ["WindowX"] = 2 }));
        Assert.True(File.Exists(configPath + ".prev"));
        var prevText = File.ReadAllText(configPath + ".prev");
        Assert.Contains("\"WindowX\"", prevText, StringComparison.Ordinal);
        Assert.Contains("1", prevText, StringComparison.Ordinal);

        // The live config holds the newest value.
        var liveText = File.ReadAllText(configPath);
        Assert.Contains("2", liveText, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_restores_the_last_good_backup_when_the_live_config_is_corrupt()
    {
        var configPath = Path.Combine(_root, "fluxmux_config.json");
        var service = new FluxMuxConfigService(configPath);

        // Establish a known-good config (this also writes the .prev backup).
        service.Save(new FluxMuxConfigDocument(new JsonObject { ["LlamaServerDir"] = "C:\\llama", ["ModelDirectory"] = "C:\\models" }));
        service.Save(new FluxMuxConfigDocument(new JsonObject { ["LlamaServerDir"] = "C:\\llama", ["ModelDirectory"] = "C:\\models", ["ActiveLocalModel"] = "qwen" }));

        // Simulate a kernel failure that corrupts the live file mid-write.
        File.WriteAllText(configPath, "{ not valid json");

        // Load must recover from the .prev backup instead of starting empty.
        var restored = service.Load();
        Assert.False(string.IsNullOrWhiteSpace(service.LastLoadNotice));
        Assert.Contains("backup", service.LastLoadNotice, StringComparison.OrdinalIgnoreCase);
        // The restored values come from the last good copy.
        Assert.Equal("C:\\llama", restored.GetString("LlamaServerDir"));
        Assert.Equal("C:\\models", restored.GetString("ModelDirectory"));
    }

    [Fact]
    public void Load_starts_empty_when_neither_live_config_nor_backup_exist()
    {
        var configPath = Path.Combine(_root, "fluxmux_config.json");
        var service = new FluxMuxConfigService(configPath);

        var doc = service.Load();
        Assert.Equal(string.Empty, service.LastLoadNotice);
        Assert.Equal(string.Empty, doc.GetString("LlamaServerDir"));
    }

    [Fact]
    public void Save_does_not_throw_when_config_file_is_locked_by_another_process()
    {
        // Reproduces the startup crash: another process holds fluxmux_config.json open,
        // so File.WriteAllText on the target throws IOException. Save must survive.
        var configPath = Path.Combine(_root, "fluxmux_config.json");
        File.WriteAllText(configPath, "{}");

        // Hold the file open with a write lock, exactly like a second instance / editor would.
        using var locker = new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var service = new FluxMuxConfigService(configPath);
        var doc = new FluxMuxConfigDocument(new JsonObject { ["WindowX"] = 42 });

        // Must not throw, even though the target stays locked for the whole attempt.
        var ex = Record.Exception(() => service.Save(doc));
        Assert.Null(ex);

        // No stray temp files should be left behind.
        var strays = Directory.GetFiles(_root, "fluxmux_config.json.tmp-*");
        Assert.Empty(strays);
    }

    [Fact]
    public void Save_succeeds_once_the_lock_is_released()
    {
        var configPath = Path.Combine(_root, "fluxmux_config.json");
        File.WriteAllText(configPath, "{}");

        // Lock the file, then release it after a short delay so a retry can win.
        var locker = new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var releaser = new Thread(() =>
        {
            Thread.Sleep(300);
            locker.Dispose();
        })
        { IsBackground = true };
        releaser.Start();

        var service = new FluxMuxConfigService(configPath);
        var doc = new FluxMuxConfigDocument(new JsonObject { ["WindowY"] = 77 });

        var ex = Record.Exception(() => service.Save(doc));
        Assert.Null(ex);

        releaser.Join(2000);
        var text = File.ReadAllText(configPath);
        Assert.Contains("\"WindowY\"", text, StringComparison.Ordinal);
        Assert.Contains("77", text, StringComparison.Ordinal);
    }
}