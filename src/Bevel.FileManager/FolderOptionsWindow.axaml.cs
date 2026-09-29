using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Bevel.Core;

namespace Bevel.FileManager;

/// <summary>
/// Folder Options — a focused Filer view/behaviour dialog (Show hidden, Hide extensions, default view,
/// info-panel style). Deliberately does NOT expose the app theme/appearance (that lives on the taskbar's
/// Properties): opening the whole app Settings from here was the bug bevel-x1x2 fixed.
/// </summary>
public partial class FolderOptionsWindow : UI.BevelWindow
{
    private readonly ISettingsService _settings;

    // Display label ↔ persisted ViewMode name. Kept in sync with Components.ViewMode.
    private static readonly (string Label, string Mode)[] Views =
    {
        ("Thumbnails", "Thumbnails"),
        ("Large Icons", "LargeIcons"),
        ("Small Icons", "SmallIcons"),
        ("List", "List"),
        ("Details", "Details"),
    };

    private static readonly (string Label, InfoPaneStyle Style)[] InfoStyles =
    {
        ("Auto (match theme)", InfoPaneStyle.Auto),
        ("Windows 2000 / Me (Web View)", InfoPaneStyle.Win2000),
        ("Windows XP (Common Tasks)", InfoPaneStyle.WinXP),
        ("Windows Vista / 7 (Navigation)", InfoPaneStyle.Modern),
        ("Windows 95 / NT 4 (minimal)", InfoPaneStyle.Win9x),
        ("Off (no panel)", InfoPaneStyle.Off),
    };

    public FolderOptionsWindow() : this(null!) { }

    public FolderOptionsWindow(ISettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        DefaultViewCombo.ItemsSource = System.Array.ConvertAll(Views, v => v.Label);
        InfoPaneCombo.ItemsSource = System.Array.ConvertAll(InfoStyles, s => s.Label);

        var s = _settings.Current;
        ShowHiddenCheck.IsChecked = s.ShowHiddenFiles;
        HideExtensionsCheck.IsChecked = s.HideKnownExtensions;
        DefaultViewCombo.SelectedIndex = IndexOfView(s.DefaultViewMode);
        InfoPaneCombo.SelectedIndex = IndexOfInfoStyle(s.InfoPaneStyle);

        OkButton.Click += OnOk;
        CancelButton.Click += OnCancel;
        ApplyButton.Click += OnApply;
    }

    private static int IndexOfView(string mode)
    {
        for (var i = 0; i < Views.Length; i++)
            if (Views[i].Mode == mode) return i;
        // Unrecognised persisted mode (corrupt / older settings) falls back to Large Icons — the classic
        // default — NOT whatever happens to sit at index 0 (Fable review, bevel-lwti).
        return System.Array.FindIndex(Views, v => v.Mode == "LargeIcons");
    }

    private static int IndexOfInfoStyle(InfoPaneStyle style)
    {
        for (var i = 0; i < InfoStyles.Length; i++)
            if (InfoStyles[i].Style == style) return i;
        return 0;
    }

    private async void OnOk(object? sender, RoutedEventArgs e) { await SaveAsync(); Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private async void OnApply(object? sender, RoutedEventArgs e) => await SaveAsync();

    private async System.Threading.Tasks.Task SaveAsync()
    {
        var viewMode = Views[System.Math.Max(0, DefaultViewCombo.SelectedIndex)].Mode;
        var infoStyle = InfoStyles[System.Math.Max(0, InfoPaneCombo.SelectedIndex)].Style;
        await _settings.UpdateAsync(cfg =>
        {
            cfg.ShowHiddenFiles = ShowHiddenCheck.IsChecked ?? false;
            cfg.HideKnownExtensions = HideExtensionsCheck.IsChecked ?? false;
            cfg.DefaultViewMode = viewMode;
            cfg.InfoPaneStyle = infoStyle;
        }, CancellationToken.None);
    }
}
