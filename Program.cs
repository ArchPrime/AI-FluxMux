using Avalonia;
#if DEBUG
using AvaloniaUI.DiagnosticsSupport;
#endif
using FluxMux.Avalonia.Services;
using System;

namespace FluxMux.Avalonia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            if (!CompiledXamlGuard.AssemblyContainsCompiledXaml(typeof(Program).Assembly))
            {
                StartupCrashLog.ReportMissingCompiledXaml();
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            StartupCrashLog.Report(ex);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
