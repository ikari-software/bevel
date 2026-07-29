using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.UI;

namespace Bevel.Taskbar;

/// <summary>Help &amp; About window (bevel-x6pv) — the real target for the Start menu's "Help" /
/// "Help and Support" entries. Identifies the shell, shows its version, and lists the handful of
/// things a new user needs to get around. Not a placeholder: it is the shell's built-in help surface
/// until a fuller guide (bevel-cp2) ships.</summary>
public sealed class AboutDialog : BevelWindow
{
    public AboutDialog()
    {
        Title = "Help and About";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var title = new TextBlock
        {
            Text = "Bevel",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        var tagline = new TextBlock
        {
            Text = "A desktop shell — classic Windows, reimagined for macOS.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var version = new TextBlock
        {
            Text = "Version " + VersionString(),
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var tips = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Getting around:\n" +
                "  •  Start menu — click Start (or press the menu key) for programs, places, and settings.\n" +
                "  •  Taskbar — every open window gets a button; middle-click a button to close it.\n" +
                "  •  Right-click the desktop for view and arrangement options.\n" +
                "  •  Run… (Start ▸ Run) opens any program, folder, document, or web address.\n" +
                "  •  In a folder, press Space to Quick Look the selection, Enter to open it.",
            Margin = new Thickness(0, 0, 0, 4),
        };

        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);

        var body = new StackPanel { Margin = new Thickness(16, 16, 16, 8) };
        body.Children.Add(title);
        body.Children.Add(tagline);
        body.Children.Add(version);
        body.Children.Add(tips);

        var buttonBar = new Border { Child = buttons, Margin = new Thickness(16, 4, 16, 12) };
        DockPanel.SetDock(buttonBar, Dock.Bottom);
        var root = new DockPanel();
        root.Children.Add(buttonBar);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static string VersionString()
    {
        var asm = typeof(AboutDialog).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Strip any "+<git-sha>" build metadata for a clean display string.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.1.0";
    }
}
