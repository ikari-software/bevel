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

        AppleEventInbound.Handler = request => _ = DispatchAsync(automation, resolver, known, request);
        AppleEventInbound.Install();
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
                    await automation.MoveAsync(items, destination, default);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[apple-event] {request.Verb} failed: {ex.Message}");
        }
    }

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
