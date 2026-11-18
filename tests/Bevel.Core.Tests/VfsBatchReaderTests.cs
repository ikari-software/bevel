using System.Runtime.CompilerServices;
using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-so27: <see cref="VfsBatchReader"/> moves a provider's enumeration (and the lazy per-node stat)
/// off the calling thread, hands the results back in bounded batches, keeps the provider's error
/// contract (a missing folder still throws at the consumer) and stops the producer on cancellation.
/// </summary>
public sealed class VfsBatchReaderTests
{
    [Fact]
    public void Enumeration_runs_off_the_calling_thread()
    {
        var provider = new RecordingProvider(count: 70);
        var root = new VfsRoot();
        root.Register(provider);
        var callerThread = Environment.CurrentManagedThreadId;

        // Drained by BLOCKING on this thread, so the producer can only ever run here if the reader
        // ran the provider inline on its caller — exactly the bug being guarded.
        var batches = DrainBlocking(VfsBatchReader.ReadBatchesAsync(
            root, VfsPath.Root(RecordingProvider.SchemeName), new EnumerateOptions(), batchSize: 32, CancellationToken.None));

        Assert.NotEmpty(provider.EnumerationThreads);
        Assert.DoesNotContain(callerThread, provider.EnumerationThreads);
        // The lazy stat (Size + Modified) is paid on the producer too, not on the consumer.
        Assert.Equal(70, provider.StatThreads.Count);
        Assert.DoesNotContain(callerThread, provider.StatThreads);

        // Order is the provider's order, in bounded batches.
        Assert.Equal(new[] { 32, 32, 6 }, batches.Select(b => b.Count));
        Assert.Equal(Enumerable.Range(0, 70).Select(i => $"n{i:D3}"),
            batches.SelectMany(b => b).Select(n => n.DisplayName));
    }

    [Fact]
    public async Task A_missing_directory_still_throws_at_the_consumer()
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var missing = new VfsPath("file", Path.Combine(Path.GetTempPath(), "bevel-missing-" + Guid.NewGuid().ToString("N")));

        // The previous attempt's pool walker swallowed this, so "folder is gone" painted an empty list.
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await foreach (var _ in VfsBatchReader.ReadBatchesAsync(root, missing, new EnumerateOptions(), 32, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task Cancellation_stops_the_producer()
    {
        var provider = new EndlessProvider();
        var root = new VfsRoot();
        root.Register(provider);
        using var cts = new CancellationTokenSource();

        var enumerator = VfsBatchReader.ReadBatchesAsync(
            root, VfsPath.Root(EndlessProvider.SchemeName), new EnumerateOptions(), batchSize: 8, cts.Token)
            .GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            cts.Cancel();

            // The producer must observe the cancellation and leave the provider's iterator — it may
            // not run on forever behind a bounded channel nobody reads.
            var finished = await Task.WhenAny(provider.Finished, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(provider.Finished, finished);

            var produced = provider.Produced;
            await Task.Delay(100);
            Assert.Equal(produced, provider.Produced);

            // And the consumer sees the cancellation, not more batches.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                while (await enumerator.MoveNextAsync()) { }
            });
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static List<IReadOnlyList<IVfsNode>> DrainBlocking(IAsyncEnumerable<IReadOnlyList<IVfsNode>> source)
    {
        var result = new List<IReadOnlyList<IVfsNode>>();
        var e = source.GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                result.Add(e.Current);
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return result;
    }

    /// <summary>A synchronous provider (like LocalFsProvider): no await anywhere, so if the reader
    /// iterated it on the caller, every recorded thread id would be the caller's.</summary>
    private sealed class RecordingProvider(int count) : StubProvider
    {
        public const string SchemeName = "recording";
        public override string Scheme => SchemeName;

        public List<int> EnumerationThreads { get; } = new();
        public List<int> StatThreads { get; } = new();

        public override async IAsyncEnumerable<IVfsNode> EnumerateAsync(
            VfsPath folder, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < count; i++)
            {
                lock (EnumerationThreads) EnumerationThreads.Add(Environment.CurrentManagedThreadId);
                yield return new RecordingNode(VfsPath.Combine(folder, $"n{i:D3}"), $"n{i:D3}", this);
            }
            await Task.CompletedTask;
        }

        public void RecordStat()
        {
            lock (StatThreads) StatThreads.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class RecordingNode(VfsPath path, string name, RecordingProvider owner) : IVfsNode
    {
        private bool _statted;
        public VfsPath Path => path;
        public string DisplayName => name;
        public VfsNodeKind Kind => VfsNodeKind.File;
        public bool MightHaveChildren => false;
        public long? Size { get { Stat(); return 1; } }
        public DateTimeOffset? Modified { get { Stat(); return DateTimeOffset.UnixEpoch; } }
        public string TypeDescription => "File";
        public IconKey IconKey => IconKey.File(".txt");
        public VfsCapabilities Caps => VfsCapabilities.None;

        private void Stat()
        {
            if (_statted) return;
            _statted = true;
            owner.RecordStat();
        }
    }

    /// <summary>Yields forever; records how far it got and when its iterator is left.</summary>
    private sealed class EndlessProvider : StubProvider
    {
        public const string SchemeName = "endless";
        public override string Scheme => SchemeName;

        private int _produced;
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Produced => Volatile.Read(ref _produced);
        public Task Finished => _finished.Task;

        public override async IAsyncEnumerable<IVfsNode> EnumerateAsync(
            VfsPath folder, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _produced);
                    yield return new VirtualNode
                    {
                        Path = VfsPath.Combine(folder, $"e{i}"), DisplayName = $"e{i}", Kind = VfsNodeKind.File,
                    };
                    await Task.Yield();
                }
            }
            finally
            {
                _finished.TrySetResult();
            }
        }
    }

    private abstract class StubProvider : IVfsProvider
    {
        public abstract string Scheme { get; }
        public abstract IAsyncEnumerable<IVfsNode> EnumerateAsync(VfsPath folder, EnumerateOptions options, CancellationToken ct);
        public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct) => ValueTask.FromResult<IVfsMutator?>(null);
        public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;
        public NameValidationResult ValidateName(VfsPath folder, string proposedName) => NameValidationResult.Ok;
    }
}
