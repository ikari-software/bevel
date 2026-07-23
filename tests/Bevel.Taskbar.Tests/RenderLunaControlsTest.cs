using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the standard control gallery under the Luna theme, so buttons, checkbox,
/// radio, textbox, progress bar, combo box, slider, group box and tabs can be eyeballed against the
/// styleguide (docs/design/luna/preview.png). Dumps a PNG; not a pixel assertion.
/// </summary>
public class RenderLunaControlsTest
{
    [AvaloniaFact]
    public void Render_luna_controls_to_png()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");

            var body = new StackPanel { Margin = new Thickness(16), Spacing = 12, Width = 360 };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(new Button { Content = "OK", MinWidth = 73, IsDefault = true });
            buttons.Children.Add(new Button { Content = "Cancel", MinWidth = 73 });
            buttons.Children.Add(new Button { Content = "Disabled", MinWidth = 73, IsEnabled = false });
            body.Children.Add(buttons);

            var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            checks.Children.Add(new CheckBox { Content = "Enabled", IsChecked = true });
            checks.Children.Add(new RadioButton { Content = "Selected", IsChecked = true });
            body.Children.Add(checks);

            body.Children.Add(new TextBox { Text = "selected text", Width = 180, HorizontalAlignment = HorizontalAlignment.Left });

            body.Children.Add(new ProgressBar { Value = 62, Minimum = 0, Maximum = 100, Height = 16 });

            var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            combo.Items.Add("Luna (Blue)");
            combo.Items.Add("Olive");
            combo.SelectedIndex = 0;
            body.Children.Add(combo);

            body.Children.Add(new Slider { Value = 55, Minimum = 0, Maximum = 100, Width = 200, HorizontalAlignment = HorizontalAlignment.Left });

            var group = new HeaderedContentControl
            {
                Header = "Appearance",
                Content = new TextBlock { Text = "Group box content", Margin = new Thickness(8) },
            };
            body.Children.Add(group);

            var tabs = new TabControl { Height = 90 };
            tabs.Items.Add(new TabItem { Header = "General", Content = new TextBlock { Text = "General tab", Margin = new Thickness(8) } });
            tabs.Items.Add(new TabItem { Header = "View" });
            tabs.Items.Add(new TabItem { Header = "Advanced" });
            body.Children.Add(tabs);

            var window = new Bevel.UI.BevelWindow
            {
                Title = "Bevel — Luna controls",
                Width = 392,
                Height = 560,
                Background = new SolidColorBrush(Color.Parse("#ECE9D8")),
                Content = body,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_CONTROLS_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-controls.png");
            frame!.Save(outPath);
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }
}
