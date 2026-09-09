using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CustomCloudProviderRegistryTests
{
    [Fact]
    public void EnsureMigrated_lifts_legacy_endpoint_into_default_custom()
    {
        var config = new JsonObject
        {
            ["CustomCompatEndpoint"] = "https://legacy.example/v1"
        };

        Assert.True(CustomCloudProviderRegistry.EnsureMigrated(config));
        Assert.Equal(
            "https://legacy.example/v1",
            CustomCloudProviderRegistry.GetEndpoint(config, CustomCloudProviderRegistry.DefaultCustomProviderName));
        Assert.Contains(
            CustomCloudProviderRegistry.DefaultCustomProviderName,
            CustomCloudProviderRegistry.ListCustomProviderNames(config));
    }

    [Fact]
    public void ListCustomProviderNames_hides_empty_default_custom_slot()
    {
        var config = new JsonObject();
        Assert.True(CustomCloudProviderRegistry.EnsureMigrated(config));
        Assert.DoesNotContain(
            CustomCloudProviderRegistry.DefaultCustomProviderName,
            CustomCloudProviderRegistry.ListCustomProviderNames(config));
        Assert.Contains("Groq", CustomCloudProviderRegistry.ListCustomProviderNames(config));
    }

    [Fact]
    public void TryRegisterNamedProvider_requires_name_and_updates_registry()
    {
        var config = new JsonObject();
        CustomCloudProviderRegistry.EnsureMigrated(config);

        Assert.False(CustomCloudProviderRegistry.TryRegisterNamedProvider(
            config,
            "",
            "https://lab.example/v1",
            updateExisting: true,
            out var missingName));
        Assert.Contains("name", missingName, System.StringComparison.OrdinalIgnoreCase);

        Assert.True(CustomCloudProviderRegistry.TryRegisterNamedProvider(
            config,
            "Work vLLM",
            "https://lab.example/v1",
            updateExisting: false,
            out _));
        Assert.True(CustomCloudProviderRegistry.IsCustomCompat("Work vLLM", config));
        Assert.Contains("Work vLLM", CustomCloudProviderRegistry.ListCustomProviderNames(config));

        Assert.True(CustomCloudProviderRegistry.TryRegisterNamedProvider(
            config,
            "Work vLLM",
            "https://lab.example/v2",
            updateExisting: true,
            out _));
        Assert.Equal("https://lab.example/v2", CustomCloudProviderRegistry.GetEndpoint(config, "Work vLLM"));
    }

    [Fact]
    public void TryAddProvider_keeps_independent_endpoints_and_keys()
    {
        var config = new JsonObject();
        var secrets = new JsonObject();
        CustomCloudProviderRegistry.EnsureMigrated(config, secrets);

        Assert.True(CustomCloudProviderRegistry.TryAddProvider(config, "Private Lab", "https://lab.example/v1", out _));
        Assert.True(CustomCloudProviderRegistry.TryAddProvider(config, "Edge Box", "https://edge.example/v1", out _));

        CustomCloudProviderRegistry.SetApiKey(secrets, "Private Lab", "lab-key");
        CustomCloudProviderRegistry.SetApiKey(secrets, "Edge Box", "edge-key");
        CustomCloudProviderRegistry.SetApiKey(secrets, CustomCloudProviderRegistry.DefaultCustomProviderName, "default-key");

        Assert.Equal("https://lab.example/v1", CustomCloudProviderRegistry.GetEndpoint(config, "Private Lab"));
        Assert.Equal("https://edge.example/v1", CustomCloudProviderRegistry.GetEndpoint(config, "Edge Box"));
        Assert.Equal("lab-key", CustomCloudProviderRegistry.GetApiKey(secrets, config, "Private Lab"));
        Assert.Equal("edge-key", CustomCloudProviderRegistry.GetApiKey(secrets, config, "Edge Box"));
        Assert.Equal("default-key", CustomCloudProviderRegistry.GetApiKey(secrets, config, CustomCloudProviderRegistry.DefaultCustomProviderName));
        Assert.True(CustomCloudProviderRegistry.IsCustomCompat("Private Lab", config));
        Assert.False(CustomCloudProviderRegistry.IsCustomCompat("OpenAI", config));
    }

    [Fact]
    public void TryAddProvider_rejects_built_in_names()
    {
        var config = new JsonObject();
        Assert.False(CustomCloudProviderRegistry.TryAddProvider(config, "OpenAI", "https://example/v1", out var error));
        Assert.Contains("built-in", error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRemoveProvider_blocks_default_slot_and_removes_named()
    {
        var config = new JsonObject();
        var secrets = new JsonObject();
        Assert.True(CustomCloudProviderRegistry.TryAddProvider(config, "Private Lab", "https://lab.example/v1", out _));
        CustomCloudProviderRegistry.SetApiKey(secrets, "Private Lab", "lab-key");

        Assert.False(CustomCloudProviderRegistry.TryRemoveProvider(
            config,
            secrets,
            CustomCloudProviderRegistry.DefaultCustomProviderName,
            out _));
        Assert.True(CustomCloudProviderRegistry.TryRemoveProvider(config, secrets, "Private Lab", out _));
        Assert.False(CustomCloudProviderRegistry.IsCustomCompat("Private Lab", config));
        Assert.True(string.IsNullOrWhiteSpace(CustomCloudProviderRegistry.GetApiKey(secrets, config, "Private Lab")));
    }

    [Fact]
    public void EnsureSecretsMigrated_lifts_legacy_api_key()
    {
        var secrets = new JsonObject { ["CustomCompatApiKey"] = "legacy-secret" };
        Assert.True(CustomCloudProviderRegistry.EnsureSecretsMigrated(secrets));
        Assert.Equal(
            "legacy-secret",
            CustomCloudProviderRegistry.GetApiKey(secrets, null, CustomCloudProviderRegistry.DefaultCustomProviderName));
    }

    [Fact]
    public void EnsureMigrated_seeds_ready_made_openai_compatible_providers()
    {
        var config = new JsonObject();
        Assert.True(CustomCloudProviderRegistry.EnsureMigrated(config));

        Assert.Equal(
            "https://api.groq.com/openai/v1",
            CustomCloudProviderRegistry.GetEndpoint(config, "Groq"));
        Assert.Equal(
            "http://127.0.0.1:11434/v1",
            CustomCloudProviderRegistry.GetEndpoint(config, "Ollama"));
        Assert.Contains("DeepSeek", CustomCloudProviderRegistry.ListCustomProviderNames(config));
        Assert.Contains("Fireworks", CustomCloudProviderRegistry.ListCustomProviderNames(config));
        Assert.Contains("Cerebras", CustomCloudProviderRegistry.ListCustomProviderNames(config));
        Assert.True(CustomCloudProviderRegistry.IsReadyMade("OpenRouter"));

        // Built-in OpenAI-compatible presets cannot be removed.
        Assert.False(CustomCloudProviderRegistry.TryRemoveProvider(config, null, "Groq", out var readyMadeError));
        Assert.Contains("built-in", readyMadeError, System.StringComparison.OrdinalIgnoreCase);

        // Operator-added named customs can still be removed and stay gone across migrate.
        Assert.True(CustomCloudProviderRegistry.TryRegisterNamedProvider(
            config,
            "Private Lab",
            "https://lab.example/v1",
            updateExisting: false,
            out _));
        var listed = CustomCloudProviderRegistry.ListCustomProviderNames(config).ToList();
        Assert.Equal("Private Lab", listed[0]);
        Assert.Contains("Groq", listed);
        Assert.True(listed.IndexOf("Private Lab") < listed.IndexOf("Groq"));
        Assert.True(CustomCloudProviderRegistry.TryRemoveProvider(config, null, "Private Lab", out _));
        Assert.False(CustomCloudProviderRegistry.EnsureMigrated(config));
        Assert.False(CustomCloudProviderRegistry.IsCustomCompat("Private Lab", config));
    }

    [Fact]
    public void EnsureMigrated_does_not_overwrite_existing_ready_made_endpoint()
    {
        var config = new JsonObject
        {
            ["CustomCloudProviders"] = new JsonObject
            {
                ["Groq"] = new JsonObject { ["Endpoint"] = "https://custom.groq.example/v1" }
            }
        };

        Assert.True(CustomCloudProviderRegistry.EnsureMigrated(config));
        Assert.Equal(
            "https://custom.groq.example/v1",
            CustomCloudProviderRegistry.GetEndpoint(config, "Groq"));
    }

    [Fact]
    public void EnsureMigrated_seed_version_bump_adds_new_ready_mades_only()
    {
        var config = new JsonObject
        {
            ["CustomCloudProviders"] = new JsonObject
            {
                ["Groq"] = new JsonObject { ["Endpoint"] = "https://api.groq.com/openai/v1" }
            },
            ["CustomCloudProvidersReadyMadeSeedVersion"] = 1
        };

        Assert.True(CustomCloudProviderRegistry.EnsureMigrated(config));
        Assert.Equal(
            "https://api.groq.com/openai/v1",
            CustomCloudProviderRegistry.GetEndpoint(config, "Groq"));
        Assert.Equal(
            "https://api.fireworks.ai/inference/v1",
            CustomCloudProviderRegistry.GetEndpoint(config, "Fireworks"));
        Assert.False(CustomCloudProviderRegistry.EnsureMigrated(config));
    }
}
