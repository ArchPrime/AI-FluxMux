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
