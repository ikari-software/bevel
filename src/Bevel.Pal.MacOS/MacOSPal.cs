using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

// macOS PAL stubs. Real implementations arrive per milestone:
//   - IWindowManager / ISystemTrayHost  -> BevelHelper (Swift) over gRPC/UDS (M2/M3)
//   - IDesktopEnvironment / IShellSession -> in-proc AppKit + `defaults` (M1/M2)
//   - IFileOperations / IIconProvider     -> in-proc copyfile(3) / NSWorkspace (M1)
// Until then every surface reports "unavailable" and throws on action, so wiring
// the MacOS PAL at the composition root compiles and fails loudly, never silently.

internal static class NotYet
{
    public const string Message =
        "Bevel.Pal.MacOS is an M0 stub. Native macOS integration lands in M1+. " +
        "Run with --pal=fake for the bootable scaffold.";

    public static Capabilities Unavailable { get; } = Capabilities.None with
    {
        Notes = new[] { "macos-pal: not implemented (M0 stub)" },
    };
}

public sealed class MacOSSystemTrayHost : ISystemTrayHost
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());

    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;
}

public sealed class MacOSShellSession : IShellSession
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task RegisterAsShellAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task UnregisterAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task LogOutAsync(LogoutKind kind, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler? SessionChanged;
}

public sealed class MacOSFileOperations : IFileOperations
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RenameAsync(string path, string newName, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
}

public sealed class MacOSAudioPlayback : IAudioPlayback
{
    public Capabilities Capabilities => NotYet.Unavailable;
    public bool Muted { get; set; }

    public Task PlayAsync(string wavPath, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
}
