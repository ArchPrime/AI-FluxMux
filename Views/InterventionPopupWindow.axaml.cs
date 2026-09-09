using Avalonia.Controls;
using FluxMux.Avalonia.ViewModels;

namespace FluxMux.Avalonia.Views;

public partial class InterventionPopupWindow : Window
{
    public InterventionPopupWindow()
    {
        InitializeComponent();
        var chooser = this.FindControl<ComboBox>("RecoveryReplacementChooser");
        if (chooser is null)
        {
            return;
        }

        chooser.DropDownOpened += OnRecoveryChooserDropDownOpened;
        chooser.DropDownClosed += OnRecoveryChooserDropDownClosed;
    }

    private void OnRecoveryChooserDropDownOpened(object? sender, System.EventArgs e)
    {
        Topmost = false;
        if (DataContext is MainViewModel vm)
        {
            vm.RecoveryChooserDropDownOpen = true;
        }
    }

    private void OnRecoveryChooserDropDownClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RecoveryChooserDropDownOpen = false;
        }

        if (IsVisible)
        {
            Topmost = true;
        }
    }
}
