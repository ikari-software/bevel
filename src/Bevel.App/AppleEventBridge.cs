using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.AppleEvents;
using Bevel.Interop.Cli;
using Bevel.Pal.MacOS;
using Bevel.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.App;

/// <summary>
/// Bridges inbound Apple Events (<see cref="AppleEventInbound"/>) to the automation command model
/// (<see cref="IShellAutomation"/>) — the same seam the CLI and bevel:// funnel through (INT-3 /
/// bevel-376). Literal file targets go straight through; descriptive references (object specifiers)
/// are converted to the command model's <see cref="ObjectSpecifier"/> and resolved by
/// <see cref="AppleEventObjectResolver"/>. macOS delivers on the UI thread under Avalonia's loop; a
/// fault is logged, never propagated.
/// </summary>
public static class AppleEventBridge
{
    public static void Wire(IServiceProvider services)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var automation = services.GetService<IShellAutomation>();
        var vfs = services.GetService<VfsRoot>();
        if (automation is null || vfs is null) return;

        var known = services.GetService<IKnownFolders>() ?? SystemKnownFolders.Instance;
        var resolver = new AppleEventObjectResolver(vfs, known);
        var registry = services.GetService<FileManagerWindowRegistry>();

        AppleEventInbound.Handler = request => _ = DispatchAsync(automation, resolver, known, request);
        AppleEventInbound.QueryHandler = query => Answer(query, known, registry, resolver);
        // bevel-odf: path/window/selection queries arrive on a SUSPENDED event and are answered
        // asynchronously (off the UI thread) — windows/selection via the automation query, which
        // reaches live Filers in their own process through SpawningShellSurface.
        AppleEventInbound.AsyncQueryHandler = (query, ct) => AnswerAsync(query, known, automation, resolver, ct);
        AppleEventInbound.Install();
    }

    /// <summary>Synchronously answers the INLINE get/count/exists kinds only (bevel-3i4/odf): the five
    /// property reads from synchronously-available state (known folders, version). Path resolution,
    /// window count and selection are NOT inline — the PAL suspends those events and they are answered
    /// by <see cref="AnswerAsync"/> off the UI thread, unbounded (no more UiThreadEnumerationCap
    /// truncation, no more blocking the UI thread on a directory walk).</summary>
    private static AeResult? Answer(AeQuery query, IKnownFolders known, FileManagerWindowRegistry? registry, AppleEventObjectResolver resolver)
    {
        // `exists <application property>` (home/desktop/…) is always true.
        if (query.Op == QueryOp.Exists && query.Kind != AeQueryKind.ResolvePaths)
            return new AeBool(true);

        switch (query.Kind)
        {
            case AeQueryKind.Version:
                return new AeText(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.1.0");
            case AeQueryKind.Home: return new AePath(known.Home.Value);
            case AeQueryKind.Desktop: return new AePath(known.Desktop.Value);
            case AeQueryKind.Trash: return new AePath(known.Trash.Value);
            case AeQueryKind.StartupDisk: return new AePath(known.StartupDisk.Value);
            case AeQueryKind.WindowCount:
                return new AeCount(registry?.Ids().Count ?? 0);
            case AeQueryKind.Selection:
                var selection = registry?.First()?.ActiveController?.Selection ?? Array.Empty<VfsPath>();
                return new AePaths(selection.Where(p => p.Scheme == "file").Select(p => p.Value).ToArray());
            // bevel-odf: ResolvePaths never arrives here — it is not an inline query, so the PAL
            // suspends the event and AnswerAsync resolves it unbounded off the UI thread. No
            // blocking wait remains on the UI thread.
            default:
                return null;
        }
    }

    /// <summary>bevel-odf: answers a get/count/exists on a SUSPENDED Apple Event, off the UI thread
    /// (internal for tests). Unlike <see cref="Answer"/> this is unbounded: filesystem specifiers
    /// resolve with enumerationCap 0 (the walk may be huge — that is the point of suspending), and
    /// window/selection counts come from <see cref="IShellAutomation.QueryAsync"/>, which reaches the
    /// live Filers (the local window registry is empty in the taskbar process — the multi-process
    /// finding). Missing targets still get a defined answer (exists=false / count=0 / get=empty).</summary>
    internal static async Task<AeResult?> AnswerAsync(
        AeQuery query, IKnownFolders known, IShellAutomation automation, AppleEventObjectResolver resolver,
        CancellationToken ct = default)
    {
        switch (query.Kind)
        {
            case AeQueryKind.WindowCount:
            {
                var snap = await automation.QueryAsync(AutomationQuery.Windows, ct).ConfigureAwait(false);
                return new AeCount(snap.Windows.Count);
            }
            case AeQueryKind.Selection:
            {
                var snap = await automation.QueryAsync(AutomationQuery.Selection, ct).ConfigureAwait(false);
                return new AePaths(snap.Selection.Where(p => p.Scheme == "file").Select(p => p.Value).ToArray());
            }
            case AeQueryKind.ResolvePaths when query.Specifier is not null:
                try
                {
                    if (query.Op == QueryOp.Exists)
                        return new AeBool(await resolver.ExistsAsync(ConvertSpec(query.Specifier), ct, 0).ConfigureAwait(false));
                    var paths = await resolver.ResolveAsync(ConvertSpec(query.Specifier), ct, 0).ConfigureAwait(false);
                    return query.Op == QueryOp.Count
                        ? new AeCount(paths.Count)
                        : new AePaths(paths.Where(p => p.Scheme == "file").Select(p => p.Value).ToArray());
                }
                catch (AutomationException)
                {
                    return query.Op switch
                    {
                        QueryOp.Exists => new AeBool(false),
                        QueryOp.Count => new AeCount(0),
                        _ => new AePaths(Array.Empty<string>()),
                    };
                }
            default:
                return Answer(query, known, null, resolver);   // the five inline property kinds
        }
    }

    // internal (not private) so the dispatch is unit-testable from Bevel.Taskbar.Tests (bevel-whz).
    internal static async Task DispatchAsync(
        IShellAutomation automation, AppleEventObjectResolver resolver, IKnownFolders known, AppleEventInbound.AeRequest request)
    {
        try
        {
            if (request.Verb == AppleEventInbound.Verb.Make)
            {
                var container = request.Container is null
                    ? known.Desktop   // `make new folder` with no `at` → Finder defaults to the desktop
                    : (await resolver.ResolveAsync(ConvertSpec(request.Container))).FirstOrDefault();
                await automation.MakeAsync(container, NewItemKind.Folder, request.Name, default);
                return;
            }

            if (request.Verb == AppleEventInbound.Verb.Launch)
            {
                // bevel-vs10: `launch program "<bundle id>"` — the SAME guard as bevelctl/bevel://:
                // a bundle id only, never a filesystem path (LooksLikePath is shared, public).
                var appId = request.Name;
                if (appId is not null && !AutomationCommandRouter.LooksLikePath(appId))
                    await automation.LaunchAsync(appId, default);
                else
                    Console.Error.WriteLine("[apple-event] launch needs an application bundle id, not a path.");
                return;
            }

            var items = new List<VfsPath>();
            items.AddRange(request.Paths.Select(p => new VfsPath("file", p)));
            foreach (var spec in request.Specifiers)
                items.AddRange(await resolver.ResolveAsync(ConvertSpec(spec)));
            if (items.Count == 0) return;

            switch (request.Verb)
            {
                case AppleEventInbound.Verb.Reveal:
                    await automation.RevealAsync(items, new RevealOptions(), default);
                    break;
                case AppleEventInbound.Verb.Delete:
                    // Destructive + no OS gate on inbound events → require the user's explicit consent
                    // before trashing files a script asked us to delete (bevel-twq).
                    if (await ConfirmDestructiveAsync("move to Trash", items.Count))
                        await automation.DeleteAsync(items, DeleteMode.Trash, default);
                    break;
                case AppleEventInbound.Verb.Duplicate:
                    await automation.DuplicateAsync(items, target: null, default);
                    break;
                case AppleEventInbound.Verb.Move:
                    if (request.Container is null)
                    {
                        Console.Error.WriteLine("[apple-event] move needs a destination.");
                        break;
                    }
                    var destination = (await resolver.ResolveAsync(ConvertSpec(request.Container))).FirstOrDefault();
                    if (await ConfirmDestructiveAsync("move", items.Count))
                        await automation.MoveAsync(items, destination, default);
                    break;
                case AppleEventInbound.Verb.Open:
                    // bevel-whz: `open <targets>` / odoc — each target opens in the filer, one call each.
                    foreach (var item in items)
                        await automation.OpenAsync(item, new OpenOptions(), default);
                    break;
                case AppleEventInbound.Verb.SetSelection:
                    // `set selection to <items>`: select them in the frontmost window.
                    var windows = await automation.QueryAsync(AutomationQuery.Windows, default);
                    if (windows.Windows.Count > 0)
                        await automation.SelectAsync(windows.Windows[0], items, default);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[apple-event] {request.Verb} failed: {ex.Message}");
        }
    }

    /// <summary>Modal, user-facing consent for an inbound destructive Apple Event (bevel-twq). Runs on
    /// the UI thread; owned by a visible Explorer window when one exists, otherwise shown ownerless.
    /// Defaults to DENY if dismissed.</summary>
    static Task<bool> ConfirmDestructiveAsync(string verb, int count)
        => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = (Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
                .Windows.FirstOrDefault(w => w.IsVisible);
            var noun = count == 1 ? "1 item" : $"{count} items";
            var dialog = new ConfirmDialog("Bevel — Automation Request",
                $"Another application is asking to {verb} {noun} via automation.\n\nAllow this?");
            return await dialog.ConfirmAsync(owner);
        });

    // ── AeSpecifier (Pal-neutral) → ObjectSpecifier (command model) ───────────────────────────

    internal static ObjectSpecifier ConvertSpec(AeSpecifier spec) => spec switch
    {
        AeProperty p => new PropertySpecifier(p.Name),
        AeByName n => new ElementByName(ClassOf(n.Class), n.Name, ConvertContainer(n.Container)),
        AeByIndex i => new ElementByIndex(ClassOf(i.Class), i.Index, ConvertContainer(i.Container)),
        AeEvery e => new EveryElement(ClassOf(e.Class), ConvertContainer(e.Container), ConvertFilter(e.Filter)),
        _ => throw new AutomationException("unsupported object specifier."),
    };

    private static ObjectSpecifier? ConvertContainer(AeSpecifier? spec) => spec is null ? null : ConvertSpec(spec);

    private static WhoseFilter? ConvertFilter(AeWhose? w) =>
        w is null ? null : new WhoseFilter(KeyOf(w.Key), OpOf(w.Op), w.Value);

    private static AeClass ClassOf(string s) => s switch
    {
        "file" => AeClass.File,
        "folder" => AeClass.Folder,
        "disk" => AeClass.Disk,
        _ => AeClass.Item,
    };

    private static WhoseKey KeyOf(string s) => s switch
    {
        "name extension" => WhoseKey.NameExtension,
        "kind" => WhoseKey.Kind,
        _ => WhoseKey.Name,
    };

    private static WhoseOp OpOf(string s) => s switch
    {
        "begins" => WhoseOp.BeginsWith,
        "ends" => WhoseOp.EndsWith,
        "contains" => WhoseOp.Contains,
        _ => WhoseOp.Equals,
    };
}
