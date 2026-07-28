using System;
using System.IO;
using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-brax: LocalTrash.CopyDirectory must recreate symlinks rather than follow them — a
/// directory symlink pointing at an ancestor otherwise recurses forever (stack overflow), and any dir
/// symlink copies content from outside the tree.</summary>
public class SymlinkCopyTest
{
    [Fact]
    public void CopyDirectory_recreates_symlinks_without_following_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "bevel-symlink-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "real"));
        File.WriteAllText(Path.Combine(src, "real", "a.txt"), "x");

        // A directory symlink pointing back at the source itself — following it would recurse forever.
        Directory.CreateSymbolicLink(Path.Combine(src, "loop"), src);
        // A file symlink to a target outside src.
        File.WriteAllText(Path.Combine(root, "target.txt"), "y");
        File.CreateSymbolicLink(Path.Combine(src, "link.txt"), Path.Combine(root, "target.txt"));

        var dst = Path.Combine(root, "dst");
        try
        {
            LocalTrash.CopyDirectory(src, dst);   // must terminate (no infinite recursion)

            Assert.True(File.Exists(Path.Combine(dst, "real", "a.txt")));           // real content copied
            Assert.NotNull(new DirectoryInfo(Path.Combine(dst, "loop")).LinkTarget); // dir symlink recreated, not expanded
            Assert.NotNull(new FileInfo(Path.Combine(dst, "link.txt")).LinkTarget);  // file symlink recreated, not dereferenced
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
