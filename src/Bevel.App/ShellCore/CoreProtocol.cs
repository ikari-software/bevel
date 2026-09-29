using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
/// hot loop), and JSON keeps the wire debuggable. Icon PNGs (~1 KB) ride along in the DTOs. It is
/// source-generated via <see cref="CoreJsonContext"/> (reflection-free, so it survives NativeAOT —
/// bevel-gww.7); enum-as-string + case-insensitive keep the frames debuggable and version-skew-tolerant.
/// </summary>
public static class CoreProtocol
{
    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TypeInfoFor(typeof(T)));

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        (T?)JsonSerializer.Deserialize(utf8, TypeInfoFor(typeof(T)))
        ?? throw new InvalidOperationException($"shell-core: null {typeof(T).Name} on the wire");

    private static JsonTypeInfo TypeInfoFor(Type type) =>
        CoreJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"shell-core: {type.Name} is not registered in CoreJsonContext");
}

/// <summary>Source-generated JSON metadata for the shell-core wire types (bevel-gww.7). Registering the
/// three envelopes pulls in their whole payload graph (ForeignWindow / RunningApp / InstalledApp /
/// PalRect); <c>UseStringEnumConverter</c> keeps enums as debuggable strings without a reflection
/// converter.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CoreEvent))]
[JsonSerializable(typeof(CoreCommand))]
[JsonSerializable(typeof(CoreResponse))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

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
    /// <summary>Full mirrored tray-item list, sent once on connect (snapshot-then-delta, bevel-m3.1.1).</summary>
    TraySnapshot,
    TrayItemAdded,
    TrayItemRemoved,
    TrayItemUpdated,
    /// <summary>Full settings blob, pushed once when a UI process connects (core-owns-settings, bevel-6nve).
    /// Carries <see cref="CoreEvent.SettingsJson"/> + <see cref="CoreEvent.SettingsVersion"/>.</summary>
    SettingsSnapshot,
    /// <summary>Settings changed in the core (its own write, or an applied UI update); pushed to every UI
    /// process so peers re-project without opening the DB. Same payload slots as the snapshot.</summary>
    SettingsChanged,
}

// ── Session protocol (bevel-4zfs) ─────────────────────────────────────────
// The core pushes NOTHING on connect. A client that has wired all its subscribers sends Hello; the
// core replies, then pushes the four stamped snapshots to that client alone. Every server→client event
// carries (Epoch, Seq): the epoch bumps whenever the core's projection is rebuilt discontinuously (the
// startup seed), and seq is gapless within an epoch — a client that sees an epoch change or a seq gap
// knows it missed something and re-issues Hello instead of silently going stale. This deletes the
// whole startup-race class in one place: the snapshot can no longer be "eaten" by a too-early connect
// (the client owns the session start), the seed's no-broadcast hole becomes the epoch bump, and the
// tray gains a pull backstop (GetTrayItems).

/// <summary>
/// A core-&gt;UI broadcast. One envelope with nullable payload slots (rather than a type hierarchy)
/// keeps the JSON flat and the STJ round-trip trivial; <see cref="Kind"/> says which slot is set.
/// </summary>
public sealed record CoreEvent(
    CoreEventKind Kind,
    ForeignWindow? Window = null,
    IReadOnlyList<ForeignWindow>? Windows = null,
    RunningApp? App = null,
    IReadOnlyList<InstalledApp>? InstalledApps = null,
    TrayItem? TrayItem = null,
    IReadOnlyList<TrayItem>? TrayItems = null,
    /// <summary>SettingsSnapshot / SettingsChanged: the canonical settings JSON blob (bevel-6nve).</summary>
    string? SettingsJson = null,
    /// <summary>SettingsSnapshot / SettingsChanged: the settings store version the blob was read at.</summary>
    int? SettingsVersion = null,
    /// <summary>Session stamp (bevel-4zfs): which projection generation this event belongs to. The epoch
    /// bumps on every discontinuous rebuild (startup seed completion); a client seeing a new epoch adopts
    /// the accompanying snapshot burst as its fresh baseline.</summary>
    int? Epoch = null,
    /// <summary>Session stamp (bevel-4zfs): gapless within an epoch. A gap means a lost frame — the client
    /// re-issues Hello (rate-limited) rather than diverging silently.</summary>
    long? Seq = null);

/// <summary>Which <c>IWindowManager</c>/<c>IAppEnvironment</c> action a <see cref="CoreCommand"/> requests.</summary>
public enum CoreCommandKind
{
    EnumerateWindows,
    Activate,
    Minimize,
    Restore,
    /// <summary>Atomic restore-if-minimized + activate (bevel-nxic): one core round-trip, one helper op.</summary>
    RestoreAndActivate,
    Close,
    Reposition,
    EnumerateInstalledApps,
    GetRunningApps,
    LaunchApp,
    ForwardTrayClick,
    CaptureWindow,
    TerminateApp,
    /// <summary>UI→core: fetch the current settings blob (the on-connect bootstrap, bevel-6nve). The reply
    /// carries <see cref="CoreResponse.SettingsJson"/> + <see cref="CoreResponse.SettingsVersion"/>.</summary>
    GetSettings,
    /// <summary>UI→core: apply a changed-keys merge patch to settings; the core is the sole writer. The
    /// patch rides <see cref="CoreCommand.SettingsPatchJson"/>.</summary>
    ApplySettingsUpdate,
    /// <summary>UI→core: menu-bar consolidation state changed (bevel-qpir). The native hide itself is
    /// applied IN the UI process (it needs a serviced AppKit run loop — the core is headless); the core
    /// just forwards the state to the Swift helper so its tray-poll cadence adapts (900ms consolidated vs
    /// 2s idle). The state rides <see cref="CoreCommand.Hidden"/>.</summary>
    SetTrayHidden,
    /// <summary>UI→core: session start (bevel-4zfs). Sent once the client has attached all its subscribers;
    /// the core replies, then pushes the four stamped snapshots to this client alone.</summary>
    Hello,
    /// <summary>UI→core: re-push the stamped snapshots to this client (bevel-4zfs) — a late-attaching
    /// adapter or a detected sequence gap asks for a fresh baseline.</summary>
    Resync,
    /// <summary>UI→core: pull the mirrored tray items (bevel-4zfs) — the tray's backstop; every other
    /// projection already had a pull (EnumerateWindows / EnumerateInstalledApps / GetSettings).</summary>
    GetTrayItems,
}

/// <summary>A UI-&gt;core request. The core executes it against the real PAL and replies with a
/// <see cref="CoreResponse"/> carrying the correlation via the transport's request/response frame.</summary>
public sealed record CoreCommand(
    CoreCommandKind Kind,
    string? WindowId = null,
    PalRect? Bounds = null,
    string? AppIdOrPath = null,
    string? TrayItemId = null,
    TrayButton? TrayButton = null,
    TrayModifiers? TrayModifiers = null,
    int? MaxWidth = null,
    int? MaxHeight = null,
    /// <summary>TerminateApp: force-quit (true) vs graceful quit (false). Bundle id rides AppIdOrPath.</summary>
    bool Force = false,
    /// <summary>ApplySettingsUpdate: the changed-keys merge patch (top-level settings keys) to apply (bevel-6nve).</summary>
    string? SettingsPatchJson = null,
    /// <summary>ForwardTrayClick: single-item reveal — the target is hidden off-screen by consolidation and
    /// must be parked + pressed rather than clicked in place (bevel-6fin).</summary>
    bool Park = false,
    /// <summary>SetTrayHidden: the new consolidation state (true = the native menu bar is hidden).</summary>
    bool Hidden = false);

/// <summary>The core's reply to a <see cref="CoreCommand"/>. <see cref="Ok"/>=false carries <see cref="Error"/>;
/// the query commands fill the matching list.</summary>
public sealed record CoreResponse(
    bool Ok,
    string? Error = null,
    IReadOnlyList<ForeignWindow>? Windows = null,
    IReadOnlyList<InstalledApp>? InstalledApps = null,
    IReadOnlyList<RunningApp>? RunningApps = null,
    bool? Delivered = null,
    byte[]? Png = null,
    /// <summary>GetSettings: the current settings JSON blob (bevel-6nve).</summary>
    string? SettingsJson = null,
    /// <summary>GetSettings: the settings store version the blob was read at.</summary>
    int? SettingsVersion = null,
    /// <summary>GetTrayItems: the core's current mirrored-tray projection (bevel-4zfs).</summary>
    IReadOnlyList<TrayItem>? TrayItems = null)
{
    public static CoreResponse Success() => new(Ok: true);
    public static CoreResponse Fail(string error) => new(Ok: false, Error: error);
}
