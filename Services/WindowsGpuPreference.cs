using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Windows Graphics preference (HKCU UserGpuPreferences). This does not disable a
/// display adapter, so llama-server can still see the main graphics card.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsGpuPreference
{
    public const int PowerSaving = 1;
    public const int HighPerformance = 2;
    public const string MotherboardGraphicsLabel = "Motherboard GPU";
    public const string MainGraphicsCardLabel = "Discrete GPU";
    public const string AffinityModeLabel = "Windows GPU affinity";
    public const string RegistrySubKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string GlobalSettingsName = "DirectXUserGlobalSettings";
    public const string LlamaServerFileName = "llama-server.exe";
    public const string OriginalBackupFileName = "UserGpuPreferences-original.reg";
    public const string LegacyBackupFileName = "UserGpuPreferences-backup.reg";
    public const string BeforeApplyBackupFileName = "UserGpuPreferences-before-apply.reg";

    private static readonly Regex PreferenceNumber = new(@"GpuPreference\s*=\s*(\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> Choices { get; } =
        [MotherboardGraphicsLabel, MainGraphicsCardLabel];

    public static string LabelFor(int preference)
        => preference == PowerSaving ? MotherboardGraphicsLabel : MainGraphicsCardLabel;

    public static int PreferenceForLabel(string? label)
        => string.Equals(label, MotherboardGraphicsLabel, StringComparison.Ordinal)
            ? PowerSaving
            : HighPerformance;

    public static string FormatPreference(int preference)
        => $"GpuPreference={preference};";

    public static int? ParsePreference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = PreferenceNumber.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return int.Parse(match.Groups[1].Value);
    }

    public static bool IsAppPreferenceName(string? name)
        => !string.IsNullOrEmpty(name)
           && !string.Equals(name, GlobalSettingsName, StringComparison.OrdinalIgnoreCase);

    public static bool IsPinnedName(string? name, IReadOnlyList<string>? extraPins = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // llama-server is the process AI-FluxMux launches. Keep it on the main graphics
        // card so a desktop-apps preference does not hide that card from local models.
        if (string.Equals(Path.GetFileName(name), LlamaServerFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (extraPins is null)
        {
            return false;
        }

        foreach (var pin in extraPins)
        {
            if (string.IsNullOrWhiteSpace(pin))
            {
                continue;
            }

            if (string.Equals(name, pin, StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(pin, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static int InferCurrent(string? defaultValue, IEnumerable<(string Name, string Value)>? entries = null)
    {
        var fromDefault = ParsePreference(defaultValue);
        if (fromDefault is PowerSaving or HighPerformance)
        {
            return fromDefault.Value;
        }

        var power = 0;
        var high = 0;
        if (entries is not null)
        {
            foreach (var (name, value) in entries)
            {
                if (!IsAppPreferenceName(name))
                {
                    continue;
                }

                var parsed = ParsePreference(value);
                if (parsed == PowerSaving)
                {
                    power++;
                }
                else if (parsed == HighPerformance)
                {
                    high++;
                }
            }
        }

        return power > high ? PowerSaving : HighPerformance;
    }

    public static bool HasHybridGraphics(IEnumerable<DisplayAdapterListItem>? adapters)
    {
        var motherboard = false;
        var discrete = false;
        if (adapters is null)
        {
            return false;
        }

        foreach (var row in adapters)
        {
            var kind = DisplayAdapterLabeling.ClassifyMemoryKind(row.NameLabel);
            if (kind == DisplayAdapterLabeling.AdapterMemoryKind.SharedSystemRam)
            {
                motherboard = true;
            }
            else if (kind == DisplayAdapterLabeling.AdapterMemoryKind.DedicatedVram)
            {
                discrete = true;
            }
        }

        return motherboard && discrete;
    }

    public static ApplyResult Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: false);
        if (key is null)
        {
            return new ApplyResult
            {
                Preference = HighPerformance,
                Label = LabelFor(HighPerformance)
            };
        }

        var preference = InferCurrent(
            key.GetValue(null) as string,
            EnumerateEntries(key));
        return new ApplyResult
        {
            Preference = preference,
            Label = LabelFor(preference)
        };
    }

    public static ApplyResult Apply(int preference, IReadOnlyList<string>? extraPins = null)
    {
        if (preference is not PowerSaving and not HighPerformance)
        {
            throw new ArgumentOutOfRangeException(nameof(preference), preference, "Use 1 (motherboard graphics) or 2 (main graphics card).");
        }

        var pins = extraPins;
        var appValue = FormatPreference(preference);
        var pinValue = FormatPreference(HighPerformance);
        var rewritten = 0;
        var pinned = 0;
        var skipped = 0;

        using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{RegistrySubKey}.");

        TryBackup(EnumerateEntries(key));
        key.SetValue(null, appValue, RegistryValueKind.String);

        foreach (var name in key.GetValueNames())
        {
            if (!IsAppPreferenceName(name))
            {
                skipped++;
                continue;
            }

            var current = key.GetValue(name) as string ?? string.Empty;
            if (ParsePreference(current) is null)
            {
                skipped++;
                continue;
            }

            string target;
            if (IsPinnedName(name, pins))
            {
                target = pinValue;
                pinned++;
            }
            else
            {
                target = appValue;
            }

            if (!string.Equals(current, target, StringComparison.Ordinal))
            {
                key.SetValue(name, target, RegistryValueKind.String);
                rewritten++;
            }
        }

        return new ApplyResult
        {
            Preference = preference,
            Label = LabelFor(preference),
            Rewritten = rewritten,
            Pinned = pinned,
            Skipped = skipped
        };
    }

    private static IEnumerable<(string Name, string Value)> EnumerateEntries(RegistryKey key)
    {
        foreach (var name in key.GetValueNames())
        {
            yield return (name, key.GetValue(name) as string ?? string.Empty);
        }
    }

    public static string BackupFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AI-FluxMux");

    public static string EscapeRegString(string? value)
        => (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    public static string FormatRegFile(IEnumerable<(string Name, string Value)> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Windows Registry Editor Version 5.00");
        builder.AppendLine();
        builder.AppendLine("[HKEY_CURRENT_USER\\" + RegistrySubKey + "]");
        foreach (var (name, value) in entries)
        {
            var escapedValue = EscapeRegString(value);
            if (string.IsNullOrEmpty(name))
            {
                builder.AppendLine("@=\"" + escapedValue + "\"");
                continue;
            }

            builder.AppendLine("\"" + EscapeRegString(name) + "\"=\"" + escapedValue + "\"");
        }

        return builder.ToString();
    }

    private static void TryBackup(IEnumerable<(string Name, string Value)> entries)
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var snapshot = FormatRegFile(entries);
            var beforeApply = Path.Combine(BackupFolder, BeforeApplyBackupFileName);
            WriteRegSnapshot(beforeApply, snapshot);

            var original = Path.Combine(BackupFolder, OriginalBackupFileName);
            var legacy = Path.Combine(BackupFolder, LegacyBackupFileName);
            if (!File.Exists(original) && !File.Exists(legacy))
            {
                WriteRegSnapshot(original, snapshot);
            }
        }
        catch (IOException)
        {
            // A missing backup must not block the preference write.
        }
        catch (UnauthorizedAccessException)
        {
            // A missing backup must not block the preference write.
        }
    }

    private static void WriteRegSnapshot(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Encoding.Unicode);
        writer.Write(contents);
        writer.Flush();
        stream.Flush(true);
    }

    public sealed class ApplyResult
    {
        public int Preference { get; init; }

        public string Label { get; init; } = MainGraphicsCardLabel;

        public int Rewritten { get; init; }

        public int Pinned { get; init; }

        public int Skipped { get; init; }
    }
}
