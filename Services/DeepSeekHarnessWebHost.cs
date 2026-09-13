using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public readonly record struct DeepSeekHarnessLaunchResult(
    bool Success,
    string Mode,
    int? ProcessId,
    string Message);

public sealed class DeepSeekHarnessWebHost : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private string _launchMode = string.Empty;

    public event Action? ProcessExited;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public bool IsManagedRunning(int webPort)
    {
        if (IsRunning)
        {
            return true;
        }

        // Keep this hot-path cheap: no netstat / reconcile. Background reconcile upgrades PID records.
        return HasFluxMuxHarnessRecordOnPort(webPort);
    }

    public static bool HasFluxMuxHarnessRecordOnPort(int webPort)
    {
        if (!TryReadPidRecord(out var recordedPid, out var recordedPort))
        {
            return false;
        }

        if (recordedPort != webPort)
        {
            return false;
        }

        if (recordedPid <= 0)
        {
            // Phantom PID 0 must not trigger netstat here; ReconcilePidRecordForPort upgrades it.
            if (!IsPortListening(webPort))
            {
                ClearPidRecord();
            }

            return false;
        }

        try
        {
            using var process = Process.GetProcessById(recordedPid);
            if (!process.HasExited)
            {
                return true;
            }
        }
        catch
        {
        }

        if (IsPortListening(webPort))
        {
            // Launcher PID died but the UI listener is still up.
            return true;
        }

        ClearPidRecord();
        return false;
    }

    public static bool ReconcilePidRecordForPort(int webPort)
    {
        if (!TryReadPidRecord(out var recordedPid, out var recordedPort) || recordedPort != webPort)
        {
            return false;
        }

        if (IsPortListening(webPort))
        {
            if (recordedPid > 0)
            {
                try
                {
                    using var process = Process.GetProcessById(recordedPid);
                    if (!process.HasExited)
                    {
                        return false;
                    }
                }
                catch
                {
                }

                if (TryFindPortListenerPid(webPort, out var replacementPid))
                {
                    WritePidRecord(replacementPid, webPort);
                }

                return false;
            }

            if (TryFindPortListenerPid(webPort, out var listenerPid))
            {
                WritePidRecord(listenerPid, webPort);
                return false;
            }

            ClearPidRecord();
            return true;
        }

        ClearPidRecord();
        return true;
    }

    public static bool TryAdoptFluxMuxHarnessOnPort(int webPort, bool uiReachable = false)
    {
        if (HasFluxMuxHarnessRecordOnPort(webPort))
        {
            return true;
        }

        if (!IsPortListening(webPort))
        {
            // HTTP probe alone must not invent a managed PID 0 record.
            _ = uiReachable;
            return false;
        }

        if (!LaunchLogIndicatesFluxMuxManaged(webPort))
        {
            return false;
        }

        if (!TryFindPortListenerPid(webPort, out var listenerPid))
        {
            return false;
        }

        WritePidRecord(listenerPid, webPort);
        return true;
    }

    public static bool LaunchLogIndicatesFluxMuxManaged(int webPort)
    {
        var portToken = "web --port " + Math.Clamp(webPort, 1, 65535).ToString(CultureInfo.InvariantCulture);
        try
        {
            var logPath = ResolveLaunchLogPath();
            if (!File.Exists(logPath))
            {
                return false;
            }

            foreach (var line in File.ReadAllLines(logPath).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal)
                    && line.Contains(portToken, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                if (_process is null || _process.HasExited)
                {
                    return null;
                }

                return _process.Id;
            }
        }
    }

    public string LaunchMode
    {
        get
        {
            lock (_gate)
            {
                return _launchMode;
            }
        }
    }

    public static string BuildWebArguments(int webPort)
    {
        var port = Math.Clamp(webPort, 1, 65535);
        return "web --port " + port.ToString(CultureInfo.InvariantCulture) + " --no-open";
    }

    public static string ResolveLaunchLogPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh",
            "fluxmux-harness-launch.log");

    public DeepSeekHarnessLaunchResult TryStart(int webPort, string? dshExecutablePath = null)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return new DeepSeekHarnessLaunchResult(
                    false,
                    _launchMode,
                    _process.Id,
                    "DeepSeek Harness web is already running (PID "
                    + _process.Id.ToString(CultureInfo.InvariantCulture)
                    + ").");
            }

            if (IsPortListening(webPort))
            {
                return new DeepSeekHarnessLaunchResult(
                    false,
                    _launchMode,
                    null,
                    "DeepSeek Harness web is already listening on "
                    + DeepSeekHarnessSetup.BuildChatUrl(webPort)
                    + ". Use the open Harness tab instead of starting a second dsh.");
            }

            var (fileName, argumentPrefix, mode) = ResolveLauncher(dshExecutablePath);
            var arguments = ComposeArguments(argumentPrefix, webPort);
            try
            {
                var logPath = ResolveLaunchLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(
                    logPath,
                    $"[{DateTime.Now:u}] {fileName} {arguments}{Environment.NewLine}",
                    Encoding.UTF8);

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.Environment[DeepSeekHarnessSetup.ApiKeyEnvVar] = DeepSeekHarnessSetup.DummyApiKey;

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    return new DeepSeekHarnessLaunchResult(
                        false,
                        mode,
                        null,
                        "Windows did not start DeepSeek Harness. Install dsh or Node.js (npx @deepseek-ai/dsh).");
                }

                process.OutputDataReceived += (_, e) => AppendLaunchLog(logPath, e.Data);
                process.ErrorDataReceived += (_, e) => AppendLaunchLog(logPath, e.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExited;
                _process = process;
                _launchMode = mode;
                WritePidRecord(process.Id, webPort);
                return new DeepSeekHarnessLaunchResult(
                    true,
                    mode,
                    process.Id,
                    "Started DeepSeek Harness web (" + mode + ", PID "
                    + process.Id.ToString(CultureInfo.InvariantCulture)
                    + "). Waiting for the chat UI to respond…");
            }
            catch (Win32Exception ex)
            {
                return new DeepSeekHarnessLaunchResult(
                    false,
                    mode,
                    null,
                    "Could not start DeepSeek Harness: " + ex.Message
                    + ". Install dsh globally or use Node.js so npx @deepseek-ai/dsh works.");
            }
            catch (Exception ex)
            {
                return new DeepSeekHarnessLaunchResult(
                    false,
                    mode,
                    null,
                    "Could not start DeepSeek Harness: " + ex.Message);
            }
        }
    }

    public void Stop()
    {
        Stop(0);
    }

    public void Stop(int webPort)
    {
        Process? toKill = null;
        lock (_gate)
        {
            toKill = _process;
            _process = null;
            _launchMode = string.Empty;
        }

        if (toKill is null)
        {
            if (webPort > 0)
            {
                TryStopManagedHarnessOnPort(webPort);
            }

            return;
        }

        try
        {
            if (!toKill.HasExited)
            {
                toKill.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            toKill.Dispose();
        }

        if (webPort > 0)
        {
            TryStopManagedHarnessOnPort(webPort);
        }
        else if (TryReadPidRecord(out _, out var recordedPort))
        {
            TryStopManagedHarnessOnPort(recordedPort);
        }
    }

    public static bool TryStopManagedHarnessOnPort(int webPort)
    {
        if (!TryReadPidRecord(out _, out var recordedPort) || recordedPort != webPort)
        {
            return false;
        }

        if (TryStopPortListener(webPort, out _))
        {
            ClearPidRecord();
            return true;
        }

        if (!IsPortListening(webPort))
        {
            ClearPidRecord();
            return true;
        }

        return false;
    }

    public static void WritePidRecord(int launcherPid, int webPort)
    {
        try
        {
            var directory = DeepSeekHarnessSetup.ResolveDshHomeDirectory();
            Directory.CreateDirectory(directory);
            var path = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
            var port = Math.Clamp(webPort, 1, 65535);
            var content = launcherPid.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine
                + port.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine;
            File.WriteAllText(path, content, Encoding.UTF8);
            InvalidatePortStatusCache();
        }
        catch
        {
        }
    }

    public static void ClearPidRecord()
    {
        try
        {
            var path = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            InvalidatePortStatusCache();
        }
        catch
        {
        }
    }

    public static bool TryReadPidRecord(out int launcherPid, out int webPort)
    {
        launcherPid = 0;
        webPort = 0;
        try
        {
            var path = DeepSeekHarnessSetup.ResolveHarnessPidFilePath();
            if (!File.Exists(path))
            {
                return false;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2
                || !int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out launcherPid)
                || !int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out webPort))
            {
                return false;
            }

            return launcherPid >= 0 && webPort is >= 1 and <= 65535;
        }
        catch
        {
            launcherPid = 0;
            webPort = 0;
            return false;
        }
    }

    private static readonly object PortStatusGate = new();
    private static int _cachedListenPort;
    private static bool _cachedListening;
    private static long _cachedListenAtMs;
    private static int _cachedListenerPort;
    private static int _cachedListenerPid;
    private static long _cachedListenerAtMs;
    private const int PortStatusCacheMs = 1500;

    public static void InvalidatePortStatusCache()
    {
        lock (PortStatusGate)
        {
            _cachedListenPort = 0;
            _cachedListenerPort = 0;
            _cachedListenerPid = 0;
        }
    }

    public static bool IsPortListening(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        var now = Environment.TickCount64;
        lock (PortStatusGate)
        {
            if (_cachedListenPort == port && now - _cachedListenAtMs < PortStatusCacheMs)
            {
                return _cachedListening;
            }
        }

        var listening = false;
        try
        {
            listening = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            listening = false;
        }

        lock (PortStatusGate)
        {
            _cachedListenPort = port;
            _cachedListening = listening;
            _cachedListenAtMs = now;
        }

        return listening;
    }

    internal static bool TryFindPortListenerPid(int port, out int ownerPid)
    {
        ownerPid = 0;
        if (port is < 1 or > 65535)
        {
            return false;
        }

        var now = Environment.TickCount64;
        lock (PortStatusGate)
        {
            if (_cachedListenerPort == port && now - _cachedListenerAtMs < PortStatusCacheMs)
            {
                ownerPid = _cachedListenerPid;
                return ownerPid > 0;
            }
        }

        if (!TryFindPortListenerPidUncached(port, out ownerPid))
        {
            lock (PortStatusGate)
            {
                _cachedListenerPort = port;
                _cachedListenerPid = 0;
                _cachedListenerAtMs = now;
            }

            return false;
        }

        lock (PortStatusGate)
        {
            _cachedListenerPort = port;
            _cachedListenerPid = ownerPid;
            _cachedListenerAtMs = now;
        }

        return true;
    }

    private static bool TryFindPortListenerPidUncached(int port, out int ownerPid)
    {
        ownerPid = 0;
        try
        {
            var netstat = RunShortCommand("netstat", "-ano -p tcp");
            if (string.IsNullOrWhiteSpace(netstat))
            {
                return false;
            }

            foreach (var line in netstat.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsListeningLineForPort(line, port))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 5
                    || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                    || pid <= 0
                    || pid == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited)
                    {
                        continue;
                    }

                    ownerPid = pid;
                    return true;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool TryStopPortListener(int port, out int ownerPid)
    {
        ownerPid = 0;
        InvalidatePortStatusCache();
        if (!TryFindPortListenerPidUncached(port, out var listenerPid))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(listenerPid);
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            ownerPid = listenerPid;
            InvalidatePortStatusCache();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsListeningLineForPort(string line, int port)
    {
        if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        var localAddress = parts[1];
        var colonIndex = localAddress.LastIndexOf(':');
        if (colonIndex < 0 || colonIndex >= localAddress.Length - 1)
        {
            return false;
        }

        var localPortText = localAddress[(colonIndex + 1)..];
        return localPortText.Equals(port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string RunShortCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return string.Empty;
            }

            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public static async Task<bool> WaitForWebUiAsync(int webPort, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await DeepSeekHarnessSetup.ProbeWebUiStatusAsync(webPort, cancellationToken).ConfigureAwait(false);
            var haveAuthUrl = TryReadWebAuthUrlFromLaunchLog(webPort, out _);
            if (DeepSeekHarnessSetup.IsWebUiReadyToOpen(status, haveAuthUrl))
            {
                return true;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public static async Task<bool> WaitForPortClosedAsync(int webPort, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPortListening(webPort))
            {
                return true;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return !IsPortListening(webPort);
    }

    internal static (string FileName, string ArgumentPrefix, string Mode) ResolveLauncher(string? dshExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(dshExecutablePath))
        {
            var trimmed = dshExecutablePath.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                return (trimmed, string.Empty, "dsh (custom path)");
            }
        }

        foreach (var candidate in FindDshOnPath())
        {
            return (candidate, string.Empty, "dsh");
        }

        if (!string.IsNullOrWhiteSpace(TryFindExecutableOnPath("npx.cmd")))
        {
            return (TryFindExecutableOnPath("npx.cmd")!, "@deepseek-ai/dsh", "npx @deepseek-ai/dsh");
        }

        if (!string.IsNullOrWhiteSpace(TryFindExecutableOnPath("npx")))
        {
            return (TryFindExecutableOnPath("npx")!, "@deepseek-ai/dsh", "npx @deepseek-ai/dsh");
        }

        return ("dsh", string.Empty, "dsh");
    }

    public static string ComposeArguments(string argumentPrefix, int webPort)
    {
        var webArgs = BuildWebArguments(webPort);
        return string.IsNullOrWhiteSpace(argumentPrefix)
            ? webArgs
            : argumentPrefix.Trim() + " " + webArgs;
    }

    private static IEnumerable<string> FindDshOnPath()
    {
        foreach (var name in new[] { "dsh", "dsh.exe", "dsh.cmd" })
        {
            var path = TryFindExecutableOnPath(name);
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static string? TryFindExecutableOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var folder in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim(), fileName);
                if (!candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(extension))
                {
                    candidate += extension;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (sender is Process process && ReferenceEquals(process, _process))
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
                _process = null;
                _launchMode = string.Empty;
                if (TryReadPidRecord(out _, out var recordedPort))
                {
                    // npx/dsh often exits after spawning node; keep managed ownership on the listener.
                    if (IsPortListening(recordedPort) && TryFindPortListenerPid(recordedPort, out var listenerPid))
                    {
                        WritePidRecord(listenerPid, recordedPort);
                    }
                    else if (!IsPortListening(recordedPort))
                    {
                        ClearPidRecord();
                    }
                }
            }
        }

        ProcessExited?.Invoke();
    }

    private static void AppendLaunchLog(string logPath, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public static bool TryReadWebAuthUrlFromLaunchLog(int webPort, out string url)
    {
        url = string.Empty;
        var logPath = ResolveLaunchLogPath();
        if (!File.Exists(logPath))
        {
            return false;
        }

        try
        {
            var lines = File.ReadAllLines(logPath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (DeepSeekHarnessSetup.TryParseWebAuthUrl(lines[i], webPort, out url))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    public static string ReadLaunchLogTail(int maxLines = 14)
    {
        var logPath = ResolveLaunchLogPath();
        if (!File.Exists(logPath))
        {
            return string.Empty;
        }

        try
        {
            var lines = File.ReadAllLines(logPath);
            if (lines.Length <= maxLines)
            {
                return string.Join(Environment.NewLine, lines);
            }

            return string.Join(Environment.NewLine, lines[^maxLines..]);
        }
        catch
        {
            return string.Empty;
        }
    }
}
