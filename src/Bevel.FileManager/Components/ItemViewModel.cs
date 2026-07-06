using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// Presentation wrapper around an IVfsNode for the item view.
/// Holds selection/edit state alongside the immutable VFS data.
/// </summary>
public sealed class ItemViewModel : INotifyPropertyChanged
{
    private readonly IVfsNode _node;
    private bool _isSelected;
    private bool _isEditing;
    private string? _editName;

    public ItemViewModel(IVfsNode node)
    {
        _node = node;
    }

    public IVfsNode Node => _node;

    // ── VFS data (immutable) ──────────────────────────────────────────

    public VfsPath Path => _node.Path;
    public string DisplayName => _node.DisplayName;
    public VfsNodeKind Kind => _node.Kind;
    public bool IsFolder => _node.Kind is VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot;
    public long? Size => _node.Size;
    public DateTimeOffset? Modified => _node.Modified;
    public string TypeDescription => _node.TypeDescription;
    public IconKey IconKey => _node.IconKey;

    public string SizeDisplay => Size switch
    {
        null => "",
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        < 1024 * 1024 * 1024 => $"{Size / (1024.0 * 1024):0} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):0} GB",
    };

    public string ModifiedDisplay => Modified?.ToString("g") ?? "";

    // ── Mutable presentation state ────────────────────────────────────

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    public string? EditName
    {
        get => _editName;
        set => SetField(ref _editName, value);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
