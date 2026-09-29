using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Bevel.FileManager.Components;

/// <summary>Win2000 Explorer Bar panel: title bar + sunken content area.</summary>
public partial class FilerBar : UserControl
{
    public static readonly new StyledProperty<object?> ContentProperty =
        ContentControl.ContentProperty.AddOwner<FilerBar>();

    static FilerBar()
    {
        ContentProperty.Changed.AddClassHandler<FilerBar>((x, e) =>
        {
            if (x.ContentHost is not null)
                x.ContentHost.Content = e.NewValue;
        });
    }

    public FilerBar() => InitializeComponent();

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Forward any content that was set during XAML init before ContentHost existed
        if (ContentHost.Content is null && Content is { } c)
            ContentHost.Content = c;
    }

    public string Title { get => TitleLabel.Text; set => TitleLabel.Text = value; }

    public event EventHandler<RoutedEventArgs>? CloseClicked
    {
        add => CloseButton.Click += value;
        remove => CloseButton.Click -= value;
    }
}