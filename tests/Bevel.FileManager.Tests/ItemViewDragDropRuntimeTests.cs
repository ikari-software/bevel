using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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
/// <c>DoDragDropAsync</c> can't run headless), these tests exercise the REAL code paths: the
/// <see cref="ItemView.ResolveDropPaths"/> fallback logic with live <see cref="DataTransfer"/>
/// instances, and the <c>AllowDrop</c> flag without which the OS never routes drop events
/// to the control at all.</para>
/// </summary>
public sealed class ItemViewDragDropRuntimeTests
{
    private static readonly DataFormat<string> JsonPayloadFormat =
        DataFormat.CreateStringApplicationFormat(FileDropPayload.DataFormat);

    /// <summary>Builds the JSON payload string — VfsPath's Parent property is recursive so
    /// we can't serialize a FileDropPayload directly; build the JSON by hand.</summary>
    private static string PayloadJson(params VfsPath[] paths)
    {
        var items = string.Join(",", paths.Select(p =>
            "{\"Scheme\":\"" + p.Scheme + "\",\"Value\":\"" + p.Value + "\"}"));
        return "{\"Paths\":[" + items + "],\"IsCut\":false,\"SourceWindowId\":null}";
    }

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

    /// <summary>The internal JSON payload is the primary source: when present and non-empty,
    /// it wins over the OS file list.</summary>
    [Fact]
    public void ResolveDropPaths_prefers_internal_payload()
    {
        var transfer = new DataTransfer();
        var payload = PayloadJson(new VfsPath("file", "/Users/test/doc.txt"));
        var item = new DataTransferItem();
        item.Set(JsonPayloadFormat, payload);
        transfer.Add(item);

        var result = ItemView.ResolveDropPaths(transfer);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("/Users/test/doc.txt", result[0].Value);
    }

    /// <summary>Malformed JSON in the payload format falls through to the OS file list
    /// rather than throwing.</summary>
    [Fact]
    public void ResolveDropPaths_malformed_json_falls_through()
    {
        var transfer = new DataTransfer();
        var badItem = new DataTransferItem();
        badItem.Set(JsonPayloadFormat, "not valid json{{{");
        transfer.Add(badItem);

        Assert.Null(ItemView.ResolveDropPaths(transfer));
    }

    /// <summary>An empty internal payload (present but zero paths) falls through to the
    /// OS file list, not an empty result.</summary>
    [Fact]
    public void ResolveDropPaths_empty_payload_falls_through_to_files()
    {
        var transfer = new DataTransfer();
        var emptyPayload = PayloadJson();
        var payloadItem = new DataTransferItem();
        payloadItem.Set(JsonPayloadFormat, emptyPayload);
        transfer.Add(payloadItem);

        // No files either — should return null, not empty.
        Assert.Null(ItemView.ResolveDropPaths(transfer));
    }

    /// <summary>A transfer with neither format returns null — OnDrop must not fire.</summary>
    [Fact]
    public void ResolveDropPaths_empty_transfer_returns_null()
    {
        Assert.Null(ItemView.ResolveDropPaths(new DataTransfer()));
        Assert.Null(ItemView.ResolveDropPaths(null));
    }

    /// <summary>Multiple paths from the payload are all preserved, in order.</summary>
    [Fact]
    public void ResolveDropPaths_preserves_all_payload_paths_in_order()
    {
        var transfer = new DataTransfer();
        var paths = new[]
        {
            new VfsPath("file", "/a/1.txt"),
            new VfsPath("file", "/a/2.txt"),
            new VfsPath("file", "/a/3.txt"),
        };
        var payload = PayloadJson(paths);
        var item = new DataTransferItem();
        item.Set(JsonPayloadFormat, payload);
        transfer.Add(item);

        var result = ItemView.ResolveDropPaths(transfer);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("/a/1.txt", result[0].Value);
        Assert.Equal("/a/2.txt", result[1].Value);
        Assert.Equal("/a/3.txt", result[2].Value);
    }

    /// <summary>Plain text on the transfer must NOT be interpreted as file paths.</summary>
    [Fact]
    public void ResolveDropPaths_ignores_plain_text()
    {
        var transfer = new DataTransfer();
        var textItem = new DataTransferItem();
        textItem.SetText("/Users/test/notAFile.txt\n/Users/test/alsoNot.txt");
        transfer.Add(textItem);

        Assert.Null(ItemView.ResolveDropPaths(transfer));
    }

    // ── DropEventArgs modifier semantics ─────────────────────────────

    /// <summary>Plain drop (no modifiers) = move. Ctrl = copy. Ctrl+Shift = link.</summary>
    [Fact]
    public void DropEventArgs_plain_drop_is_move()
    {
        var e = new DropEventArgs(new[] { new VfsPath("file", "/a.txt") }, ctrl: false, shift: false);
        Assert.False(e.IsCopy);
        Assert.False(e.IsLink);
    }

    [Fact]
    public void DropEventArgs_ctrl_is_copy()
    {
        var e = new DropEventArgs(new[] { new VfsPath("file", "/a.txt") }, ctrl: true, shift: false);
        Assert.True(e.IsCopy);
        Assert.False(e.IsLink);
    }

    [Fact]
    public void DropEventArgs_ctrl_shift_is_link()
    {
        var e = new DropEventArgs(new[] { new VfsPath("file", "/a.txt") }, ctrl: true, shift: true);
        Assert.False(e.IsCopy);
        Assert.True(e.IsLink);
    }

    [Fact]
    public void DropEventArgs_shift_alone_is_move_not_link()
    {
        var e = new DropEventArgs(new[] { new VfsPath("file", "/a.txt") }, ctrl: false, shift: true);
        Assert.False(e.IsCopy);
        Assert.False(e.IsLink);
    }

}
