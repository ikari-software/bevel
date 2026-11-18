using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Bevel.Core.Vfs;

/// <summary>
/// Reads a folder's listing OFF the calling thread and hands it back in bounded batches (bevel-so27).
///
/// <para>Why it exists: <see cref="IVfsProvider.EnumerateAsync"/> is free to be synchronous on its
/// caller — <see cref="LocalFsProvider"/> is (one directory read, no await) — so an <c>await foreach</c>
/// on the UI thread used to do the whole walk, plus each entry's stat, on the UI thread. The provider
/// contract stays as it is (other callers rely on it); this helper is the one place that moves the
/// work to the pool, so every UI consumer gets the same fix.</para>
///
/// <para>Contract: a <see cref="Task.Run(Func{Task})"/> producer iterates the provider and touches each
/// node's <see cref="IVfsNode.Size"/> and <see cref="IVfsNode.Modified"/> there, so a lazy stat is paid
/// on the pool rather than when a UI cell first binds. Batches flow through a bounded channel
/// (capacity <see cref="ChannelCapacity"/>), so a consumer that stops reading stops the walk too.
/// Order is the provider's order. A provider failure completes the channel WITH that exception, so it
/// is rethrown at the consumer unchanged ("folder is gone" still throws
/// <see cref="DirectoryNotFoundException"/>, an unknown scheme <see cref="KeyNotFoundException"/>).
/// Cancelling <c>ct</c>, or abandoning the enumeration early, cancels the producer.</para>
///
/// <para>Threading: this type knows nothing about any UI framework (Bevel.Core must not reference
/// Avalonia). It never resumes on a captured context itself; a consumer that iterates with a plain
/// <c>await foreach</c> on the UI thread therefore gets each batch delivered back on the UI thread,
/// one dispatcher job per batch. Such a consumer must re-check its token after every await — a batch
/// can already be in flight when a newer navigation cancels.</para>
/// </summary>
public static class VfsBatchReader
{
    /// <summary>How many batches may be read ahead of the consumer.</summary>
    public const int ChannelCapacity = 4;

    public static async IAsyncEnumerable<IReadOnlyList<IVfsNode>> ReadBatchesAsync(
        VfsRoot root,
        VfsPath folder,
        EnumerateOptions options,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var channel = Channel.CreateBounded<IVfsNode[]>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Linked so that BOTH the caller's cancellation and an early exit from this iterator (break,
        // dispose, an exception in the consumer's loop body) stop the walk.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(
            () => ProduceAsync(root, folder, options, batchSize, channel.Writer, producerCts.Token),
            CancellationToken.None);

        try
        {
            // WaitToReadAsync rethrows the exception the producer completed the channel with.
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var batch))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return batch;
                }
            }
        }
        finally
        {
            producerCts.Cancel();
            // ProduceAsync never faults (it routes every exception into the channel), so this only
            // waits for the walk to let go of the provider — bounded by the provider honouring ct.
            await producer.ConfigureAwait(false);
        }
    }

    private static async Task ProduceAsync(
        VfsRoot root,
        VfsPath folder,
        EnumerateOptions options,
        int batchSize,
        ChannelWriter<IVfsNode[]> writer,
        CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            var buffer = new List<IVfsNode>(batchSize);
            await foreach (var node in root.EnumerateAsync(folder, options, ct).ConfigureAwait(false))
            {
                // Pay the lazy stat here, on the pool, not on first bind in the UI.
                _ = node.Size;
                _ = node.Modified;

                buffer.Add(node);
                if (buffer.Count < batchSize) continue;
                await writer.WriteAsync(buffer.ToArray(), ct).ConfigureAwait(false);
                buffer.Clear();
            }

            if (buffer.Count > 0)
                await writer.WriteAsync(buffer.ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never swallowed: the consumer rethrows it from WaitToReadAsync. (The previous attempt at
            // this card lost DirectoryNotFoundException here and painted an empty folder instead.)
            error = ex;
        }
        finally
        {
            writer.TryComplete(error);
        }
    }
}
