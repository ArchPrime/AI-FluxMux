namespace FluxMux.Avalonia.Services;

public enum LocalValidateLaunchAction
{
    ProbeRunning,
    Reload
}

/// <summary>
/// Validate to endpoint must not unload a live llama-server just because the
/// model profile was renamed. When launch settings really changed, the UI asks
/// before stopping llama-server — especially if a Client app turn is still running.
/// </summary>
public static class LocalValidateLaunchPolicy
{
    public const string ConfirmStopTitle = "Validate to endpoint";

    public const string ValidateCancelledGuidance =
        "Validate cancelled. llama-server is still running.";

    public static LocalValidateLaunchAction Decide(
        bool localAlive,
        bool sameLocalModel,
        bool sameLaunchFingerprint)
    {
        if (localAlive && sameLocalModel && sameLaunchFingerprint)
        {
            return LocalValidateLaunchAction.ProbeRunning;
        }

        return LocalValidateLaunchAction.Reload;
    }

    public static string ConfirmStopMessage(bool endpointTurnInFlight)
        => endpointTurnInFlight
            ? "llama-server is still answering a Client app turn. **Validate to endpoint** will stop llama-server and cut that reply off. The Client app may keep waiting."
            : "llama-server is still running. **Validate to endpoint** will stop it and load this model profile.";
}
