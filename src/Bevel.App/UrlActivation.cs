using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Bevel.Interop.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.App;

/// <summary>
/// Delivers <c>bevel://</c> URLs to the automation command model (08-os-interop §3.3 / M4-D.2,
/// bevel-6dc). macOS routes a <c>bevel://</c> open to the running app as an <c>OpenUri</c> activation
/// — Avalonia surfaces it through <see cref="IActivatableLifetime"/>, backed by the
/// <c>CFBundleURLTypes</c> declaration in Info.plist. We parse it with the same
/// <see cref="BevelUrlParser"/> the CLI uses and run it through the same
/// <see cref="AutomationCommandRouter"/> (INT-3), in-process — URL events are delivered to this
/// process directly, so there's no socket hop.
/// </summary>
public static class UrlActivation
{
    public static void Wire(Application app, IServiceProvider services)
    {
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime activatable) return;
        var router = services.GetService<AutomationCommandRouter>();
        if (router is null) return;

        activatable.Activated += async (_, e) =>
        {
            if (e is not ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } activation) return;

            var (command, error) = BevelUrlParser.Parse(activation.Uri);
            if (command is null)
            {
                Console.Error.WriteLine($"[bevel://] {error}");
                return;
            }

            try
            {
                var result = await router.ExecuteAsync(command);
                if (result.ExitCode != ExitCodes.Ok)
                    Console.Error.WriteLine($"[bevel://] {result.Output}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bevel://] {ex.Message}");
            }
        };
    }
}
