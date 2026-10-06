using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace FluxMux.Avalonia.Views;

/// <summary>Which recovery action the user picked in the local-launch OOM recovery dialog.</summary>
public enum LocalLaunchRecoveryAction
{
    None = 0,
    UseSmallerContext = 1,
    RetryAsIs = 2,
    PickSmallerModel = 3,
}

public partial class LocalLaunchRecoveryDialog : Window
{
    public LocalLaunchRecoveryDialog()
    {
        InitializeComponent();
        RequestedThemeVariant = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light;
    }

    public void Configure(string title, string message, string smallerContextLabel)
    {
        Title = "AI-FluxMux — " + title;
        HeadingText.Text = title;
        MarkedText.ApplyTo(MessageText, message);
        SmallerContextButton.Content = string.IsNullOrWhiteSpace(smallerContextLabel) ? "Use a smaller context" : smallerContextLabel;
        RetryButton.Content = "Retry as-is";
        SmallerModelButton.Content = "Pick a smaller model";
    }

    private void SmallerContext_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.UseSmallerContext);
    private void Retry_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.RetryAsIs);
    private void SmallerModel_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.PickSmallerModel);
}