using Bevel.App;
using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

public sealed class RoleHeartbeatTests : IDisposable
{
    public RoleHeartbeatTests() => RoleHeartbeatStore.ClearAll();
    public void Dispose() => RoleHeartbeatStore.ClearAll();

    [Fact]
    public void Write_Read_round_trips_fields_and_escaped_errors()
    {
        var hb = new RoleHeartbeat(
            ShellRole.Taskbar, Pid: 42, Stamp: "1|ver|9", Proto: BuildStamp.Protocol,
            HeartbeatStatus.Failed, CoreConnected: false,
            Error: "line1\nquote \" and \\ slash");
        RoleHeartbeatStore.Write(hb);

        var read = RoleHeartbeatStore.Read(ShellRole.Taskbar);
        Assert.True(read.HasValue);
        Assert.Equal(ShellRole.Taskbar, read.Value.Role);
        Assert.Equal(42, read.Value.Pid);
        Assert.Equal("1|ver|9", read.Value.Stamp);
        Assert.Equal(HeartbeatStatus.Failed, read.Value.Status);
        Assert.False(read.Value.CoreConnected);
        Assert.Equal("line1\nquote \" and \\ slash", read.Value.Error);
        Assert.False(read.Value.Stale);
    }

    [Fact]
    public void Parse_rejects_a_file_with_no_role()
    {
        Assert.Null(RoleHeartbeatStore.Parse("pid=1\nstatus=ready\n"));
    }

    [Fact]
    public void Clear_removes_one_role_without_touching_another()
    {
        RoleHeartbeatStore.Write(new RoleHeartbeat(ShellRole.Core, 1, "s", 1, HeartbeatStatus.Ready, true, ""));
        RoleHeartbeatStore.Write(new RoleHeartbeat(ShellRole.Taskbar, 2, "s", 1, HeartbeatStatus.Ready, true, ""));
        RoleHeartbeatStore.Clear(ShellRole.Core);
        Assert.Null(RoleHeartbeatStore.Read(ShellRole.Core));
        Assert.NotNull(RoleHeartbeatStore.Read(ShellRole.Taskbar));
    }

    [Fact]
    public void Format_message_names_the_fault_and_the_hold()
    {
        var text = ShellHealthAlert.Format(new HealthVerdict(
            ShellRole.Taskbar, HealthAction.Hold, HealthFault.StartupFailed, "XamlLoadException"));
        Assert.Contains("Taskbar", text, StringComparison.Ordinal);
        Assert.Contains("StartupFailed", text, StringComparison.Ordinal);
        Assert.Contains("XamlLoadException", text, StringComparison.Ordinal);
        Assert.Contains("stopped restarting", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_hold_writes_status_file_and_invokes_native_once()
    {
        var calls = 0;
        ShellHealthAlert.Show(
            new HealthVerdict(ShellRole.Core, HealthAction.Hold, HealthFault.CrashLoop, "died 3 times"),
            native: (_, _) => Interlocked.Increment(ref calls));
        Assert.True(File.Exists(ShellHealthAlert.StatusPath));
        var status = File.ReadAllText(ShellHealthAlert.StatusPath);
        Assert.Contains("CrashLoop", status, StringComparison.Ordinal);
        // Native is fire-and-forget on the thread pool.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (Volatile.Read(ref calls) == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Show_restart_does_not_pop_a_dialog()
    {
        var calls = 0;
        ShellHealthAlert.Show(
            new HealthVerdict(ShellRole.Core, HealthAction.Restart, HealthFault.VersionSkew, "stamp"),
            native: (_, _) => Interlocked.Increment(ref calls));
        Thread.Sleep(50);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Current_stamp_includes_protocol_and_is_stable()
    {
        var a = BuildStamp.Current();
        var b = BuildStamp.Current();
        Assert.StartsWith(BuildStamp.Protocol + "|", a, StringComparison.Ordinal);
        Assert.Equal(a, b);
        Assert.Equal("BEVEL_BUILD_STAMP", BuildStamp.EnvVar);
    }
}
