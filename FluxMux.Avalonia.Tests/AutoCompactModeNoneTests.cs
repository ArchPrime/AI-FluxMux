using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Regression tests for "Context compaction controlled by: the Client app" (mode "none").
/// When the per-profile settings callback returns null (profile unresolvable) or mode
/// "none", the gateway does NOT run its regular or Port-rules compaction; it only
/// shortens as a last resort once the prompt reaches 95% of the Context, so a turn the
/// Client app did not shorten in time still runs instead of being refused.
/// </summary>
public sealed class AutoCompactModeNoneTests
{
    private static readonly MethodInfo TryApplyForwardCompact =
        typeof(FluxMuxGatewayHost).GetMethod(
            "TryApplyForwardCompact",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(TryApplyForwardCompact));

    private sealed class TempPaths : IDisposable
    {
        public string StatePath { get; }
        public string LastServedPath { get; }
        public string RecommendPath { get; }
        public string CloudBreakerPath { get; }
        public string LocalReloadPath { get; }
        public string LogPath { get; }
        public string ContextPath { get; }
        public string LastRouteExplanationPath { get; }

        public TempPaths()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fluxmux_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            StatePath = Path.Combine(dir, "state.json");
            LastServedPath = Path.Combine(dir, "last_served.json");
            RecommendPath = Path.Combine(dir, "recommend.json");
            CloudBreakerPath = Path.Combine(dir, "cloud_breaker.json");
            LocalReloadPath = Path.Combine(dir, "local_reload.json");
            LogPath = Path.Combine(dir, "log.txt");
            ContextPath = Path.Combine(dir, "context.json");
            LastRouteExplanationPath = Path.Combine(dir, "last_route.json");
        }

        public void Dispose()
        {
            try
            {
                var dir = Path.GetDirectoryName(StatePath);
                if (dir is not null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static FluxMuxGatewayHost CreateHost(
        TempPaths paths,
        Func<string, string, (string Mode, double TriggerPercent, int KeepTurns, int KeepToolResults, int PinUserChars)?>? settingsCallback)
    {
        var host = new FluxMuxGatewayHost(
            paths.StatePath,
            paths.LastServedPath,
            paths.RecommendPath,
            paths.CloudBreakerPath,
            paths.LocalReloadPath,
            paths.LogPath,
            paths.ContextPath,
            paths.LastRouteExplanationPath);
        host.GetLocalAutoCompactSettings = settingsCallback;
        return host;
    }

    private static (JsonObject state, JsonObject payload) BuildOverflowingState()
    {
        var state = new JsonObject
        {
            ["local_preferred_model"] = "test-model",
            ["local_variant"] = "default",
            ["local_context"] = "4096"
        };

        var bigContent = new string('x', 20000);
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are a coding assistant." },
                new JsonObject { ["role"] = "user", ["content"] = "Turn 1" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Reply 1" },
                new JsonObject { ["role"] = "user", ["content"] = "Turn 2" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Reply 2" },
                new JsonObject { ["role"] = "user", ["content"] = "Turn 3" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Reply 3" },
                new JsonObject { ["role"] = "user", ["content"] = "Turn 4" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Reply 4" },
                new JsonObject { ["role"] = "user", ["content"] = bigContent },
                new JsonObject { ["role"] = "assistant", ["content"] = "Reply 5" },
                new JsonObject { ["role"] = "user", ["content"] = "Turn 6" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Reply 6" }
            }
        };

        return (state, payload);
    }

    private static bool InvokeTryApplyForwardCompact(
        FluxMuxGatewayHost host,
        JsonObject payload,
        JsonObject state,
        bool compactOn)
    {
        // measuredPromptTokens is null: these tests exercise the estimate-based path.
        return (bool)TryApplyForwardCompact.Invoke(host, new object[] { payload, state, compactOn, null, false })!;
    }

    [Fact]
    public void Client_app_mode_leaves_a_prompt_below_the_last_resort_line_alone()
    {
        using var paths = new TempPaths();
        // Port rules would compact this (Compact is on and the prompt is long), but the
        // profile leaves shortening to the Client app and the prompt is far below 95%.
        var host = CreateHost(paths, (model, variant) =>
            ("none", 0.60, 6, 8, 2000));

        var (state, payload) = BuildOverflowingState();
        state["local_context"] = "65536";
        var result = InvokeTryApplyForwardCompact(host, payload, state, compactOn: true);

        Assert.False(result, "With the Client app shortening, AI-FluxMux must not compact below the last-resort line.");
    }

    [Fact]
    public void Client_app_mode_still_shortens_as_a_last_resort_when_the_prompt_overflows()
    {
        using var paths = new TempPaths();
        var host = CreateHost(paths, (model, variant) =>
            ("none", 0.80, 6, 8, 2000));

        var (state, payload) = BuildOverflowingState();
        var result = InvokeTryApplyForwardCompact(host, payload, state, compactOn: true);

        Assert.True(result, "A turn over 95% of the Context must be shortened so it runs, even when the Client app controls context compaction.");
    }

    [Fact]
    public void Unresolvable_profile_gets_only_the_last_resort()
    {
        using var paths = new TempPaths();
        var host = CreateHost(paths, settingsCallback: null);

        var (state, payload) = BuildOverflowingState();
        state["local_context"] = "65536";
        Assert.False(InvokeTryApplyForwardCompact(host, payload, state, compactOn: true),
            "An unresolvable profile is treated like 'the Client app': no Port-rules compaction below the last-resort line.");

        var (overState, overPayload) = BuildOverflowingState();
        Assert.True(InvokeTryApplyForwardCompact(host, overPayload, overState, compactOn: true),
            "Above the last-resort line it is still shortened so the turn runs.");
    }

    [Fact]
    public void Port_rules_mode_still_compacts_when_prompt_overflows()
    {
        using var paths = new TempPaths();
        var host = CreateHost(paths, (model, variant) =>
            ("port-rules", 0.80, 6, 8, 2000));

        var (state, payload) = BuildOverflowingState();
        var result = InvokeTryApplyForwardCompact(host, payload, state, compactOn: true);

        Assert.True(result, "Port-rules mode should still apply the Port-rules compaction fallback when the prompt overflows.");
    }

    [Fact]
    public void Per_profile_mode_compacts_when_trigger_exceeded()
    {
        using var paths = new TempPaths();
        var host = CreateHost(paths, (model, variant) =>
            ("per-profile", 0.60, 6, 8, 2000));

        var (state, payload) = BuildOverflowingState();
        var result = InvokeTryApplyForwardCompact(host, payload, state, compactOn: true);

        Assert.True(result, "Per-profile mode should compact when the prompt exceeds the trigger threshold.");
    }
}