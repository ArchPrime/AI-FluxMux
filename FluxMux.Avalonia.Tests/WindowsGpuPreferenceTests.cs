using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class WindowsGpuPreferenceTests
{
    [Theory]
    [InlineData("GpuPreference=1;", 1)]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("SpecificAdapter=VEN&DEV;GpuPreference=1;", 1)]
    [InlineData("", null)]
    [InlineData("SwapEffectUpgradeEnable=1;", null)]
    public void ParsePreference_reads_the_windows_graphics_value(string value, int? expected)
    {
        Assert.Equal(expected, WindowsGpuPreference.ParsePreference(value));
    }

    [Fact]
    public void InferCurrent_prefers_the_key_default_over_per_app_entries()
    {
        var inferred = WindowsGpuPreference.InferCurrent(
            "GpuPreference=1;",
            [("C:\\app.exe", "GpuPreference=2;")]);

        Assert.Equal(WindowsGpuPreference.PowerSaving, inferred);
        Assert.Equal(WindowsGpuPreference.MotherboardGraphicsLabel, WindowsGpuPreference.LabelFor(inferred));
    }

    [Fact]
    public void InferCurrent_uses_the_majority_when_default_is_empty()
    {
        var inferred = WindowsGpuPreference.InferCurrent(
            "",
            [
                ("C:\\a.exe", "GpuPreference=1;"),
                ("C:\\b.exe", "GpuPreference=1;"),
                ("C:\\c.exe", "GpuPreference=2;")
            ]);

        Assert.Equal(WindowsGpuPreference.PowerSaving, inferred);
    }

    [Theory]
    [InlineData(@"C:\Tools\llama.cpp\llama-server.exe", true)]
    [InlineData(@"D:\models\bin\llama-server.exe", true)]
    [InlineData(@"C:\Program Files\ExampleApp\ExampleApp.exe", false)]
    public void IsPinnedName_keeps_llama_server_on_the_main_card(string name, bool pinned)
    {
        Assert.Equal(pinned, WindowsGpuPreference.IsPinnedName(name));
    }

    [Fact]
    public void IsPinnedName_can_keep_a_caller_supplied_path_on_the_main_card()
    {
        Assert.True(WindowsGpuPreference.IsPinnedName(
            @"C:\LocalServer\llama-server.exe",
            [@"C:\LocalServer\llama-server.exe"]));
    }

    [Fact]
    public void IsAppPreferenceName_skips_the_global_settings_value()
    {
        Assert.False(WindowsGpuPreference.IsAppPreferenceName(WindowsGpuPreference.GlobalSettingsName));
        Assert.True(WindowsGpuPreference.IsAppPreferenceName(@"C:\Windows\explorer.exe"));
        Assert.False(WindowsGpuPreference.IsAppPreferenceName(""));
    }

    [Fact]
    public void HasHybridGraphics_needs_motherboard_and_main_card()
    {
        Assert.False(WindowsGpuPreference.HasHybridGraphics(
        [
            new DisplayAdapterListItem { NameLabel = "NVIDIA GeForce RTX 4090" }
        ]));
        Assert.True(WindowsGpuPreference.HasHybridGraphics(
        [
            new DisplayAdapterListItem { NameLabel = "NVIDIA GeForce RTX 4090" },
            new DisplayAdapterListItem { NameLabel = "Intel(R) UHD Graphics 630" }
        ]));
    }

    [Fact]
    public void FormatRegFile_exports_the_default_and_app_values()
    {
        var text = WindowsGpuPreference.FormatRegFile(
        [
            ("", "GpuPreference=1;"),
            (@"C:\Games\App.exe", "GpuPreference=2;")
        ]);

        Assert.Contains("Windows Registry Editor Version 5.00", text, StringComparison.Ordinal);
        Assert.Contains("[HKEY_CURRENT_USER\\" + WindowsGpuPreference.RegistrySubKey + "]", text, StringComparison.Ordinal);
        Assert.Contains("@=\"GpuPreference=1;\"", text, StringComparison.Ordinal);
        Assert.Contains("\"C:\\\\Games\\\\App.exe\"=\"GpuPreference=2;\"", text, StringComparison.Ordinal);
    }
}
