using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bevel.Taskbar;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for the taskbar / Start-menu view-models.
/// Matches the codebase's hand-rolled MVVM convention (see Bevel.FileManager ItemViewModel) —
/// no external MVVM toolkit. Property setters raise change notifications so a data-bound,
/// virtualized view reflects the background model without the view mutating controls by hand.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Sets <paramref name="field"/> and raises PropertyChanged when the value changes.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
