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
    private bool _isDisconnected;

    /// <param name="connection">Link health to the shell core (null in tests / all-in-one, treated
    /// as always connected). Drives the tray disconnected indicator.</param>
    public TaskbarViewModel(ShellModel model, StartMenuViewModel startMenu,
        IShellConnectionStatus? connection = null, ISystemTrayHost? tray = null,
        SettingsService? settings = null, IAppEnvironment? appEnv = null, IIconProvider? icons = null)
    {
        Model = model;
        ShowDesktopCommand = new AsyncRelayCommand(Model.MinimizeAllAsync);
        StartMenu = startMenu;
        _connection = connection;
        _projector = new TaskbarItemsProjector(model.Windows);
        Tray = new TrayViewModel(tray);
        Tray.Start();
        Stacks = new StacksViewModel(
            settings?.Current.TaskbarStacks ?? Enumerable.Empty<string>(), appEnv, icons);
        if (connection is not null)
        {
            _isDisconnected = !connection.IsConnected;
            connection.ConnectionChanged += OnConnectionChanged;
        }
    }

    public ShellModel Model { get; }

    /// <summary>Minimizes every window — bound by the far-right "Show desktop" sliver (bevel-cust).</summary>
    public System.Windows.Input.ICommand ShowDesktopCommand { get; }
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

    public void Dispose()
    {
        if (_connection is not null)
            _connection.ConnectionChanged -= OnConnectionChanged;
        _projector.Dispose();
        Tray.Dispose();
        Stacks.Dispose();
    }
}
