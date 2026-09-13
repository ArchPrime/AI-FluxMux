namespace FluxMux.Avalonia.Services;

/// <summary>
/// A profile that later needs Validate again stays in its Quick Select
/// slot with the warning triangle. It does not drop out of the picker.
/// Never-validated profiles stay out of Quick Select until they pass.
/// </summary>
public static class QuickSelectProfileOffer
{
    public static bool KeepInSlotList(bool endpointValidated, bool hasEndpointWarning, bool assignedToSlot)
        => endpointValidated || (hasEndpointWarning && assignedToSlot);
}
