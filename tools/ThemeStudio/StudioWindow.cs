using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Bevel.UI;
using Bevel.UI.Luna;
using Classic.CommonControls;

namespace ThemeStudio;

/// <summary>
/// The Theme Studio window. It is itself a <see cref="BevelWindow"/>, so its OWN title bar is a live
/// preview of the caption tokens. Three columns: the token panel (left, generated from the full
/// <see cref="ThemeTokens"/> set so EVERY metric + colour is editable), a gallery of the real themed
/// controls (centre), and the Luna design plate for reference (right). Every edit is pushed into
/// <see cref="Application"/>.Resources (which outranks the theme's Styles-set resources) and remembered
/// so a theme/variant switch can REMOVE them and rebuild the panel from what the new theme defines.
/// </summary>
public sealed class StudioWindow : BevelWindow
{
    private string _color = "Blue";
    private string _gloss = "Gloss";

    // Every override the user has made, keyed by resource key (string for Bevel.*; object for the
    // vendored SystemColors/SystemParameters caption keys). Removed wholesale on a theme switch.
    private readonly Dictionary<object, object> _over = new();

    private readonly StackPanel _tokenHost = new() { Spacing = 6, Margin = new Thickness(6) };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SteelBlue };
    private readonly TextBox _export = new()
    {
        IsReadOnly = true, AcceptsReturn = true, MinHeight = 150,
        FontFamily = new FontFamily("Menlo,Consolas,monospace"), FontSize = 11,
    };

    // Plate-derived targets (logical px, already ÷ 1.32) shown next to the matching editors.
    private static readonly Dictionary<string, string> Targets = new()
    {
        ["Bevel.Metric.CornerRadius"] = "8,8,0,0",
        ["Bevel.Metric.CaptionPadding"] = "~10.6,0,6,0",
        ["Bevel.Metric.WindowContentBorder"] = "3,0,3,3",
        ["Bevel.Metric.WindowContentInset"] = "0",
        ["Bevel.Metric.CaptionHeight"] = "25",
        ["Bevel.Metric.CaptionButtonWidth"] = "21",
        ["Bevel.Metric.CaptionButtonHeight"] = "21",
    };

    public StudioWindow()
    {
        Title = "Bevel · Theme Studio";
        Width = 1300;
        Height = 820;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*,440"), Margin = new Thickness(8) };
        Add(grid, new ScrollViewer { Content = _tokenHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0);
        Add(grid, BuildGallery(), 1);
        Add(grid, BuildPlate(), 2);
        Content = grid;

        PopulateTokens();
    }

    private static void Add(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }

    // ── Token panel (generated from the full token set, rebuilt on theme switch) ──

    private void PopulateTokens()
    {
        _tokenHost.Children.Clear();

        _tokenHost.Children.Add(Header("THEME"));
        _tokenHost.Children.Add(LabelledRow("Theme", ThemeCombo()));
        _tokenHost.Children.Add(LabelledRow("Luna colour", ColorCombo()));
        _tokenHost.Children.Add(LabelledRow("Luna gloss", GlossCombo()));

        var tokens = typeof(ThemeTokens)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (Name: f.Name, Key: (string)f.GetRawConstantValue()!))
            .ToList();

        _tokenHost.Children.Add(Header("CAPTION"));
        _tokenHost.Children.Add(DoubleRow("Caption height", SystemParameters.WindowCaptionHeightKey, CurDouble(SystemParameters.WindowCaptionHeightKey, 25), 14, 40, "25"));
        _tokenHost.Children.Add(ColourRow("Caption colour", SystemColors.ActiveCaptionColorKey, null, CurHex(SystemColors.ActiveCaptionColorKey, "#0A51DF")));
        _tokenHost.Children.Add(ColourRow("Caption gradient", SystemColors.GradientActiveCaptionColorKey, null, CurHex(SystemColors.GradientActiveCaptionColorKey, "#3F88EC")));
        _tokenHost.Children.Add(ColourRow("Caption text", SystemColors.ActiveCaptionTextColorKey, null, CurHex(SystemColors.ActiveCaptionTextColorKey, "#FFFFFF")));

        _tokenHost.Children.Add(Header("DIMENSIONS"));
        foreach (var t in tokens.Where(t => t.Name.StartsWith("Metric")).OrderBy(t => t.Name))
            _tokenHost.Children.Add(MetricEditor(Pretty(t.Name, "Metric"), t.Key));

        _tokenHost.Children.Add(Header("CHROME COLOURS"));
        foreach (var t in tokens.Where(t => t.Name.StartsWith("Color") && !t.Name.StartsWith("ColorIcon")).OrderBy(t => t.Name))
            _tokenHost.Children.Add(ColourRow(Pretty(t.Name, "Color"), t.Key, "Bevel.Brush." + t.Key["Bevel.Color.".Length..], CurHex(t.Key, "#000000")));

        _tokenHost.Children.Add(Header("ICON COLOURS"));
        foreach (var t in tokens.Where(t => t.Name.StartsWith("ColorIcon")).OrderBy(t => t.Name))
            _tokenHost.Children.Add(ColourRow(Pretty(t.Name, "Color"), t.Key, "Bevel.Brush." + t.Key["Bevel.Color.".Length..], CurHex(t.Key, "#000000")));

        _tokenHost.Children.Add(Header("EXPORT  (theme.json)"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var save = new Button { Content = "Save to theme.json" };
        save.Click += (_, _) => Save();
        var copy = new Button { Content = "Copy JSON" };
        copy.Click += (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(_export.Text); };
        var reset = new Button { Content = "Reset to theme" };
        reset.Click += (_, _) => { ClearOverrides(); RebuildLater(); };
        buttons.Children.Add(save);
        buttons.Children.Add(copy);
        buttons.Children.Add(reset);
        _tokenHost.Children.Add(buttons);
        _tokenHost.Children.Add(_status);
        _tokenHost.Children.Add(_export);

        RefreshExport();
    }

    private ComboBox ThemeCombo()
    {
        var c = Combo(ThemeService.Themes.Select(t => t.Display), IndexOf(ThemeService.Themes, ThemeService.Current));
        c.SelectionChanged += (_, _) =>
        {
            var id = ThemeService.Themes[Math.Max(0, c.SelectedIndex)].Id;
            ClearOverrides();                       // drop tweaks so the new theme's own values show
            ThemeService.Apply(id);
            if (id == "luna") LunaVariantService.Apply(_color, _gloss);
            else LunaVariantService.Clear();        // else Luna's injected caption/highlight brushes linger
            RebuildLater();                         // re-read editors from what the theme now defines
        };
        return c;
    }

    private ComboBox ColorCombo()
    {
        var c = Combo(LunaVariantService.Colors.Select(x => x.Display), IndexOf(LunaVariantService.Colors, _color));
        c.SelectionChanged += (_, _) => { _color = LunaVariantService.Colors[Math.Max(0, c.SelectedIndex)].Id; ClearOverrides(); LunaVariantService.Apply(_color, _gloss); RebuildLater(); };
        return c;
    }

    private ComboBox GlossCombo()
    {
        var c = Combo(LunaVariantService.Glosses.Select(x => x.Display), IndexOf(LunaVariantService.Glosses, _gloss));
        c.SelectionChanged += (_, _) => { _gloss = LunaVariantService.Glosses[Math.Max(0, c.SelectedIndex)].Id; ClearOverrides(); LunaVariantService.Apply(_color, _gloss); RebuildLater(); };
        return c;
    }

    /// <summary>Remove every override from Application.Resources so DynamicResource falls back to the
    /// theme's own value (this is what makes a switch to Classic fully redraw as Classic).</summary>
    private void ClearOverrides()
    {
        if (Application.Current is { } app)
            foreach (var key in _over.Keys) app.Resources.Remove(key);
        _over.Clear();
    }

    // Rebuild after the current event settles (mutating the tree from inside a control's own event is unsafe).
    private void RebuildLater() => Dispatcher.UIThread.Post(PopulateTokens, DispatcherPriority.Background);

    // ── Editors ─────────────────────────────────────────────────────────────

    private Control MetricEditor(string label, string key)
    {
        var val = Res(key);
        Targets.TryGetValue(key, out var target);
        return val switch
        {
            double d => DoubleRow(label, key, d, 0, MaxFor(d), target),
            Thickness th => ThicknessRow(label, key, th, target, isCorner: false),
            CornerRadius cr => ThicknessRow(label, key, new Thickness(cr.TopLeft, cr.TopRight, cr.BottomRight, cr.BottomLeft), target, isCorner: true),
            _ => LabelledRow(label, new TextBlock { Text = "(unhandled)", Foreground = Brushes.Gray }),
        };
    }

    private Control DoubleRow(string label, object key, double init, double min, double max, string? target)
    {
        var value = new TextBlock { FontSize = 11, MinWidth = 40, TextAlignment = TextAlignment.Right };
        var slider = new Slider { Minimum = min, Maximum = max, Value = init };
        void Show(double v) => value.Text = v.ToString("0.##", CultureInfo.InvariantCulture);
        Show(init);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            Show(slider.Value);
            Track(key, slider.Value);
        };
        return EditorBlock(label, target, Row(slider, value));
    }

    private Control ThicknessRow(string label, string key, Thickness init, string? target, bool isCorner)
    {
        var box = new TextBox { Text = FormatTh(init), FontSize = 11 };
        void Apply()
        {
            try
            {
                var th = Thickness.Parse(box.Text!.Trim());
                Track(key, isCorner ? new CornerRadius(th.Left, th.Top, th.Right, th.Bottom) : th);
            }
            catch { /* ignore mid-typing */ }
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        box.LostFocus += (_, _) => Apply();
        return EditorBlock(label, target, box);
    }

    private Control ColourRow(string label, object colorKey, string? brushKey, string initHex)
    {
        var swatch = new Border { Width = 20, Height = 16, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var box = new TextBox { Text = initHex, FontSize = 11 };
        void Apply()
        {
            try
            {
                var c = Color.Parse(box.Text!.Trim());
                swatch.Background = new SolidColorBrush(c);
                Track(colorKey, c);
                if (brushKey is not null) SetRes(brushKey, new SolidColorBrush(c));
            }
            catch { /* ignore mid-typing */ }
        }
        try { swatch.Background = new SolidColorBrush(Color.Parse(initHex)); } catch { }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        box.LostFocus += (_, _) => Apply();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(box, 1);
        box.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(swatch);
        row.Children.Add(box);
        return LabelledRow(label, row);
    }

    /// <summary>Push to Application.Resources, remember for export + later removal, and refresh.</summary>
    private void Track(object key, object val)
    {
        SetRes(key, val);
        _over[key] = val;
        RefreshExport();
    }

    // ── Gallery (real themed controls) ──────────────────────────────────────

    private Control BuildGallery()
    {
        var col = new StackPanel { Spacing = 10, Margin = new Thickness(10) };

        var menu = new Menu();
        foreach (var top in new[] { "File", "Edit", "View", "Help" })
            menu.Items.Add(new MenuItem { Header = top });
        col.Children.Add(menu);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(new Button { Content = "OK", Width = 84 });
        buttons.Children.Add(new Button { Content = "Apply", Width = 84 });
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84 });
        buttons.Children.Add(new Button { Content = "Disabled", Width = 84, IsEnabled = false });
        col.Children.Add(buttons);

        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        checks.Children.Add(new CheckBox { Content = "Enabled", IsChecked = true });
        checks.Children.Add(new CheckBox { Content = "Unchecked" });
        checks.Children.Add(new RadioButton { Content = "Selected", IsChecked = true, GroupName = "g" });
        checks.Children.Add(new RadioButton { Content = "Option", GroupName = "g" });
        col.Children.Add(checks);

        col.Children.Add(new TextBox { Text = "selected text", Width = 220, HorizontalAlignment = HorizontalAlignment.Left });

        var tabs = new TabControl { Height = 130 };
        tabs.Items.Add(new TabItem { Header = "General", Content = new TextBlock { Text = "General pane", Margin = new Thickness(10) } });
        tabs.Items.Add(new TabItem { Header = "View", Content = new TextBlock { Text = "View pane", Margin = new Thickness(10) } });
        tabs.Items.Add(new TabItem { Header = "Advanced", Content = new TextBlock { Text = "Advanced pane", Margin = new Thickness(10) } });
        col.Children.Add(tabs);

        col.Children.Add(new ProgressBar { Value = 60, Height = 18 });
        col.Children.Add(new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 20, ViewportSize = 30, AllowAutoHide = false, Height = 18 });

        var ctx = new ContextMenu();
        ctx.Items.Add(new MenuItem { Header = "Open", InputGesture = new KeyGesture(Key.Enter) });
        ctx.Items.Add(new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Meta) });
        ctx.Items.Add(new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Meta) });
        ctx.Items.Add(new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Meta) });
        ctx.Items.Add(new Separator());
        var props = new MenuItem { Header = "Properties" };
        props.Items.Add(new MenuItem { Header = "Details" });
        ctx.Items.Add(props);
        ctx.Items.Add(new MenuItem { Header = "Delete", IsEnabled = false });
        var showMenu = new Button { Content = "Show context menu ▾" };
        showMenu.Click += (_, _) => ctx.Open(showMenu);
        col.Children.Add(showMenu);

        var body = new Border { Child = col, Padding = new Thickness(4) };
        body.Bind(Border.BackgroundProperty, this.GetResourceObservable("Bevel.Brush.ButtonFace"));
        return new HeaderedContentControl { Header = "Gallery", Content = body };
    }

    private Control BuildPlate()
    {
        var col = new StackPanel { Spacing = 6, Margin = new Thickness(6) };
        col.Children.Add(Header("DESIGN PLATE  (1.32× mockup)"));
        col.Children.Add(new TextBlock
        {
            Text = "Reference only. Editor targets are already converted to logical px (plate ÷ 1.32).",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11,
        });
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ThemeStudio/Assets/preview.png"));
            col.Children.Add(new Image { Source = new Bitmap(s), Stretch = Stretch.Uniform });
        }
        catch (Exception e)
        {
            col.Children.Add(new TextBlock { Text = "plate not found: " + e.Message, Foreground = Brushes.Red });
        }
        return new ScrollViewer { Content = col };
    }

    // ── Export ──────────────────────────────────────────────────────────────

    private void RefreshExport()
    {
        var lines = new List<string>();
        var metrics = _over.Where(kv => kv.Key is string s && s.StartsWith("Bevel.Metric."))
            .Select(kv => ((string)kv.Key, kv.Value)).OrderBy(kv => kv.Item1).ToList();
        if (metrics.Count > 0)
        {
            lines.Add("// metrics:");
            foreach (var (key, val) in metrics)
            {
                var name = key["Bevel.Metric.".Length..];
                lines.Add(val switch
                {
                    double d => $"\"{name}\": {{ \"double\": {Trim(d)} }},",
                    Thickness th => $"\"{name}\": {{ \"thickness\": \"{FormatTh(th)}\" }},",
                    CornerRadius cr => $"\"{name}\": {{ \"cornerRadius\": \"{FormatCr(cr)}\" }},",
                    _ => $"// {name}: (unhandled)",
                });
            }
        }
        var colours = _over.Where(kv => kv.Key is string s && s.StartsWith("Bevel.Color."))
            .Select(kv => ((string)kv.Key, kv.Value)).OrderBy(kv => kv.Item1).ToList();
        if (colours.Count > 0)
        {
            lines.Add("// palette:");
            foreach (var (key, val) in colours)
                if (val is Color c)
                    lines.Add($"\"{key["Bevel.Color.".Length..]}\": \"#{c.R:X2}{c.G:X2}{c.B:X2}\",");
        }
        _export.Text = lines.Count == 0 ? "// tweak anything — the JSON appears here" : string.Join("\n", lines);
    }

    // ── Save to source ──────────────────────────────────────────────────────

    /// <summary>
    /// Write the current Bevel.* overrides back to the active theme's source. theme.json is the source of
    /// truth for both themes; Luna also patches its hand-mirrored LunaTokens.axaml (not yet wired to
    /// ThemeGen), while Win2000 runs ThemeGen so Tokens.axaml + ThemeTokens.cs regenerate. Targeted regex
    /// so only the touched values change (formatting + comments preserved). Caption SystemColors keys are
    /// not persisted yet (they live outside theme.json).
    /// </summary>
    private void Save()
    {
        var root = FindRepoRoot();
        if (root is null) { _status.Text = "could not locate repo root (Bevel.sln)"; return; }

        var luna = ThemeService.Current == "luna";
        var dir = Path.Combine(root, "src", luna ? "Bevel.Themes.Luna" : "Bevel.Themes.Win2000");
        var jsonPath = Path.Combine(dir, "theme.json");
        var xamlPath = Path.Combine(dir, luna ? "LunaTokens.axaml" : "Tokens.axaml");

        var saved = 0;
        var skipped = new List<string>();
        foreach (var (k, v) in _over)
        {
            if (k is not string key) { skipped.Add(k.ToString() ?? "?"); continue; }
            if (key.StartsWith("Bevel.Metric."))
            {
                var name = key["Bevel.Metric.".Length..];
                var (json, xaml) = MetricSnippets(v);
                if (json is null) { skipped.Add(name); continue; }
                var ok = PatchJson(jsonPath, $"\"{name}\"\\s*:\\s*\\{{[^{{}}]*\\}}", $"\"{name}\": {json}");
                if (luna && xaml is not null) PatchXaml(xamlPath, key, xaml);
                if (ok) saved++; else skipped.Add(name);
            }
            else if (key.StartsWith("Bevel.Color.") && v is Color c)
            {
                var name = key["Bevel.Color.".Length..];
                var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                var ok = PatchJson(jsonPath, $"\"{name}\"\\s*:\\s*\"#[0-9A-Fa-f]{{6,8}}\"", $"\"{name}\": \"{hex}\"");
                if (luna) PatchXaml(xamlPath, key, hex);
                if (ok) saved++; else skipped.Add(name);
            }
        }

        var note = skipped.Count == 0 ? "" : $"  (skipped: {string.Join(", ", skipped)})";
        if (luna)
            _status.Text = $"saved {saved} → theme.json + LunaTokens.axaml{note}";
        else
        {
            _status.Text = $"patched {saved} → theme.json — running ThemeGen…{note}";
            Task.Run(() =>
            {
                var r = RunThemeGen(root);
                Dispatcher.UIThread.Post(() => _status.Text = $"saved {saved} → theme.json (themegen: {r}){note}");
            });
        }
    }

    private static (string? Json, string? Xaml) MetricSnippets(object v) => v switch
    {
        double d => ($"{{ \"double\": {Trim(d)} }}", Trim(d)),
        Thickness th => ($"{{ \"thickness\": \"{FormatTh(th)}\" }}", FormatTh(th)),
        CornerRadius cr => ($"{{ \"cornerRadius\": \"{FormatCr(cr)}\" }}", FormatCr(cr)),
        _ => (null, null),
    };

    private static bool PatchJson(string path, string pattern, string replacement)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        var rx = new Regex(pattern);
        if (!rx.IsMatch(text)) return false;
        File.WriteAllText(path, rx.Replace(text, m => replacement, 1));
        return true;
    }

    private static void PatchXaml(string path, string xKey, string inner)
    {
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var rx = new Regex("(<[A-Za-z]+\\s+x:Key=\"" + Regex.Escape(xKey) + "\"\\s*>)[^<]*(</[A-Za-z]+>)");
        if (rx.IsMatch(text))
            File.WriteAllText(path, rx.Replace(text, m => m.Groups[1].Value + inner + m.Groups[2].Value, 1));
    }

    private static string RunThemeGen(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"run --project \"{Path.Combine(root, "tools", "ThemeGen")}\"")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(90_000);
            return p.ExitCode == 0 ? "ok" : "failed";
        }
        catch (Exception e) { return "error: " + e.Message; }
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "Bevel.sln"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void SetRes(object key, object val) { if (Application.Current is { } a) a.Resources[key] = val; }

    private static object? Res(object key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) ? v : null;

    private static string CurHex(object key, string dflt) => Res(key) is Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : dflt;
    private static double CurDouble(object key, double dflt) => Res(key) is double d ? d : dflt;

    private static double MaxFor(double v) => v <= 12 ? 24 : v <= 32 ? 64 : Math.Ceiling(v * 1.6);

    private static string FormatTh(Thickness t) => $"{Trim(t.Left)},{Trim(t.Top)},{Trim(t.Right)},{Trim(t.Bottom)}";
    private static string FormatCr(CornerRadius c) => $"{Trim(c.TopLeft)},{Trim(c.TopRight)},{Trim(c.BottomRight)},{Trim(c.BottomLeft)}";
    private static string Trim(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pretty(string constName, string prefix)
    {
        var s = constName.Substring(prefix.Length);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected) =>
        new() { ItemsSource = items.ToList(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeight.Bold, FontSize = 11, Margin = new Thickness(0, 10, 0, 2), Foreground = Brushes.Gray,
    };

    private static Control LabelledRow(string label, Control control)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 1, 0, 1) };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        Grid.SetColumn(t, 0);
        Grid.SetColumn(control, 1);
        g.Children.Add(t);
        g.Children.Add(control);
        return g;
    }

    private static Control EditorBlock(string label, string? target, Control editor)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var lbl = new TextBlock { Text = label, FontSize = 12 };
        Grid.SetColumn(lbl, 0);
        head.Children.Add(lbl);
        if (target is not null)
        {
            var tgt = new TextBlock { Text = "target " + target, FontSize = 10, Foreground = Brushes.SteelBlue };
            Grid.SetColumn(tgt, 1);
            head.Children.Add(tgt);
        }
        return new StackPanel { Margin = new Thickness(0, 1, 0, 1), Children = { head, editor } };
    }

    private static Control Row(Control fill, Control tail)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44") };
        Grid.SetColumn(fill, 0);
        Grid.SetColumn(tail, 1);
        row.Children.Add(fill);
        row.Children.Add(tail);
        return row;
    }

    private static int IndexOf(IReadOnlyList<(string Id, string Display)> list, string id)
    {
        for (var i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
        return 0;
    }
}
