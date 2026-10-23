using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.ViewModels;
using FluxMux.Avalonia.Views;
using FluxMux.Updates.Core;

namespace FluxMux.Avalonia;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\AI-FluxMux.SingleInstance";
    private const string SingleInstanceActivateEventName = "Local\\AI-FluxMux.SingleInstance.Activate";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _singleInstanceActivateEvent;
    private static CancellationTokenSource? _singleInstanceActivateCts;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The shared update core resolves the DeepSeek Harness launcher through a pluggable
        // resolver. The app registers its own (which honours a configured dsh path); the
        // standalone updater falls back to a PATH lookup when no resolver is registered.
        DeepSeekHarnessUpdateCheck.SetLauncherResolver(path => DeepSeekHarnessWebHost.ResolveLauncher(path));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Crash diagnostics: write full stack to Desktop on any unhandled exception.
            // In Avalonia 12.1.1 the event lives on Avalonia.Threading.Dispatcher
            // (not on Application); the UI dispatcher is Dispatcher.UIThread.
            //
            // Benign exceptions (cancellation, socket teardown during shutdown) are LOGGED
            // but HANDLED (args.Handled = true) so they do not crash the app. These are
            // expected during normal shutdown / user-cancelled launches and were previously
            // crashing the app on every restart.
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                var ex = args.Exception;
                // Benign shutdown/cancellation artifacts are expected and should NOT write a
                // crash report (they were filling the desktop with FluxMux_Crash_*.txt).
                // Only genuinely unexpected exceptions get a report.
                if (IsBenignShutdownException(ex))
                {
                    args.Handled = true;
                    return;
                }

                CrashLogger.LogUnhandled(ex, "Avalonia UI thread (Dispatcher.UnhandledException)");
                // Anything else still crashes (args.Handled stays false).
                args.Handled = false;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    // Benign shutdown/cancellation artifacts are expected and should NOT write
                    // a crash report. Only genuinely unexpected exceptions get a report.
                    if (!IsBenignShutdownException(ex))
                    {
                        CrashLogger.LogUnhandled(ex, "AppDomain.UnhandledException");
                    }
                }
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                // Benign socket/cancellation exceptions from fire-and-forget tasks (e.g. a
                // TCP probe cancelled during shutdown) are EXPECTED and should NOT write a
                // crash report. Only genuinely unexpected exceptions get a report.
                if (IsBenignShutdownException(args.Exception))
                {
                    // Mark observed so the finalizer thread never rethrows; no report.
                    args.SetObserved();
                    return;
                }

                CrashLogger.LogUnhandled(args.Exception, "TaskScheduler.UnobservedTaskException");
                // Always mark observed so the finalizer thread never rethrows and crashes.
                args.SetObserved();
            };

            if (!TryAcquireSingleInstance())
            {
                desktop.Shutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var configPath = ResolveFluxMuxConfigPath();
            var configService = new FluxMuxConfigService(configPath);
            ApplyRequestedTheme(configService.Load().GetString("UiTheme", "Light"));
            var dependencyAuditService = new DependencyAuditService(configPath);
            var runtimeService = new FluxMuxRuntimeService(configPath);
            desktop.Exit += (_, _) =>
            {
                try
                {
                    if (!runtimeService.HasCompletedExitShutdown)
                    {
                        runtimeService.ShutdownManagedRuntimesAsync(shutdownGateway: true).Wait(TimeSpan.FromSeconds(3));
                    }
                }
                catch
                {
                }

                StopSingleInstanceActivationListener();
                ReleaseSingleInstance();
            };
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(configService, dependencyAuditService, runtimeService),
            };
            StartSingleInstanceActivationListener(desktop.MainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// True when an unhandled exception is a benign shutdown/cancellation artifact that should
    /// be logged but NOT crash the app. These occur routinely when the user cancels a launch,
    /// the app is shutting down, or a fire-and-forget TCP probe is torn down mid-connect.
    /// Treating them as fatal was crashing the app on every restart (see the accumulating
    /// FluxMux_Crash_*.txt reports).
    /// </summary>
    private static bool IsBenignShutdownException(Exception? exception)
    {
        // Walk the exception chain (including AggregateException inner exceptions) looking for
        // a benign type. If any link is benign, the whole thing is a shutdown artifact.
        var queue = new Queue<Exception?>(new[] { exception });
        while (queue.Count > 0)
        {
            var ex = queue.Dequeue();
            if (ex is null)
            {
                continue;
            }

            if (ex is OperationCanceledException
                || ex is TaskCanceledException
                || ex is ObjectDisposedException
                || ex is System.Net.Sockets.SocketException
                || ex is System.Net.Http.HttpRequestException
                || ex is IOException)
            {
                return true;
            }

            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    queue.Enqueue(inner);
                }
                continue;
            }

            if (ex.InnerException is not null)
            {
                queue.Enqueue(ex.InnerException);
            }
        }

        return false;
    }

    public static void ApplyRequestedTheme(string? theme)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Dark
            : ThemeVariant.Light;
    }

    public static bool IsDarkTheme
        => Current?.ActualThemeVariant == ThemeVariant.Dark
           || Current?.RequestedThemeVariant == ThemeVariant.Dark;

    public static string StripeBackground(int index)
        => index % 2 == 0
            ? (IsDarkTheme ? "#1D2939" : "#FFFFFF")
            : (IsDarkTheme ? "#152033" : "#F8FAFC");

    private static string ResolveFluxMuxConfigPath()
    {
        return FluxMuxConfigPaths.Resolve(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            File.Exists);
    }

    private static bool TryAcquireSingleInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            if (TryCreateMutex())
            {
                return true;
            }

            ReplaceOtherInstances();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (TryCreateMutex())
                {
                    return true;
                }

                Thread.Sleep(250);
            }

            StartupCrashLog.Notify(
                "AI-FluxMux could not replace the previous session",
                "A previous FluxMux.Avalonia process is still running. Close it in Task Manager, then start AI-FluxMux again.");
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryCreateMutex()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        return createdNew;
    }

    private static void ReplaceOtherInstances()
    {
        foreach (var process in Process.GetProcessesByName("FluxMux.Avalonia"))
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                process.Kill();
                process.WaitForExit(8000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void ReleaseSingleInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private static void StartSingleInstanceActivationListener(global::Avalonia.Controls.Window mainWindow)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _singleInstanceActivateEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                SingleInstanceActivateEventName);
            _singleInstanceActivateCts = new CancellationTokenSource();
            var token = _singleInstanceActivateCts.Token;

            _ = Task.Run(() => ListenForActivationRequestsAsync(mainWindow, token), token);
        }
        catch
        {
        }
    }

    private static async Task ListenForActivationRequestsAsync(global::Avalonia.Controls.Window mainWindow, CancellationToken cancellationToken)
    {
        var activateEvent = _singleInstanceActivateEvent;
        if (activateEvent is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!activateEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                {
                    continue;
                }

                await Dispatcher.UIThread.InvokeAsync(() => ActivateMainWindow(mainWindow));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void StopSingleInstanceActivationListener()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _singleInstanceActivateCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _singleInstanceActivateCts?.Dispose();
            _singleInstanceActivateCts = null;
            _singleInstanceActivateEvent?.Dispose();
            _singleInstanceActivateEvent = null;
        }
    }

    private static void SignalRunningInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(SingleInstanceActivateEventName);
            activateEvent.Set();
        }
        catch
        {
        }
    }

    private static void ActivateMainWindow(global::Avalonia.Controls.Window mainWindow)
    {
        if (mainWindow.WindowState == global::Avalonia.Controls.WindowState.Minimized)
        {
            mainWindow.WindowState = global::Avalonia.Controls.WindowState.Normal;
        }

        mainWindow.Show();
        mainWindow.Activate();
    }
}