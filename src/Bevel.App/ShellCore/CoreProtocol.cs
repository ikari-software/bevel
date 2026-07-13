using System.Text.Json;
using System.Text.Json.Serialization;
using Bevel.Pal.Abstractions;

namespace Bevel.App.ShellCore;

/// <summary>
/// The wire contract between the headless shell-core owner process and the UI role processes
/// (bevel-gww.3). The core owns the single live projection of shell state — the foreign-window list
/// and the installed/running-app registries, fed by ONE subscription to the Swift helper — and
/// pushes it to every UI process, so each process doesn't re-run window enumeration / app watchers.
///
/// Two directions ride the <see cref="ShellCore.Ipc"/> transport (opaque byte frames; this layer is
/// the serialization on top):
///   • core -> UI  broadcasts: <see cref="CoreEvent"/> — a snapshot on connect, then deltas.
///   • UI -> core  requests:   <see cref="CoreCommand"/> -> <see cref="CoreResponse"/> — the window
///     actions and app launches an <c>IWindowManager</c>/<c>IAppEnvironment</c> client turns into RPCs.
///
/// Serialization is plain UTF-8 JSON: the payloads are small and low-frequency (window events, not a
/// hot loop), and JSON keeps the wire debuggable. Icon PNGs (~1 KB) ride along in the DTOs. A
/// JsonSerializerContext for NativeAOT is P7's concern; reflection-based STJ is fine under JIT/R2R.
/// </summary>
public static class CoreProtocol
{
    /// <summary>Enum-as-string (debuggable frames) + case-insensitive so a field rename on one side
    /// degrades to a null rather than a hard parse failure across a version skew.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options)
        ?? throw new InvalidOperationException($"shell-core: null {typeof(T).Name} on the wire");
}

/// <summary>Which shell-state change a <see cref="CoreEvent"/> carries.</summary>
public enum CoreEventKind
{
    /// <summary>Full window list, sent once when a UI process connects (snapshot-then-delta).</summary>
    WindowSnapshot,
    WindowOpened,
    WindowClosed,
    WindowChanged,
    WindowForeground,
    /// <summary>Full installed-app list, sent on connect and whenever the set changes.</summary>
    InstalledAppsSnapshot,
    AppLaunched,
    AppTerminated,
}

/// <summary>
/// A core-&gt;UI broadcast. One envelope with nullable payload slots (rather than a type hierarchy)
/// keeps the JSON flat and the STJ round-trip trivial; <see cref="Kind"/> says which slot is set.
/// </summary>
public sealed record CoreEvent(
    CoreEventKind Kind,
    ForeignWindow? Window = null,
    IReadOnlyList<ForeignWindow>? Windows = null,
    RunningApp? App = null,
    IReadOnlyList<InstalledApp>? InstalledApps = null);

/// <summary>Which <c>IWindowManager</c>/<c>IAppEnvironment</c> action a <see cref="CoreCommand"/> requests.</summary>
public enum CoreCommandKind
{
    EnumerateWindows,
    Activate,
    Minimize,
    Restore,
    Close,
    Reposition,
    EnumerateInstalledApps,
    GetRunningApps,
    LaunchApp,
}

/// <summary>A UI-&gt;core request. The core executes it against the real PAL and replies with a
/// <see cref="CoreResponse"/> carrying the correlation via the transport's request/response frame.</summary>
public sealed record CoreCommand(
    CoreCommandKind Kind,
    string? WindowId = null,
    PalRect? Bounds = null,
    string? AppIdOrPath = null);

/// <summary>The core's reply to a <see cref="CoreCommand"/>. <see cref="Ok"/>=false carries <see cref="Error"/>;
/// the query commands fill the matching list.</summary>
public sealed record CoreResponse(
    bool Ok,
    string? Error = null,
    IReadOnlyList<ForeignWindow>? Windows = null,
    IReadOnlyList<InstalledApp>? InstalledApps = null,
    IReadOnlyList<RunningApp>? RunningApps = null)
{
    public static CoreResponse Success() => new(Ok: true);
    public static CoreResponse Fail(string error) => new(Ok: false, Error: error);
}
