using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ProfileSettingsFingerprintTests
{
    [Fact]
    public void Local_fingerprint_treats_images_enabled_as_a_different_profile()
    {
        var withoutVision = SampleLocal(visionEnabled: "Disabled");
        var withVision = SampleLocal(visionEnabled: "Enabled");

        Assert.NotEqual(ProfileSettingsFingerprint.Local(withoutVision), ProfileSettingsFingerprint.Local(withVision));
    }

    [Fact]
    public void Local_fingerprint_treats_missing_images_as_disabled()
    {
        var omitted = SampleLocal(visionEnabled: null);
        omitted.Remove("LocalVisionEnabled");
        var disabled = SampleLocal(visionEnabled: "Disabled");

        Assert.Equal(ProfileSettingsFingerprint.Local(omitted), ProfileSettingsFingerprint.Local(disabled));
    }

    [Fact]
    public void Local_fingerprint_matches_when_only_the_copy_name_would_differ()
    {
        var left = SampleLocal(visionEnabled: "Enabled");
        var right = SampleLocal(visionEnabled: "enabled");

        Assert.Equal(ProfileSettingsFingerprint.Local(left), ProfileSettingsFingerprint.Local(right));
    }

    [Fact]
    public void Local_fingerprint_ignores_the_unused_auto_compress_flag()
    {
        var on = SampleLocal(visionEnabled: "Disabled");
        var off = SampleLocal(visionEnabled: "Disabled");
        off["AutoCompressEnabled"] = false;

        Assert.Equal(ProfileSettingsFingerprint.Local(on), ProfileSettingsFingerprint.Local(off));
    }

    [Fact]
    public void Local_fingerprint_treats_max_tokens_as_a_different_profile()
    {
        var shortReply = SampleLocal(visionEnabled: "Disabled");
        var longReply = SampleLocal(visionEnabled: "Disabled");
        longReply["OverrideMaxTokens"] = "16384";

        Assert.NotEqual(ProfileSettingsFingerprint.Local(shortReply), ProfileSettingsFingerprint.Local(longReply));
    }

    [Fact]
    public void Launch_fingerprint_ignores_max_tokens_and_temperature()
    {
        var baseline = SampleLocal(visionEnabled: "Disabled");
        var overlayOnly = SampleLocal(visionEnabled: "Disabled");
        overlayOnly["OverrideMaxTokens"] = "16384";
        overlayOnly["LocalTemperature"] = "0.15";

        Assert.Equal(LocalLaunchFingerprint.From(baseline), LocalLaunchFingerprint.From(overlayOnly));
    }

    [Fact]
    public void Launch_fingerprint_ignores_reasoning_on_vs_off()
    {
        var off = SampleLocal(visionEnabled: "Disabled");
        var on = SampleLocal(visionEnabled: "Disabled");
        on["LocalReasoning"] = "On";

        Assert.Equal(LocalLaunchFingerprint.From(off), LocalLaunchFingerprint.From(on));
    }

    private static JsonObject SampleLocal(string? visionEnabled)
    {
        var settings = new JsonObject
        {
            ["OverrideContext"] = "60416",
            ["OverrideThreads"] = "12",
            ["LocalThreadsBatch"] = "4",
            ["LocalTemperature"] = "0.3",
            ["LocalGpuOffloadMode"] = "GPU only",
            ["GpuLayers"] = "999",
            ["LocalFlashAttention"] = "Enabled",
            ["LocalKvCacheTypeK"] = "q8_0",
            ["LocalKvCacheTypeV"] = "q8_0",
            ["LocalChatTemplate"] = "Auto",
            ["LocalMultiUserMode"] = "Disabled",
            ["LocalUnbanTokensMode"] = "Disabled",
            ["AutoCompressEnabled"] = true,
            ["OverrideMaxTokens"] = "8192",
            ["LocalBatchSize"] = "1024",
            ["LocalUbatchSize"] = "256",
            ["LocalSpecType"] = "draft-mtp",
            ["LocalCacheReuse"] = "256",
            ["LocalCacheRam"] = "Unlimited",
            ["LocalFit"] = "Enabled",
            ["LocalSwaFull"] = "Enabled",
            ["LocalReasoning"] = "Off",
            ["LocalChatParser"] = "Jinja",
            ["LocalVisionProjectorPath"] = @"C:\models\mmproj-F16.gguf",
            ["LocalVisionMaxImageEdge"] = "1344"
        };
        if (visionEnabled is not null)
        {
            settings["LocalVisionEnabled"] = visionEnabled;
        }

        return settings;
    }
}
