using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.Pal.Abstractions;
using Bevel.UI;

namespace Bevel.Taskbar;

/// <summary>What a ShutDownDialog choice means (bevel-4vce). Bevel entries run the shell's own
/// quit/restart; OS entries go through <see cref="IShellSession.LogOutAsync"/>.</summary>
public enum ShutDownAction
{
    QuitBevel,
    RestartBevel,
    Lock,
    LogOff,
    RestartComputer,
    ShutDown,
}

/// <summary>Win2000 "Shut Down" dialog — "What do you want the computer to do?" (bevel-4vce). Code-built
/// like <see cref="RunDialog"/>, theme-styled controls only. Bevel entries (Quit/Restart Bevel) always
/// work; the OS entries are enabled only when a session seam is present. Cancel/X resolves to null.</summary>
public sealed class ShutDownDialog : BevelWindow
{
    private readonly TaskCompletionSource<ShutDownAction?> _tcs = new();
    private readonly ComboBox _choice;

    /// <param name="session">OS session seam — when null the OS-level entries render disabled.</param>
    public ShutDownDialog(Func<LogoutKind, Task>? session)
    {
        Title = "Shut Down Bevel";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var prompt = new TextBlock
        {
            Text = "What do you want the computer to do?",
            Margin = new Thickness(16, 16, 16, 8),
        };

        _choice = new ComboBox { Margin = new Thickness(16, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        var entries = new List<(string Label, ShutDownAction Action, bool Enabled)>
        {
            ("Quit Bevel", ShutDownAction.QuitBevel, true),          // default
            ("Restart Bevel", ShutDownAction.RestartBevel, true),
            ("Lock", ShutDownAction.Lock, session is not null),
            ("Log off", ShutDownAction.LogOff, session is not null),
            ("Restart computer", ShutDownAction.RestartComputer, session is not null),
            ("Shut down", ShutDownAction.ShutDown, session is not null),
        };
        foreach (var (label, _, _) in entries)
            _choice.Items.Add(label);
        _choice.SelectedIndex = 0;
        // Disabled OS entries: keep them visible but unselectable (combo items can't be disabled
        // individually, so guard on selection change).
        _choice.SelectionChanged += (_, _) =>
        {
            if (_choice.SelectedIndex > 1 && session is null)
                _choice.SelectedIndex = 0;   // no session seam → OS entries are not real
        };

        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => Complete(Current());
        cancel.Click += (_, _) => Complete(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 12, 16, 12),
            Children = { ok, cancel },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new StackPanel { Children = { prompt, _choice } });
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Complete(null); };
    }

    private ShutDownAction? Current() => _choice.SelectedIndex switch
    {
        1 => ShutDownAction.RestartBevel,
        2 => ShutDownAction.Lock,
        3 => ShutDownAction.LogOff,
        4 => ShutDownAction.RestartComputer,
        5 => ShutDownAction.ShutDown,
        _ => ShutDownAction.QuitBevel,
    };

    private void Complete(ShutDownAction? result)
    {
        _tcs.TrySetResult(result);
        Close();
    }

    /// <summary>Shows the dialog and resolves to the chosen action, or null on Cancel/dismiss.</summary>
    public async Task<ShutDownAction?> PickAsync(Window? owner)
    {
        Closed += (_, _) => _tcs.TrySetResult(null);
        if (owner is not null) _ = ShowDialog(owner);
        else Show();
        return await _tcs.Task;
    }
}
