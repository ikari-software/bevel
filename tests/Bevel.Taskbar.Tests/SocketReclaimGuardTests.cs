using System;
using System.IO;
using Bevel.App;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-zcgd (review fixes, 2026-11-22). Bevel deletes a stale socket file before binding, and a socket
/// path can come from an environment override, so two guards keep that delete from hitting anything else:
/// the path must be inside Bevel's own socket directories, and the file must look like a socket.
/// </summary>
public class SocketReclaimGuardTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "bvlguard");
    private static readonly string[] Dirs = { Path.Combine(Base, "bevel-core"), Path.Combine(Base, "bevel-filers") };

    [Theory]
    [InlineData("bevel-core/core.sock")]
    [InlineData("bevel-core/launcher.sock")]
    [InlineData("bevel-filers/filer-42.sock")]
    [InlineData("bevel-filers")]                 // FilerControlEndpoint guards the directory itself
    public void Paths_inside_the_socket_dirs_pass(string rel)
        => Assert.True(BevelRuntimeDir.IsUnderAllowedDir(Path.Combine(Base, rel), Dirs, caseInsensitive: false));

    [Theory]
    [InlineData("bevel-core-evil/core.sock")]    // sibling prefix: the original guard let this through
    [InlineData("bevel-core/../important.txt")]  // traversal out of the dir
    [InlineData("other/core.sock")]
    [InlineData("important.txt")]                // the temp root itself is not a socket dir
    public void Paths_outside_the_socket_dirs_are_rejected(string rel)
        => Assert.False(BevelRuntimeDir.IsUnderAllowedDir(Path.Combine(Base, rel), Dirs, caseInsensitive: false));

    [Fact]
    public void Relative_and_empty_paths_are_rejected()
    {
        Assert.False(BevelRuntimeDir.IsUnderAllowedDir("bevel-core/core.sock", Dirs, caseInsensitive: false));
        Assert.False(BevelRuntimeDir.IsUnderAllowedDir("", Dirs, caseInsensitive: false));
    }

    [Fact]
    public void Case_folding_is_opt_in()
    {
        var shouty = Path.Combine(Base, "BEVEL-CORE", "core.sock");
        Assert.False(BevelRuntimeDir.IsUnderAllowedDir(shouty, Dirs, caseInsensitive: false));
        Assert.True(BevelRuntimeDir.IsUnderAllowedDir(shouty, Dirs, caseInsensitive: true));
    }

    [Fact]
    public void The_real_socket_paths_pass_the_guard()
    {
        Assert.Equal(Path.Combine(BevelRuntimeDir.CoreDir, "core.sock"),
            BevelRuntimeDir.GuardSocketPathInRuntimeDir(Path.Combine(BevelRuntimeDir.CoreDir, "core.sock")));
        Assert.Throws<InvalidOperationException>(() =>
            BevelRuntimeDir.GuardSocketPathInRuntimeDir(BevelRuntimeDir.CoreDir + "-evil/core.sock"));
    }

    [Fact]
    public void Binding_never_deletes_a_file_with_content()
    {
        if (OperatingSystem.IsWindows()) return;   // the content check is Unix-only (AF_UNIX files differ there)
        var path = Path.Combine(Path.GetTempPath(), $"bvlg-{Guid.NewGuid():N}"[..14] + ".sock");
        File.WriteAllText(path, "not a socket");
        try
        {
            // Refused either by the live-owner probe (a non-socket file never answers like a dead socket)
            // or by the reclaim check: what matters is that the content survives.
            Assert.ThrowsAny<Exception>(() => UdsSocketClaim.BindListener(path, backlog: 1));
            Assert.Equal("not a socket", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
