using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;

namespace FluxMux.Avalonia.Views;

public static class ThemedDialog
{
    public static bool Confirm(
        string title,
        string message,
        string confirmLabel = "OK",
        string cancelLabel = "Cancel")
        => Show(title, message, showCancel: true, confirmLabel, cancelLabel);

    public static void Warn(string title, string message, string okLabel = "OK")
        => Show(title, message, showCancel: false, okLabel);

    /// <summary>
    /// Shows the local-launch OOM recovery dialog with three actions:
    /// "Use a smaller context", "Retry as-is", and "Pick a smaller model".
    /// Returns the chosen action, or <see cref="Views.LocalLaunchRecoveryAction.None"/> if the
    /// dialog could not be shown (no main window).
    /// </summary>
    public static Views.LocalLaunchRecoveryAction LaunchRecovery(
        string title,
        string message,
        string smallerContextLabel = "Use a smaller context")
    {
        var dialog = new Views.LocalLaunchRecoveryDialog();
        dialog.Configure(title, message, smallerContextLabel);
        dialog.RequestedThemeVariant = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light;

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            return Views.LocalLaunchRecoveryAction.None;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            var result = Views.LocalLaunchRecoveryAction.None;
            var frame = new DispatcherFrame();
            _ = WaitForDialogAsync();
            Dispatcher.UIThread.PushFrame(frame);
            return result;

            async Task WaitForDialogAsync()
            {
                result = await dialog.ShowDialog<Views.LocalLaunchRecoveryAction>(owner);
                frame.Continue = false;
            }
        }

        return dialog.ShowDialog<Views.LocalLaunchRecoveryAction>(owner).GetAwaiter().GetResult();
    }

    private static bool Show(
        string title,
        string message,
        bool showCancel,
        string confirmLabel = "OK",
        string cancelLabel = "Cancel")
    {
        var dialog = new ConfirmDialogWindow();
        dialog.Configure(title, message, showCancel, confirmLabel, cancelLabel);
        dialog.RequestedThemeVariant = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light;

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            return false;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            var result = false;
            var frame = new DispatcherFrame();
            _ = WaitForDialogAsync();
            Dispatcher.UIThread.PushFrame(frame);
            return result;

            async Task WaitForDialogAsync()
            {
                result = await dialog.ShowDialog<bool>(owner);
                frame.Continue = false;
            }
        }

        return dialog.ShowDialog<bool>(owner).GetAwaiter().GetResult();
    }
}
