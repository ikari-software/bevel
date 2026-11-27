using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Top-level view-model for <see cref="TaskbarView"/>: a thin projection of the background
/// <see cref="ShellModel"/>. The window-button strip binds <see cref="Windows"/> (an observable
/// collection kept current off the UI thread) via a virtualization-friendly ItemsControl, so
/// the view never builds or mutates buttons by hand. Per-button width (the Win2000 shrink-to-fit
/// sizing, and the XP grow/shrink animation) lives on each <see cref="TaskItemViewModel"/>; the
/// view's layout pass computes the shared target and pushes it to every live button.
/// </summary>
public sealed class TaskbarViewModel : ObservableObject, IDisposable
{
    private readonly IShellConnectionStatus? _connection;
    private readonly TaskbarItemsProjector _projector;
    private readonly ISettingsService? _settings;
    private bool _isDisconnected;
    private bool _consolidated;
    private bool _disposed;
    /// <summary>bevel-be86: pinned app bundle ids in pin order, mirrored from
    /// <see cref="BevelSettings.TaskbarPinnedApps"/> (or kept locally when no settings service is
    /// wired, e.g. unit tests). Drives both the projector's synthetic pin buttons and the context
    /// menu's Pin/Unpin label.</summary>
    private readonly List<string> _pinnedApps = new();

    /// <param name="connection">Link health to the shell core (null in tests / all-in-one, treated
    /// as always connected). Drives the tray disconnected indicator.</param>
    /// <param name="openFolder">bevel-hsvx: forwarded to every stack, so a stack's "Open folder"
    /// opens a supervised Filer instead of the platform default. Resolved from DI in the app
    /// (CompositionRoot registers it next to IFilerSpawner); null in tests.</param>
    public TaskbarViewModel(ShellModel model, StartMenuViewModel startMenu,
        IShellConnectionStatus? connection = null, ISystemTrayHost? tray = null,
        ISettingsService? settings = null, IAppEnvironment? appEnv = null, IIconProvider? icons = null,
        IThumbnailProvider? thumbnails = null, Action<string>? openFolder = null)
    {
        Model = model;
        ShowDesktopCommand = new AsyncRelayCommand(Model.MinimizeAllAsync);
        CascadeCommand = new AsyncRelayCommand(() => Arrange(WindowArranger.Cascade));
        TileHorizontallyCommand = new AsyncRelayCommand(() => Arrange(WindowArranger.TileHorizontally));
        TileVerticallyCommand = new AsyncRelayCommand(() => Arrange(WindowArranger.TileVertically));
        StartMenu = startMenu;
        _connection = connection;
        _projector = new TaskbarItemsProjector(model.Windows);
        Tray = new TrayViewModel(tray);
        Tray.Start();
        Stacks = new StacksViewModel(
            settings?.Current.TaskbarStacks ?? Enumerable.Empty<string>(), appEnv, icons, thumbnails, openFolder);
        if (connection is not null)
        {
            // Subscribe BEFORE reading IsConnected (same discipline as App.axaml.cs's settings.Changed
            // fix): a connection flip landing in the gap between the read and the subscribe would
            // otherwise never invoke OnConnectionChanged for that transition, leaving IsDisconnected
            // wrong until some LATER, unrelated flip corrects it. Subscribing first means such a flip
            // is always observed, even if it lands between these two lines.
            connection.ConnectionChanged += OnConnectionChanged;
            _isDisconnected = !connection.IsConnected;
        }

        // Strategy C (bevel-7hf4): drive menu-bar consolidation from settings — apply the current value
        // and re-apply whenever settings change (the 750ms poll raises Changed). Subscribe BEFORE the
        // first read for the same reason as above: a settings snapshot landing between the read and the
        // subscribe would otherwise be lost until the next unrelated settings change (or never, in a
        // session with no further edits) — matching the RemoteSettingsService/App.axaml.cs discipline.
        _settings = settings;
        if (settings is not null)
        {
            settings.Changed += OnSettingsChanged;
            var startup = settings.Current;   // one snapshot read for both startup applies (bevel-be86 keeps the guarded read count)
            ApplyConsolidation(startup.TaskbarConsolidateMenuBar);
            ApplyPins(startup.TaskbarPinnedApps);   // bevel-be86 (subscribe-before-read, as above)
        }
        else
        {
            ApplyPins(Enumerable.Empty<string>());
        }
    }

    // ── Pinned apps (bevel-be86) ────────────────────────────────────────

    /// <summary>The pinned app bundle ids in pin order.</summary>
    public IReadOnlyList<string> PinnedApps => _pinnedApps;

    /// <summary>Whether <paramref name="bundleId"/> is pinned — feeds the task-button context menu's
    /// Pin/Unpin label. False for a null/empty key (nothing to pin on). The key is normalized first, so
    /// an <c>app:</c>-prefixed window id and the bare bundle id name the same pin.</summary>
    public bool IsAppPinned(string? bundleId)
    {
        var key = TaskbarPins.NormalizeKey(bundleId);
        return key is not null && _pinnedApps.Contains(key);
    }

    /// <summary>Pushes a pin set onto the projector (synthetic buttons for pinned apps with no window)
    /// and mirrors it locally. Idempotent — duplicate ids collapse, an unchanged set re-plans nothing
    /// new. The list is SANITIZED first (bevel-be86 review): path-shaped / over-length / non-bundle-id
    /// entries are dropped at this apply boundary too, so a crafted value arriving via a settings
    /// broadcast never reaches the projector or the launch sink. UI thread.</summary>
    private void ApplyPins(IEnumerable<string>? pins)
    {
        if (_disposed) return;   // guard posted applies (Changed can land during/after teardown)
        var list = TaskbarPins.Sanitize(pins);
        _pinnedApps.Clear();
        _pinnedApps.AddRange(list);
        _projector.SetPinnedApps(_pinnedApps, Model.CreatePinnedItem);
    }

    /// <summary>bevel-be86: pins (or unpins) an app, persisting through the settings store
    /// (<see cref="BevelSettings.TaskbarPinnedApps"/>). Idempotent: pinning an already-pinned app is a
    /// no-op (the id appears once), unpinning an unpinned one likewise. The strip updates when the
    /// write lands — immediately for a local store (Current mutates in place), or when the core's
    /// settings broadcast arrives (Changed) in split mode. The key is normalized (an <c>app:</c>
    /// prefix is stripped) and validated — a path-shaped id is refused outright, never persisted or
    /// launched.</summary>
    public async Task TogglePinAsync(string? bundleId)
    {
        var key = TaskbarPins.NormalizeKey(bundleId);
        if (!TaskbarPins.IsValidBundleId(key)) return;   // never key (or persist) a non-bundle-id
        if (_settings is null)
        {
            var pinLocal = !IsAppPinned(key);
            ApplyPins(pinLocal ? _pinnedApps.Append(key!) : _pinnedApps.Where(p => p != key));
            return;
        }
        // Lost-update guard (bevel-be86 review): the patch below replaces the WHOLE key, so with two
        // taskbar instances the last write wins. Re-read Current immediately before building the
        // patch, and if the store's version moved under us mid-write (ReloadIfChangedAsync pulled an
        // external write), rebuild from the fresh snapshot and retry once.
        var wantPinned = !IsAppPinned(key);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = _settings.Current;   // fresh re-read before building the patch
            var already = (current.TaskbarPinnedApps ?? Array.Empty<string>()).Contains(key);
            if (already == wantPinned) break;   // converged (our write or a peer's already landed)
            await _settings.UpdateAsync(s =>
            {
                var cur = s.TaskbarPinnedApps ?? Array.Empty<string>();
                s.TaskbarPinnedApps = wantPinned
                    ? cur.Append(key!).Distinct(StringComparer.Ordinal).ToArray()
                    : cur.Where(p => !string.Equals(p, key, StringComparison.Ordinal)).ToArray();
            });
            // Version mismatch = an external write landed while ours was in flight → retry once from
            // the fresh state. (No-op returning false on the remote store, where the broadcast itself
            // bumps Version.)
            if (!await _settings.ReloadIfChangedAsync()) break;
        }
        // Local stores mutate Current in place, so re-sync now; the remote path is a no-op here and
        // the broadcast's Changed handler applies it when the core answers.
        ApplyPins(_settings.Current.TaskbarPinnedApps);
    }

    public ShellModel Model { get; }

    /// <summary>Minimizes every window — bound by the far-right "Show desktop" sliver (bevel-cust).</summary>
    public System.Windows.Input.ICommand ShowDesktopCommand { get; }

    /// <summary>Work-area provider (primary display minus the taskbar band), wired by TaskbarView from
    /// its TaskbarWindow (bevel-apm); null in tests → the arrange commands no-op.</summary>
    public Func<PalRect?>? WorkArea { get; set; }

    /// <summary>Bar-menu window arrangement commands (bevel-apm): cascade, tile horizontally, tile
    /// vertically over the work area.</summary>
    public System.Windows.Input.ICommand CascadeCommand { get; }
    public System.Windows.Input.ICommand TileHorizontallyCommand { get; }
    public System.Windows.Input.ICommand TileVerticallyCommand { get; }

    private System.Threading.Tasks.Task Arrange(Func<PalRect, int, IReadOnlyList<PalRect>> layout)
    {
        var area = WorkArea?.Invoke();
        return area is { } a
            ? Model.ArrangeAsync(layout, a)
            : System.Threading.Tasks.Task.CompletedTask;
    }

    public StartMenuViewModel StartMenu { get; }

    /// <summary>The notification-area tray (mirrored menu-bar status items, bevel-m3.1).</summary>
    public TrayViewModel Tray { get; }

    /// <summary>The taskbar folder stacks (recent-contents flyouts, bevel-12g).</summary>
    public StacksViewModel Stacks { get; }

    /// <summary>The flat per-window collection (source of truth). Kept for callers/tests that want
    /// the raw windows; the strip binds <see cref="Items"/> instead.</summary>
    public ObservableCollection<TaskItemViewModel> Windows => Model.Windows;

    /// <summary>The displayed strip items — single-window buttons and, when grouping is on, app groups
    /// (bevel-m2.10.3). With grouping off this mirrors <see cref="Windows"/> 1:1.</summary>
    public ObservableCollection<ITaskbarItem> Items => _projector.Items;

    /// <summary>Enables/disables XP-style window grouping (applied at startup from settings).</summary>
    public void SetGrouping(TaskbarGroupingMode mode) => _projector.SetGrouping(mode);

    public void SetSort(TaskbarWindowSort sort, bool windowlessLast) => _projector.SetSort(sort, windowlessLast);
    /// <summary>bevel-jt30: live toggle for listing windows on other Spaces.</summary>
    public void SetShowOtherSpaces(bool show) => _projector.SetShowOtherSpaces(show);

    /// <summary>True while the taskbar has lost its link to the shell core (commands fail and the
    /// strip is stale until it reconnects). Bound to the tray's disconnected indicator.</summary>
    public bool IsDisconnected
    {
        get => _isDisconnected;
        private set => SetProperty(ref _isDisconnected, value);
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        // ConnectionChanged fires on a transport/reconnect thread; hop to the UI thread before
        // touching the bound property.
        Dispatcher.UIThread.Post(() => IsDisconnected = !connected);
    }

    private void OnSettingsChanged()
    {
        var current = _settings?.Current;   // one snapshot read for both applies
        ApplyConsolidation(current?.TaskbarConsolidateMenuBar ?? false);
        // bevel-be86: pins changed (possibly pushed from another process) — re-project the strip.
        // Changed fires off the transport thread; the projector's collections are UI-thread-owned.
        var pins = current?.TaskbarPinnedApps;
        Dispatcher.UIThread.Post(() => { if (!_disposed) ApplyPins(pins); });   // guard: disposed between post and run
    }

    /// <summary>Applies the consolidation setting via the tray host, skipping redundant RPCs when the
    /// state is unchanged (Changed can fire for unrelated setting edits).</summary>
    private void ApplyConsolidation(bool consolidated)
    {
        if (consolidated == _consolidated) return;
        _consolidated = consolidated;
        Tray.SetConsolidated(consolidated);
    }

    public void Dispose()
    {
        _disposed = true;   // guards the posted ApplyPins/RePlan (bevel-be86 review)
        if (_connection is not null)
            _connection.ConnectionChanged -= OnConnectionChanged;
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;
        _projector.Dispose();
        Tray.Dispose();
        Stacks.Dispose();
    }
}
