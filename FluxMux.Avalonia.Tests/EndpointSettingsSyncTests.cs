using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class EndpointSettingsSyncTests
{
    [Fact]
    public void Catalog_keeps_built_ins_when_config_omits_the_key()
    {
        var defs = EndpointAdapterCatalog.Resolve(new FluxMuxConfigDocument(new JsonObject()));
        Assert.Contains(defs, item => item.Id == EndpointAdapterCatalog.ClineId && item.Enabled);
        Assert.Contains(defs, item => item.Id == EndpointAdapterCatalog.HarnessId && item.Enabled);
    }

    [Fact]
    public void Catalog_can_disable_a_built_in_and_add_json_merge()
    {
        var listed = new[]
        {
            new JsonObject
            {
                ["id"] = "cline-4x",
                ["kind"] = "cline-4x",
                ["enabled"] = false
            },
            new JsonObject
            {
                ["id"] = "my-client",
                ["kind"] = "json-merge",
                ["enabled"] = true,
                ["path"] = "%USERPROFILE%\\.my-client\\settings.json",
                ["baseUrlPath"] = "baseUrl",
                ["set"] = new JsonObject
                {
                    ["name"] = "{displayName}",
                    ["context"] = "{context}"
                }
            }
        };

        var defs = EndpointAdapterCatalog.Resolve(listed);
        Assert.Contains(defs, item => item.Id == "cline-4x" && !item.Enabled);
        Assert.Contains(defs, item => item.Id == "harness-yaml" && item.Enabled);
        var extra = Assert.Single(defs, item => item.Id == "my-client");
        Assert.Equal("json-merge", extra.Kind);
        Assert.Equal("{displayName}", extra.Set["name"]);
    }

    [Fact]
    public void Json_merge_writes_tokens_when_base_url_points_at_port()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "baseUrl": "http://127.0.0.1:5001",
              "name": "local"
            }
            """);

        var adapter = new JsonMergeEndpointAdapter(new EndpointAdapterDefinition(
            "my-client",
            "json-merge",
            true,
            path,
            true,
            "baseUrl",
            new Dictionary<string, string>
            {
                ["name"] = "{displayName}",
                ["context"] = "{context}",
                ["supportsImages"] = "{imagesOn}"
            }));

        var result = adapter.Apply(new EndpointSettingsSnapshot(
            5001,
            213056,
            213056,
            16384,
            true,
            false,
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            "local",
            false));

        Assert.True(result.Success);
        Assert.True(result.Changed);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", root["name"]!.GetValue<string>());
        Assert.Equal(213056, root["context"]!.GetValue<int>());
        Assert.True(root["supportsImages"]!.GetValue<bool>());
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Json_merge_skips_when_base_url_is_not_this_port()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "baseUrl": "https://openrouter.ai/api/v1" }""");

        var adapter = new JsonMergeEndpointAdapter(new EndpointAdapterDefinition(
            "other",
            "json-merge",
            true,
            path,
            true,
            "baseUrl",
            new Dictionary<string, string> { ["name"] = "{displayName}" }));

        var result = adapter.Apply(SampleSnapshot());
        Assert.True(result.Skipped);
        Assert.Contains("not pointed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Orchestrator_runs_every_enabled_adapter()
    {
        var first = new StubAdapter("a");
        var second = new StubAdapter("b");
        var batch = EndpointSettingsSync.Apply(SampleSnapshot(), [first, second]);
        Assert.Equal(2, batch.Results.Count);
        Assert.True(first.Called);
        Assert.True(second.Called);
    }

    private static EndpointSettingsSnapshot SampleSnapshot()
        => new(5001, 213056, 213056, 16384, true, false, "local", "local", false);

    private sealed class StubAdapter(string id) : IEndpointSettingsAdapter
    {
        public string Id { get; } = id;
        public bool Called { get; private set; }

        public EndpointSettingsSyncResult Apply(EndpointSettingsSnapshot snapshot)
        {
            Called = true;
            return new EndpointSettingsSyncResult(Id, true, true, false, Id + " ok");
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fluxmux-endpoint-sync-" + Guid.NewGuid().ToString("N"));

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
