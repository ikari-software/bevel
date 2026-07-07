using Bevel.Core.Vfs;

namespace Bevel.FileManager.Navigation;

/// <summary>
/// Back/Forward/Up navigation history for the file manager.
/// Manages a stack of VfsPath entries with back/forward pointers.
/// </summary>
public sealed class NavigationStack
{
    private readonly List<VfsPath> _history = new();
    private int _position = -1;
    private int _maxCapacity;

    public NavigationStack(int maxCapacity = 100)
    {
        _maxCapacity = maxCapacity;
    }

    public VfsPath Current =>
        _position >= 0 && _position < _history.Count
            ? _history[_position]
            : VfsPath.Root("file");

    public bool CanGoBack => _position > 0;
    public bool CanGoForward => _position < _history.Count - 1;

    public int Count => _history.Count;
    public int Position => _position;

    /// <summary>All entries in visit order (oldest first) — for building a History dropdown.</summary>
    public IReadOnlyList<VfsPath> Entries => _history;

    /// <summary>
    /// Entries before the current position, most-recent-first (i.e. one step back is index 0).
    /// </summary>
    public IReadOnlyList<VfsPath> Back
    {
        get
        {
            if (_position <= 0) return Array.Empty<VfsPath>();
            var result = new VfsPath[_position];
            for (var i = 0; i < _position; i++)
                result[i] = _history[_position - 1 - i];
            return result;
        }
    }

    /// <summary>
    /// Entries after the current position, nearest-next-first (i.e. one step forward is index 0).
    /// </summary>
    public IReadOnlyList<VfsPath> Forward
    {
        get
        {
            if (_position >= _history.Count - 1) return Array.Empty<VfsPath>();
            var count = _history.Count - _position - 1;
            var result = new VfsPath[count];
            for (var i = 0; i < count; i++)
                result[i] = _history[_position + 1 + i];
            return result;
        }
    }

    /// <summary>The entry at the given absolute index in <see cref="Entries"/>, or null if out of range.</summary>
    public VfsPath? PeekAt(int index)
        => index >= 0 && index < _history.Count ? _history[index] : null;

    /// <summary>
    /// Repositions the current pointer to <paramref name="index"/> (an absolute index into
    /// <see cref="Entries"/>) WITHOUT pushing a new entry or truncating forward history, and
    /// returns the path now current. Returns null (no-op) when <paramref name="index"/> is out
    /// of range.
    /// </summary>
    public VfsPath? GoTo(int index)
    {
        if (index < 0 || index >= _history.Count) return null;
        _position = index;
        return _history[index];
    }

    /// <summary>
    /// Navigate to a new path, pushing it onto the history stack.
    /// Clears any forward entries when navigating to a new location.
    /// </summary>
    public void Push(VfsPath path)
    {
        if (_position >= 0 && _history[_position] == path)
            return;

        // Remove any forward entries
        if (_position < _history.Count - 1)
            _history.RemoveRange(_position + 1, _history.Count - _position - 1);

        _history.Add(path);
        _position = _history.Count - 1;

        // Trim from the front if we exceed capacity
        if (_history.Count > _maxCapacity)
        {
            var excess = _history.Count - _maxCapacity;
            _history.RemoveRange(0, excess);
            _position -= excess;
        }
    }

    public VfsPath? GoBack()
    {
        if (!CanGoBack) return null;
        return _history[--_position];
    }

    public VfsPath? GoForward()
    {
        if (!CanGoForward) return null;
        return _history[++_position];
    }

    public VfsPath? GoUp()
    {
        if (Current.IsRoot) return null;
        return new VfsPath(Current.Scheme, Current.ParentValue);
    }

    public void Clear()
    {
        _history.Clear();
        _position = -1;
    }
}
