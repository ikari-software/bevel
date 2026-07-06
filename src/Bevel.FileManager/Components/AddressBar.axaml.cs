using Avalonia.Controls;
using Avalonia.Input;

namespace Bevel.FileManager.Components;

public partial class AddressBar : UserControl
{
    public event EventHandler<string>? AddressNavigated;

    private bool _isUpdating;

    public AddressBar() => InitializeComponent();

    public void SetAddress(string path)
    {
        _isUpdating = true;
        AddressComboBox.Text = path;
        _isUpdating = false;
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToAddress();
            e.Handled = true;
        }
    }

    private void OnAddressSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (AddressComboBox.SelectedItem is string selected)
        {
            AddressNavigated?.Invoke(this, selected);
        }
    }

    private void OnGoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        NavigateToAddress();
    }

    private void NavigateToAddress()
    {
        var text = AddressComboBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        AddressNavigated?.Invoke(this, text);
    }
}
