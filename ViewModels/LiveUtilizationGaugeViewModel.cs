using CommunityToolkit.Mvvm.ComponentModel;

namespace FluxMux.Avalonia.ViewModels;

public partial class LiveUtilizationGaugeViewModel : ViewModelBase
{
    public string Title { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string Badge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; } = "—";

    [ObservableProperty]
    public partial string DetailText { get; set; } = "Waiting for readout…";

    [ObservableProperty]
    public partial string Tooltip { get; set; } = "Live utilization, updated about once a second.";
}
