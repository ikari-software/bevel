using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.UI;

namespace Bevel.App;

/// <summary>Minimal Win2000-style modal confirmation (Allow / Cancel). Used to authorize inbound
/// destructive Apple Events before they execute (bevel-twq): a same-user process can script the shell
/// into trashing/moving files, and macOS does not TCC-gate events inbound to our own app, so the user
/// must approve. Defaults to Cancel (Escape / close = deny).</summary>
public sealed class ConfirmDialog : BevelWindow
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var allow = new Button { Content = "Allow", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        allow.Click += (_, _) => Complete(true);
        cancel.Click += (_, _) => Complete(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(allow);
        buttons.Children.Add(cancel);

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 8) };
        var buttonBar = new Border { Child = buttons, Margin = new Thickness(16, 4, 16, 12) };
        DockPanel.SetDock(buttonBar, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(buttonBar);
        root.Children.Add(text);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Complete(false); };
    }

    private void Complete(bool result)
    {
        _tcs.TrySetResult(result);
        Close();
    }

    /// <summary>Shows the dialog and resolves to the user's choice. Uses a real modal ShowDialog when an
    /// owner window exists; otherwise shows ownerless (an AE can arrive with no Explorer window open).
    /// Deny is the default if the window is dismissed without a choice.</summary>
    public async Task<bool> ConfirmAsync(Window? owner)
    {
        Closed += (_, _) => _tcs.TrySetResult(false);   // dismissed without choosing → deny
        if (owner is not null) _ = ShowDialog(owner);
        else Show();
        return await _tcs.Task;
    }
}
