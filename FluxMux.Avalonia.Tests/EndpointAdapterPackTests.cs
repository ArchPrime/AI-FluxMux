using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class EndpointAdapterPackTests
{
    [Fact]
    public void Build_writes_portable_pack_without_profiles()
    {
        var config = new FluxMuxConfigDocument(new JsonObject
        {
            ["LocalProfiles"] = new JsonObject
            {
                ["demo.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "8192" }
            },
            [EndpointAdapterCatalog.ConfigKey] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "my-client",
                    ["kind"] = "json-merge",
                    ["enabled"] = true,
                    ["path"] = "%USERPROFILE%\\.my-client\\settings.json"
                }
            }
        });

        var pack = EndpointAdapterPack.Build(config, 5001, "Cline", 3080);

        Assert.Equal(EndpointAdapterPack.Protocol, pack["protocol"]?.ToString());
        Assert.Equal(EndpointAdapterPack.Kind, pack["kind"]?.ToString());
        Assert.Equal(5001, pack["Port"]?.GetValue<int>());
        Assert.Equal("Cline", pack["EndpointApp"]?.ToString());
        Assert.Equal(3080, pack["HarnessWebPort"]?.GetValue<int>());
        Assert.Null(pack["LocalProfiles"]);
        Assert.Null(pack["CloudKeys"]);
        Assert.NotNull(pack[EndpointAdapterCatalog.ConfigKey] as JsonArray);
        Assert.Equal("my-client", pack[EndpointAdapterCatalog.ConfigKey]![0]!["id"]?.ToString());
    }

    [Fact]
    public void TryRead_accepts_example_file_shape()
    {
        var json = """
            {
              "notes": "copy objects, do not replace the whole config",
              "EndpointAdapters": [
                { "id": "example-openai-client", "kind": "json-merge", "enabled": false, "path": "x.json" }
              ]
            }
            """;

        Assert.True(EndpointAdapterPack.TryRead(json, out var pack, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("example-openai-client", pack[EndpointAdapterCatalog.ConfigKey]![0]!["id"]?.ToString());
    }

    [Fact]
    public void TryRead_rejects_secrets_and_foreign_protocol()
    {
        Assert.False(EndpointAdapterPack.TryRead("""{"ApiKey":"sk-secret","Port":5001}""", out _, out var secretsError));
        Assert.Contains("secrets", secretsError, StringComparison.OrdinalIgnoreCase);

        Assert.False(EndpointAdapterPack.TryRead("""{"protocol":"Other","kind":"endpoint-adapters","Port":5001}""", out _, out var protocolError));
        Assert.Contains("AI-FluxMux", protocolError, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_merges_endpoint_keys_only_and_accepts_orchestrator_port()
    {
        var config = new FluxMuxConfigDocument(new JsonObject
        {
            ["OrchestratorPort"] = 8080,
            ["EndpointApp"] = "Other",
            ["LocalProfiles"] = new JsonObject
            {
                ["keep.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "4096" }
            }
        });

        var pack = new JsonObject
        {
            ["protocol"] = EndpointAdapterPack.Protocol,
            ["kind"] = EndpointAdapterPack.Kind,
            ["OrchestratorPort"] = 5001,
            ["EndpointApp"] = "Cline",
            ["HarnessWebPort"] = 3080,
            ["LocalProfiles"] = new JsonObject
            {
                ["ignore.gguf::(defaults)"] = new JsonObject()
            },
            [EndpointAdapterCatalog.ConfigKey] = new JsonArray
            {
                new JsonObject { ["id"] = "cline-4x", ["kind"] = "cline-4x", ["enabled"] = false }
            }
        };

        var message = EndpointAdapterPack.Apply(config, pack);

        Assert.Equal(5001, config.GetInt("OrchestratorPort"));
        Assert.Equal("Cline", config.GetString("EndpointApp"));
        Assert.Equal(3080, config.GetInt("HarnessWebPort"));
        Assert.Equal("keep.gguf::(defaults)", Assert.Single(config.GetObjectKeys("LocalProfiles")));
        Assert.False(config.GetObjectArray(EndpointAdapterCatalog.ConfigKey)[0]["enabled"]!.GetValue<bool>());
        Assert.Contains("Model profiles in that file were ignored", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_leaves_adapters_alone_when_pack_omits_the_array()
    {
        var config = new FluxMuxConfigDocument(new JsonObject
        {
            [EndpointAdapterCatalog.ConfigKey] = new JsonArray
            {
                new JsonObject { ["id"] = "keep-me", ["kind"] = "json-merge", ["path"] = "a.json" }
            }
        });

        EndpointAdapterPack.Apply(config, new JsonObject { ["Port"] = 5001 });

        Assert.Equal("keep-me", config.GetObjectArray(EndpointAdapterCatalog.ConfigKey)[0]["id"]?.ToString());
    }

    [Fact]
    public void BackupConfigFile_writes_a_sibling_copy()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fluxmux_config.json");
        File.WriteAllText(path, """{"OrchestratorPort":5001}""");

        var backup = EndpointAdapterPack.BackupConfigFile(path);

        Assert.True(File.Exists(backup));
        Assert.Contains(".bak-endpoint-", backup, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(backup));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fluxmux-endpoint-pack-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
