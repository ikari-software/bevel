using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The split-only automation host (bevel-e7a7): bevelctl/bevel:// must work with NO Filer window
/// open. Covers the three seams that make that true —
/// (1) the socket host is registered for the PERSISTENT taskbar, not the on-demand Filer;
/// (2) the window verbs (open/reveal) SPAWN a Filer instead of building an in-process window;
/// (3) the filesystem verbs still run directly against the VFS from the host process.
/// </summary>
public sealed class AutomationHostRoutingTests
{
    private static VfsPath P(string p) => new("file", p);
    private static CancellationToken Ct => CancellationToken.None;

    // ── (1) DI: the socket host lives on the persistent role, never the Filer ─────────────────

    [Theory]
    [InlineData(ShellRole.Taskbar, true)]
    [InlineData(ShellRole.Filer, false)]
    [InlineData(ShellRole.Desktop, false)]
    [InlineData(ShellRole.Core, false)]
    public void Socket_host_is_registered_only_for_the_persistent_taskbar(ShellRole role, bool expected)
    {
        var services = new ServiceCollection();
        services.AddBevelModules(role);

        var hasSocketHost = services.Any(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(AutomationSocketHost));

        Assert.Equal(expected, hasSocketHost);
    }

    [Fact]
    public void Taskbar_gets_the_spawning_surface_and_Filer_the_live_one()
    {
        var taskbar = new ServiceCollection();
        taskbar.AddBevelModules(ShellRole.Taskbar);
        Assert.Equal(typeof(SpawningShellSurface), SurfaceImpl(taskbar));

        var filer = new ServiceCollection();
        filer.AddBevelModules(ShellRole.Filer);
        Assert.Equal(typeof(FileManagerShellSurface), SurfaceImpl(filer));

        static Type? SurfaceImpl(IServiceCollection s) =>
            s.Last(d => d.ServiceType == typeof(IShellSurface)).ImplementationType;
    }

    // ── (2) Window verbs spawn a Filer (no live in-process window) ───────────────────────────

    [Fact]
    public async Task Open_verb_spawns_an_filer_at_the_container()
    {
        var (router, spawner) = BuildHost();

        var res = await router.ExecuteAsync(new ParsedCommand { Verb = BevelVerb.Open, Paths = new[] { P("/tmp/dir") } }, Ct);

        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Equal("/tmp/dir", spawner.Path);
        Assert.Null(spawner.SelectPath);
    }

    [Fact]
    public async Task Reveal_verb_spawns_an_filer_at_the_parent_selecting_the_item()
    {
        var (router, spawner) = BuildHost();

        var res = await router.ExecuteAsync(new ParsedCommand { Verb = BevelVerb.Reveal, Paths = new[] { P("/tmp/dir/file.txt") } }, Ct);

        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Equal("/tmp/dir", spawner.Path);              // spawned at the parent folder
        Assert.Equal("/tmp/dir/file.txt", spawner.SelectPath); // and told to highlight the item
    }

    [Fact]
    public async Task Select_in_frontmost_reports_no_window_rather_than_silently_succeeding()
    {
        var (router, spawner) = BuildHost();

        // The persistent host has no addressable live window: query-windows is empty, so the CLI's
        // "select in the frontmost window" path returns a clear ShellNotRunning, not a no-op success.
        var res = await router.ExecuteAsync(new ParsedCommand { Verb = BevelVerb.Select, Paths = new[] { P("/tmp/x") } }, Ct);

        Assert.Equal(ExitCodes.ShellNotRunning, res.ExitCode);
        Assert.Null(spawner.Path);   // nothing spawned
    }

    // ── (3) Filesystem verbs run directly on the VFS from the host — no window, no spawn ──────────

    [Fact]
    public async Task Mkdir_runs_on_the_vfs_and_never_touches_the_spawner()
    {
        var work = System.IO.Directory.CreateTempSubdirectory("bevel-e7a7-").FullName;
        try
        {
            var (router, spawner) = BuildHost(work);

            var target = P(System.IO.Path.Combine(work, "Made"));
            var res = await router.ExecuteAsync(new ParsedCommand { Verb = BevelVerb.Mkdir, Paths = new[] { target } }, Ct);

            Assert.Equal(ExitCodes.Ok, res.ExitCode);
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(work, "Made")));
            Assert.Null(spawner.Path);   // a filesystem verb never spawns a Filer
        }
        finally { try { System.IO.Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private static (AutomationCommandRouter Router, RecordingSpawner Spawner) BuildHost(string? work = null)
    {
        var spawner = new RecordingSpawner();
        var surface = new SpawningShellSurface(spawner);
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider(trashDirectory: work ?? System.IO.Path.GetTempPath()));
        var auto = new ShellAutomation(vfs, surface);
        return (new AutomationCommandRouter(auto), spawner);
    }

    private sealed class RecordingSpawner : IFilerSpawner
    {
        public string? Path;
        public string? SelectPath;
        public bool Search;

        public void Spawn(string path, string? selectPath = null, bool search = false)
        {
            Path = path;
            SelectPath = selectPath;
            Search = search;
        }
    }
}
