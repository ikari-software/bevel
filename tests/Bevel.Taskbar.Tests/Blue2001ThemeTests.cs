using Bevel.Core;
using System;
using System.IO;
using Avalonia;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The Luna theme is a per-theme control-template set (bevel-dob): ThemeService adds Blue2001Theme.axaml
/// to Application.Styles so its ControlThemes override the Classic ones and its tokens override the
/// base palette/metrics. These assert the engine end-to-end and dump a PNG of the Luna push-buttons
/// to eyeball against docs/design/luna/styleguide.html.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class Blue2001ThemeTests
{
    private static object? Resolve(string key)
    {
        Application.Current!.TryGetResource(key, null, out var value);
        return value;
    }

    [AvaloniaFact]
    public void Setting_the_Theme_property_retemplates_a_live_control_in_place()
    {
        // The foundation for live geometry+layout theming: does swapping a control's explicit
        // ControlTheme at runtime re-template an already-shown control IN PLACE (no rebuild)?
        static ControlTheme Make(IBrush bg)
        {
            var t = new ControlTheme(typeof(Button));
            t.Add(new Setter(TemplatedControl.TemplateProperty,
                new FuncControlTemplate((_, _) => new Border { Name = "root", Background = bg })));
            return t;
        }
        var red = Make(Brushes.Red);
        var blue = Make(Brushes.Blue);

        var btn = new Button { Theme = red, Content = "x" };
        var window = new Window { Content = btn, SystemDecorations = SystemDecorations.None };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var before = btn.GetVisualDescendants().OfType<Border>().First().Background;

        // Swap the ControlTheme on the SAME, already-shown control — the whole template (geometry) changes.
        btn.Theme = blue;
        Dispatcher.UIThread.RunJobs();
        var after = btn.GetVisualDescendants().OfType<Border>().First().Background;

        Assert.Equal(Brushes.Red, before);
        Assert.Equal(Brushes.Blue, after);   // re-templated live, in place, no detach/reattach
    }

    [AvaloniaFact]
    public void Switching_theme_with_a_live_tabcontrol_does_not_throw()
    {
        // Regression (whole-shell crash): the Classic TabControl template sets GridExtensions
        // .ColumnDefinitionsEx/.RowDefinitionsEx. Re-templating a live TabControl on a theme swap cleared
        // those to null, and the vendored handler's AddRange(null) threw ArgumentNullException — which took
        // down the shell whenever the theme was switched with a tabbed window (the Properties dialog) open.
        // Earlier tests only swapped a lone button/panel, so this path was never exercised.
        try
        {
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "General", Content = new TextBlock { Text = "x" } });
            tabs.Items.Add(new TabItem { Header = "Start" });
            var window = new Window
            {
                Width = 320, Height = 220, SystemDecorations = SystemDecorations.None, Content = tabs,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var ex = Record.Exception(() =>
            {
                Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
                Dispatcher.UIThread.RunJobs();
                Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
                Dispatcher.UIThread.RunJobs();
                Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
                Dispatcher.UIThread.RunJobs();
            });
            Assert.Null(ex);
        }
        finally { Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999); }
    }

    [AvaloniaFact]
    public void Switching_theme_retemplates_bound_buttons_live_in_place()
    {
        // The engine (bevel-dob): App.axaml binds Button.Theme="{DynamicResource Bevel.Theme.Button}";
        // each theme supplies its own Bevel.Theme.Button ControlTheme. Swapping the theme swaps that
        // resource, and DynamicResource re-resolves the Theme → the SAME already-shown button
        // re-templates in place (Classic solid <-> Luna gradient), with NO detach/reattach, no rebuild.
        try
        {
            var ok = new Button { Content = "OK", MinWidth = 90 };
            var window = new Window { Content = ok, SystemDecorations = SystemDecorations.None };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.IsNotType<LinearGradientBrush>(ok.Background);   // default (win2000) = Classic solid

            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<LinearGradientBrush>(ok.Background);      // Luna glossy — live, in place

            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
            Dispatcher.UIThread.RunJobs();
            Assert.IsNotType<LinearGradientBrush>(ok.Background);   // reverts live
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Taskbar_surface_brush_swaps_grey_to_luna_gradient_live()
    {
        // Shell surfaces (bevel-dob) ride the same DynamicResource contract as controls, but via a
        // shared *brush* token instead of a Theme swap: the taskbar binds RootGrid.Background to
        // Bevel.Brush.TaskbarBackground, so swapping the theme recolours the bar in place — no rebuild.
        try
        {
            var bar = new Border { Width = 300, Height = 30 };
            bar[!Border.BackgroundProperty] =
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Bevel.Brush.TaskbarBackground");
            var window = new Window { Content = bar, SystemDecorations = SystemDecorations.None };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<SolidColorBrush>(bar.Background);   // win2000 = flat grey

            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<LinearGradientBrush>(bar.Background);   // Luna blue gradient — live, in place

            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<SolidColorBrush>(bar.Background);   // reverts live
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Luna_theme_swaps_tokens_and_overrides_the_button_template()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);

            // Tokens: the Luna palette + metrics override the base (XP Luna column, §3 / LUN-02).
            Assert.Equal(25d, Resolve("Bevel.Metric.CaptionHeight"));
            Assert.Equal(Color.Parse("#ECE9D8"), (Color)Resolve("Bevel.Color.ButtonFace")!);
            // The Styles set loaded — its own Luna.* gradient brush resolves.
            Assert.IsType<LinearGradientBrush>(Resolve("Blue2001.Brush.Button"));

            // The crux (bevel-dob): the Luna ControlTheme wins over Classic's {x:Type Button}, so a
            // plain Button's background is the Luna gradient, not the Classic solid ControlBrush.
            var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var ok = new Button { Content = "OK", MinWidth = 90 };
            buttonsRow.Children.Add(ok);
            buttonsRow.Children.Add(new Button { Content = "Apply", MinWidth = 90, IsDefault = true });
            buttonsRow.Children.Add(new Button { Content = "Cancel", MinWidth = 90 });
            buttonsRow.Children.Add(new Button { Content = "Disabled", MinWidth = 90, IsEnabled = false });

            var checksCol = new StackPanel { Spacing = 6 };
            checksCol.Children.Add(new CheckBox { Content = "Enabled", IsChecked = true });
            checksCol.Children.Add(new CheckBox { Content = "Unchecked" });
            var radiosCol = new StackPanel { Spacing = 6 };
            radiosCol.Children.Add(new RadioButton { Content = "Selected", IsChecked = true });
            radiosCol.Children.Add(new RadioButton { Content = "Option" });
            var selRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
            selRow.Children.Add(checksCol);
            selRow.Children.Add(radiosCol);

            var progress = new ProgressBar
            {
                Value = 40, Minimum = 0, Maximum = 100,
                Width = 300, Height = 18, HorizontalAlignment = HorizontalAlignment.Left,
            };

            var tabs = new TabControl { Width = 300, Height = 70, HorizontalAlignment = HorizontalAlignment.Left };
            tabs.Items.Add(new TabItem { Header = "General", Content = new TextBlock { Text = "  ", Margin = new Thickness(8) } });
            tabs.Items.Add(new TabItem { Header = "View" });
            tabs.Items.Add(new TabItem { Header = "Advanced" });

            var combo = new ComboBox { Width = 140 };
            combo.Items.Add("Bevel (Blue)");
            combo.Items.Add("Olive");
            combo.SelectedIndex = 0;
            var textbox = new TextBox { Width = 160, Text = "selected text" };
            var scroll = new ScrollBar
            {
                Orientation = Orientation.Vertical, Height = 90,
                Minimum = 0, Maximum = 100, Value = 30, ViewportSize = 40,
                AllowAutoHide = false,
            };
            var inputs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Top };
            inputs.Children.Add(combo);
            inputs.Children.Add(textbox);
            inputs.Children.Add(scroll);

            var root = new StackPanel { Spacing = 16, Margin = new Thickness(18) };
            root.Children.Add(buttonsRow);
            root.Children.Add(selRow);
            root.Children.Add(inputs);
            root.Children.Add(progress);
            root.Children.Add(tabs);

            var window = new Window
            {
                Width = 470,
                Height = 360,
                SystemDecorations = SystemDecorations.None,
                Background = new SolidColorBrush(Color.Parse("#ECE9D8")),
                Content = root,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.IsType<LinearGradientBrush>(ok.Background);

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_RENDER_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-buttons.png");
            frame!.Save(outPath);
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }
}
