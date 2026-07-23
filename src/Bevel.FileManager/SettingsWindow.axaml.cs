using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Bevel.Core;

namespace Bevel.FileManager;

/// <summary>
/// Minimal settings window: theme selector + shell kill-switch (M1).
/// </summary>
public partial class SettingsWindow : UI.BevelWindow
{
    private readonly SettingsService _settings;

    public SettingsWindow() : this(null!) { }

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        // Data-driven from the theme registry so the list is exactly the switchable set.
        ThemeCombo.ItemsSource = Bevel.UI.ThemeService.Themes.Select(t => t.Display).ToList();

        // Load current values
        var s = _settings.Current;
        ThemeCombo.SelectedIndex = IndexOfTheme(s.ThemeId);
        ShellEnabledCheck.IsChecked = s.ShellEnabled;
        ShowHiddenCheck.IsChecked = s.ShowHiddenFiles;
        CrispBevelsCheck.IsChecked = _settings.ThemeOverridesFor(s.ThemeId).CrispBevels ?? false;

        // Wire buttons
        OkButton.Click += OnOkClick;
        CancelButton.Click += OnCancelClick;
        ApplyButton.Click += OnApplyClick;
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        await SaveAsync();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnApplyClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>Registry index of a theme id (default theme's slot if unknown).</summary>
    private static int IndexOfTheme(string id)
    {
        var themes = Bevel.UI.ThemeService.Themes;
        for (var i = 0; i < themes.Count; i++)
            if (themes[i].Id == id) return i;
        return 0;
    }

    /// <summary>The id of the currently-selected theme (default if nothing valid is selected).</summary>
    private string SelectedThemeId()
    {
        var themes = Bevel.UI.ThemeService.Themes;
        var idx = ThemeCombo.SelectedIndex;
        return idx >= 0 && idx < themes.Count ? themes[idx].Id : Bevel.UI.ThemeService.DefaultTheme;
    }

    private async Task SaveAsync()
    {
        var themeId = SelectedThemeId();
        await _settings.UpdateAsync(s =>
        {
            s.ThemeId = themeId;
            s.ShellEnabled = ShellEnabledCheck.IsChecked ?? true;
            s.ShowHiddenFiles = ShowHiddenCheck.IsChecked ?? false;
        }, CancellationToken.None);

        // Live theme switch: the picker reskins the running app immediately by swapping the theme's
        // Styles set + tokens (bevel-dob) — no restart.
        Bevel.UI.ThemeService.Apply(themeId);

        // Whitelisted per-theme override (bevel-wym): persisted under theme:<id> and applied
        // live — the Application-level resource shadow flips every bevel immediately.
        var crisp = CrispBevelsCheck.IsChecked == true;
        await _settings.UpdateThemeOverridesAsync(
            themeId, o => o.CrispBevels = crisp ? true : null, CancellationToken.None);
        if (Avalonia.Application.Current is { } app)
            UI.ThemeOptions.ApplyCrispBevels(app, crisp);
    }
}