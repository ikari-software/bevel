using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.UI;

/// <summary>
/// A fixed-capacity, memory-mapped-file-backed pool of decoded BGRA icons, keyed by string
/// (e.g. <c>"/Applications/Safari.app|32"</c>) → <see cref="PalImage"/>. It is the cross-process
/// storage substrate for the multi-process shell (phase P6, bevel-gww.6): the shell-core process
/// decodes an icon ONCE via NSWorkspace and <see cref="TryAdd"/>s the pixels here; the taskbar and
/// explorer UI processes map the SAME file and <see cref="TryGet"/> the pixels straight out of the
/// shared pages instead of each re-decoding the same icon.
///
/// <para><b>Concurrency contract — SINGLE WRITER, MANY READERS.</b> Exactly ONE process (the owner)
/// ever calls <see cref="TryAdd"/>; any number of processes (including the owner) call
/// <see cref="TryGet"/>. Because there is only one writer there is NO write-write race, so we need
/// NO cross-process lock — no <see cref="Mutex"/>/<see cref="Semaphore"/> (named ones are Windows-only
/// on Unix anyway). The ONLY synchronization is the acquire/release ordering around the
/// <c>publishedCount</c> cursor described below. This makes both paths lock-free.</para>
///
/// <para><b>Lock-free publish protocol.</b> The store is append-only: entry <c>i</c> lives at slot
/// <c>i</c> and is written EXACTLY ONCE, then frozen forever (published entries are never mutated).
/// A single 64-bit <c>publishedCount</c> in the header is the publish cursor. To add, the writer
/// (1) writes the BGRA blob and the directory entry for slot <c>publishedCount</c>, (2) issues a
/// release fence, then (3) stores <c>publishedCount + 1</c>. A reader (1) loads <c>publishedCount</c>,
/// (2) issues an acquire fence, then (3) only ever looks at entries <c>[0, publishedCount)</c>.
/// The fence pair guarantees that any reader which OBSERVES the incremented count also observes the
/// fully-written blob+entry underneath it — so a reader can never see a half-written slot (no torn
/// reads). Entries at index &gt;= the observed count are simply invisible until a later load.</para>
///
/// <para><b>Why this is safe across processes.</b> Every process maps the same physical pages, and a
/// <see cref="MemoryMappedViewAccessor"/> over offset 0 is page-aligned, so the 8-byte
/// <c>publishedCount</c> at offset 16 is 8-byte aligned and its load/store is a single atomic CPU
/// access on x64/arm64. The <see cref="Interlocked.MemoryBarrier"/> fences supply the release/acquire
/// ordering; hardware cache coherence supplies inter-process visibility of the underlying pages.</para>
///
/// <para><b>Readers copy out.</b> <see cref="TryGet"/> copies the BGRA into a fresh <c>byte[]</c> so
/// the shared pages stay immutable and no reader can scribble on another's pixels (Avalonia copies
/// into its own bitmap anyway). Lookup is a linear scan of the directory — n is hundreds of icons,
/// so this is deliberately kept simple rather than indexed.</para>
/// </summary>
public sealed class MmfBgraPool : IDisposable
{
    // ---- File format ----------------------------------------------------------------------------
    // Header (fixed 64 bytes):
    //   [ 0] int   magic
    //   [ 4] int   formatVersion
    //   [ 8] int   slotCapacity
    //   [12] int   maxBgraBytes  (per-slot blob ceiling)
    //   [16] long  publishedCount  <- the atomic publish cursor (8-byte aligned)
    //   [24..64]   reserved / padding
    // Directory: slotCapacity fixed entries of EntrySize bytes each, at DirBase.
    //   [ 0] long  keyHash        (FNV-1a 64 over the FULL utf-8 key)
    //   [ 8] int   keyByteLength  (FULL utf-8 length, may exceed KeyField)
    //   [12] int   width
    //   [16] int   height
    //   [20] int   bgraLength
    //   [24] byte[KeyField] key bytes (stored TRUNCATED to KeyField; see key-collision note)
    // Blob region: slotCapacity * maxBgraBytes at BlobBase; slot i's BGRA at BlobBase + i*maxBgraBytes.

    private const int Magic = 0x314C4D42;   // 'B','M','L','1' — bump on any layout change
    private const int FormatVersion = 1;

    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffCapacity = 8;
    private const int OffMaxBgra = 12;
    private const int OffPublished = 16;    // long, 8-byte aligned
    private const int HeaderSize = 64;

    private const int KeyField = 256;       // fixed key-bytes field; long keys are stored truncated
    private const int EHash = 0;
    private const int EKeyLen = 8;
    private const int EWidth = 12;
    private const int EHeight = 16;
    private const int EBgraLen = 20;
    private const int EKeyBytes = 24;
    private const int EntrySize = EKeyBytes + KeyField; // 280

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _slotCapacity;
    private readonly int _maxBgraBytes;
    private readonly long _dirBase;
    private readonly long _blobBase;

    private MmfBgraPool(
        MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, int slotCapacity, int maxBgraBytes)
    {
        _mmf = mmf;
        _accessor = accessor;
        _slotCapacity = slotCapacity;
        _maxBgraBytes = maxBgraBytes;
        _dirBase = HeaderSize;
        _blobBase = _dirBase + (long)slotCapacity * EntrySize;
    }

    /// <summary>Number of icons published so far (the append cursor). Diagnostic; races with the writer.</summary>
    public long PublishedCount => ReadPublishedCountAcquire();

    /// <summary>Per-slot count and blob ceiling this pool was created with.</summary>
    public int SlotCapacity => _slotCapacity;
    public int MaxBgraBytes => _maxBgraBytes;

    /// <summary>
    /// Creates the pool file at <paramref name="path"/> (sized to hold header + directory + blob) and
    /// initializes its header, OR — if a pool with MATCHING geometry already exists there — attaches to
    /// it without touching its contents. The SAME call is used by the writer and by every reader: the
    /// first caller creates, the rest attach. If a file exists at <paramref name="path"/> with a
    /// DIFFERENT geometry (magic/version/capacity/maxBgra), it is recreated (its data is discarded).
    ///
    /// <para>The caller owns the choice of <paramref name="path"/> (e.g. a per-user runtime dir on
    /// macOS); this just creates the parent directory if needed. The writer should open first so a
    /// reader never observes a mid-initialization file — the single-writer contract makes that ordering
    /// natural.</para>
    /// </summary>
    /// <param name="slotCapacity">Maximum number of distinct icons the pool can hold.</param>
    /// <param name="maxBgraBytes">Maximum BGRA byte length any single icon may occupy.</param>
    public static MmfBgraPool CreateOrOpen(string path, int slotCapacity, int maxBgraBytes)
    {
        if (slotCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(slotCapacity));
        if (maxBgraBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBgraBytes));

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        long fileSize = HeaderSize
            + (long)slotCapacity * EntrySize
            + (long)slotCapacity * maxBgraBytes;

        // FileShare.ReadWrite so reader processes can map the same file while the writer holds it.
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Size mismatch => brand-new or wrong geometry: (re)size and force header init.
        bool needInit = fs.Length != fileSize;
        if (needInit)
            fs.SetLength(fileSize);

        // CreateFromFile takes ownership of the stream (leaveOpen: false) and closes it on Dispose.
        var mmf = MemoryMappedFile.CreateFromFile(
            fs, mapName: null, fileSize, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
        var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);

        try
        {
            // Same size but validate the header actually describes THIS geometry; if not, recreate.
            if (!needInit)
            {
                needInit = accessor.ReadInt32(OffMagic) != Magic
                    || accessor.ReadInt32(OffVersion) != FormatVersion
                    || accessor.ReadInt32(OffCapacity) != slotCapacity
                    || accessor.ReadInt32(OffMaxBgra) != maxBgraBytes;
            }

            if (needInit)
            {
                // Publish LAST: zero the cursor first so a racing reader sees an empty (not corrupt)
                // pool even mid-init, then stamp the geometry, then re-arm the cursor at 0.
                accessor.Write(OffPublished, 0L);
                accessor.Write(OffMagic, Magic);
                accessor.Write(OffVersion, FormatVersion);
                accessor.Write(OffCapacity, slotCapacity);
                accessor.Write(OffMaxBgra, maxBgraBytes);
                Interlocked.MemoryBarrier();
                accessor.Write(OffPublished, 0L);
            }
        }
        catch
        {
            accessor.Dispose();
            mmf.Dispose();
            throw;
        }

        return new MmfBgraPool(mmf, accessor, slotCapacity, maxBgraBytes);
    }

    /// <summary>
    /// WRITER-ONLY. Publishes <paramref name="image"/> under <paramref name="key"/>. Returns:
    /// <list type="bullet">
    /// <item><c>true</c> and does nothing if <paramref name="key"/> is already published (idempotent —
    /// re-adding is a no-op, never a duplicate slot and never a mutation of the frozen entry).</item>
    /// <item><c>false</c> if the pool is full (published == capacity) or the blob exceeds
    /// <see cref="MaxBgraBytes"/>. The pool stays healthy and previously-published entries stay
    /// readable.</item>
    /// <item><c>true</c> after appending the blob+entry and advancing the publish cursor otherwise.</item>
    /// </list>
    /// Must be called from the single owning process only; concurrent writers are NOT supported.
    /// </summary>
    public bool TryAdd(string key, PalImage image)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (image.Bgra is null) return false;
        if (image.Bgra.Length > _maxBgraBytes) return false;

        var keyBytes = Encoding.UTF8.GetBytes(key);
        long keyHash = Fnv1a64(keyBytes);

        // Single writer: reading our own cursor with acquire is enough to enumerate what's published.
        long count = ReadPublishedCountAcquire();
        if (FindSlot(keyBytes, keyHash, count) >= 0)
            return true; // already there — idempotent

        if (count >= _slotCapacity)
            return false; // full

        int slot = (int)count;

        // (1) Write the blob into slot `slot`'s region.
        long blobOff = _blobBase + (long)slot * _maxBgraBytes;
        if (image.Bgra.Length > 0)
            _accessor.WriteArray(blobOff, image.Bgra, 0, image.Bgra.Length);

        // (1) Write the directory entry. Key bytes are stored truncated to KeyField; the FULL length
        //     and FULL hash are also stored so lookup can reject a same-prefix/different-length key.
        long e = _dirBase + (long)slot * EntrySize;
        int keyStore = Math.Min(keyBytes.Length, KeyField);
        _accessor.Write(e + EHash, keyHash);
        _accessor.Write(e + EKeyLen, keyBytes.Length);
        _accessor.Write(e + EWidth, image.Width);
        _accessor.Write(e + EHeight, image.Height);
        _accessor.Write(e + EBgraLen, image.Bgra.Length);
        if (keyStore > 0)
            _accessor.WriteArray(e + EKeyBytes, keyBytes, 0, keyStore);

        // (2) release + (3) publish: any reader that sees `slot+1` sees the blob+entry above, whole.
        WritePublishedCountRelease(count + 1);
        return true;
    }

    /// <summary>
    /// READER (any process). Copies the icon published under <paramref name="key"/> into a fresh
    /// <see cref="PalImage"/> (readers never alias the shared pages). Returns <c>false</c> on a miss —
    /// including on a not-yet-written or empty pool (<c>publishedCount == 0</c>): it fails gracefully
    /// rather than throwing. Lookup is an acquire load of the cursor followed by a linear scan of the
    /// first <c>publishedCount</c> entries (hash match, then exact key compare).
    /// </summary>
    public bool TryGet(string key, out PalImage image)
    {
        image = default!;
        if (key is null) return false;

        var keyBytes = Encoding.UTF8.GetBytes(key);
        long keyHash = Fnv1a64(keyBytes);

        long count = ReadPublishedCountAcquire();
        int slot = FindSlot(keyBytes, keyHash, count);
        if (slot < 0)
            return false;

        long e = _dirBase + (long)slot * EntrySize;
        int width = _accessor.ReadInt32(e + EWidth);
        int height = _accessor.ReadInt32(e + EHeight);
        int bgraLen = _accessor.ReadInt32(e + EBgraLen);

        // These fields come straight out of a SHARED, cross-process file that a corrupt/truncated
        // pool — or a hostile same-UID mapper — can populate with garbage. The writer validates on
        // TryAdd; the reader must fail closed too, or `new byte[bgraLen]` OOMs / throws on the
        // off-thread icon path (ce-review: security). Reject anything the writer couldn't have
        // legitimately stored.
        if (width <= 0 || height <= 0 || bgraLen <= 0
            || bgraLen > _maxBgraBytes || bgraLen < (long)width * height * 4)
            return false;

        var bgra = new byte[bgraLen];
        long blobOff = _blobBase + (long)slot * _maxBgraBytes;
        _accessor.ReadArray(blobOff, bgra, 0, bgraLen);

        image = new PalImage(width, height, bgra);
        return true;
    }

    /// <summary>
    /// Linear scan of directory entries <c>[0, count)</c> for <paramref name="keyBytes"/>. Matches on
    /// keyHash first (cheap 8-byte compare), then requires equal FULL key length, then compares the
    /// stored key prefix (up to <see cref="KeyField"/> bytes) against the candidate.
    ///
    /// <para><b>Key-collision tradeoff.</b> Keys ≤256 bytes are compared in full, so matches are exact.
    /// A key LONGER than 256 bytes is compared only on (hash, full length, first 256 bytes): two such
    /// keys that share all three would collide. That is astronomically unlikely for real icon keys
    /// (paths + size suffix), and the failure mode is a wrong-but-valid icon, never corruption.</para>
    /// </summary>
    private int FindSlot(byte[] keyBytes, long keyHash, long count)
    {
        int cmpLen = Math.Min(keyBytes.Length, KeyField);
        var scratch = cmpLen > 0 ? new byte[cmpLen] : Array.Empty<byte>();

        for (long i = 0; i < count; i++)
        {
            long e = _dirBase + i * EntrySize;
            if (_accessor.ReadInt64(e + EHash) != keyHash)
                continue;
            if (_accessor.ReadInt32(e + EKeyLen) != keyBytes.Length)
                continue;
            if (cmpLen == 0)
                return (int)i; // both empty keys, hashes matched

            _accessor.ReadArray(e + EKeyBytes, scratch, 0, cmpLen);
            if (scratch.AsSpan().SequenceEqual(keyBytes.AsSpan(0, cmpLen)))
                return (int)i;
        }
        return -1;
    }

    // ---- Memory-ordering primitives on the mapped cursor ----------------------------------------
    // The full fences pair with each other across processes: the writer's release (fence THEN store)
    // ensures the blob+entry writes are globally visible before the new count; the reader's acquire
    // (load THEN fence) ensures no entry read is hoisted above the count load. So observing count N
    // guarantees seeing the first N entries whole.

    private long ReadPublishedCountAcquire()
    {
        long v = _accessor.ReadInt64(OffPublished);
        Interlocked.MemoryBarrier(); // acquire: keep later entry reads below this load
        return v;
    }

    private void WritePublishedCountRelease(long value)
    {
        Interlocked.MemoryBarrier(); // release: keep earlier blob/entry writes above this store
        _accessor.Write(OffPublished, value);
    }

    /// <summary>FNV-1a 64-bit — a small, stable, non-crypto hash over the UTF-8 key bytes.</summary>
    private static long Fnv1a64(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in data)
        {
            h ^= b;
            h *= prime;
        }
        return unchecked((long)h);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
