using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Bevel.Core;
using Bevel.UI;

namespace Bevel.FileManager.Components;

/// <summary>"Organize Favorites" dialog (bevel-4pgk): reorder and remove entries of the persisted
/// <see cref="BevelSettings.FilerFavorites"/> list. Code-built like <see cref="MessageDialog"/>; every
/// edit writes through <see cref="ISettingsService.UpdateAsync"/> so all open Filer windows see it on the
/// settings fan-out.</summary>
public sealed class OrganizeFavoritesDialog : BevelWindow
{
    private readonly ISettingsService _settings;

    internal ListBox List => _list;
    internal Button UpButton => _up;
    internal Button DownButton => _down;
    internal Button RemoveButton => _remove;

    private readonly ListBox _list;
    private readonly Button _up;
    private readonly Button _down;
    private readonly Button _remove;

    public OrganizeFavoritesDialog(ISettingsService settings)
    {
        _settings = settings;
        Title = "Organize Favorites";
        Width = 440;
        Height = 340;
        MinWidth = 320;
        MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox();
        _list.SelectionChanged += (_, _) => UpdateButtons();

        _up = new Button { Content = "Move _Up", MinWidth = 90 };
        _down = new Button { Content = "Move _Down", MinWidth = 90 };
        _remove = new Button { Content = "_Remove", MinWidth = 90 };
        var close = new Button { Content = "_Close", MinWidth = 90, IsCancel = true };

        _up.Click += (_, _) => Apply(FilerFavorites.Move(Current, Selected, Selected - 1), Selected - 1);
        _down.Click += (_, _) => Apply(FilerFavorites.Move(Current, Selected, Selected + 1), Selected + 1);
        _remove.Click += (_, _) => Apply(FilerFavorites.Remove(Current, Current[Selected]), Math.Max(0, Selected - 1));
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(12, 4, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _up, _down, _remove, close },
        };

        var root = new DockPanel { Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        Refresh();
    }

    private string[] Current => _settings.Current.FilerFavorites;
    private int Selected => _list.SelectedIndex;

    private void Apply(string[] next, int select)
    {
        _ = _settings.UpdateAsync(s => s.FilerFavorites = next);
        Refresh();
        if (_list.ItemCount > 0)
            _list.SelectedIndex = Math.Clamp(select, 0, _list.ItemCount - 1);
        UpdateButtons();
    }

    private void Refresh()
    {
        _list.ItemsSource = Current
            .Select(p => Core.Vfs.VfsPath.TryParse(p, out var path)
                ? Navigation.HistoryEntry.LabelFor(path)
                : null)
            .Where(label => label is not null)
            .ToList();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var n = Current.Length;
        var i = Selected;
        _up.IsEnabled = i > 0;
        _down.IsEnabled = i >= 0 && i < n - 1;
        _remove.IsEnabled = i >= 0 && i < n;
    }
}
