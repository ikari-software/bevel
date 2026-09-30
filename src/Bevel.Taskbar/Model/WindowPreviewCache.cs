using System.Diagnostics.CodeAnalysis;

namespace Bevel.Taskbar;

/// <summary>
/// Per-window PNG cache for taskbar hover previews. Lets the UI paint a stale thumbnail
/// immediately on hover, then refresh asynchronously (ce-optimize bevel-taskbar-preview-responsive).
/// Thread-safe; capacity-bounded LRU.
/// </summary>
public sealed class WindowPreviewCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _map = new();
    private readonly LinkedList<string> _lru = new();

    public WindowPreviewCache(int capacity = 48)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public bool TryGet(string windowId, [NotNullWhen(true)] out byte[]? png)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        lock (_gate)
        {
            if (!_map.TryGetValue(windowId, out var e))
            {
                png = null;
                return false;
            }
            _lru.Remove(e.Node);
            _lru.AddFirst(e.Node);
            png = e.Png;
            return true;
        }
    }

    public void Set(string windowId, byte[] png)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0) return;
        lock (_gate)
        {
            if (_map.TryGetValue(windowId, out var existing))
            {
                existing.Png = png;
                _lru.Remove(existing.Node);
                _lru.AddFirst(existing.Node);
                return;
            }
            while (_map.Count >= _capacity && _lru.Last is { } last)
            {
                _map.Remove(last.Value);
                _lru.RemoveLast();
            }
            var node = _lru.AddFirst(windowId);
            _map[windowId] = new Entry(png, node);
        }
    }

    public void Remove(string windowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        lock (_gate)
        {
            if (!_map.TryGetValue(windowId, out var e)) return;
            _map.Remove(windowId);
            _lru.Remove(e.Node);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    private sealed class Entry(byte[] png, LinkedListNode<string> node)
    {
        public byte[] Png = png;
        public LinkedListNode<string> Node = node;
    }
}
