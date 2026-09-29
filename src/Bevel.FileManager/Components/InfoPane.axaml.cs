using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Bevel.Core;
using Bevel.UI.Luna;

namespace Bevel.FileManager.Components;

/// <summary>Left Filer sidebar with selectable era presets: Win2000/Me Web View, XP Common Tasks,
/// Vista/7 navigation, or a minimal Win95/NT4 panel. All native vector — no web engine. The window drives it through
/// the same simple API (Title/Description/ObjectCount/links); the code-behind holds those values and
/// applies them to whichever style view is active. "Off" is handled by the host, which collapses the pane.</summary>
public partial class InfoPane : UserControl
{
    private string _title = "";
    private string _description = "";
    private string _instruction = "Select an item to view its description.";
    private string _count = "";
    private readonly List<(string Text, Action OnClick)> _links = new();
    private readonly List<(string Text, Action OnClick)> _tasks = new();
    private readonly IBrush _xpDefaultWatermark;
    private readonly IBrush _xpDefaultHeader;
    private readonly IBrush _xpDefaultBorder;
    private readonly IBrush _xpDefaultHeadingText;
    private readonly IBrush _xpDefaultBodyText;
    private readonly IBrush _xpDefaultLinkText;
    private readonly IBrush _xpDefaultArrow;
    private readonly IBrush _xpDefaultMutedText;
    private readonly Dictionary<Control, int> _xpSectionAnimationVersions = new();
    private InfoPaneStyle _style = InfoPaneStyle.Win2000;

    public InfoPane()
    {
        InitializeComponent();
        _xpDefaultWatermark = (IBrush)Resources["InfoPane.Xp.Watermark"]!;
        _xpDefaultHeader = (IBrush)Resources["InfoPane.Xp.Header"]!;
        _xpDefaultBorder = (IBrush)Resources["InfoPane.Xp.Border"]!;
        _xpDefaultHeadingText = (IBrush)Resources["InfoPane.Xp.HeadingText"]!;
        _xpDefaultBodyText = (IBrush)Resources["InfoPane.Xp.BodyText"]!;
        _xpDefaultLinkText = (IBrush)Resources["InfoPane.Xp.LinkText"]!;
        _xpDefaultArrow = (IBrush)Resources["InfoPane.Xp.Arrow"]!;
        _xpDefaultMutedText = (IBrush)Resources["InfoPane.Xp.MutedText"]!;
        AttachedToVisualTree += (_, _) => LunaVariantService.Changed += OnLunaVariantChanged;
        DetachedFromVisualTree += (_, _) => LunaVariantService.Changed -= OnLunaVariantChanged;
    }

    /// <summary>Selected visual style. Off leaves the (empty) pane; the host collapses the column.</summary>
    public InfoPaneStyle Style
    {
        get => _style;
        set { if (_style == value) return; _style = value; ApplyStyle(); }
    }

    public string Title { get => _title; set { _title = value; Apply(); } }
    public string Description { get => _description; set { _description = value; Apply(); } }
    public string Instruction { get => _instruction; set { _instruction = value; Apply(); } }
    public string ObjectCount { get => _count; set { _count = value; Apply(); } }

    public void ClearLinks() { _links.Clear(); ApplyLinks(); }

    public void AddLink(string text, Action onClick)
    {
        _links.Add((text, onClick));
        ApplyLinks();
    }

    public void ClearTasks() { _tasks.Clear(); ApplyLinks(); }

    public void AddTask(string text, Action onClick)
    {
        _tasks.Add((text, onClick));
        ApplyLinks();
    }

    private void ApplyStyle()
    {
        Win2000View.IsVisible = _style == InfoPaneStyle.Win2000;
        WinXPView.IsVisible = _style == InfoPaneStyle.WinXP;
        ModernView.IsVisible = _style == InfoPaneStyle.Modern;
        Win9xView.IsVisible = _style == InfoPaneStyle.Win9x;
        if (_style == InfoPaneStyle.WinXP)
            ApplyXpPalette();
        Apply();
    }

    private void ApplyXpPalette()
    {
        CopyApplicationBrush("Luna.Brush.InfoPaneWatermark", "InfoPane.Xp.Watermark", _xpDefaultWatermark);
        CopyApplicationBrush("Luna.Brush.InfoPaneHeader", "InfoPane.Xp.Header", _xpDefaultHeader);
        CopyApplicationBrush("Luna.Brush.InfoPaneBorder", "InfoPane.Xp.Border", _xpDefaultBorder);
        CopyApplicationBrush("Luna.Brush.InfoPaneHeadingText", "InfoPane.Xp.HeadingText", _xpDefaultHeadingText);
        CopyApplicationBrush("Luna.Brush.InfoPaneBodyText", "InfoPane.Xp.BodyText", _xpDefaultBodyText);
        CopyApplicationBrush("Luna.Brush.InfoPaneLinkText", "InfoPane.Xp.LinkText", _xpDefaultLinkText);
        CopyApplicationBrush("Luna.Brush.InfoPaneArrow", "InfoPane.Xp.Arrow", _xpDefaultArrow);
        CopyApplicationBrush("Luna.Brush.InfoPaneMutedText", "InfoPane.Xp.MutedText", _xpDefaultMutedText);
    }

    private void OnLunaVariantChanged()
    {
        if (_style != InfoPaneStyle.WinXP) return;
        ApplyXpPalette();
        ApplyLinks();
    }

    private async void OnXpTasksToggle(object? sender, RoutedEventArgs e)
        => await SetXpSectionAsync((ToggleButton)sender!, XpTasksPanel, (RotateTransform)XpTasksChevron.RenderTransform!,
            "File and Folder Tasks");

    private async void OnXpPlacesToggle(object? sender, RoutedEventArgs e)
        => await SetXpSectionAsync((ToggleButton)sender!, XpLinksPanel, (RotateTransform)XpPlacesChevron.RenderTransform!,
            "Other Places");

    private async void OnXpDetailsToggle(object? sender, RoutedEventArgs e)
        => await SetXpSectionAsync((ToggleButton)sender!, XpDetailsPanel, (RotateTransform)XpDetailsChevron.RenderTransform!,
            "Details");

    private async Task SetXpSectionAsync(ToggleButton toggle, Control body, RotateTransform rotation,
        string sectionName)
    {
        var expanded = toggle.IsChecked == true;
        var version = _xpSectionAnimationVersions.TryGetValue(body, out var current) ? current + 1 : 1;
        _xpSectionAnimationVersions[body] = version;

        rotation.Angle = expanded ? 0 : 180;
        var command = expanded ? "Collapse" : "Expand";
        AutomationProperties.SetName(toggle, $"{command} {sectionName}");
        ToolTip.SetTip(toggle, $"{command} {sectionName}");

        if (expanded)
        {
            body.IsVisible = true;
            body.Opacity = 0;
            await Task.Yield();
            if (_xpSectionAnimationVersions.GetValueOrDefault(body) == version)
                body.Opacity = 1;
            return;
        }

        body.Opacity = 0;
        await Task.Delay(160);
        if (_xpSectionAnimationVersions.GetValueOrDefault(body) == version && toggle.IsChecked != true)
            body.IsVisible = false;
    }

    private void CopyApplicationBrush(string sourceKey, string targetKey, IBrush fallback)
    {
        if (Application.Current is { } app &&
            app.TryFindResource(sourceKey, null, out var value) && value is IBrush)
            Resources[targetKey] = value;
        else
            Resources[targetKey] = fallback;
    }

    private void Apply()
    {
        switch (_style)
        {
            case InfoPaneStyle.Win2000:
                TitleLabel.Text = _title;
                InstructionLabel.Text = _instruction;
                DescriptionLabel.Text = _description;
                ObjectCountLabel.Text = _count;
                break;
            case InfoPaneStyle.WinXP:
                XpTitle.Text = _title;
                XpDescription.Text = _description;
                XpCount.Text = _count;
                break;
            case InfoPaneStyle.Modern:
                ModernTitle.Text = _title;
                ModernDescription.Text = _description;
                ModernCount.Text = _count;
                break;
            case InfoPaneStyle.Win9x:
                Win9xTitle.Text = _title;
                Win9xDescription.Text = _description;
                Win9xCount.Text = _count;
                break;
        }
        ApplyLinks();
    }

    private void ApplyLinks()
    {
        // Win2000 links (blue underlined "See also"), XP task rows, and Vista/7 favorite rows.
        if (_style == InfoPaneStyle.Win2000)
            FillLinks(SeeAlsoPanel, new SolidColorBrush(0xFF0000EE), underline: true);
        else if (_style == InfoPaneStyle.WinXP)
        {
            FillTaskLinks();
            FillLinks(XpLinksPanel, XpBrush("InfoPane.Xp.LinkText"), underline: false, withArrow: true,
                arrowBrush: XpBrush("InfoPane.Xp.Arrow"));
        }
        else if (_style == InfoPaneStyle.Modern)
        {
            var modernText = new SolidColorBrush(0xFF26384B);
            FillLinks(ModernLinksPanel, modernText, underline: false, modernRow: true);
            FillTasks(ModernTasksPanel, modernText, modernRow: true);
        }
    }

    private void FillTaskLinks()
    {
        FillTasks(XpTasksPanel, XpBrush("InfoPane.Xp.LinkText"), withArrow: true,
            arrowBrush: XpBrush("InfoPane.Xp.Arrow"));
    }

    private IBrush XpBrush(string key) => Resources[key] as IBrush ?? Brushes.Black;

    private void FillTasks(StackPanel host, IBrush color, bool withArrow = false, bool modernRow = false,
        IBrush? arrowBrush = null)
    {
        host.Children.Clear();
        foreach (var (text, onClick) in _tasks)
            AddTextLink(host, text, color, onClick, withArrow: withArrow || modernRow,
                modernRow: modernRow, arrowBrush: arrowBrush);
    }

    private void FillLinks(StackPanel host, IBrush color, bool underline, bool withArrow = false,
        bool modernRow = false, IBrush? arrowBrush = null)
    {
        host.Children.Clear();
        foreach (var (text, onClick) in _links)
            AddTextLink(host, text, color, onClick, underline, withArrow, modernRow, arrowBrush);
    }

    private static void AddTextLink(StackPanel host, string text, IBrush color, Action onClick,
        bool underline = false, bool withArrow = false, bool modernRow = false, IBrush? arrowBrush = null)
    {
        var link = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = color,
            TextDecorations = underline ? Avalonia.Media.TextDecorations.Underline : null,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        if (!withArrow && !modernRow)
        {
            // Win2000 "See also" link. A bare TextBlock is unfocusable and invisible to assistive
            // tech, so make it keyboard-reachable (Focusable + Enter/Space) and give it an accessible
            // name, matching the XP chevron pattern (AutomationProperties.SetName).
            link.Cursor = new Cursor(StandardCursorType.Hand);
            link.Margin = new Thickness(0, 1);
            link.Focusable = true;
            AutomationProperties.SetName(link, text);
            link.PointerPressed += (_, _) => onClick();
            link.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    onClick();
                    e.Handled = true;
                }
            };
            host.Children.Add(link);
            return;
        }

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = modernRow ? new CornerRadius(2) : new CornerRadius(0),
            Padding = modernRow ? new Thickness(6, 4) : new Thickness(1, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            Tag = onClick,
        };
        // The interactive row is focusable but its child glyph/label carry no accessible name, so name
        // the row itself (matching the XP chevron pattern) — otherwise a screen reader announces "group".
        AutomationProperties.SetName(row, text);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
        var glyph = withArrow ? CreateXpTaskGlyph(arrowBrush) : CreateModernPlaceGlyph(text);
        content.Children.Add(glyph);
        Grid.SetColumn(link, 1);
        link.Margin = new Thickness(5, 0, 0, 0);
        content.Children.Add(link);
        row.Child = content;
        row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(modernRow ? 0xFFE4EFFB : 0xFFEAF2FF);
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, _) => onClick();
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                onClick();
                e.Handled = true;
            }
        };
        host.Children.Add(row);
    }

    private static Control CreateXpTaskGlyph(IBrush? brush = null) => new ShapePath
    {
        Data = Geometry.Parse("M3,2 L9,7 L3,12 L3,9 L6,7 L3,5 Z"),
        Fill = brush ?? new SolidColorBrush(0xFF5E86C5), Width = 12, Height = 14, Stretch = Stretch.Uniform,
    };

    private static Control CreateModernPlaceGlyph(string text)
    {
        if (text.Contains("Computer", StringComparison.OrdinalIgnoreCase))
            return new ShapePath
            {
                Data = Geometry.Parse("M1,2 L13,2 L13,10 L1,10 Z M4,12 L10,12 M7,10 L7,12"),
                Fill = new SolidColorBrush(0xFFD6DDE5), Stroke = new SolidColorBrush(0xFF637487),
                StrokeThickness = 1, Width = 14, Height = 14, Stretch = Stretch.Uniform,
            };

        return new ShapePath
        {
            Data = Geometry.Parse("M1,4 L6,4 L8,6 L14,6 L14,13 L1,13 Z"),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#F7DB79"), 0),
                    new GradientStop(Color.Parse("#D7A934"), 1),
                },
            },
            Stroke = new SolidColorBrush(0xFF9B741F), StrokeThickness = 1,
            Width = 14, Height = 14, Stretch = Stretch.Uniform,
        };
    }
}
