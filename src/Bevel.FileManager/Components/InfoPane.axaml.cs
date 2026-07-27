using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Bevel.Core;

namespace Bevel.FileManager.Components;

/// <summary>Left folder info pane with a selectable style (Folder Options): Win2000 banner, WinXP Luna
/// task-pane, or a minimal Win9x panel. All native vector — no web engine. The window drives it through
/// the same simple API (Title/Description/ObjectCount/links); the code-behind holds those values and
/// applies them to whichever style view is active. "Off" is handled by the host, which collapses the pane.</summary>
public partial class InfoPane : UserControl
{
    private string _title = "";
    private string _description = "";
    private string _instruction = "Select an item to view its description.";
    private string _count = "";
    private readonly List<(string Text, Action OnClick)> _links = new();
    private InfoPaneStyle _style = InfoPaneStyle.Win2000;

    public InfoPane() => InitializeComponent();

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

    private void ApplyStyle()
    {
        Win2000View.IsVisible = _style == InfoPaneStyle.Win2000;
        WinXPView.IsVisible = _style == InfoPaneStyle.WinXP;
        Win9xView.IsVisible = _style == InfoPaneStyle.Win9x;
        Apply();
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
        // Win2000 links (blue underlined "See also"); WinXP "Other Places" rows. Win9x omits links.
        if (_style == InfoPaneStyle.Win2000)
            FillLinks(SeeAlsoPanel, 0xFF003399, underline: true);
        else if (_style == InfoPaneStyle.WinXP)
            FillLinks(XpLinksPanel, 0xFF1B3E86, underline: false);
    }

    private void FillLinks(StackPanel host, uint color, bool underline)
    {
        host.Children.Clear();
        foreach (var (text, onClick) in _links)
        {
            var link = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new Avalonia.Media.SolidColorBrush(color),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                TextDecorations = underline ? Avalonia.Media.TextDecorations.Underline : null,
                Margin = new Avalonia.Thickness(0, 1),
            };
            var captured = onClick;
            link.PointerPressed += (_, _) => captured();
            host.Children.Add(link);
        }
    }
}
