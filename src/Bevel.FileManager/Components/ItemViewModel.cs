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
    private IVfsNode _node;
    private bool _isSelected;
    private bool _isEditing;
    private string? _editName;
    private bool _showFullName;

    public ItemViewModel(IVfsNode node)
    {
        _node = node;
    }

    public IVfsNode Node => _node;

    /// <summary>
    /// Refresh the underlying node in place (same identity, new metadata) during a differential
    /// reload — keeps the row's object, selection, and virtualization slot, and just re-notifies
    /// the derived display properties. Part of the keyed reconcile that avoids list flicker.
    /// Raises only what actually changed (bevel-so27): a reload hands EVERY row a fresh node, and
    /// re-notifying unchanged cells re-measures the whole list for nothing. <see cref="Node"/> is
    /// always raised — its identity did change. (Size/Modified were already read on the pool by
    /// <see cref="VfsBatchReader"/>, so comparing them here costs no stat on the UI thread.)
    /// </summary>
    public void Update(IVfsNode node)
    {
        if (ReferenceEquals(_node, node)) return;
        var old = _node;
        _node = node;
        if (old.DisplayName != node.DisplayName || old.Kind != node.Kind) OnPropertyChanged(nameof(DisplayName));
        if (old.Size != node.Size) OnPropertyChanged(nameof(SizeDisplay));
        if (old.Modified != node.Modified) OnPropertyChanged(nameof(ModifiedDisplay));
        if (old.TypeDescription != node.TypeDescription) OnPropertyChanged(nameof(TypeDescription));
        if (old.IconKey != node.IconKey) OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(Node));
    }

    /// <summary>Icon views: this is the one selected item of a focused view, so its label shows the whole
    /// name over the rows below instead of two lines + ellipsis (bevel-m9jy). Set by ItemView only.</summary>
    public bool ShowFullName
    {
        get => _showFullName;
        set { if (_showFullName == value) return; _showFullName = value; OnPropertyChanged(); }
    }

    // ── VFS data ──────────────────────────────────────────────────────

    public VfsPath Path => _node.Path;

    /// <summary>Folder Options "Hide extensions for known types" — app-wide, so a process-wide flag rather
    /// than a per-item field. A full re-list rebuilds every ItemViewModel, so toggling + reload applies it.</summary>
    public static bool HideKnownExtensions;

    /// <summary>The real on-disk name (used for rename, so hiding the extension never truncates a file).</summary>
    public string RealName => _node.DisplayName;

    public string DisplayName
    {
        get
        {
            var name = _node.DisplayName;
            if (!HideKnownExtensions || IsFolder) return name;
            var ext = System.IO.Path.GetExtension(name);
            // Never strip dotfiles (".bashrc" → ext == the whole name) or extensionless names.
            if (ext.Length <= 1 || ext.Length >= name.Length) return name;
            return name[..^ext.Length];
        }
    }
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
