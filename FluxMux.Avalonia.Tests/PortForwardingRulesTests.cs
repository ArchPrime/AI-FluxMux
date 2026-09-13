using System;
using System.IO;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class PortForwardingRulesTests
{
    [Fact]
    public void Missing_file_loads_code_defaults()
    {
        var store = new PortForwardingRulesStore(
            Path.Combine(Path.GetTempPath(), "no-such-port-rules-" + Guid.NewGuid().ToString("N") + ".json"));
        var rules = store.Load();
        Assert.Equal(PortForwardingRules.Defaults, rules);
        Assert.Equal(string.Empty, store.LastLoadNotice);
    }

    [Fact]
    public void Dirty_file_is_quarantined_and_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-port-rules-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FluxMuxConfigPaths.PortRulesFileName);
        File.WriteAllText(path, "{ not json");
        var store = new PortForwardingRulesStore(path);
        var rules = store.Load();
        Assert.Equal(PortForwardingRules.Defaults.RunawayOmittedResults, rules.RunawayOmittedResults);
        Assert.Contains("could not read", store.LastLoadNotice, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Save_round_trip_keeps_allow_listed_values()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-port-rules-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FluxMuxConfigPaths.PortRulesFileName);
        var store = new PortForwardingRulesStore(path);
        var saved = PortForwardingRules.Defaults with
        {
            CompactWatermarkPercent = 80,
            RapidChurnConsecutive = 5,
            LoadingRetryCount = 4,
            SkipPrefixCacheAfterCompact = false,
            ClientMaxTokensMode = PortForwardingRules.ClientMaxTokensClient,
            StopHygieneMode = PortForwardingRules.StopIgnore,
            MillAtOmittedEnabled = false,
            MaxPicturesEnabled = true
        };
        store.Save(saved);
        var loaded = store.Load();
        Assert.False(loaded.MillAtOmittedEnabled);
        Assert.True(loaded.MaxPicturesEnabled);
        Assert.Equal(80, loaded.CompactWatermarkPercent);
        Assert.Equal(5, loaded.RapidChurnConsecutive);
        Assert.Equal(4, loaded.LoadingRetryCount);
        Assert.False(loaded.SkipPrefixCacheAfterCompact);
        Assert.Equal(PortForwardingRules.ClientMaxTokensClient, loaded.ClientMaxTokensMode);
        Assert.Equal(PortForwardingRules.StopIgnore, loaded.StopHygieneMode);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Clamp_drops_out_of_range_and_unknown_modes()
    {
        var rules = new PortForwardingRules
        {
            CompactWatermarkPercent = 10,
            LoadingRetryCount = 99,
            ClientMaxTokensMode = "mystery",
            StopHygieneMode = "cut"
        }.Clamp();
        Assert.Equal(50, rules.CompactWatermarkPercent);
        Assert.Equal(12, rules.LoadingRetryCount);
        Assert.Equal(PortForwardingRules.ClientMaxTokensProfile, rules.ClientMaxTokensMode);
        Assert.Equal(PortForwardingRules.StopPassThrough, rules.StopHygieneMode);
    }

    [Fact]
    public void Unknown_json_keys_are_ignored()
    {
        var root = new JsonObject
        {
            ["compactWatermarkPercent"] = 70,
            ["LocalProfiles"] = "no",
            ["apiKey"] = "secret"
        };
        var rules = PortForwardingRulesStore.Read(root).Clamp();
        Assert.Equal(70, rules.CompactWatermarkPercent);
        Assert.DoesNotContain("apiKey", PortForwardingRulesStore.Write(rules).ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("LocalProfiles", PortForwardingRulesStore.Write(rules).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_uses_a_lower_watermark_from_the_snapshot()
    {
        var rules = PortForwardingRules.Defaults with { CompactWatermarkPercent = 50 };
        Assert.True(LocalHistoryCompaction.IsNearLimit(9000, 16384, rules));
        Assert.False(LocalHistoryCompaction.IsNearLimit(9000, 16384));
    }

    [Fact]
    public void Rapid_churn_uses_snapshot_thresholds()
    {
        var rules = PortForwardingRules.Defaults with
        {
            RunawayOmittedResults = 10,
            RapidChurnSeconds = 5,
            RapidChurnConsecutive = 2
        };
        Assert.Equal(1, LocalToolResultClearing.NextRapidChurnStreak(10, TimeSpan.FromSeconds(2), 0, rules));
        Assert.Equal(0, LocalToolResultClearing.NextRapidChurnStreak(10, TimeSpan.FromSeconds(6), 1, rules));
        Assert.True(LocalToolResultClearing.ShouldHaltAsRapidChurn(2, rules));
        Assert.False(LocalToolResultClearing.ShouldHaltAsRapidChurn(2));
    }

    [Fact]
    public void Master_off_forwards_as_usual_and_keeps_saved_group_ticks()
    {
        var saved = PortForwardingRules.Defaults with
        {
            Enabled = false,
            CompactEnabled = true,
            MillAtOmittedEnabled = true,
            HangEnabled = true,
            ClientMaxTokensMode = PortForwardingRules.ClientMaxTokensProfile,
            StopHygieneMode = PortForwardingRules.StopIgnore
        };
        var live = saved.ForForwarding();
        Assert.False(live.Enabled);
        Assert.False(live.CompactEnabled);
        Assert.False(live.OmitEnabled);
        Assert.False(live.MaxPicturesEnabled);
        Assert.False(live.MillAtOmittedEnabled);
        Assert.False(live.HangEnabled);
        Assert.False(live.RepeatedCommandEnabled);
        Assert.False(live.DiagnosticDumpEnabled);
        Assert.False(live.Loading503Enabled);
        Assert.Equal(PortForwardingRules.ClientMaxTokensClient, live.ClientMaxTokensMode);
        Assert.Equal(PortForwardingRules.StopPassThrough, live.StopHygieneMode);
        Assert.True(saved.CompactEnabled);
        Assert.True(saved.MillAtOmittedEnabled);
        Assert.Equal(PortForwardingRules.Defaults, PortForwardingRules.Defaults.ForForwarding());
    }

    [Fact]
    public void Legacy_enforceStops_false_turns_off_stops_and_keeps_forwarding()
    {
        var rules = PortForwardingRulesStore.Read(new JsonObject { ["enforceStops"] = false }).Clamp();
        Assert.False(rules.MillAtOmittedEnabled);
        Assert.False(rules.ClosedLoopCeilingEnabled);
        Assert.False(rules.ObserveOnlyMillEnabled);
        Assert.False(rules.RapidChurnEnabled);
        Assert.False(rules.HangEnabled);
        Assert.False(rules.Loading503Enabled);
        Assert.False(rules.RepeatedCommandEnabled);
        Assert.False(rules.DiagnosticDumpEnabled);
        Assert.True(rules.CompactEnabled);
        Assert.True(rules.OmitEnabled);
        Assert.True(rules.MaxPicturesEnabled);
    }

    [Fact]
    public void Mill_at_omitted_off_stays_open_before_the_ceiling()
    {
        var off = PortForwardingRules.Defaults with { MillAtOmittedEnabled = false };
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "watch" }
            }
        };
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted: 117, off));
        Assert.False(LocalToolResultClearing.ShouldHaltAsToolMill(payload, omitted: 128, off));
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(
            payload,
            omitted: LocalToolResultClearing.ClosedLoopRunawayOmittedResults,
            off));
        Assert.True(LocalToolResultClearing.ShouldHaltAsToolMill(
            payload,
            omitted: 32,
            PortForwardingRules.Defaults with { ClosedLoopLookbackEnabled = false }));
        Assert.True(LocalToolResultClearing.ShouldHaltAsRapidChurn(8, PortForwardingRules.Defaults));
        Assert.False(LocalToolResultClearing.ShouldHaltAsRapidChurn(8, PortForwardingRules.Defaults with { RapidChurnEnabled = false }));
    }

    [Fact]
    public void Mills_off_still_drop_older_pictures()
    {
        var off = PortForwardingRules.Defaults with
        {
            MillAtOmittedEnabled = false,
            ObserveOnlyMillEnabled = false,
            ClosedLoopCeilingEnabled = false,
            MaxForwardedImages = 1
        };
        var picture = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "frame" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,aa" } }
        };
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = picture.DeepClone() },
                new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                new JsonObject { ["role"] = "user", ["content"] = picture.DeepClone() },
                new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                new JsonObject { ["role"] = "user", ["content"] = picture.DeepClone() }
            }
        };

        var dropped = LocalChatPayloadSignals.KeepMostRecentImages(payload, off.MaxForwardedImages);
        Assert.True(dropped >= 1);
        Assert.True(off.MaxPicturesEnabled);
    }

    [Fact]
    public void Hang_first_byte_uses_snapshot_think_rate()
    {
        var rules = PortForwardingRules.Defaults with
        {
            ThinkTokensPerSecond = 10,
            FirstByteSeconds = 10,
            MaxThinkFirstByteSeconds = 40
        };
        Assert.Equal(TimeSpan.FromSeconds(30), LocalStreamHangPolicy.FirstByteDeadlineForThinkBudget(300, rules));
        Assert.Equal(
            TimeSpan.FromSeconds(LocalStreamHangPolicy.FirstByteSeconds),
            LocalStreamHangPolicy.FirstByteDeadlineForThinkBudget(300));
    }

    [Fact]
    public void Loading_retry_only_trips_on_daemon_loading_model()
    {
        var rules = PortForwardingRules.Defaults with { LoadingRetryCount = 6, LoadingRetryDelaySeconds = 2 };
        Assert.True(LocalLoadingRetryPolicy.ShouldRetry(503, "Loading model", 0, rules));
        Assert.False(LocalLoadingRetryPolicy.ShouldRetry(503, "overloaded", 0, rules));
        Assert.False(LocalLoadingRetryPolicy.ShouldRetry(400, "Loading model", 0, rules));
        Assert.False(LocalLoadingRetryPolicy.ShouldRetry(503, "Loading model", 6, rules));
    }

    [Fact]
    public void Compact_sets_cache_prompt_false_when_the_checkbox_is_on()
    {
        var payload = new JsonObject();
        LocalPrefixCachePolicy.ApplyAfterCompact(payload, compactApplied: true);
        Assert.False(payload["cache_prompt"]!.GetValue<bool>());

        var keep = new JsonObject();
        LocalPrefixCachePolicy.ApplyAfterCompact(
            keep,
            compactApplied: true,
            PortForwardingRules.Defaults with { SkipPrefixCacheAfterCompact = false });
        Assert.Null(keep["cache_prompt"]);
    }

    [Fact]
    public void Max_tokens_modes_choose_profile_client_or_smaller()
    {
        Assert.Equal(4096, LocalRequestOverlayRouting.ChooseMaxTokens(128000, 4096, PortForwardingRules.ClientMaxTokensProfile));
        Assert.Equal(128000, LocalRequestOverlayRouting.ChooseMaxTokens(128000, 4096, PortForwardingRules.ClientMaxTokensClient));
        Assert.Equal(4096, LocalRequestOverlayRouting.ChooseMaxTokens(128000, 4096, PortForwardingRules.ClientMaxTokensSmaller));
        Assert.Equal(4096, LocalRequestOverlayRouting.ChooseMaxTokens(0, 4096, PortForwardingRules.ClientMaxTokensClient));
    }

    [Fact]
    public void Overlay_apply_honors_client_max_tokens_mode()
    {
        var overlay = new JsonObject { ["max_tokens"] = 4096, ["temperature"] = 0.2 };
        var clientWins = new JsonObject { ["max_tokens"] = 8000 };
        LocalRequestOverlayRouting.Apply(
            clientWins,
            overlay,
            PortForwardingRules.Defaults with { ClientMaxTokensMode = PortForwardingRules.ClientMaxTokensClient },
            clientMaxTokens: 8000);
        Assert.Equal(8000, clientWins["max_tokens"]!.GetValue<int>());

        var profileWins = new JsonObject { ["max_tokens"] = 8000 };
        LocalRequestOverlayRouting.Apply(
            profileWins,
            overlay,
            PortForwardingRules.Defaults,
            clientMaxTokens: 8000);
        Assert.Equal(4096, profileWins["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Stop_hygiene_can_drop_client_stop()
    {
        var payload = new JsonObject { ["stop"] = new JsonArray("\n") };
        LocalStopHygiene.Apply(payload, PortForwardingRules.StopPassThrough);
        Assert.NotNull(payload["stop"]);
        LocalStopHygiene.Apply(payload, PortForwardingRules.StopIgnore);
        Assert.Null(payload["stop"]);
    }

    [Fact]
    public void Port_rules_file_sits_beside_config()
    {
        var config = Path.Combine(@"C:\Users\demo\AppData\Roaming\AI-FluxMux", FluxMuxConfigPaths.ConfigFileName);
        Assert.Equal(
            Path.Combine(@"C:\Users\demo\AppData\Roaming\AI-FluxMux", FluxMuxConfigPaths.PortRulesFileName),
            PortForwardingRulesStore.ResolvePath(config));
    }

    [Fact]
    public void Revert_to_defaults_leaves_the_saved_file_so_Load_can_restore_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxmux-port-rules-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FluxMuxConfigPaths.PortRulesFileName);
        var store = new PortForwardingRulesStore(path);
        store.Save(PortForwardingRules.Defaults with { CompactWatermarkPercent = 60 });
        var vm = new PortForwardingRulesViewModel(store, _ => { });
        vm.Load();
        Assert.Equal(60, (int)vm.CompactWatermarkPercent);

        vm.ResetCommand.Execute(null);
        Assert.Equal(PortForwardingRules.Defaults.CompactWatermarkPercent, (int)vm.CompactWatermarkPercent);
        Assert.Equal(60, store.Load().CompactWatermarkPercent);

        vm.LoadSavedCommand.Execute(null);
        Assert.Equal(60, (int)vm.CompactWatermarkPercent);
        Directory.Delete(dir, recursive: true);
    }
}
