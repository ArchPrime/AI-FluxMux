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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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