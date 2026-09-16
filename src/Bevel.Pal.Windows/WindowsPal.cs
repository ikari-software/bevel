using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

// Windows PAL shared bootstrap idiom (bevel-ncfp.1 / U1). Same shape as Bevel.Pal.MacOS's M0: the
// signatures name the shell's needs; every real Win32 mechanism lands per later unit (U2-U12). Each
// capability now lives in its own file (Windows<Surface>.cs) so units land independently. The small
// remaining surfaces (permission broker, audio) live here.

internal static class NotYet
{
    public const string Message =
        "Bevel.Pal.Windows is a bootstrap stub (bevel-ncfp.1). Native Win32 integration lands per unit " +
        "(U2-U12). Run with --pal=fake for the demo scaffold.";

    public static Capabilities Unavailable { get; } = Capabilities.None with
    {
        Notes = new[] { "windows-pal: not implemented (bootstrap stub)" },
    };
}

/// <summary>Windows has no TCC (U9 / bevel-ncfp.9): every shell permission is NotApplicable — the UI
/// treats that as "nothing to request", and UAC elevation is handled per-action where actually needed.</summary>
public sealed class WindowsPermissionBroker : IPermissionBroker
{
    public ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default)
        => ValueTask.FromResult(PermissionState.NotApplicable);

    public Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default)
        => Task.FromResult(PermissionState.NotApplicable);

    public event EventHandler<ShellPermission>? PermissionChanged;
}

/// <summary>U10 (bevel-ncfp.10): browser live-tab enumeration is UIAutomation-shaped on Windows and
/// deferred for v1 (the favicon-DB reading is already portable). Reports supporting nothing, so the
/// taskbar's Tabs submenu simply never appears — never an error.</summary>
public sealed class WindowsTabProvider : ITabProvider
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public bool SupportsApp(string? bundleId) => false;

    public ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AppTab>>(Array.Empty<AppTab>());

    public Task ActivateAsync(AppTab tab, CancellationToken ct = default) => Task.CompletedTask;
}
