using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;

namespace Bevel.App;

/// <summary>
/// The parked window's handoff endpoint (bevel-t48y Task 4): the <c>--park</c> Filer's hidden
/// <see cref="FileManagerWindow"/> is handed to the launcher's <c>Show</c> request through this
/// host — the window-side half of the pre-warm. The window is built HIDDEN and UNREGISTERED (a
/// hidden window must not appear in the taskbar's window aggregation), and this host serves
/// exactly ONE handoff: after showing, it forgets the window, so a second <c>Show</c> to the same
/// (now ordinary) Filer fails cleanly instead of re-showing a stale path.
/// </summary>
public interface IParkedFilerWindowHost
{
    /// <summary>True while a hidden parked window is waiting for its handoff.</summary>
    bool HasWindow { get; }

    /// <summary>Shows the parked window at <paramref name="openPath"/> (Find mode / reveal-select
    /// when given) — marshalled to the UI thread; never blocks the transport caller.</summary>
    void Show(string openPath, bool search, string? selectPath);
}

/// <summary>Production host (Filer role only): holds the parked window from
/// <c>App.CreateFilerSurfaceCore</c> until the launcher's Show dial claims it.</summary>
public sealed class ParkedFilerWindowHost : IParkedFilerWindowHost
{
    private readonly FileManagerWindowRegistry _registry;
    // Volatile: written on the UI thread at park build, read on a transport thread per Show dial.
    private FileManagerWindow? _window;

    public ParkedFilerWindowHost(FileManagerWindowRegistry registry) => _registry = registry;

    public bool HasWindow => Volatile.Read(ref _window) is not null;

    public void SetWindow(FileManagerWindow window) => Volatile.Write(ref _window, window);

    public void Show(string openPath, bool search, string? selectPath)
    {
        var window = Volatile.Read(ref _window);
        if (window is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // The handoff is the window's FIRST registration — the taskbar's aggregation (and the
            // automation surface) only ever saw filers with a visible window, so a parked window
            // never showed up as a phantom window the user can't see.
            _registry.Register(window);
            window.ActiveController?.NavigateTo(new VfsPath("file", openPath));
            if (search) window.BeginSearch();                                   // bevel-x6pv
            if (!string.IsNullOrEmpty(selectPath))
                window.SelectAfterLoad(new[] { new VfsPath("file", selectPath) }); // bevel-e7a7
            window.Show();
            window.Activate();
            Volatile.Write(ref _window, null); // handed off — one handoff per park
        });
    }
}
