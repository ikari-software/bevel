using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bevel.UI;

namespace Bevel.Taskbar;

/// <summary>Win2000-style <b>Run</b> dialog, reimagined on macOS (bevel-x6pv). "Type the name of a
/// program, folder, document, or Internet resource, and Bevel will open it." Everything routes through
/// <c>/usr/bin/open</c> — the same handler Finder uses — so a path, a folder, a URL, or an app name all
/// just work. This is a real launcher, not a shell: it opens things, it doesn't exec arbitrary commands.</summary>
public sealed class RunDialog : BevelWindow
{
    private readonly TextBox _input;

    public RunDialog()
    {
        Title = "Run";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var prompt = new TextBlock
        {
            Text = "Type the name of a program, folder, document, or Internet resource, and Bevel will open it for you.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 8),
        };

        _input = new TextBox { Margin = new Thickness(16, 0, 16, 0), Watermark = "Open:" };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Launch(); e.Handled = true; } };

        var ok = new Button { Content = "Open", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => Launch();
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var buttonBar = new Border { Child = buttons, Margin = new Thickness(16, 8, 16, 12) };
        DockPanel.SetDock(prompt, Dock.Top);
        DockPanel.SetDock(_input, Dock.Top);
        DockPanel.SetDock(buttonBar, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(prompt);
        root.Children.Add(buttonBar);
        root.Children.Add(_input);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened += (_, _) => _input.Focus();
    }

    private void Launch()
    {
        var args = RunResolver.Resolve(_input.Text);
        if (args is null) return;   // empty input — keep the dialog open

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            catch { /* nothing to open / not launchable — swallow like Finder's silent decline */ }
        }
        Close();
    }
}

/// <summary>Turns a Run input string into arguments for <c>/usr/bin/open</c>. Pure + platform-neutral so
/// the routing is unit-testable without launching anything.</summary>
public static class RunResolver
{
    /// <summary>Resolves the input to <c>open</c> arguments, or <c>null</c> for empty input.
    /// A URL opens as-is; an existing path (with <c>~</c> expanded) opens directly; anything else is
    /// treated as an application name (<c>open -a &lt;name&gt;</c>).</summary>
    public static string[]? Resolve(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        // A URL / scheme (http://, https://, mailto:, x-apple.systempreferences:, …) opens verbatim.
        if (text.Contains("://", StringComparison.Ordinal) || SchemeLike(text))
            return new[] { text };

        var expanded = ExpandHome(text);
        if (File.Exists(expanded) || Directory.Exists(expanded))
            return new[] { expanded };

        // Otherwise it's an application name — `open -a TextEdit` launches the app by name.
        return new[] { "-a", text };
    }

    /// <summary>A leading scheme like <c>mailto:</c> or <c>x-apple.systempreferences:</c> (a colon before
    /// any slash), so single-slash-less URL schemes still open as URLs rather than app names.</summary>
    private static bool SchemeLike(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        var slash = text.IndexOf('/');
        // Windows-drive-style "C:" never happens on macOS paths; a scheme has letters before the colon.
        return (slash < 0 || colon < slash) && text[0] is not ('/' or '~' or '.');
    }

    private static string ExpandHome(string path)
        => path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length <= 2 ? "" : path[2..])
            : path;
}
