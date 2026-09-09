using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ProxyRuntimeStateSecretsTests
{
    [Fact]
    public void Redact_copy_clears_key_fields_and_leaves_the_source_intact()
    {
        var state = new JsonObject
        {
            ["provider"] = "OpenAI",
            ["openai_key"] = "sk-live",
            ["gemini_key"] = "gem-live",
            ["cloud_phase"] = "ready"
        };

        var redacted = ProxyRuntimeStateSecrets.RedactCopy(state);
        Assert.Equal(string.Empty, redacted["openai_key"]?.ToString());
        Assert.Equal(string.Empty, redacted["gemini_key"]?.ToString());
        Assert.Equal("ready", redacted["cloud_phase"]?.ToString());
        Assert.Equal("sk-live", state["openai_key"]?.ToString());
        Assert.True(ProxyRuntimeStateSecrets.LooksLikeSecretDump(state));
        Assert.False(ProxyRuntimeStateSecrets.LooksLikeSecretDump(redacted));
    }

    [Fact]
    public void Capture_and_apply_round_trip_without_writing_stars()
    {
        var state = new JsonObject { ["openai_key"] = "sk-live" };
        var memory = new JsonObject();
        ProxyRuntimeStateSecrets.Capture(state, memory);
        var empty = new JsonObject { ["openai_key"] = string.Empty };
        ProxyRuntimeStateSecrets.Apply(empty, memory);
        Assert.Equal("sk-live", empty["openai_key"]?.ToString());
        ProxyRuntimeStateSecrets.Capture(new JsonObject { ["openai_key"] = "********" }, memory);
        Assert.Equal("sk-live", memory["openai_key"]?.ToString());
    }

    [Fact]
    public void Cloud_label_is_provider_and_model_not_a_test_host_name()
    {
        Assert.Equal("Gemini / gemini-flash", ProxyRuntimeStateSecrets.FormatCloudLabel("Gemini", "gemini-flash"));
        Assert.Equal("OpenAI", ProxyRuntimeStateSecrets.FormatCloudLabel("OpenAI", " "));
        Assert.True(ProxyRuntimeStateSecrets.LastServedWasCloud("cloud"));
        Assert.False(ProxyRuntimeStateSecrets.LastServedWasCloud("local"));
    }
}
