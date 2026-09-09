using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ClineContextSyncTests
{
    [Fact]
    public void PointsAtFluxMuxPort_accepts_localhost_and_optional_v1()
    {
        Assert.True(ClineContextSync.PointsAtFluxMuxPort("http://127.0.0.1:5001", 5001));
        Assert.True(ClineContextSync.PointsAtFluxMuxPort("http://localhost:5001/", 5001));
        Assert.True(ClineContextSync.PointsAtFluxMuxPort("http://127.0.0.1:5001/v1", 5001));
        Assert.False(ClineContextSync.PointsAtFluxMuxPort("http://127.0.0.1:5002", 5001));
        Assert.False(ClineContextSync.PointsAtFluxMuxPort("https://openrouter.ai/api/v1", 5001));
        Assert.False(ClineContextSync.PointsAtFluxMuxPort(null, 5001));
    }

    [Fact]
    public void Merge_writes_loaded_context_onto_local_and_leaves_other_providers()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": {
                    "provider": "openai-compatible",
                    "model": "local",
                    "baseUrl": "http://127.0.0.1:5001"
                  }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": {
                    "name": "OpenAI Compatible",
                    "baseUrl": "http://127.0.0.1:5001",
                    "defaultModelId": "local"
                  },
                  "models": {
                    "local": {
                      "name": "local",
                      "maxInputTokens": 128000,
                      "capabilities": ["streaming", "tools", "images"]
                    }
                  }
                },
                "openrouter": {
                  "models": {
                    "keep-me": { "name": "keep-me" }
                  }
                }
              }
            }
            """);

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056),
            modelsPath,
            providersPath);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.False(result.Skipped);
        Assert.Contains("213056", result.Message, StringComparison.Ordinal);
        Assert.Contains("Cline 4.x", result.Message, StringComparison.Ordinal);

        var root = JsonNode.Parse(File.ReadAllText(modelsPath))!.AsObject();
        var local = root["providers"]!["openai-compatible"]!["models"]!["local"]!.AsObject();
        Assert.Equal(213056, local["contextWindow"]!.GetValue<int>());
        Assert.Equal(213056, local["maxInputTokens"]!.GetValue<int>());
        Assert.Equal("streaming", local["capabilities"]![0]!.GetValue<string>());
        Assert.DoesNotContain(local["capabilities"]!.AsArray(), node =>
            (node?.ToString() ?? string.Empty).Equals("images", StringComparison.OrdinalIgnoreCase));
        Assert.False(local["supportsVision"]!.GetValue<bool>());
        Assert.Equal("local", local["name"]!.GetValue<string>());
        Assert.NotNull(root["providers"]!["openrouter"]!["models"]!["keep-me"]);
        Assert.True(File.Exists(modelsPath + ".bak"));
    }

    [Fact]
    public void Merge_is_idempotent_when_context_already_matches()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "http://127.0.0.1:5001", "model": "local" }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:5001", "defaultModelId": "local" },
                  "models": {
                    "local": { "name": "local", "contextWindow": 213056, "maxInputTokens": 213056 }
                  }
                }
              }
            }
            """);

        var first = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056),
            modelsPath,
            providersPath);
        var second = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056),
            modelsPath,
            providersPath);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(second.Changed);
    }

    [Fact]
    public void Merge_skips_when_openai_compatible_points_at_another_server()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "http://127.0.0.1:11434", "model": "local" }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:11434" },
                  "models": { "local": { "maxInputTokens": 128000 } }
                }
              }
            }
            """);

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056),
            modelsPath,
            providersPath);

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        var local = JsonNode.Parse(File.ReadAllText(modelsPath))!
            ["providers"]!["openai-compatible"]!["models"]!["local"]!.AsObject();
        Assert.Equal(128000, local["maxInputTokens"]!.GetValue<int>());
        Assert.Null(local["contextWindow"]);
    }

    [Fact]
    public void Merge_creates_models_json_when_only_providers_point_at_port()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "http://127.0.0.1:5001", "model": "local" }
                }
              }
            }
            """);

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 163840),
            modelsPath,
            providersPath);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        var local = JsonNode.Parse(File.ReadAllText(modelsPath))!
            ["providers"]!["openai-compatible"]!["models"]!["local"]!.AsObject();
        Assert.Equal(163840, local["contextWindow"]!.GetValue<int>());
        Assert.Equal(163840, local["maxInputTokens"]!.GetValue<int>());
    }

    [Fact]
    public void Merge_skips_when_cline_settings_are_missing()
    {
        using var dir = new TempDir();
        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056),
            Path.Combine(dir.Path, "models.json"),
            Path.Combine(dir.Path, "providers.json"));

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.False(File.Exists(Path.Combine(dir.Path, "models.json")));
    }

    [Fact]
    public void Merge_sets_images_capability_from_loaded_profile()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "http://127.0.0.1:5001", "model": "local" }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:5001", "defaultModelId": "local" },
                  "models": { "local": { "name": "local", "maxInputTokens": 128000 } }
                }
              }
            }
            """);

        var on = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 163840, imagesOn: true),
            modelsPath,
            providersPath);
        Assert.True(on.Changed);
        var local = JsonNode.Parse(File.ReadAllText(modelsPath))!
            ["providers"]!["openai-compatible"]!["models"]!["local"]!.AsObject();
        Assert.True(local["supportsVision"]!.GetValue<bool>());
        Assert.Contains(local["capabilities"]!.AsArray(), node =>
            (node?.ToString() ?? string.Empty).Equals("images", StringComparison.OrdinalIgnoreCase));

        var off = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056, imagesOn: false),
            modelsPath,
            providersPath);
        Assert.True(off.Changed);
        local = JsonNode.Parse(File.ReadAllText(modelsPath))!
            ["providers"]!["openai-compatible"]!["models"]!["local"]!.AsObject();
        Assert.False(local["supportsVision"]!.GetValue<bool>());
        Assert.DoesNotContain(local["capabilities"]!.AsArray(), node =>
            (node?.ToString() ?? string.Empty).Equals("images", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Merge_turns_off_cline_auto_condense_and_updates_openai_model_info()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        var statePath = Path.Combine(dir.Path, "globalState.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "http://127.0.0.1:5001", "model": "local" }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:5001", "defaultModelId": "local" },
                  "models": {
                    "local": { "name": "local", "contextWindow": 213056, "maxInputTokens": 213056, "supportsVision": false }
                  }
                }
              }
            }
            """);
        File.WriteAllText(statePath, """
            {
              "useAutoCondense": true,
              "planModeOpenAiModelInfo": { "contextWindow": 128000, "supportsImages": true },
              "actModeOpenAiModelInfo": { "contextWindow": 128000, "supportsImages": true }
            }
            """);

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(5001, 213056, imagesOn: false),
            modelsPath,
            providersPath,
            statePath);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Contains("Auto compact", result.Message, StringComparison.Ordinal);
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        Assert.False(state["useAutoCondense"]!.GetValue<bool>());
        Assert.Equal(213056, state["planModeOpenAiModelInfo"]!["contextWindow"]!.GetValue<int>());
        Assert.Equal(213056, state["planModeOpenAiModelInfo"]!["maxInputTokens"]!.GetValue<int>());
        Assert.Equal("local", state["planModeOpenAiModelInfo"]!["name"]!.GetValue<string>());
        Assert.False(state["planModeOpenAiModelInfo"]!["supportsImages"]!.GetValue<bool>());
        Assert.False(state["actModeOpenAiModelInfo"]!["supportsImages"]!.GetValue<bool>());
        Assert.Null(state["planModeOpenAiModelId"]);
        Assert.Null(state["actModeOpenAiModelId"]);
        Assert.True(File.Exists(statePath + ".bak"));
    }

    [Fact]
    public void Merge_writes_loaded_profile_name_and_keeps_model_id_local()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        var statePath = Path.Combine(dir.Path, "globalState.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "http://127.0.0.1:5001", "model": "local" }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:5001", "defaultModelId": "local" },
                  "models": {
                    "local": { "name": "local", "maxInputTokens": 128000 }
                  }
                }
              }
            }
            """);
        File.WriteAllText(statePath, """
            {
              "planModeOpenAiModelId": "local",
              "actModeOpenAiModelId": "local",
              "planModeOpenAiModelInfo": { "contextWindow": 128000 },
              "actModeOpenAiModelInfo": { "contextWindow": 128000 }
            }
            """);

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(
                5001,
                213056,
                displayName: "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf"),
            modelsPath,
            providersPath,
            statePath);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        var root = JsonNode.Parse(File.ReadAllText(modelsPath))!.AsObject();
        var models = root["providers"]!["openai-compatible"]!["models"]!.AsObject();
        Assert.Equal(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            models["local"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            models["Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            root["providers"]!["openai-compatible"]!["provider"]!["defaultModelId"]!.GetValue<string>());
        Assert.Equal(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            JsonNode.Parse(File.ReadAllText(providersPath))!
                ["providers"]!["openai-compatible"]!["settings"]!["model"]!.GetValue<string>());
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", state["planModeOpenAiModelId"]!.GetValue<string>());
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", state["actModeOpenAiModelId"]!.GetValue<string>());
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", state["planModeOpenAiModelInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", state["actModeOpenAiModelInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_keeps_Cline_stored_profile_label_next_to_local()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": {
                    "baseUrl": "http://127.0.0.1:5001",
                    "model": "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"
                  }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": {
                    "baseUrl": "http://127.0.0.1:5001",
                    "defaultModelId": "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"
                  },
                  "models": {
                    "local": { "name": "local", "maxInputTokens": 128000 },
                    "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)": {
                      "name": "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)",
                      "maxInputTokens": 128000
                    }
                  }
                }
              }
            }
            """);

        var result = ClineContextSync.MergeIntoModelsFile(
            ClineContextSyncOptions.Create(
                5001,
                213056,
                displayName: "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf"),
            modelsPath,
            providersPath);

        Assert.True(result.Success);
        var models = JsonNode.Parse(File.ReadAllText(modelsPath))!
            ["providers"]!["openai-compatible"]!["models"]!.AsObject();
        Assert.NotNull(models["local"]);
        Assert.NotNull(models["Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf"]);
        Assert.NotNull(models["Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"]);

        var ids = ClineContextSync.ReadAdvertisedModelIds(5001, modelsPath, providersPath);
        Assert.Contains("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)", ids);
        Assert.Contains("local", ids);
    }

    [Fact]
    public void ReadAdvertisedModelIds_can_read_while_cline_has_the_file_open()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": {
                    "baseUrl": "http://127.0.0.1:5001",
                    "model": "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"
                  }
                }
              }
            }
            """);
        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:5001", "defaultModelId": "local" },
                  "models": { "local": { "name": "local" } }
                }
              }
            }
            """);

        using var held = new FileStream(
            providersPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        var ids = ClineContextSync.ReadAdvertisedModelIds(5001, modelsPath, providersPath);
        Assert.Contains("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)", ids);
    }

    [Fact]
    public void ReadPortStatus_separates_no_cline_from_cline_pointing_somewhere_else()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");

        // Nothing written yet: Cline has probably never run on this PC.
        Assert.Equal(ClinePortStatus.NotFound, ClineContextSync.ReadPortStatus(5001, modelsPath, providersPath));

        File.WriteAllText(providersPath, """
            {
              "providers": {
                "openai-compatible": {
                  "settings": { "baseUrl": "https://openrouter.ai/api/v1", "model": "local" }
                }
              }
            }
            """);
        Assert.Equal(
            ClinePortStatus.PointsElsewhere,
            ClineContextSync.ReadPortStatus(5001, modelsPath, providersPath));

        File.WriteAllText(modelsPath, """
            {
              "version": 1,
              "providers": {
                "openai-compatible": {
                  "provider": { "baseUrl": "http://127.0.0.1:5001", "defaultModelId": "local" }
                }
              }
            }
            """);
        Assert.Equal(
            ClinePortStatus.PointsAtPort,
            ClineContextSync.ReadPortStatus(5001, modelsPath, providersPath));

        // The same files against a different Port are not a match.
        Assert.Equal(
            ClinePortStatus.PointsElsewhere,
            ClineContextSync.ReadPortStatus(5002, modelsPath, providersPath));
    }

    [Fact]
    public void ReadPortStatus_reads_the_global_state_base_url_too()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        var statePath = Path.Combine(dir.Path, "globalState.json");
        File.WriteAllText(statePath, """
            { "openAiBaseUrl": "http://localhost:5001/v1" }
            """);

        Assert.Equal(
            ClinePortStatus.PointsAtPort,
            ClineContextSync.ReadPortStatus(5001, modelsPath, providersPath, statePath));
    }

    [Fact]
    public void ReadPortStatus_treats_unreadable_settings_as_present_rather_than_throwing()
    {
        using var dir = new TempDir();
        var modelsPath = Path.Combine(dir.Path, "models.json");
        var providersPath = Path.Combine(dir.Path, "providers.json");
        File.WriteAllText(providersPath, "{ this is not json");

        Assert.Equal(
            ClinePortStatus.PointsElsewhere,
            ClineContextSync.ReadPortStatus(5001, modelsPath, providersPath));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fluxmux-cline-sync-" + Guid.NewGuid().ToString("N"));

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
