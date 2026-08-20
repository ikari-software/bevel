using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The launcher control channel is one command byte on the wire (bevel-gww.4 / bevel-gdie). These assert
/// the desktop-toggle verbs (bevel-gdie) survive the serialize→byte→parse the launcher performs
/// (<c>(LauncherControl.Command)payload.Span[0]</c>), and that the state probe degrades gracefully when
/// there is no launcher to reach.
/// </summary>
public sealed class LauncherControlTests
{
    [Fact]
    public void Commands_round_trip_through_the_wire_byte()
    {
        // The verbs and their fixed on-the-wire byte values (a change here is a protocol break between the
        // taskbar child and the launcher). bevel-gdie adds SpawnDesktop / CloseDesktop / QueryDesktop.
        var expected = new (LauncherControl.Command Command, byte Byte)[]
        {
            (LauncherControl.Command.RestartAll, 1),
            (LauncherControl.Command.RestartCore, 2),
            (LauncherControl.Command.Quit, 3),
            (LauncherControl.Command.SpawnDesktop, 4),
            (LauncherControl.Command.CloseDesktop, 5),
            (LauncherControl.Command.QueryDesktop, 6),
        };

        foreach (var (command, b) in expected)
        {
            // Encode exactly as TrySend does...
            var payload = new[] { (byte)command };
            Assert.Equal(b, payload[0]);
            // ...and decode exactly as the launcher's control loop does.
            Assert.Equal(command, (LauncherControl.Command)payload[0]);
        }
    }

    [Fact]
    public void QueryDesktopRunning_returns_null_when_unsupervised()
    {
        // No launcher control env in the test process → the probe must degrade to null (unknown), so the
        // Start-menu label falls back to its "Show Desktop" default instead of crashing. (If a launcher
        // env somehow leaked into the test host, skip the assertion rather than fail spuriously.)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LauncherControl.SocketEnv)))
            return;

        Assert.Null(LauncherControl.QueryDesktopRunning());
    }
}
