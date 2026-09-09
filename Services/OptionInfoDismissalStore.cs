using System;
using System.Collections.Generic;
using System.Linq;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Remembers which option notes the operator hid. Default-shown notes use
/// <see cref="DismissedConfigKey"/>; notes that start compact use
/// <see cref="RevealedConfigKey"/> after <see cref="MoreInfoLabel"/>.
/// </summary>
public sealed class OptionInfoDismissalStore
{
    public const string DismissedConfigKey = "DismissedOptionInfo";
    public const string RevealedConfigKey = "RevealedOptionInfo";
    public const string MoreInfoLabel = "More info";
    public const string HideLabel = "Hide";
    public const string ShowAllLabel = "Show all option notes";

    public static OptionInfoDismissalStore Current { get; private set; } = CreateEmpty();

    private readonly HashSet<string> _dismissed;
    private readonly HashSet<string> _revealed;
    private readonly Action<IReadOnlyList<string>, IReadOnlyList<string>>? _persist;

    public event EventHandler? Changed;

    public OptionInfoDismissalStore(
        IEnumerable<string>? dismissed,
        IEnumerable<string>? revealed,
        Action<IReadOnlyList<string>, IReadOnlyList<string>>? persist)
    {
        _dismissed = new HashSet<string>(Normalize(dismissed), StringComparer.Ordinal);
        _revealed = new HashSet<string>(Normalize(revealed), StringComparer.Ordinal);
        _persist = persist;
    }

    public bool HasHiddenDefaultNotes => _dismissed.Count > 0;

    public static void Attach(FluxMuxConfigDocument config, Action save)
    {
        Current = new OptionInfoDismissalStore(
            config.GetStringArray(DismissedConfigKey),
            config.GetStringArray(RevealedConfigKey),
            (dismissed, revealed) =>
            {
                config.SetStringArray(DismissedConfigKey, dismissed);
                config.SetStringArray(RevealedConfigKey, revealed);
                save();
            });
    }

    public bool IsNoteVisible(string? id, bool startDismissed)
    {
        var key = NormalizeId(id);
        if (key.Length == 0)
        {
            return !startDismissed;
        }

        if (_dismissed.Contains(key))
        {
            return false;
        }

        if (_revealed.Contains(key))
        {
            return true;
        }

        return !startDismissed;
    }

    public void Hide(string? id)
    {
        var key = NormalizeId(id);
        if (key.Length == 0)
        {
            return;
        }

        var changed = _dismissed.Add(key);
        changed |= _revealed.Remove(key);
        if (changed)
        {
            Persist();
        }
    }

    public void Show(string? id)
    {
        var key = NormalizeId(id);
        if (key.Length == 0)
        {
            return;
        }

        var changed = _dismissed.Remove(key);
        changed |= _revealed.Add(key);
        if (changed)
        {
            Persist();
        }
    }

    public void ShowAllDefaultNotes()
    {
        if (_dismissed.Count == 0)
        {
            return;
        }

        _dismissed.Clear();
        Persist();
    }

    private void Persist()
    {
        _persist?.Invoke(_dismissed.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            _revealed.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static OptionInfoDismissalStore CreateEmpty()
        => new([], [], persist: null);

    private static IEnumerable<string> Normalize(IEnumerable<string>? values)
        => (values ?? []).Select(NormalizeId).Where(id => id.Length > 0);

    private static string NormalizeId(string? id)
        => string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
}
