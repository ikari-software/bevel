using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.AppleEvents;
using Bevel.Pal.MacOS;
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
        AppleEventInbound.Install();
    }

    /// <summary>Synchronously answers a get/count/exists (bevel-3i4). Reads only synchronously-available
    /// state — known folders, the window registry, the active controller's selection — plus a blocking
    /// resolve for filesystem specifiers (VFS I/O, no UI-thread round-trip, so no deadlock).
    ///
    /// <para>Deadlock-safety is NOT the same as boundedness: this runs on Avalonia's AE dispatch (UI)
    /// thread, so an unbounded <c>count of every file of folder &lt;hugeDir&gt;</c> would freeze the whole
    /// shell for the length of the directory walk (bevel-o3sa). The resolve is therefore hard-capped at
    /// <see cref="AppleEventObjectResolver.UiThreadEnumerationCap"/> entries per container, and
    /// <c>exists</c> uses the short-circuiting <see cref="AppleEventObjectResolver.ExistsAsync"/>. Net
    /// semantics under the cap: <c>count</c> returns at most the cap, <c>get</c> returns at most the cap,
    /// <c>exists</c> short-circuits on the first match (so it is exact whenever the target lies within
    /// the first cap entries — a match beyond the cap position reads as false). The async command path
    /// (DispatchAsync) stays unbounded — it doesn't run on the UI thread.</para></summary>
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
            case AeQueryKind.ResolvePaths when query.Specifier is not null:
                try
                {
                    // Bounded on the UI thread (bevel-o3sa): cap the walk, and let exists short-circuit.
                    const int cap = AppleEventObjectResolver.UiThreadEnumerationCap;
                    if (query.Op == QueryOp.Exists)
                        return new AeBool(resolver.ExistsAsync(ConvertSpec(query.Specifier), default, cap).GetAwaiter().GetResult());
                    var paths = resolver.ResolveAsync(ConvertSpec(query.Specifier), default, cap).GetAwaiter().GetResult();
                    return query.Op == QueryOp.Count
                        ? new AeCount(paths.Count)
                        : new AePaths(paths.Where(p => p.Scheme == "file").Select(p => p.Value).ToArray());
                }
                catch (AutomationException)
                {
                    // A specifier that doesn't resolve (missing folder/file) gets a DEFINED answer —
                    // exists=false / count=0 / get=empty — instead of an exception that escapes to
                    // HandleEvent and drops the reply as an empty success (REL-2 review).
                    return query.Op switch
                    {
                        QueryOp.Exists => new AeBool(false),
                        QueryOp.Count => new AeCount(0),
                        _ => new AePaths(Array.Empty<string>()),
                    };
                }
            default:
                return null;
        }
    }

    private static async Task DispatchAsync(
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
