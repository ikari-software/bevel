using Bevel.App.Supervision;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-9790: the launcher decides "another Bevel is already serving" BEFORE it spawns any child.
/// <see cref="IncumbentProbe"/> is that decision over real Unix sockets in a temp dir (short paths —
/// macOS <c>sun_path</c> is 104 bytes).
/// </summary>
public sealed class IncumbentProbeTests
{
    private static string NewSocketPath()
        => Path.Combine(Path.GetTempPath(), $"bvlinc-{Guid.NewGuid():N}"[..15] + ".sock");

    [Fact]
    public void A_live_listener_is_reported()
    {
        var path = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(path, backlog: 4);
        try
        {
            Assert.Equal(path, IncumbentProbe.FirstLiveSocket(path));
        }
        finally { UdsSocketClaim.ReleaseListener(listener, path); }
    }

    [Fact]
    public void A_stale_socket_file_is_not_reported()
    {
        var path = NewSocketPath();
        // Disposing a raw bound listener leaves the file behind: what a crashed owner leaves.
        UdsSocketClaim.BindListener(path, backlog: 4).Dispose();
        try
        {
            Assert.True(File.Exists(path));
            Assert.Null(IncumbentProbe.FirstLiveSocket(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_path_is_not_reported()
    {
        Assert.Null(IncumbentProbe.FirstLiveSocket(NewSocketPath()));
    }

    [Fact]
    public void The_first_live_path_wins_when_several_are_given()
    {
        var stale = NewSocketPath();
        UdsSocketClaim.BindListener(stale, backlog: 4).Dispose();
        var live = NewSocketPath();
        using var listener = UdsSocketClaim.BindListener(live, backlog: 4);
        try
        {
            Assert.Equal(live, IncumbentProbe.FirstLiveSocket(NewSocketPath(), stale, live));
        }
        finally { UdsSocketClaim.ReleaseListener(listener, live); File.Delete(stale); }
    }
}
