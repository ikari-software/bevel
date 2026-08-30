using System.Linq;
using Bevel.App.ShellCore;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Round-trips the shell-core wire contract (bevel-gww.3): the core->UI event envelope and the
/// UI->core command/response, including the DTOs they carry (icon bytes, window bounds). A silent
/// serialization break here would desync every UI process from the owner's state.
/// </summary>
public sealed class CoreProtocolTests
{
    private static readonly ForeignWindow SampleWindow = new(
        new ForeignWindowId("cg-1618"),
        Title: "Documents",
        AppId: "com.apple.finder",
        IsMinimized: false,
        IsFocused: true,
        Bounds: new PalRect(10, 20, 800, 600),
        IconPng: new byte[] { 1, 2, 3, 4, 250, 251, 252 });

    private static T RoundTrip<T>(T value) => CoreProtocol.Deserialize<T>(CoreProtocol.Serialize(value));

    [Fact]
    public void Window_snapshot_event_round_trips_with_icon_bytes()
    {
        var evt = new CoreEvent(CoreEventKind.WindowSnapshot, Windows: new[] { SampleWindow });

        var back = RoundTrip(evt);

        Assert.Equal(CoreEventKind.WindowSnapshot, back.Kind);
        var w = Assert.Single(back.Windows!);
        Assert.Equal("cg-1618", w.Id.Value);
        Assert.Equal("Documents", w.Title);
        Assert.Equal("com.apple.finder", w.AppId);
        Assert.True(w.IsFocused);
        Assert.Equal(new PalRect(10, 20, 800, 600), w.Bounds);
        Assert.Equal(SampleWindow.IconPng, w.IconPng); // byte[] survives (base64)
    }

    [Fact]
    public void Window_delta_event_round_trips_a_single_window()
    {
        var back = RoundTrip(new CoreEvent(CoreEventKind.WindowForeground, Window: SampleWindow));

        Assert.Equal(CoreEventKind.WindowForeground, back.Kind);
        Assert.Null(back.Windows);
        Assert.Equal("cg-1618", back.Window!.Id.Value);
    }

    [Fact]
    public void Installed_apps_snapshot_round_trips()
    {
        var evt = new CoreEvent(CoreEventKind.InstalledAppsSnapshot, InstalledApps: new[]
        {
            new InstalledApp("com.a", "Alpha", "/Applications/Alpha.app"),
            new InstalledApp("com.b", "Beta", null),
        });

        var back = RoundTrip(evt);

        Assert.Equal(["Alpha", "Beta"], back.InstalledApps!.Select(a => a.DisplayName));
        Assert.Null(back.InstalledApps![1].IconPath);
    }

    [Fact]
    public void Command_with_bounds_round_trips()
    {
        var cmd = new CoreCommand(CoreCommandKind.Reposition, WindowId: "cg-1618", Bounds: new PalRect(0, 0, 640, 480));

        var back = RoundTrip(cmd);

        Assert.Equal(CoreCommandKind.Reposition, back.Kind);
        Assert.Equal("cg-1618", back.WindowId);
        Assert.Equal(new PalRect(0, 0, 640, 480), back.Bounds);
        Assert.Null(back.AppIdOrPath);
    }

    [Fact]
    public void RestoreAndActivate_command_round_trips(/* bevel-nxic */)
    {
        var back = RoundTrip(new CoreCommand(CoreCommandKind.RestoreAndActivate, WindowId: "cg-1618"));

        Assert.Equal(CoreCommandKind.RestoreAndActivate, back.Kind);
        Assert.Equal("cg-1618", back.WindowId);
    }

    [Fact]
    public void Response_carries_query_results_and_failure()
    {
        var ok = RoundTrip(new CoreResponse(Ok: true, RunningApps: new[] { new RunningApp("com.a", "Alpha", 42) }));
        Assert.True(ok.Ok);
        Assert.Equal(42, Assert.Single(ok.RunningApps!).ProcessId);

        var fail = RoundTrip(CoreResponse.Fail("helper unavailable"));
        Assert.False(fail.Ok);
        Assert.Equal("helper unavailable", fail.Error);
    }

    [Fact]
    public void Settings_envelopes_round_trip_json_blob_and_version(/* bevel-6nve */)
    {
        const string blob = """{ "themeId": "luna", "taskbarOpacity": 70 }""";

        // core→UI snapshot / change event
        var evt = RoundTrip(new CoreEvent(CoreEventKind.SettingsSnapshot, SettingsJson: blob, SettingsVersion: 42));
        Assert.Equal(CoreEventKind.SettingsSnapshot, evt.Kind);
        Assert.Equal(blob, evt.SettingsJson);
        Assert.Equal(42, evt.SettingsVersion);

        // UI→core apply-update command
        var cmd = RoundTrip(new CoreCommand(CoreCommandKind.ApplySettingsUpdate, SettingsPatchJson: blob));
        Assert.Equal(CoreCommandKind.ApplySettingsUpdate, cmd.Kind);
        Assert.Equal(blob, cmd.SettingsPatchJson);

        // core→UI GetSettings reply
        var resp = RoundTrip(new CoreResponse(Ok: true, SettingsJson: blob, SettingsVersion: 42));
        Assert.True(resp.Ok);
        Assert.Equal(blob, resp.SettingsJson);
        Assert.Equal(42, resp.SettingsVersion);
    }
}
