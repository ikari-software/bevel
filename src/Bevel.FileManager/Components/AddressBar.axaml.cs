using Avalonia.Controls;
using Avalonia.Input;

namespace Bevel.FileManager.Components;

public partial class AddressBar : UserControl
{
    public event EventHandler<string>? AddressNavigated;

    public AddressBar() => InitializeComponent();

    public void SetAddress(string path) => AddressBox.Text = path;

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToAddress();
            e.Handled = true;
        }
    }

    private void OnGoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => NavigateToAddress();

    private void NavigateToAddress()
    {
        var text = AddressBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        AddressNavigated?.Invoke(this, text);
    }
}
