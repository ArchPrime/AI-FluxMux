using System;
using Avalonia;
using Avalonia.Controls;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Restores every default-shown option note that was hidden with
/// <see cref="OptionInfoDismissalStore.HideLabel"/>.
/// </summary>
public sealed class OptionInfoShowAllButton : Button
{
    private OptionInfoDismissalStore? _store;

    public OptionInfoShowAllButton()
    {
        Content = OptionInfoDismissalStore.ShowAllLabel;
        Classes.Add("optionInfo");
        Click += (_, _) => Store.ShowAllDefaultNotes();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        Refresh();
    }

    private OptionInfoDismissalStore Store => _store ?? OptionInfoDismissalStore.Current;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _store = OptionInfoDismissalStore.Current;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_store is not null)
        {
            _store.Changed -= OnStoreChanged;
            _store = null;
        }
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
        => IsVisible = Store.HasHiddenDefaultNotes;
}
