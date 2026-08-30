using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.Cli;
using Xunit;

namespace Bevel.Core.Tests.Interop;

/// <summary>
/// The router is where bevelctl and bevel:// converge on IShellAutomation (INT-3). These drive it
/// with a recording fake so they assert the exact seam call + the rendered output/exit code.
/// </summary>
public class AutomationCommandRouterTests
{
    private readonly RecordingAutomation _auto = new();
    private AutomationCommandRouter Router => new(_auto);

    private static VfsPath P(string p) => new("file", p);
    private Task<CommandResult> Run(ParsedCommand cmd) => Router.ExecuteAsync(cmd, CancellationToken.None);

    [Fact]
    public async Task Reveal_calls_seam_and_reports()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Reveal, Paths = new[] { P("/a"), P("/b") }, NewWindow = true });
        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Equal("reveal:2:True", Assert.Single(_auto.Calls));
        Assert.Contains("revealed 2 item(s) in window 7", res.Output);
    }

    [Fact]
    public async Task Reveal_json()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Reveal, Paths = new[] { P("/a") }, Json = true });
        Assert.Equal("{\"window\":7,\"selected\":1}", res.Output);
    }

    [Fact]
    public async Task Mkdir_maps_to_make_folder_at_parent()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Mkdir, Paths = new[] { P("/tmp/New") } });
        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Equal("make:/tmp:Folder:New", Assert.Single(_auto.Calls));
    }

    [Fact]
    public async Task Delete_default_is_trash()
    {
        await Run(new ParsedCommand { Verb = BevelVerb.Delete, Paths = new[] { P("/x") } });
        Assert.Equal("delete:1:Trash", Assert.Single(_auto.Calls));
    }

    [Fact]
    public async Task Delete_permanent()
    {
        await Run(new ParsedCommand { Verb = BevelVerb.Delete, Paths = new[] { P("/x") }, Permanent = true });
        Assert.Equal("delete:1:Permanent", Assert.Single(_auto.Calls));
    }

    [Fact]
    public async Task Duplicate_reports_new_paths()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Duplicate, Paths = new[] { P("/x.txt") } });
        Assert.Equal("/x.txt copy", res.Output);
    }

    [Fact]
    public async Task Move_uses_destination()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Move, Paths = new[] { P("/x") }, Target = new VfsPath("file", "/dst") });
        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Contains("move:1:/dst", _auto.Calls);
    }

    [Fact]
    public async Task Move_without_destination_is_bad_args()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Move, Paths = new[] { P("/x") } });
        Assert.Equal(ExitCodes.BadArgs, res.ExitCode);
    }

    [Fact]
    public async Task Query_version()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Version });
        Assert.Equal("9.9", res.Output);
    }

    [Fact]
    public async Task Query_windows_json()
    {
        _auto.Windows = new[] { new WindowRef(1), new WindowRef(4) };
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Windows, Json = true });
        Assert.Equal("{\"windows\":[1,4]}", res.Output);
    }

    [Fact]
    public async Task Select_without_windows_reports_shell_not_running()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Select, Paths = new[] { P("/x") } });
        Assert.Equal(ExitCodes.ShellNotRunning, res.ExitCode);
    }

    [Fact]
    public async Task Select_targets_frontmost_window()
    {
        _auto.Windows = new[] { new WindowRef(5) };
        await Run(new ParsedCommand { Verb = BevelVerb.Select, Paths = new[] { P("/x"), P("/y") } });
        Assert.Contains("select:5:2", _auto.Calls);
    }

    [Fact]
    public async Task Automation_fault_becomes_not_found()
    {
        _auto.Throw = true;
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Reveal, Paths = new[] { P("/x") } });
        Assert.Equal(ExitCodes.NotFound, res.ExitCode);
        Assert.Contains("read-only", res.Output);
    }

    [Fact]
    public async Task Launch_calls_seam_and_reports()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Launch, AppId = "com.apple.TextEdit" });
        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Equal("launch:com.apple.TextEdit", Assert.Single(_auto.Calls));
        Assert.Contains("launched com.apple.TextEdit", res.Output);
    }

    [Fact]
    public async Task Launch_without_appid_is_bad_args_and_does_not_hit_the_seam()
    {
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Launch });
        Assert.Equal(ExitCodes.BadArgs, res.ExitCode);
        Assert.Empty(_auto.Calls);
    }

    [Theory]
    [InlineData("/Applications/Evil.app")]     // absolute path
    [InlineData("/tmp/planted.app")]
    [InlineData("Evil.app")]                    // relative .app
    [InlineData("~/Downloads/x.app")]           // tilde
    [InlineData("../../Applications/Safari.app")]
    [InlineData("some/relative/path")]
    public async Task Launch_refuses_a_filesystem_path_and_does_not_hit_the_seam(string target)
    {
        // bevel-318a: the web-reachable bevel://launch must not LSOpen an arbitrary rooted path —
        // only a bundle id (LaunchServices resolves those against installed apps).
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Launch, AppId = target });
        Assert.Equal(ExitCodes.BadArgs, res.ExitCode);
        Assert.Empty(_auto.Calls);   // never reached the launcher
    }

    [Fact]
    public async Task Query_programs_lists_ids_and_names()
    {
        _auto.Programs = new[] { new ProgramInfo("com.a.One", "One"), new ProgramInfo("com.b.Two", "Two") };
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Programs });
        Assert.Equal(ExitCodes.Ok, res.ExitCode);
        Assert.Equal("query:Programs", Assert.Single(_auto.Calls));
        Assert.Contains("com.a.One", res.Output);
        Assert.Contains("Two", res.Output);
    }

    [Fact]
    public async Task Query_programs_json_emits_id_array()
    {
        _auto.Programs = new[] { new ProgramInfo("com.a.One", "One") };
        var res = await Run(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Programs, Json = true });
        Assert.Equal("{\"programs\":[\"com.a.One\"]}", res.Output);
    }

    private sealed class RecordingAutomation : IShellAutomation
    {
        public readonly List<string> Calls = new();
        public bool Throw;
        public IReadOnlyList<WindowRef> Windows = Array.Empty<WindowRef>();
        public IReadOnlyList<VfsPath> Selection = Array.Empty<VfsPath>();
        public IReadOnlyList<ProgramInfo> Programs = Array.Empty<ProgramInfo>();

        public Task<RevealResult> RevealAsync(IReadOnlyList<VfsPath> items, RevealOptions opts, CancellationToken ct)
        {
            Calls.Add($"reveal:{items.Count}:{opts.NewWindow}");
            if (Throw) throw new AutomationException("that location is read-only.");
            return Task.FromResult(new RevealResult(new WindowRef(7), items.Count));
        }

        public Task<WindowRef> OpenAsync(VfsPath c, OpenOptions o, CancellationToken ct)
        {
            Calls.Add($"open:{c.Value}:{o.View}");
            return Task.FromResult(new WindowRef(3));
        }

        public Task SelectAsync(WindowRef w, IReadOnlyList<VfsPath> items, CancellationToken ct)
        {
            Calls.Add($"select:{w.Id}:{items.Count}");
            return Task.CompletedTask;
        }

        public Task<VfsPath> MakeAsync(VfsPath parent, NewItemKind kind, string? name, CancellationToken ct)
        {
            Calls.Add($"make:{parent.Value}:{kind}:{name}");
            return Task.FromResult(new VfsPath("file", Path.Combine(parent.Value, name ?? "untitled folder")));
        }

        public Task DeleteAsync(IReadOnlyList<VfsPath> items, DeleteMode mode, CancellationToken ct)
        {
            Calls.Add($"delete:{items.Count}:{mode}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VfsPath>> DuplicateAsync(IReadOnlyList<VfsPath> items, VfsPath? target, CancellationToken ct)
        {
            Calls.Add($"dup:{items.Count}:{target?.Value ?? "null"}");
            return Task.FromResult<IReadOnlyList<VfsPath>>(items.Select(i => new VfsPath("file", i.Value + " copy")).ToArray());
        }

        public Task<IReadOnlyList<VfsPath>> MoveAsync(IReadOnlyList<VfsPath> items, VfsPath destination, CancellationToken ct)
        {
            Calls.Add($"move:{items.Count}:{destination.Value}");
            return Task.FromResult<IReadOnlyList<VfsPath>>(
                items.Select(i => new VfsPath("file", $"{destination.Value}/{i.FileName}")).ToArray());
        }

        public Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct)
        {
            Calls.Add($"query:{query}");
            return Task.FromResult(new BevelStateSnapshot { Version = "9.9", Windows = Windows, Selection = Selection, Programs = Programs });
        }

        public Task SetAsync(AutomationTarget t, AutomationProperty p, string v, CancellationToken ct)
        {
            Calls.Add("set");
            return Task.CompletedTask;
        }

        public Task LaunchAsync(string appId, CancellationToken ct)
        {
            Calls.Add($"launch:{appId}");
            return Task.CompletedTask;
        }
    }
}
