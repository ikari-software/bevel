using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.UI;

namespace Bevel.FileManager.Components;

/// <summary>Minimal Win2000-style modal message box: a wrapped message and an OK button. Used to
/// surface file-operation failures the user must see — a status-bar line would be clobbered by the
/// post-operation refresh, whereas a modal dialog blocks until acknowledged.</summary>
public sealed class MessageDialog : BevelWindow
{
    public MessageDialog(string title, string message)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button
        {
            Content = "OK",
            MinWidth = 75,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ok.Click += (_, _) => Close();

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 8),
        };

        var buttonBar = new Border { Child = ok, Margin = new Thickness(16, 4, 16, 12) };
        DockPanel.SetDock(buttonBar, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(buttonBar);
        root.Children.Add(text);
        Content = root;

        // Enter/Esc both dismiss.
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) Close(); };
    }

    public Task ShowMessageAsync(Window owner) => ShowDialog(owner);
}
