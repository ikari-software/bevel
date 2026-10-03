using Bevel.Core;
using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of a Luna context menu (bevel-menu-luna), to eyeball the XP menu anatomy —
/// white item area, cream icon gutter, rounded corners, right-aligned accelerators, submenu arrow,
/// disabled greying. Opens a real <see cref="ContextMenu"/> and captures its popup TopLevel to a PNG.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderBlue2001MenuTest
{
    [AvaloniaFact]
    public void Render_luna_context_menu_to_png()
    {
        Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
        try
        {
            var target = new Border { Width = 40, Height = 40 };
            var win = new Window { Width = 260, Height = 260, Content = target };
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Open", InputGesture = new KeyGesture(Key.Enter) });
            menu.Items.Add(new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Meta) });
            menu.Items.Add(new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Meta) });
            menu.Items.Add(new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Meta) });
            menu.Items.Add(new Separator());
            var props = new MenuItem { Header = "Properties" };
            props.Items.Add(new MenuItem { Header = "Details" });
            menu.Items.Add(props);
            menu.Items.Add(new MenuItem { Header = "Delete", IsEnabled = false });

            menu.Open(target);
            for (var i = 0; i < 12; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            var root = menu.GetVisualRoot() as TopLevel;
            Assert.NotNull(root);
            var frame = root!.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_MENU_LUNA_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-menu-luna.png");
            frame!.Save(outPath);
        }
        finally { Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999); }
    }

    /// <summary>The taskbar/clock menus use MenuFlyout, whose presenter is a distinct type from
    /// ContextMenu — render it directly to prove the Luna container override reaches it too.</summary>
    [AvaloniaFact]
    public void Render_luna_menu_flyout_presenter_to_png()
    {
        Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
        try
        {
            var presenter = new MenuFlyoutPresenter();
            presenter.Items.Add(new MenuItem { Header = "Cascade Windows" });
            presenter.Items.Add(new MenuItem { Header = "Show the Desktop" });
            presenter.Items.Add(new Separator());
            presenter.Items.Add(new MenuItem { Header = "Lock the Taskbar", IsChecked = true });
            presenter.Items.Add(new MenuItem { Header = "Properties", InputGesture = new KeyGesture(Key.Enter) });

            var win = new Window
            {
                Width = 240, Height = 200,
                Content = new Border { Padding = new Thickness(20), Child = presenter },
            };
            win.Show();
            for (var i = 0; i < 12; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_FLYOUT_LUNA_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-flyout-luna.png");
            frame!.Save(outPath);
        }
        finally { Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999); }
    }
}
