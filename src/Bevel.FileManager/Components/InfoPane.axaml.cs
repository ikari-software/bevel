using Avalonia.Controls;

namespace Bevel.FileManager.Components;

/// <summary>Win2000-style folder info pane with gradient banner, description, and links.</summary>
public partial class InfoPane : UserControl
{
    public InfoPane() => InitializeComponent();

    public string Title { get => TitleLabel.Text; set => TitleLabel.Text = value; }
    public string Description { get => DescriptionLabel.Text; set => DescriptionLabel.Text = value; }
    public string Instruction { get => InstructionLabel.Text; set => InstructionLabel.Text = value; }
    public string ObjectCount { get => ObjectCountLabel.Text; set => ObjectCountLabel.Text = value; }

    public void ClearLinks() => SeeAlsoPanel.Children.Clear();

    public void AddLink(string text, Action onClick)
    {
        var link = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new Avalonia.Media.SolidColorBrush(0xFF003399),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            TextDecorations = Avalonia.Media.TextDecorations.Underline,
            Margin = new Avalonia.Thickness(0, 2),
        };
        link.PointerPressed += (_, _) => onClick();
        SeeAlsoPanel.Children.Add(link);
    }
}