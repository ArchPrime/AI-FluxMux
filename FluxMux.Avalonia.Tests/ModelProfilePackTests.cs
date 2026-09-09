using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ModelProfilePackTests
{
    [Fact]
    public void Build_strips_validate_stamps_and_secrets()
    {
        var config = new FluxMuxConfigDocument(new JsonObject
        {
            ["GeminiApiKey"] = "sk-live",
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["demo.gguf::(defaults)"] = new JsonObject
                {
                    ["OverrideContext"] = "8192",
                    ["EndpointValidatedUtc"] = "2026-01-01T00:00:00Z",
                    ["EndpointWarning"] = "stale"
                }
            }
        });

        var pack = ModelProfilePack.Build(config);

        Assert.Equal(ModelProfilePack.Protocol, pack["protocol"]?.ToString());
        Assert.Equal(ModelProfilePack.Kind, pack["kind"]?.ToString());
        Assert.Null(pack["GeminiApiKey"]);
        var profile = Assert.IsType<JsonObject>(pack[ModelProfilePack.LocalKey]!["demo.gguf::(defaults)"]);
        Assert.Equal("8192", profile["OverrideContext"]?.ToString());
        Assert.Null(profile["EndpointValidatedUtc"]);
        Assert.Null(profile["EndpointWarning"]);
    }

    [Fact]
    public void TryRead_rejects_secrets_scripts_and_live_config()
    {
        Assert.False(ModelProfilePack.TryRead(
            """{"protocol":"FluxMux","kind":"model-profiles","ApiKey":"sk-secret","LocalProfiles":{"a.gguf::(defaults)":{"OverrideContext":"1"}}}""",
            out _,
            out var secretsError));
        Assert.Contains("secret", secretsError, StringComparison.OrdinalIgnoreCase);

        Assert.False(ModelProfilePack.TryRead(
            """{"protocol":"FluxMux","kind":"model-profiles","LocalProfiles":{"a.gguf::(defaults)":{"OverrideContext":"<script>x</script>"}}}""",
            out _,
            out var scriptError));
        Assert.Contains("script", scriptError, StringComparison.OrdinalIgnoreCase);

        Assert.False(ModelProfilePack.TryRead(
            """{"protocol":"FluxMux","kind":"model-profiles","OrchestratorPort":5001,"LocalProfiles":{"a.gguf::(defaults)":{"OverrideContext":"1"}}}""",
            out _,
            out var liveError));
        Assert.Contains("live", liveError, StringComparison.OrdinalIgnoreCase);

        Assert.False(ModelProfilePack.TryRead(
            """{"protocol":"FluxMux","kind":"endpoint-adapters","Port":5001}""",
            out _,
            out var adaptersError));
        Assert.Contains("Endpoint adapters", adaptersError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_rejects_bad_keys_and_nested_objects()
    {
        Assert.False(ModelProfilePack.TryRead(
            """{"protocol":"FluxMux","kind":"model-profiles","LocalProfiles":{"not-a-model::(defaults)":{"OverrideContext":"1"}}}""",
            out _,
            out var keyError));
        Assert.Contains("not a valid local", keyError, StringComparison.Ordinal);

        Assert.False(ModelProfilePack.TryRead(
            """{"protocol":"FluxMux","kind":"model-profiles","LocalProfiles":{"a.gguf::(defaults)":{"nested":{"x":1}}}}""",
            out _,
            out var nestedError));
        Assert.Contains("nested", nestedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_adds_only_non_conflicting_rows()
    {
        var live = Local("keep.gguf::(defaults)", "8192");
        live.Root[ModelProfilePack.LocalKey]!.AsObject()["keep.gguf::Long context"] = new JsonObject
        {
            ["OverrideContext"] = "131072"
        };

        var pack = new JsonObject
        {
            ["protocol"] = ModelProfilePack.Protocol,
            ["kind"] = ModelProfilePack.Kind,
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["keep.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "16384" },
                ["keep.gguf::copy"] = new JsonObject { ["OverrideContext"] = "131072" },
                ["other.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "4096" }
            }
        };

        var result = ModelProfilePack.Apply(live, pack);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.SameNameDifferentSettings);
        Assert.Equal(1, result.SameSettingsDifferentName);
        Assert.Equal("8192", live.Root[ModelProfilePack.LocalKey]!["keep.gguf::(defaults)"]!["OverrideContext"]?.ToString());
        Assert.Null(live.Root[ModelProfilePack.LocalKey]!["keep.gguf::copy"]);
        Assert.Equal("4096", live.Root[ModelProfilePack.LocalKey]!["other.gguf::(defaults)"]!["OverrideContext"]?.ToString());
        Assert.True(result.Changed);
    }

    [Fact]
    public void Apply_treats_identical_row_as_already_present()
    {
        var live = Local("keep.gguf::(defaults)", "8192");
        var pack = ModelProfilePack.Build(live);

        var result = ModelProfilePack.Apply(live, pack);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Apply_allows_same_settings_on_a_different_gguf()
    {
        var live = Local("keep.gguf::(defaults)", "8192");
        var pack = new JsonObject
        {
            ["protocol"] = ModelProfilePack.Protocol,
            ["kind"] = ModelProfilePack.Kind,
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["other.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "8192" }
            }
        };

        var result = ModelProfilePack.Apply(live, pack);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.SameSettingsDifferentName);
        Assert.NotNull(live.Root[ModelProfilePack.LocalKey]!["other.gguf::(defaults)"]);
    }

    [Fact]
    public void Apply_skips_local_row_when_gguf_is_missing()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "keep.gguf"), [0]);

        var live = new FluxMuxConfigDocument(new JsonObject());
        var pack = new JsonObject
        {
            ["protocol"] = ModelProfilePack.Protocol,
            ["kind"] = ModelProfilePack.Kind,
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["keep.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "8192" },
                ["missing.gguf::(defaults)"] = new JsonObject { ["OverrideContext"] = "4096" }
            }
        };

        var result = ModelProfilePack.Apply(live, pack, dir.Path);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.MissingGguf);
        Assert.NotNull(live.Root[ModelProfilePack.LocalKey]!["keep.gguf::(defaults)"]);
        Assert.Null(live.Root[ModelProfilePack.LocalKey]!["missing.gguf::(defaults)"]);
        var skip = Assert.Single(result.SkipDetails);
        Assert.Equal(ModelProfilePackSkip.MissingGguf, skip.Reason);
        Assert.Contains("missing.gguf::(defaults)", skip.Message, StringComparison.Ordinal);
        Assert.Contains("Model Directory", skip.Message, StringComparison.Ordinal);
        Assert.Contains("Open Diagnostics", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_skips_images_on_when_projector_is_missing()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "vision.gguf"), [0]);

        var live = new FluxMuxConfigDocument(new JsonObject());
        var pack = new JsonObject
        {
            ["protocol"] = ModelProfilePack.Protocol,
            ["kind"] = ModelProfilePack.Kind,
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["vision.gguf::text"] = new JsonObject
                {
                    ["OverrideContext"] = "8192",
                    ["LocalVisionEnabled"] = "Off"
                },
                ["vision.gguf::images"] = new JsonObject
                {
                    ["OverrideContext"] = "4096",
                    ["LocalVisionEnabled"] = "On",
                    ["LocalVisionProjectorPath"] = "mmproj-missing.gguf"
                }
            }
        };

        var result = ModelProfilePack.Apply(live, pack, dir.Path);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.MissingProjector);
        Assert.NotNull(live.Root[ModelProfilePack.LocalKey]!["vision.gguf::text"]);
        Assert.Null(live.Root[ModelProfilePack.LocalKey]!["vision.gguf::images"]);
        var skip = Assert.Single(result.SkipDetails);
        Assert.Equal(ModelProfilePackSkip.MissingProjector, skip.Reason);
        Assert.Contains("vision.gguf::images", skip.Message, StringComparison.Ordinal);
        Assert.Contains("mmproj", skip.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_accepts_images_on_when_projector_file_exists()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "vision.gguf"), [0]);
        File.WriteAllBytes(Path.Combine(dir.Path, "mmproj-vision.gguf"), [0]);

        var live = new FluxMuxConfigDocument(new JsonObject());
        var pack = new JsonObject
        {
            ["protocol"] = ModelProfilePack.Protocol,
            ["kind"] = ModelProfilePack.Kind,
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["vision.gguf::images"] = new JsonObject
                {
                    ["OverrideContext"] = "4096",
                    ["LocalVisionEnabled"] = "On",
                    ["LocalVisionProjectorPath"] = "mmproj-vision.gguf"
                }
            }
        };

        var result = ModelProfilePack.Apply(live, pack, dir.Path);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.MissingProjector);
        var stored = Assert.IsType<JsonObject>(live.Root[ModelProfilePack.LocalKey]!["vision.gguf::images"]);
        Assert.True(File.Exists(stored["LocalVisionProjectorPath"]?.ToString()));
    }

    [Fact]
    public void BackupConfigFile_writes_a_sibling_copy()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fluxmux_config.json");
        File.WriteAllText(path, """{"LocalProfiles":{}}""");

        var backup = ModelProfilePack.BackupConfigFile(path);

        Assert.True(File.Exists(backup));
        Assert.Contains(".bak-profiles-", backup, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(backup));
    }

    [Fact]
    public void WritePreImportBackup_writes_a_clean_pack_beside_the_live_config()
    {
        using var dir = new TempDir();
        var configPath = Path.Combine(dir.Path, "fluxmux_config.json");
        File.WriteAllText(configPath, """{"LocalProfiles":{}}""");
        var config = new FluxMuxConfigDocument(new JsonObject
        {
            ["GeminiApiKey"] = "sk-live",
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                ["demo.gguf::(defaults)"] = new JsonObject
                {
                    ["OverrideContext"] = "8192",
                    ["EndpointValidatedUtc"] = "2026-01-01T00:00:00Z"
                }
            }
        });

        var backup = ModelProfilePack.WritePreImportBackup(config, configPath);

        Assert.True(File.Exists(backup));
        Assert.Contains("-pre-import-", Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.True(ModelProfilePack.TryRead(File.ReadAllText(backup), out var pack, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Null(pack["GeminiApiKey"]);
        Assert.Null(pack[ModelProfilePack.LocalKey]!["demo.gguf::(defaults)"]!["EndpointValidatedUtc"]);
        Assert.Equal("8192", pack[ModelProfilePack.LocalKey]!["demo.gguf::(defaults)"]!["OverrideContext"]?.ToString());
    }

    private static FluxMuxConfigDocument Local(string key, string context)
        => new(new JsonObject
        {
            [ModelProfilePack.LocalKey] = new JsonObject
            {
                [key] = new JsonObject { ["OverrideContext"] = context }
            }
        });

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fluxmux-model-pack-" + Guid.NewGuid().ToString("N"));

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
