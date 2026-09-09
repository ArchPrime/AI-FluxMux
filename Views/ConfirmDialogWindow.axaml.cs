using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace FluxMux.Avalonia.Views;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light;
    }

    public void Configure(
        string title,
        string message,
        bool showCancel,
        string confirmLabel = "OK",
        string cancelLabel = "Cancel")
    {
        Title = "AI-FluxMux — " + title;
        HeadingText.Text = title;
        MarkedText.ApplyTo(MessageText, message);
        OkButton.Content = string.IsNullOrWhiteSpace(confirmLabel) ? "OK" : confirmLabel;
        CancelButton.Content = string.IsNullOrWhiteSpace(cancelLabel) ? "Cancel" : cancelLabel;
        CancelButton.IsVisible = showCancel;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
