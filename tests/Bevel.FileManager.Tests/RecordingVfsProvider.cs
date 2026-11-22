using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Synchronous test provider (like LocalFsProvider: no real await) for the bevel-so27 enumeration
/// tests. Records the thread every entry is produced on and how often each folder was enumerated,
/// and offers gates so a test can put a listing in a known state by construction, not by timing.
///
/// Folders (scheme <c>rec</c>):
///   /top        40 folders + 60 .txt files
///   /slow       the same shape, "slow"-prefixed; blocks BEFORE its first entry until <see cref="ReleaseSlow"/>
///   /failslow   blocks until <see cref="ReleaseSlow"/>, then throws DirectoryNotFoundException
///   /fail       throws DirectoryNotFoundException immediately
///   /gated      32 files "gated-a…", then blocks (cancellably) until <see cref="ReleaseGate"/>, then 32 "gated-b…"
///   /many       128 files (exactly 4 batches of 32 — all fit the reader's 4-slot channel)
///   /manydirs   128 folders
///   /stale      64 "stale"-prefixed files (2 batches)
///   /staledirs  64 "stale"-prefixed folders
///   /empty      nothing
/// </summary>
internal sealed class RecordingVfsProvider : IVfsProvider
{
    public const string SchemeName = "rec";
    public string Scheme => SchemeName;

    private readonly ManualResetEventSlim _slowGate = new(false);
    private readonly ManualResetEventSlim _gate = new(false);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _ended = new();
    private readonly ConcurrentDictionary<string, int> _enumerations = new();

    public List<int> Threads { get; } = new();

    public static VfsPath Path(string folder) => new(SchemeName, folder);

    public void ReleaseSlow() => _slowGate.Set();
    public void ReleaseGate() => _gate.Set();

    public int Enumerations(string folder) => _enumerations.GetValueOrDefault(folder);

    /// <summary>Blocks until an enumeration of <paramref name="folder"/> has left the provider — for
    /// the batch-multiple folders that means every batch is already sitting in the reader's channel.</summary>
    public void WaitEnded(string folder)
    {
        if (!_ended.GetOrAdd(folder, _ => new ManualResetEventSlim(false)).Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException($"Enumeration of {folder} did not end.");
    }

    public static string[] FolderNames(string folder) => folder switch
    {
        "/top" => Dirs("", 40),
        "/slow" => Dirs("slow", 40),
        "/manydirs" => Dirs("many", 128),
        "/staledirs" => Dirs("stale", 64),
        _ => Array.Empty<string>(),
    };

    public static string[] Names(string folder) => folder switch
    {
        "/top" => Dirs("", 40).Concat(Files("", 60)).ToArray(),
        "/slow" => Dirs("slow", 40).Concat(Files("slow", 60)).ToArray(),
        "/gated" => Files("gated-a", 32).Concat(Files("gated-b", 32)).ToArray(),
        "/many" => Files("many", 128),
        "/manydirs" => Dirs("many", 128),
        "/stale" => Files("stale", 64),
        "/staledirs" => Dirs("stale", 64),
        _ => Array.Empty<string>(),
    };

    private static string[] Dirs(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}dir{i:D3}").ToArray();
    private static string[] Files(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}file{i:D3}.txt").ToArray();

    public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
        VfsPath folder, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var name = folder.Value;
        _enumerations.AddOrUpdate(name, 1, (_, n) => n + 1);
        try
        {
            if (name is "/slow" or "/failslow") _slowGate.Wait(TimeSpan.FromSeconds(10));
            if (name is "/fail" or "/failslow") throw new DirectoryNotFoundException($"Simulated: {name} is gone");

            var names = Names(name);
            for (var i = 0; i < names.Length; i++)
            {
                if (name == "/gated" && i == 32) _gate.Wait(TimeSpan.FromSeconds(10), ct);
                ct.ThrowIfCancellationRequested();
                lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
                var entry = names[i];
                yield return entry.EndsWith(".txt", StringComparison.Ordinal)
                    ? new VirtualNode
                    {
                        Path = VfsPath.Combine(folder, entry), DisplayName = entry, Kind = VfsNodeKind.File,
                        Size = 1, TypeDescription = "Text Document", IconKey = IconKey.File(".txt"),
                    }
                    : VirtualNode.Folder(VfsPath.Combine(folder, entry), entry);
            }
            await Task.CompletedTask;
        }
        finally
        {
            _ended.GetOrAdd(name, _ => new ManualResetEventSlim(false)).Set();
        }
    }

    public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
        => ValueTask.FromResult<IVfsNode>(VirtualNode.Folder(path, path.FileName));
    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct) => throw new NotSupportedException();
    /// <summary>Off by default (read-only, like before). The free-name-scan tests turn it on: the controller
    /// returns before enumerating anything when a folder has no mutator, so without one they test nothing.</summary>
    public bool Writable { get; set; }

    public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
        => ValueTask.FromResult<IVfsMutator?>(Writable ? new WritableStub() : null);

    /// <summary>Just enough of a mutator for NewFolderAsync / NewFileAsync; everything else is unsupported.</summary>
    private sealed class WritableStub : IVfsMutator
    {
        public ValueTask<VfsPath> CreateFolderAsync(VfsPath parent, string name, CancellationToken ct)
            => ValueTask.FromResult(new VfsPath(parent.Scheme, parent.Value.TrimEnd('/') + "/" + name));
        public ValueTask<Stream> OpenWriteAsync(VfsPath file, CancellationToken ct)
            => ValueTask.FromResult<Stream>(new MemoryStream());
        public ValueTask RenameAsync(VfsPath path, string newName, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<VfsPath> MoveAsync(VfsPath path, VfsPath destinationParent, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<VfsPath?> DeleteAsync(VfsPath path, bool toTrash, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask SetAttributesAsync(VfsPath path, VfsNodeAttributes attributes, CancellationToken ct) => throw new NotSupportedException();
    }
    public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;
    public NameValidationResult ValidateName(VfsPath folder, string proposedName) => NameValidationResult.Ok;
}
