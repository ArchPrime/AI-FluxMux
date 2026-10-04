using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LlamaServerUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fluxmux-llama-update-" + Guid.NewGuid().ToString("n"));

    public LlamaServerUpdateServiceTests()
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
    public async Task Missing_install_dir_fails_without_downloading()
    {
        var outcome = await LlamaServerUpdateService.UpdateAsync(
            new HttpClient(),
            Path.Combine(_root, "does-not-exist"),
            "https://example.com/llama.zip",
            null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("not set or does not exist", outcome.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_exe_fails_without_downloading()
    {
        var dir = Path.Combine(_root, "no-exe");
        Directory.CreateDirectory(dir);

        var outcome = await LlamaServerUpdateService.UpdateAsync(
            new HttpClient(),
            dir,
            "https://example.com/llama.zip",
            null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("llama-server.exe was not found", outcome.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_zip_url_fails_without_downloading()
    {
        var dir = MakeInstall("empty-url");

        var outcome = await LlamaServerUpdateService.UpdateAsync(
            new HttpClient(),
            dir,
            string.Empty,
            null,
            runningPids: []);

        Assert.False(outcome.Succeeded);
        Assert.Contains("No matching llama-server zip", outcome.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_swaps_in_new_files_and_keeps_backup()
    {
        var dir = MakeInstall("swap");
        File.WriteAllText(Path.Combine(dir, "marker-old.txt"), "old");

        var serverZip = Path.Combine(_root, "server.zip");
        MakeLlamaZip(serverZip, nestedFolder: null, extraFile: "marker-new.txt");
        var cudartZip = Path.Combine(_root, "cudart.zip");
        MakeLlamaZip(cudartZip, nestedFolder: null, extraFile: "cudart64_13.dll");

        using var server = new LocalHttpServer(serverZip);
        using var cudart = new LocalHttpServer(cudartZip);

        var outcome = await LlamaServerUpdateService.UpdateAsync(
            new HttpClient(),
            dir,
            server.Url,
            cudart.Url,
            reportProgress: _ => { },
            runningPids: []);

        Assert.True(outcome.Succeeded, outcome.StatusText);
        Assert.NotNull(outcome.BackupPath);
        Assert.True(Directory.Exists(outcome.BackupPath!), "Backup folder should exist");
        Assert.True(File.Exists(Path.Combine(dir, "llama-server.exe")), "New llama-server.exe should be installed");
        Assert.True(File.Exists(Path.Combine(dir, "marker-new.txt")), "New marker file should be installed");
        Assert.True(File.Exists(Path.Combine(dir, "cudart64_13.dll")), "CUDA DLL should be merged in");
        Assert.True(File.Exists(Path.Combine(outcome.BackupPath!, "marker-old.txt")), "Old marker should be in the backup");
        Assert.False(File.Exists(Path.Combine(dir, "marker-old.txt")), "Old marker should not remain in the new install");
    }

    [Fact]
    public async Task Update_restores_cuda_dll_missing_from_new_zip()
    {
        var dir = MakeInstall("restore");
        File.WriteAllText(Path.Combine(dir, "cublas64_13.dll"), "old-cublas");

        var serverZip = Path.Combine(_root, "server-restore.zip");
        MakeLlamaZip(serverZip, nestedFolder: "nested", extraFile: null);

        using var server = new LocalHttpServer(serverZip);

        var outcome = await LlamaServerUpdateService.UpdateAsync(
            new HttpClient(),
            dir,
            server.Url,
            cudartZipUrl: null,
            reportProgress: _ => { },
            runningPids: []);

        Assert.True(outcome.Succeeded, outcome.StatusText);
        Assert.True(File.Exists(Path.Combine(dir, "llama-server.exe")), "Nested payload should be resolved");
        Assert.True(File.Exists(Path.Combine(dir, "cublas64_13.dll")), "Missing CUDA DLL should be restored from the backup");
    }

    private string MakeInstall(string folderName)
    {
        var dir = Path.Combine(_root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "llama-server.exe"), "stub");
        return dir;
    }

    private static void MakeLlamaZip(string zipPath, string? nestedFolder, string? extraFile)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("llama-server.exe");
        using (var stream = entry.Open())
        {
            stream.Write([0, 1, 2]);
        }

        if (extraFile is not null)
        {
            var extra = archive.CreateEntry(extraFile);
            using var extraStream = extra.Open();
            extraStream.Write([0]);
        }

        if (nestedFolder is not null)
        {
            var nested = archive.CreateEntry(nestedFolder + "/nested-marker.txt");
            using var nestedStream = nested.Open();
            nestedStream.Write([0]);
        }
    }

    private sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _file;

        public string Url { get; }

        public LocalHttpServer(string file)
        {
            _file = file;
            var port = 18000 + new Random().Next(1, 20000);
            var prefix = "http://127.0.0.1:" + port + "/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            Url = prefix + "file.zip";

            var thread = new System.Threading.Thread(() =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = _listener.GetContext();
                    }
                    catch
                    {
                        return;
                    }

                    try
                    {
                        var bytes = File.ReadAllBytes(_file);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/zip";
                        context.Response.OutputStream.Write(bytes);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
            })
            { IsBackground = true };
            thread.Start();
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }
        }
    }
}