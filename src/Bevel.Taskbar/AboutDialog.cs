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
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var mark = BevelMark.Create(96);
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.Margin = new Thickness(0, 0, 16, 0);

        var title = new TextBlock
        {
            Text = "Bevel",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        var tagline = new TextBlock
        {
            Text = "A contemporary desktop shell for macOS, Windows, and Linux.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var version = new TextBlock
        {
            Text = "Version " + VersionString(),
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 0),
        };

        var identity = new StackPanel();
        identity.Children.Add(title);
        identity.Children.Add(tagline);
        identity.Children.Add(version);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(mark);
        header.Children.Add(identity);

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
            Margin = new Thickness(0, 0, 0, 12),
        };

        var notice = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 8),
            Text =
                "Trademarks and Compatibility Notice\n" +
                "Bevel is an independent, open-source software project developed by ikari.software. " +
                "Bevel is not affiliated with, endorsed by, sponsored by, or associated with Microsoft Corporation, " +
                "Apple Inc., or the Linux Foundation.\n" +
                "Microsoft, Windows, Windows 2000, Windows XP, Windows Vista, and the Windows logo are registered " +
                "trademarks of Microsoft Corporation in the United States and other countries. Apple, macOS, and the " +
                "Apple logo are registered trademarks of Apple Inc., registered in the U.S. and other countries. " +
                "Linux is the registered trademark of Linus Torvalds.\n" +
                "All third-party trademarks, product names, and visual references are used strictly in a descriptive " +
                "and referential capacity to communicate operating system compatibility and design history. " +
                "All trademarks remain the property of their respective owners.",
        };

        var tuxCredit = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 4),
            Text = "Tux the penguin was created by Larry Ewing (lewing@isc.tamu.edu) using The GIMP.",
        };

        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);

        var body = new StackPanel { Margin = new Thickness(16, 16, 16, 8) };
        body.Children.Add(header);
        body.Children.Add(tips);
        body.Children.Add(notice);
        body.Children.Add(tuxCredit);

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
