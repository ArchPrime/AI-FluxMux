using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace FluxMux.Avalonia.Views;

/// <summary>Which recovery action the user picked in the local-launch recovery dialog.</summary>
public enum LocalLaunchRecoveryAction
{
    None = 0,
    UseSmallerContext = 1,
    RetryAsIs = 2,
    PickSmallerModel = 3,
    StopAndCleanUp = 4,
    WaitLonger = 5,
}

public partial class LocalLaunchRecoveryDialog : Window
{
    public LocalLaunchRecoveryDialog()
    {
        InitializeComponent();
        RequestedThemeVariant = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Light;
    }

    /// <summary>
    /// Configures the dialog's heading, message, and which buttons are visible.
    /// The "Use a smaller context" button is shown only when <paramref name="showSmallerContext"/>
    /// is true; the "Stop and clean up" button only when <paramref name="showStop"/> is true;
    /// and the "Wait longer" button only when <paramref name="showWaitLonger"/> is true.
    /// </summary>
    public void Configure(
        string title,
        string message,
        string smallerContextLabel,
        bool showSmallerContext = true,
        bool showStop = false,
        string stopLabel = "Stop and clean up",
        bool showWaitLonger = false)
    {
        Title = "AI-FluxMux — " + title;
        HeadingText.Text = title;
        MarkedText.ApplyTo(MessageText, message);
        SmallerContextButton.Content = string.IsNullOrWhiteSpace(smallerContextLabel) ? "Use a smaller context" : smallerContextLabel;
        SmallerContextButton.IsVisible = showSmallerContext;
        RetryButton.Content = "Retry as-is";
        SmallerModelButton.Content = "Pick a smaller model";
        StopButton.Content = string.IsNullOrWhiteSpace(stopLabel) ? "Stop and clean up" : stopLabel;
        StopButton.IsVisible = showStop;
        WaitLongerButton.Content = "Wait longer";
        WaitLongerButton.IsVisible = showWaitLonger;
    }

    private void SmallerContext_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.UseSmallerContext);
    private void Retry_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.RetryAsIs);
    private void SmallerModel_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.PickSmallerModel);
    private void Stop_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.StopAndCleanUp);
    private void WaitLonger_Click(object? sender, RoutedEventArgs e) => Close(LocalLaunchRecoveryAction.WaitLonger);
}