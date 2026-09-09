namespace FluxMux.Avalonia.ViewModels;

public sealed class ProfileTemplateSourceViewModel
{
    public ProfileTemplateSourceViewModel(string routeType, string provider, string model, string variant)
    {
        RouteType = routeType;
        Provider = provider;
        Model = model;
        Variant = variant;
        var branch = variant == MainViewModel.BaseVariantDisplayName ? "Base defaults" : $"|- {variant}";
        DisplayName = routeType == "Cloud"
            ? $"{provider} / {model} / {branch}"
            : $"{model} / {branch}";
    }

    public string RouteType { get; }

    public string Provider { get; }

    public string Model { get; }

    public string Variant { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}