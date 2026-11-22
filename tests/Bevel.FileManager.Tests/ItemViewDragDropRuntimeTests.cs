using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Runtime tests for the drag &amp; drop data flow (bevel-u2x6).
///
/// <para>Unlike <see cref="ItemViewDragSourceTests"/> (which pins source structure because
/// <c>DoDragDrop</c> can't run headless), these tests exercise the REAL code paths: the
/// <see cref="ItemView.ResolveDropPaths"/> fallback logic with live <see cref="DataObject"/>
/// instances, and the <c>AllowDrop</c> flag without which the OS never routes drop events
/// to the control at all.</para>
/// </summary>
public sealed class ItemViewDragDropRuntimeTests
{
    // ── AllowDrop — the control must accept drops ────────────────────

    /// <summary>Without AllowDrop=true, Avalonia never routes DragOver/Drop to the control —
    /// every drop is silently ignored. This was the actual root cause of bevel-u2x6.</summary>
    [AvaloniaFact]
    public void ItemView_accepts_drops()
    {
        var view = new ItemView();
        var w = new Window { Content = view, Width = 400, Height = 300 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(DragDrop.GetAllowDrop(view),
            "ItemView must have AllowDrop=true, otherwise the OS never delivers drop events "
            + "and every drag-drop is a silent no-op (bevel-u2x6).");
    }

    // ── ResolveDropPaths — the drop-side fallback logic ──────────────

    /// <summary>The internal payload is the primary source: when present and non-empty,
    /// it wins over the OS file list.</summary>
    [Fact]
    public void ResolveDropPaths_prefers_internal_payload()
    {
        var data = new DataObject();
        var payloadPaths = new[] { new VfsPath("file", "/Users/test/doc.txt") };
        data.Set(FileDropPayload.DataFormat, new FileDropPayload { Paths = payloadPaths });
        // Also set a file-names list — the payload must win.
        data.Set(DataFormats.FileNames, new[] { "/Users/test/other.txt" });

        var result = ItemView.ResolveDropPaths(data);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("/Users/test/doc.txt", result[0].Value);
    }

    /// <summary>When the internal payload did not survive (cross-process drag), the OS file
    /// list is the fallback — this is what makes drops from Finder/other apps work.
    /// Uses DataFormats.FileNames (plain strings) which is what the fallback reads when
    /// no IStorageItem objects are present.</summary>
    [Fact]
    public void ResolveDropPaths_falls_back_to_os_file_names()
    {
        var data = new DataObject();
        // No FileDropPayload — only the OS file list as plain path strings.
        data.Set(DataFormats.FileNames, new[] { "/Users/test/fromFinder.txt", "/Users/test/second.pdf" });

        var result = ItemView.ResolveDropPaths(data);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("/Users/test/fromFinder.txt", result[0].Value);
        Assert.Equal("/Users/test/second.pdf", result[1].Value);
        Assert.All(result, p => Assert.Equal("file", p.Scheme));
    }

    /// <summary>An empty internal payload (present but zero paths) should fall through to
    /// the OS file list, not return an empty result.</summary>
    [Fact]
    public void ResolveDropPaths_empty_payload_falls_through_to_files()
    {
        var data = new DataObject();
        data.Set(FileDropPayload.DataFormat, new FileDropPayload { Paths = Array.Empty<VfsPath>() });
        data.Set(DataFormats.FileNames, new[] { "/Users/test/fallback.txt" });

        var result = ItemView.ResolveDropPaths(data);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("/Users/test/fallback.txt", result[0].Value);
    }

    /// <summary>A data object with neither format returns null — OnDrop must not fire.</summary>
    [Fact]
    public void ResolveDropPaths_empty_data_returns_null()
    {
        var data = new DataObject();
        Assert.Null(ItemView.ResolveDropPaths(data));
    }

    /// <summary>A payload with default (empty) Paths falls through to the file list.</summary>
    [Fact]
    public void ResolveDropPaths_default_payload_falls_through()
    {
        var data = new DataObject();
        data.Set(FileDropPayload.DataFormat, new FileDropPayload());
        data.Set(DataFormats.FileNames, new[] { "/x/y.txt" });

        var result = ItemView.ResolveDropPaths(data);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("/x/y.txt", result[0].Value);
    }

    /// <summary>Multiple paths from the payload are all preserved, in order.</summary>
    [Fact]
    public void ResolveDropPaths_preserves_all_payload_paths_in_order()
    {
        var data = new DataObject();
        var paths = new[]
        {
            new VfsPath("file", "/a/1.txt"),
            new VfsPath("file", "/a/2.txt"),
            new VfsPath("file", "/a/3.txt"),
        };
        data.Set(FileDropPayload.DataFormat, new FileDropPayload { Paths = paths, IsCut = true });

        var result = ItemView.ResolveDropPaths(data);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("/a/1.txt", result[0].Value);
        Assert.Equal("/a/2.txt", result[1].Value);
        Assert.Equal("/a/3.txt", result[2].Value);
    }
}
