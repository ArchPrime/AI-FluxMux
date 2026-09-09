namespace FluxMux.Avalonia.ViewModels;

public sealed class RecoveryReplacementOption
{
    public RecoveryReplacementOption(
        string key,
        string displayName,
        bool isCloud,
        string localModel = "",
        string localVariant = "",
        string cloudProvider = "",
        string cloudModel = "")
    {
        Key = key;
        DisplayName = displayName;
        IsCloud = isCloud;
        LocalModel = localModel;
        LocalVariant = localVariant;
        CloudProvider = cloudProvider;
        CloudModel = cloudModel;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public bool IsCloud { get; }
    public string LocalModel { get; }
    public string LocalVariant { get; }
    public string CloudProvider { get; }
    public string CloudModel { get; }

    public override string ToString() => DisplayName;
}
