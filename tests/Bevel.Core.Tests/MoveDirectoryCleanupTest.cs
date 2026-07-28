using System;
using System.IO;
using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-a3r: if the cross-volume move's copy fails partway, the partial destination must be
/// cleaned up and the source left fully intact — never a half-move.</summary>
public class MoveDirectoryCleanupTest
{
    [Fact]
    public void MoveDirectory_cleans_partial_copy_and_keeps_source_on_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "bevel-move-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dst");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        File.WriteAllText(Path.Combine(source, "b.txt"), "B");
        // Pre-create dest with a colliding file, so Directory.Move fails (dest exists) and the
        // fallback CopyDirectory then throws on File.Copy of the duplicate name partway through.
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "existing");

        try
        {
            Assert.ThrowsAny<IOException>(() => LocalTrash.MoveDirectory(source, dest));

            // Source fully intact — the move never half-completed.
            Assert.True(File.Exists(Path.Combine(source, "a.txt")));
            Assert.True(File.Exists(Path.Combine(source, "b.txt")));
            // The orphaned partial destination was removed.
            Assert.False(Directory.Exists(dest));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
