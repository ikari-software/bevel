using Avalonia.Interactivity;

namespace Bevel.Taskbar;

public partial class ThemePreviewWindow : Bevel.UI.BevelWindow
{
    public ThemePreviewWindow()
    {
        InitializeComponent();
        CloseButton.Click += OnClose;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
