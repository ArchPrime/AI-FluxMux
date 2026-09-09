namespace FluxMux.Avalonia.ViewModels;

public sealed class LocalVisionProjectorChoice
{
    public string Path { get; init; } = string.Empty;

    public string Label { get; init; } = "(none)";

    public override string ToString() => Label;
}
