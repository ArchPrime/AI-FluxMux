using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public const string AffinityModeLabel = "Windows GPU affinity - full batch defaults toggle";
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
        // The process name arrives extension-less in most call sites (e.g. "llama-server"
        // from nvidia-smi / ProcessName), so match the filename ignoring the ".exe" suffix.
        if (MatchesLlamaServer(name))
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

    /// <summary>
    /// True when <paramref name="name"/> refers to the llama-server executable, whether it is
    /// given as "llama-server", "llama-server.exe", or a full path. The ".exe" suffix is ignored
    /// because most callers pass the bare process name (nvidia-smi process_name or
    /// Process.ProcessName), which has no extension.
    /// </summary>
    private static bool MatchesLlamaServer(string name)
    {
        var fileName = Path.GetFileName(name);
        var normalizedInput = StripExe(fileName);
        var normalizedPinned = StripExe(LlamaServerFileName);
        return string.Equals(normalizedInput, normalizedPinned, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExe(string value)
        => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;

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

    /// <summary>
    /// Updates the GPU affinity for a single app. This allows the user to reassign an individual
    /// app to a different GPU after a bulk assign.
    /// </summary>
    public static bool ApplyForApp(string appName, int preference)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        // Don't allow changing the affinity for pinned apps (e.g., llama-server).
        if (IsPinnedName(appName))
        {
            return false;
        }

        // The registry key is the full executable path (e.g. "C:\Program Files\...\Archicad.exe"),
        // not just the process name. If we're given a bare process name, resolve the full path
        // from the running process.
        var registryKey = ResolveRegistryKey(appName);
        if (string.IsNullOrWhiteSpace(registryKey))
        {
            return false;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the GPU preference registry key.");

        var target = FormatPreference(preference);
        var currentValue = key.GetValue(registryKey) as string;

        // If the value is already the target, the app is already on the desired GPU.
        // Treat this as success (no change needed) so the UI shows the correct state
        // and offers the restart (to apply the affinity to the running process).
        if (string.Equals(currentValue, target, StringComparison.Ordinal))
        {
            return true;
        }

        key.SetValue(registryKey, target, RegistryValueKind.String);
        return true;
    }

    /// <summary>
    /// Resolves the registry key name for an app. Windows stores GPU affinity under the
    /// full executable path (e.g. "C:\Program Files\Graphisoft\Archicad 29\Archicad.exe"),
    /// not the bare process name. If <paramref name="appName"/> is already a full path,
    /// return it as-is. Otherwise, look up the running process with that name and return
    /// its MainModule file name. Returns null if no match is found.
    /// </summary>
    private static string? ResolveRegistryKey(string appName)
    {
        // If it already looks like a full path (contains a directory separator and ends with .exe),
        // use it directly.
        if (appName.Contains('\\') && appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return appName;
        }

        // Otherwise, try to find a running process with this name and get its full path.
        try
        {
            var processes = Process.GetProcessesByName(appName);
            foreach (var proc in processes)
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Access to MainModule can fail for elevated/system processes; skip.
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // GetProcessesByName can throw if the process name is invalid; ignore.
        }

        return null;
    }

    /// <summary>
    /// Restarts a running app by process name so a GPU-affinity change takes effect immediately.
    /// Finds all running instances with the given (bare) process name, records the first one's
    /// executable path and working directory, kills them all, then relaunches a single instance.
    /// Returns true if the app was successfully restarted, false otherwise (e.g. no running
    /// instance found, or the relaunch failed).
    /// </summary>
    public static bool RestartAppByName(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            System.Diagnostics.Debug.WriteLine($"[RestartApp] No app name provided");
            return false;
        }

        System.Diagnostics.Debug.WriteLine($"[RestartApp] Restarting '{appName}'...");

        Process? template = null;
        string? exePath = null;
        string? workingDir = null;
        var toKill = new List<Process>();

        try
        {
            var processes = Process.GetProcessesByName(appName);
            foreach (var proc in processes)
            {
                try
                {
                    if (template is null)
                    {
                        template = proc;
                        exePath = proc.MainModule?.FileName;
                        workingDir = exePath is null ? null : Path.GetDirectoryName(exePath);
                    }
                    toKill.Add(proc);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RestartApp] MainModule access failed for {appName}: {ex.Message}");
                    toKill.Add(proc);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RestartApp] GetProcessesByName failed for '{appName}': {ex.Message}");
            return false;
        }

        System.Diagnostics.Debug.WriteLine($"[RestartApp] Found {toKill.Count} instance(s) of '{appName}', exePath={exePath}");

        if (toKill.Count == 0 || string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            System.Diagnostics.Debug.WriteLine($"[RestartApp] Cannot restart: no instances or exe not found (exePath={exePath})");
            foreach (var p in toKill)
            {
                try { p.Dispose(); } catch { }
            }
            return false;
        }

        // Kill all running instances. Heavy apps (e.g. ArchiCAD) can take longer than a few
        // seconds to shut down, so use a generous timeout and verify the process actually
        // exited before relaunching.
        var allExited = true;
        foreach (var proc in toKill)
        {
            try
            {
                if (!proc.HasExited)
                {
                    System.Diagnostics.Debug.WriteLine($"[RestartApp] Killing PID {proc.Id} ({proc.ProcessName})...");
                    proc.Kill(entireProcessTree: true);
                    // Wait up to 15 seconds for the process to exit.
                    var exited = proc.WaitForExit(15000);
                    System.Diagnostics.Debug.WriteLine($"[RestartApp] PID {proc.Id} exited={exited}");
                    if (!exited)
                    {
                        allExited = false;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestartApp] Kill failed for PID {proc.Id}: {ex.Message}");
                allExited = false;
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        // If the process didn't fully exit, don't relaunch (would create a duplicate).
        if (!allExited)
        {
            System.Diagnostics.Debug.WriteLine($"[RestartApp] Not all processes exited; skipping relaunch");
            return false;
        }

        // Relaunch a single instance.
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(exePath) ?? string.Empty : workingDir,
                UseShellExecute = true,
            };
            System.Diagnostics.Debug.WriteLine($"[RestartApp] Relaunching {exePath}...");
            Process.Start(startInfo);
            System.Diagnostics.Debug.WriteLine($"[RestartApp] Relaunch succeeded");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RestartApp] Relaunch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restarts a specific running app by its full executable path (e.g.
    /// "C:\Program Files\Graphisoft\Archicad 30\node.exe"). Unlike
    /// <see cref="RestartAppByName"/>, which restarts ALL processes with the given name,
    /// this method only restarts the process(es) whose executable path matches exactly.
    /// This is important for apps like the ArchiCAD MCP server, which runs as node.exe —
    /// restarting by name would kill every node.exe on the system, not just the ArchiCAD one.
    /// Returns true if the app was successfully restarted, false otherwise.
    /// </summary>
    public static bool RestartAppByPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            System.Diagnostics.Debug.WriteLine("[RestartAppByPath] No full path provided");
            return false;
        }

        System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] Restarting '{fullPath}'...");

        string? exePath = null;
        string? workingDir = null;
        var toKill = new List<Process>();

        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (exePath is null)
                        {
                            exePath = path;
                            workingDir = Path.GetDirectoryName(path);
                        }
                        toKill.Add(proc);
                    }
                }
                catch
                {
                    // Access to MainModule can fail for elevated/system processes; skip.
                }
                finally
                {
                    // Don't dispose here — we need the process objects to kill them.
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] GetProcesses failed: {ex.Message}");
            return false;
        }

        if (toKill.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] No running process found for '{fullPath}'");
            return false;
        }

        // Kill all matching processes.
        var allExited = true;
        foreach (var proc in toKill)
        {
            try
            {
                if (!proc.HasExited)
                {
                    System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] Killing PID {proc.Id} ({proc.ProcessName})...");
                    proc.Kill(entireProcessTree: true);
                    var exited = proc.WaitForExit(15000);
                    System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] PID {proc.Id} exited={exited}");
                    if (!exited)
                    {
                        allExited = false;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] Kill failed for PID {proc.Id}: {ex.Message}");
                allExited = false;
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        if (!allExited)
        {
            System.Diagnostics.Debug.WriteLine("[RestartAppByPath] Not all processes exited; skipping relaunch");
            return false;
        }

        // Relaunch a single instance.
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(exePath) ?? string.Empty : workingDir,
                UseShellExecute = true,
            };
            System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] Relaunching {exePath}...");
            Process.Start(startInfo);
            System.Diagnostics.Debug.WriteLine("[RestartAppByPath] Relaunch succeeded");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RestartAppByPath] Relaunch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads the current GPU affinity for a single app. Returns the preference value
    /// (PowerSaving = 1, HighPerformance = 2) or null if the app is not in the registry.
    /// </summary>
    public static int? GetAppPreference(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: false);
        if (key is null)
        {
            return null;
        }

        var value = key.GetValue(appName) as string;
        return ParsePreference(value);
    }

    /// <summary>
    /// A single per-app GPU affinity entry read from the <c>UserGpuPreferences</c> registry.
    /// </summary>
    public sealed record GpuAppAffinity(string AppName, int Preference);

    /// <summary>
    /// Returns every per-app GPU affinity entry in the <c>UserGpuPreferences</c> registry,
    /// covering both apps on the Discrete GPU (HighPerformance = 2) and apps on the
    /// Motherboard GPU (PowerSaving = 1). The global settings entry is excluded.
    /// Returns an empty list when the key does not exist.
    /// </summary>
    public static IReadOnlyList<GpuAppAffinity> GetAllAppAffinities()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: false);
        if (key is null)
        {
            return Array.Empty<GpuAppAffinity>();
        }

        var entries = new List<GpuAppAffinity>();
        foreach (var (name, value) in EnumerateEntries(key))
        {
            if (!IsAppPreferenceName(name))
            {
                continue;
            }

            var preference = ParsePreference(value);
            if (preference is PowerSaving or HighPerformance)
            {
                entries.Add(new GpuAppAffinity(name, preference.Value));
            }
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.AppName, b.AppName));
        return entries;
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
