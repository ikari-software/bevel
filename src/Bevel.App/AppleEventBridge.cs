using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Pal.MacOS;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.App;

/// <summary>
/// Bridges inbound Apple Events (<see cref="AppleEventInbound"/>) to the automation command model
/// (<see cref="IShellAutomation"/>) — the same seam the CLI and bevel:// funnel through (INT-3 /
/// bevel-376). macOS delivers the AE on the UI thread under Avalonia's run loop, so the automation is
/// called directly (its window verbs marshal internally). A fault is logged, never propagated.
/// </summary>
public static class AppleEventBridge
{
    public static void Wire(IServiceProvider services)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var automation = services.GetService<IShellAutomation>();
        if (automation is null) return;

        AppleEventInbound.Handler = (verb, paths) =>
        {
            var items = paths.Select(p => new VfsPath("file", p)).ToList();
            _ = DispatchAsync(automation, verb, items);
        };
        AppleEventInbound.Install();
    }

    private static async Task DispatchAsync(IShellAutomation automation, AppleEventInbound.Verb verb, IReadOnlyList<VfsPath> items)
    {
        try
        {
            switch (verb)
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
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[apple-event] {verb} failed: {ex.Message}");
        }
    }
}
