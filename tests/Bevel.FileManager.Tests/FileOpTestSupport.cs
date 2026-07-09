using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;

namespace Bevel.FileManager.Tests;

/// <summary>Lightweight in-memory IVfsNode for view/sort tests.</summary>
public sealed class FakeNode : IVfsNode
{
    public required VfsPath Path { get; init; }
    public required string DisplayName { get; init; }
    public VfsNodeKind Kind { get; init; } = VfsNodeKind.File;
    public bool MightHaveChildren { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string TypeDescription { get; init; } = "File";
    public IconKey IconKey { get; init; }
    public VfsCapabilities Caps { get; init; }
    public IReadOnlyDictionary<string, object?> ExtraColumns { get; init; } = new Dictionary<string, object?>();

    public static FakeNode File(string name, long size = 1024, string type = "Text Document", DateTimeOffset? modified = null)
        => new()
        {
            Path = new VfsPath("test", name),
            DisplayName = name,
            Kind = VfsNodeKind.File,
            Size = size,
            TypeDescription = type,
            Modified = modified ?? DateTimeOffset.UnixEpoch,
        };
}

/// <summary>Conflict handler that returns a fixed resolution and counts invocations.</summary>
public sealed class StubConflictHandler : IConflictHandler
{
    private readonly ConflictResolution _resolution;
    public int Calls { get; private set; }

    public StubConflictHandler(ConflictResolution resolution = ConflictResolution.Yes) => _resolution = resolution;

    public ValueTask<ConflictResolution> ResolveConflictAsync(
        VfsPath source, VfsPath destination, long? sourceSize, long? destSize,
        DateTimeOffset? sourceModified, DateTimeOffset? destModified, ConflictScope scope,
        CancellationToken ct)
    {
        Calls++;
        return new ValueTask<ConflictResolution>(_resolution);
    }
}

/// <summary>
/// Wraps a real IVfsProvider so tests can inject failures and observe calls without
/// re-implementing an in-memory filesystem: force read-only behavior, throw from
/// ResolveAsync for chosen paths, and count/record resolves.
/// </summary>
public sealed class DecoratingVfsProvider : IVfsProvider
{
    private readonly IVfsProvider _inner;

    public DecoratingVfsProvider(IVfsProvider inner) => _inner = inner;

    public string Scheme => _inner.Scheme;

    /// <summary>When true, GetMutatorAsync returns null (simulating a read-only destination).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Returns an exception to throw for a given path, or null to resolve normally.</summary>
    public Func<VfsPath, Exception?>? ResolveInterceptor { get; set; }

    private readonly object _gate = new();
    private readonly List<VfsPath> _resolved = new();

    public IReadOnlyList<VfsPath> ResolvedPaths
    {
        get { lock (_gate) return _resolved.ToList(); }
    }

    public int ResolveCountFor(VfsPath path)
    {
        lock (_gate) return _resolved.Count(p => p == path);
    }

    public async ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
    {
        lock (_gate) _resolved.Add(path);
        if (ResolveInterceptor?.Invoke(path) is { } ex)
            throw ex;
        return await _inner.ResolveAsync(path, ct);
    }

    public IAsyncEnumerable<IVfsNode> EnumerateAsync(VfsPath folder, EnumerateOptions options, CancellationToken ct)
        => _inner.EnumerateAsync(folder, options, ct);

    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
        => _inner.OpenReadAsync(file, ct);

    public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
        => ReadOnly ? ValueTask.FromResult<IVfsMutator?>(null) : _inner.GetMutatorAsync(folder, ct);

    public IDirectoryWatcher? CreateWatcher(VfsPath folder) => _inner.CreateWatcher(folder);

    public NameValidationResult ValidateName(VfsPath folder, string proposedName)
        => _inner.ValidateName(folder, proposedName);
}
