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

        // Load current values
        var s = _settings.Current;
        ThemeCombo.SelectedIndex = s.ThemeId switch
        {
            "luna" => 1,
            "win11" => 2,
            _ => 0,
        };
        ShellEnabledCheck.IsChecked = s.ShellEnabled;
        ShowHiddenCheck.IsChecked = s.ShowHiddenFiles;

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

    private async Task SaveAsync()
    {
        await _settings.UpdateAsync(s =>
        {
            s.ThemeId = ThemeCombo.SelectedIndex switch
            {
                1 => "luna",
                2 => "win11",
                _ => "win2000",
            };
            s.ShellEnabled = ShellEnabledCheck.IsChecked ?? true;
            s.ShowHiddenFiles = ShowHiddenCheck.IsChecked ?? false;
        }, CancellationToken.None);
    }
}