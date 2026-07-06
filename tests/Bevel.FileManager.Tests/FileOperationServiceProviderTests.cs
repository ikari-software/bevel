using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests that drive FileOperationService through a decorating provider to inject
/// provider-level failures: read-only destination (bevel-c7l), a resolve error that must
/// not be mistaken for "absent" and overwrite (bevel #3), and single-resolve conflict
/// handling (bevel-5ty).
/// </summary>
public sealed class FileOperationServiceProviderTests : IDisposable
{
    private readonly string _root;
    private readonly VfsRoot _vfs = new();
    private readonly DecoratingVfsProvider _provider;

    public FileOperationServiceProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"bevel-fopp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _provider = new DecoratingVfsProvider(new LocalFsProvider());
        _vfs.Register(_provider);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private VfsPath P(params string[] parts) => new("file", Path.Combine(new[] { _root }.Concat(parts).ToArray()));
    private string Abs(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    [Fact]
    public async Task Copy_to_read_only_destination_fails_cleanly_without_partial_write()
    {
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "payload");
        _provider.ReadOnly = true; // GetMutatorAsync returns null

        using var svc = new FileOperationService(_vfs, new StubConflictHandler());
        var result = await svc.ExecuteAsync(new CopyRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        var item = Assert.Single(result.ItemResults);
        Assert.Equal(FileItemResultStatus.Failed, item.Status);
        Assert.Contains("read-only", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Abs("dst", "a.txt")));   // no partial/garbage file
    }

    [Fact]
    public async Task Copy_does_not_overwrite_when_the_destination_resolve_errors()
    {
        // bevel #3: a non-"not found" resolve failure must NOT be treated as "absent"
        // (which would skip the conflict prompt and overwrite the existing file).
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "NEW");
        await File.WriteAllTextAsync(Abs("dst", "a.txt"), "OLD");

        var destPath = P("dst", "a.txt");
        _provider.ResolveInterceptor = path =>
            path == destPath ? new IOException("device busy") : null;

        using var svc = new FileOperationService(_vfs, new StubConflictHandler(ConflictResolution.Yes));
        var result = await svc.ExecuteAsync(new CopyRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        var item = Assert.Single(result.ItemResults);
        Assert.Equal(FileItemResultStatus.Failed, item.Status);
        Assert.Equal("OLD", await File.ReadAllTextAsync(Abs("dst", "a.txt")));   // untouched
    }

    [Fact]
    public async Task Conflict_resolves_the_destination_exactly_once()
    {
        // bevel-5ty: HandleConflictIfNeeded previously resolved the destination twice
        // (existence check + size/modified read). It must now resolve it once.
        Directory.CreateDirectory(Abs("dst"));
        await File.WriteAllTextAsync(Abs("a.txt"), "NEW");
        await File.WriteAllTextAsync(Abs("dst", "a.txt"), "OLD");

        using var svc = new FileOperationService(_vfs, new StubConflictHandler(ConflictResolution.Yes));
        await svc.ExecuteAsync(new CopyRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(1, _provider.ResolveCountFor(P("dst", "a.txt")));
        Assert.Equal("NEW", await File.ReadAllTextAsync(Abs("dst", "a.txt")));   // overwrite happened
    }
}
