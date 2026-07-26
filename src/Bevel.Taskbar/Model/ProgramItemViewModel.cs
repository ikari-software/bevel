using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// One Start-menu Programs entry, as data. The menu binds a list of these and opens instantly;
/// icons are NOT rendered up front (that was the ~2s first-open freeze — 284 synchronous icon
/// renders on the UI thread). Instead each item's icon loads lazily off-thread the first time
/// the item is realized (<see cref="EnsureIcon"/>), so only the handful actually shown ever
/// render, and even those never touch the UI thread until the finished bitmap is assigned.
/// </summary>
public sealed class ProgramItemViewModel : ObservableObject
{
    private readonly IAppEnvironment? _appEnv;
    private readonly IconLoader _icons;
    private readonly string _appId;
    private readonly string? _iconPath;
    private Bitmap? _iconSource;
    private bool _iconRequested;

    public ProgramItemViewModel(InstalledApp app, IAppEnvironment? appEnv, IconLoader icons)
    {
        DisplayName = app.DisplayName;
        SubLabel = app.Subtitle;
        _appId = app.AppId;
        _iconPath = app.IconPath;
        _appEnv = appEnv;
        _icons = icons;
        LaunchCommand = new AsyncRelayCommand(LaunchAsync);
    }

    /// <summary>Stable identity for reconciliation — the app's bundle id (e.g. com.apple.Safari),
    /// which survives a rename or a moved bundle, unlike <see cref="DisplayName"/> or the icon path.</summary>
    public string AppId => _appId;

    public string DisplayName { get; }

    /// <summary>Optional short category label (e.g. "Developer Tools") shown as a grey second line under
    /// the name in the Luna Start menu's featured column. Null/empty for apps that declare no category.</summary>
    public string? SubLabel { get; }

    /// <summary>True when this item has a <see cref="SubLabel"/> — drives the two-line Start-menu row.</summary>
    public bool HasSubLabel => !string.IsNullOrEmpty(SubLabel);

    public Bitmap? IconSource { get => _iconSource; private set => SetProperty(ref _iconSource, value); }

    /// <summary>Command bound to the item; launches the app and closes the menu (host-wired).</summary>
    public ICommand LaunchCommand { get; }

    /// <summary>Raised after a successful launch so the host can close the Start menu.</summary>
    public event Action? Launched;

    /// <summary>
    /// Kicks the lazy off-thread icon load, idempotently. Called when the item's container is
    /// realized by the (virtualized) list, so scrolling — not opening — pays for icons.
    /// </summary>
    public void EnsureIcon()
    {
        if (_iconRequested) return;
        _iconRequested = true;
        _ = LoadIconAsync();
    }

    /// <summary>Pixel size the app icon is rendered at. Kept well above the largest on-screen use
    /// (Luna pins at 28, All Programs at 18, the classic cascade at 16) and doubled again for hi-DPI,
    /// so every display site downscales a crisp source instead of upscaling a 16px one (which looked
    /// blurry everywhere but the 16px cascade). Downscaling uses HighQuality interpolation at each site.</summary>
    private const int IconPixelSize = 64;

    private async Task LoadIconAsync()
    {
        var bmp = await _icons.LoadAsync(_iconPath, IconPixelSize).ConfigureAwait(false);
        if (bmp is null) return;
        // Only the assignment marshals to the UI thread; the render happened off-thread.
        await Dispatcher.UIThread.InvokeAsync(() => IconSource = bmp);
    }

    private async Task LaunchAsync()
    {
        if (_appEnv is not null)
        {
            try { await _appEnv.LaunchAsync(_appId); }
            catch (Exception ex) { TaskbarLog.Swallowed("Program.Launch", ex); }
        }
        Launched?.Invoke();
    }
}
