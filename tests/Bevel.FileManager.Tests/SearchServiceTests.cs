using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

public class SearchServiceTests
{
    private const string Scheme = "fake";

    private static VfsPath P(string value) => new(Scheme, value);

    private static IVfsNode Folder(string path) => new FakeNode
    {
        Path = P(path),
        DisplayName = new VfsPath(Scheme, path).FileName,
        Kind = VfsNodeKind.Folder,
        MightHaveChildren = true,
    };

    private static IVfsNode File(string path) => new FakeNode
    {
        Path = P(path),
        DisplayName = new VfsPath(Scheme, path).FileName,
        Kind = VfsNodeKind.File,
    };

    /// <summary>
    /// Builds a small fixed tree:
    /// <code>
    /// root/
    ///   fileA_match.txt                (depth 1)
    ///   dirB/                          (depth 1)
    ///     fileB_match.txt              (depth 2)
    ///     dirC/                        (depth 2)
    ///       fileC_match.txt            (depth 3)
    ///       dirD/                      (depth 3)
    ///         fileD_match.txt          (depth 4)
    ///   dirPlain/                      (depth 1, no matching descendants)
    ///     plain.txt
    /// </code>
    /// </summary>
    private static (VfsRoot Root, VfsPath RootPath) BuildTree(HashSet<VfsPath>? throwingFolders = null)
    {
        var root = P("root");
        var dirB = P("root/dirB");
        var dirC = P("root/dirB/dirC");
        var dirD = P("root/dirB/dirC/dirD");
        var dirPlain = P("root/dirPlain");

        var children = new Dictionary<VfsPath, List<IVfsNode>>
        {
            [root] = new()
            {
                File("root/fileA_match.txt"),
                Folder("root/dirB"),
                Folder("root/dirPlain"),
            },
            [dirB] = new()
            {
                File("root/dirB/fileB_match.txt"),
                Folder("root/dirB/dirC"),
            },
            [dirC] = new()
            {
                File("root/dirB/dirC/fileC_match.txt"),
                Folder("root/dirB/dirC/dirD"),
            },
            [dirD] = new()
            {
                File("root/dirB/dirC/dirD/fileD_match.txt"),
            },
            [dirPlain] = new()
            {
                File("root/dirPlain/plain.txt"),
            },
        };

        var provider = new FakeVfsProvider(children, throwingFolders ?? new HashSet<VfsPath>());
        var vfsRoot = new VfsRoot();
        vfsRoot.Register(provider);
        return (vfsRoot, root);
    }

    private static async Task<List<IVfsNode>> Drain(IAsyncEnumerable<IVfsNode> seq, CancellationToken ct = default)
    {
        var list = new List<IVfsNode>();
        await foreach (var n in seq.WithCancellation(ct))
            list.Add(n);
        return list;
    }

    [Fact]
    public async Task Finds_matches_by_substring_case_insensitively()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "MATCH"));

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Contains("match", r.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Case_insensitive_query_matches_mixed_case_name()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "filea"));

        Assert.Single(results);
        Assert.Equal("fileA_match.txt", results[0].DisplayName);
    }

    [Fact]
    public async Task No_match_returns_empty()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "doesnotexist"));

        Assert.Empty(results);
    }

    [Fact]
    public async Task Blank_query_returns_empty_without_walking()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "   "));

        Assert.Empty(results);
    }

    [Fact]
    public async Task MaxDepth_zero_only_searches_direct_children_of_root()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "match", maxDepth: 0));

        Assert.Single(results);
        Assert.Equal("fileA_match.txt", results[0].DisplayName);
    }

    [Fact]
    public async Task MaxDepth_caps_how_far_the_walk_recurses()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        // depth 1: root's children (fileA, dirB) + dirB's children (fileB, dirC) — dirC's
        // children are one level too deep and must not appear.
        var results = await Drain(svc.SearchAsync(root, "match", maxDepth: 1));

        Assert.Equal(
            new[] { "fileA_match.txt", "fileB_match.txt" },
            results.Select(r => r.DisplayName).OrderBy(x => x));
    }

    [Fact]
    public async Task Default_depth_reaches_all_four_levels_of_the_fixture()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "match"));

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.DisplayName == "fileD_match.txt");
    }

    [Fact]
    public async Task MaxResults_stops_the_walk_early()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);

        var results = await Drain(svc.SearchAsync(root, "match", maxResults: 2));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Inaccessible_folder_is_skipped_without_failing_the_whole_search()
    {
        var dirB = P("root/dirB");
        var (vfsRoot, root) = BuildTree(throwingFolders: new HashSet<VfsPath> { dirB });
        var svc = new SearchService(vfsRoot);

        // dirB (and everything under it — fileB/dirC/fileC/dirD/fileD) is unreachable, but the
        // sibling fileA at root and dirPlain's contents are still enumerable.
        var results = await Drain(svc.SearchAsync(root, "match"));

        Assert.Single(results);
        Assert.Equal("fileA_match.txt", results[0].DisplayName);
    }

    [Fact]
    public async Task Cancellation_stops_the_walk()
    {
        var (vfsRoot, root) = BuildTree();
        var svc = new SearchService(vfsRoot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Drain(svc.SearchAsync(root, "match"), cts.Token));
    }

    private sealed class FakeNode : IVfsNode
    {
        public VfsPath Path { get; init; }
        public string DisplayName { get; init; } = "";
        public VfsNodeKind Kind { get; init; }
        public bool MightHaveChildren { get; init; }
        public long? Size { get; init; }
        public DateTimeOffset? Modified { get; init; }
        public string TypeDescription { get; init; } = "";
        public IconKey IconKey { get; init; }
        public VfsCapabilities Caps { get; init; }
        public IReadOnlyDictionary<string, object?> ExtraColumns { get; init; } = new Dictionary<string, object?>();
    }

    /// <summary>Minimal in-memory IVfsProvider driven by a fixed folder->children map, with the
    /// ability to make specific folders throw on enumeration (simulating permission errors etc).</summary>
    private sealed class FakeVfsProvider : IVfsProvider
    {
        private readonly Dictionary<VfsPath, List<IVfsNode>> _children;
        private readonly HashSet<VfsPath> _throwingFolders;

        public FakeVfsProvider(Dictionary<VfsPath, List<IVfsNode>> children, HashSet<VfsPath> throwingFolders)
        {
            _children = children;
            _throwingFolders = throwingFolders;
        }

        public string Scheme => "fake";

        public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
            VfsPath folder, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            if (_throwingFolders.Contains(folder))
                throw new UnauthorizedAccessException($"Simulated access failure for {folder}");

            if (!_children.TryGetValue(folder, out var kids))
                yield break;

            foreach (var node in kids)
            {
                ct.ThrowIfCancellationRequested();
                yield return node;
                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
            => throw new NotImplementedException();

        public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
            => new((IVfsMutator?)null);

        public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;

        public NameValidationResult ValidateName(VfsPath folder, string proposedName)
            => NameValidationResult.Ok;
    }
}

/// <summary>Light construction/smoke coverage for the SearchPane control — it only needs to
/// build, expose NameQuery, and raise SearchRequested; it must never execute a search itself.</summary>
public class SearchPaneSmokeTests
{
    [AvaloniaFact]
    public void Constructs_and_shows_default_state()
    {
        var pane = new SearchPane();
        new Avalonia.Controls.Window { Content = pane, Width = 400, Height = 200 }.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, pane.NameQuery);
    }

    [AvaloniaFact]
    public void NameQuery_round_trips()
    {
        var pane = new SearchPane();
        new Avalonia.Controls.Window { Content = pane, Width = 400, Height = 200 }.Show();
        Dispatcher.UIThread.RunJobs();

        pane.NameQuery = "report";

        Assert.Equal("report", pane.NameQuery);
    }

    [AvaloniaFact]
    public void SearchRequested_fires_on_enter_with_trimmed_query()
    {
        var pane = new SearchPane();
        var w = new Avalonia.Controls.Window { Content = pane, Width = 400, Height = 200 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        string? raised = null;
        pane.SearchRequested += (_, q) => raised = q;
        pane.NameQuery = "  report  ";

        // The pane focuses NameInput on load; Enter there must raise SearchRequested with the
        // trimmed query. This is the only way the pane participates in a search — it has no
        // VfsRoot/SearchService of its own, so it is structurally incapable of executing one.
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("report", raised);
    }

    [AvaloniaFact]
    public void Blank_query_does_not_raise_SearchRequested()
    {
        var pane = new SearchPane();
        var w = new Avalonia.Controls.Window { Content = pane, Width = 400, Height = 200 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var raised = false;
        pane.SearchRequested += (_, _) => raised = true;

        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void SetResultCount_and_SetStatus_do_not_throw()
    {
        var pane = new SearchPane();
        new Avalonia.Controls.Window { Content = pane, Width = 400, Height = 200 }.Show();
        Dispatcher.UIThread.RunJobs();

        pane.SetResultCount(0);
        pane.SetResultCount(1);
        pane.SetResultCount(42);
        pane.SetStatus("Searching...");
        pane.Reset();

        Assert.Equal(string.Empty, pane.NameQuery);
    }

    [AvaloniaFact]
    public void CloseRequested_can_be_subscribed()
    {
        var pane = new SearchPane();
        new Avalonia.Controls.Window { Content = pane, Width = 400, Height = 200 }.Show();
        Dispatcher.UIThread.RunJobs();

        var fired = false;
        pane.CloseRequested += (_, _) => fired = true;

        Assert.False(fired); // construction alone must not raise it
    }
}
