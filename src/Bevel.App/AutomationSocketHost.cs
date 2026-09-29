using System;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Interop.Cli;
using Microsoft.Extensions.Hosting;

namespace Bevel.App;

/// <summary>
/// Hosts the bevelctl socket (08-os-interop.md §3.2) in the process that owns the live file manager,
/// dispatching each connection's argv through BevelCtlParser → <see cref="AutomationCommandRouter"/>
/// → the one <see cref="Bevel.Interop.IShellAutomation"/> seam. Registered only for the FM-hosting
/// roles (All / Filer); a bind failure is logged, never fatal (a second instance simply doesn't
/// own the socket).
/// </summary>
public sealed class AutomationSocketHost : IHostedService, IAsyncDisposable
{
    private readonly AutomationCommandRouter _router;
    private AutomationSocketServer? _server;

    public AutomationSocketHost(AutomationCommandRouter router) => _router = router;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _server = new AutomationSocketServer(AutomationSocket.DefaultPath, async (args, ct) =>
            {
                var (command, error) = BevelCtlParser.Parse(args);
                return error is not null
                    ? new CommandResult(ExitCodes.BadArgs, error)
                    : await _router.ExecuteAsync(command!, ct);
            });
            _server.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bevelctl] socket unavailable, CLI disabled this run: {ex.Message}");
            _server = null;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.DisposeAsync();
        _server = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
    }
}
