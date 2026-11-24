using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bevel.UI;

/// <summary>Minimal Win2000-style modal confirmation (confirm / Cancel). Moved from Bevel.App to
/// Bevel.UI so Filer can use it too (bevel-j905 — the Trash's Empty confirmation); App's inbound
/// Apple Event authorization uses it unchanged (bevel-twq): a same-user process can script the shell
/// into trashing/moving files, and macOS does not TCC-gate events inbound to our own app, so the user
/// must approve. Defaults to Cancel (Escape / close = deny); <paramref name="destructive"/> makes the
/// confirm button the non-default one and Cancel the default, for irreversible actions.</summary>
public sealed class ConfirmDialog : BevelWindow
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public ConfirmDialog(string title, string message, string confirmLabel = "Allow", bool destructive = false)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var confirm = new Button
        {
            Content = confirmLabel,
            MinWidth = 80,
            IsDefault = !destructive,   // destructive actions must not be one keystroke away
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 80,
            IsCancel = true,
            IsDefault = destructive,   // destructive: Cancel is the default button
        };
        confirm.Click += (_, _) => Complete(true);
        cancel.Click += (_, _) => Complete(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(confirm);
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
    /// owner window exists; otherwise shows ownerless (an AE can arrive with no Filer window open).
    /// Deny is the default if the window is dismissed without a choice.</summary>
    public async Task<bool> ConfirmAsync(Window? owner)
    {
        Closed += (_, _) => _tcs.TrySetResult(false);   // dismissed without choosing → deny
        if (owner is not null) _ = ShowDialog(owner);
        else Show();
        return await _tcs.Task;
    }
}
