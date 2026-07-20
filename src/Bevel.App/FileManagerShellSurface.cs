using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.Interop;

namespace Bevel.App;

/// <summary>
/// The live-file-manager implementation of the command model's window seam (IShellSurface,
/// 08-os-interop.md §3.1 / M4-B). Every operation is marshalled onto the UI thread and addresses
/// windows by the stable ids in <see cref="FileManagerWindowRegistry"/>. The filesystem verbs
/// (make/delete/duplicate/query-application) never reach here — they run in
/// <see cref="ShellAutomation"/> against the VFS; only the window-coupled verbs do.
///
/// <para>v1 note: <c>reveal</c>/<c>select</c> set the controller-level selection (what <c>get
/// selection</c> reads back) and focus the window; pushing that selection into the on-screen
/// ItemView highlight is a follow-up (the ItemView selection sync is view→model today).</para>
/// </summary>
public sealed class FileManagerShellSurface : IShellSurface
{
    private readonly FileManagerWindowFactory _factory;
    private readonly FileManagerWindowRegistry _registry;

    public FileManagerShellSurface(FileManagerWindowFactory factory, FileManagerWindowRegistry registry)
    {
        _factory = factory;
        _registry = registry;
    }

    public Task<WindowRef> OpenAsync(VfsPath container, ViewMode? view, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = _factory.Create(container);
            window.Activate();
            return RefFor(window);
        }).GetTask();

    public Task<WindowRef> RevealAsync(IReadOnlyList<VfsPath> items, bool newWindow, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var container = items.Count > 0 ? items[0].Parent : VfsPath.Root("file");
            FileManagerWindow window;
            if (newWindow || _registry.First() is not { } existing)
            {
                window = _factory.Create(container);
            }
            else
            {
                window = existing;
                window.ActiveController?.NavigateTo(container);
            }

            window.ActiveController?.SetSelection(items);
            window.SelectAfterLoad(items);   // highlight in the view once the folder loads (bevel-nwo)
            window.Activate();
            return RefFor(window);
        }).GetTask();

    public Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_registry.TryGet(window.Id, out var w))
                throw new AutomationException($"window id {window.Id} is not open.");
            w.ActiveController?.SetSelection(items);
            w.SelectAfterLoad(items);
        }).GetTask();

    public Task<IReadOnlyList<WindowRef>> QueryWindowsAsync(CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<WindowRef>)_registry.Ids().Select(id => new WindowRef(id)).ToArray()).GetTask();

    public Task<IReadOnlyList<VfsPath>> QuerySelectionAsync(WindowRef? window, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = window is { } wr && _registry.TryGet(wr.Id, out var byId) ? byId : _registry.First();
            return (IReadOnlyList<VfsPath>)(w?.ActiveController?.Selection.ToArray() ?? Array.Empty<VfsPath>());
        }).GetTask();

    public Task SetAsync(AutomationTarget target, AutomationProperty prop, string value, CancellationToken ct) =>
        Task.FromException(new AutomationException($"'set {prop}' is not supported in v1."));

    private WindowRef RefFor(FileManagerWindow window) => new(_registry.IdOf(window) ?? _registry.Register(window));
}
