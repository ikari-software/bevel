using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
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
    private bool _isDisconnected;

    /// <param name="connection">Link health to the shell core (null in tests / all-in-one, treated
    /// as always connected). Drives the tray disconnected indicator.</param>
    public TaskbarViewModel(ShellModel model, StartMenuViewModel startMenu, IShellConnectionStatus? connection = null)
    {
        Model = model;
        StartMenu = startMenu;
        _connection = connection;
        if (connection is not null)
        {
            _isDisconnected = !connection.IsConnected;
            connection.ConnectionChanged += OnConnectionChanged;
        }
    }

    public ShellModel Model { get; }
    public StartMenuViewModel StartMenu { get; }

    public ObservableCollection<TaskItemViewModel> Windows => Model.Windows;

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
    }
}
