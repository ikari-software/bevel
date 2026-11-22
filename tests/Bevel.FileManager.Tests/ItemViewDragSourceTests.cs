using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Input;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// A drag out of the Filer must carry real files (bevel-u2x6).
///
/// <para><c>ItemView.StartDragAsync</c> set only the private <c>FileDropPayload</c> format, so dragging
/// a file onto Finder, the desktop, Mail or a browser did NOTHING — silently, because a drag offering
/// no format the target understands is simply refused, with no error anywhere. The drop side already
/// accepted <c>DataFormats.Files</c> and the taskbar's stack flyout already set it, so the gap was in
/// this one method. Since every Filer window is now its own process, it also meant Filer-to-Filer drags
/// depended on a managed object surviving a trip between two processes.</para>
///
/// <para><b>Why these are source assertions.</b> <c>DragDrop.DoDragDrop</c> only runs inside a live
/// pointer gesture and blocks on a platform drag session, so there is no headless way to start one and
/// read back the <c>DataObject</c> — which is precisely why this had no coverage at all. Before this
/// change NOTHING in the repo referenced FileDropPayload, DoDragDrop or DataFormats.Files from a test.
/// These guards pin the two properties that were actually wrong, and are honest about being
/// structural: they would not catch a drag that is broken for some other reason.</para>
/// </summary>
public sealed class ItemViewDragSourceTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bevel.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "src", "Bevel.FileManager", "Components", "ItemView.axaml.cs");
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join('\n', text.Split('\n').Select(l => Regex.Replace(l, @"//.*$", string.Empty)));
    }

    private static string StartDragBody()
    {
        var lines = Source().Split('\n');
        var decl = Array.FindIndex(lines, l => l.Contains("Task StartDragAsync("));
        Assert.True(decl >= 0, "StartDragAsync not found in ItemView.axaml.cs — if it was renamed, "
                               + "update this guard rather than deleting it.");
        return string.Join('\n', lines.Skip(decl).Take(30));
    }

    /// <summary>The OS file list is what every non-Bevel target reads. Without it the drag is a no-op
    /// <summary>The OS file list is what every non-Bevel target reads. Without it the drag is a no-op
    /// outside Bevel. IStorageItems under "Files" are only set when the press-time resolve finished;
    /// FileNames (plain path strings) is always set and is what the wrapper converts to
    /// DataFormat.File → NSPasteboardTypeFileUrl on macOS.</summary>
    [Fact]
    public void The_drag_offers_the_os_file_list()
    {
        Assert.Contains("DataFormats.Files", StartDragBody());
        Assert.Contains("DataFormats.FileNames", StartDragBody());
    }

    /// <summary>FileNames must be set BEFORE the storage-task check — it is the reliable fallback
    /// that always works, because DataObjectToDataTransferWrapper converts each string to a
    /// BclStorageItem → DataFormat.File. Uri[] under "Files" is silently dropped by the wrapper.</summary>
    [Fact]
    public void The_drag_always_offers_plain_path_strings()
    {
        var body = StartDragBody();
        Assert.Contains("DataFormats.FileNames", body);
        // FileNames must come before the storage-task conditional, not inside its else branch.
        var fileNamesIdx = body.IndexOf("DataFormats.FileNames", StringComparison.Ordinal);
        var taskCheckIdx = body.IndexOf("_dragStorageTask is", StringComparison.Ordinal);
        Assert.True(fileNamesIdx < taskCheckIdx,
            "DataFormats.FileNames must be set unconditionally, before the storage-task check — "
            + "it is the only format the wrapper reliably converts to native file URLs.");
    }

    /// <summary>...and the internal payload must stay, because it is what preserves Bevel's own
    /// move/copy semantics on an internal drop. Setting only one of the two formats is a regression in
    /// either direction.</summary>
    [Fact]
    public void The_drag_still_offers_the_internal_payload()
    {
        Assert.Contains("FileDropPayload.DataFormat", StartDragBody());
    }

    /// <summary>A drop whose internal payload did not survive falls back to the file list.
    ///
    /// This test verifies that the ResolveDropPaths method correctly falls back from
    /// FileDropPayload to DataFormats.Files when the payload is unavailable.</summary>
    [Fact]
    public void A_drop_whose_internal_payload_did_not_survive_falls_back_to_the_file_list()
    {
        var src = Source();
        
        // Verify ResolveDropPaths method exists with correct signature
        Assert.Contains("internal static List<VfsPath>? ResolveDropPaths(IDataObject data)", src);
        
        // Verify it prioritizes FileDropPayload
        Assert.Contains("data.Contains(FileDropPayload.DataFormat)", src);
        
        // Verify it falls back to DataFormats.Files
        Assert.Contains("data.Contains(DataFormats.Files)", src);
        
        // Verify OnDrop uses ResolveDropPaths
        Assert.Contains("paths = ResolveDropPaths(e.Data)", src);
    }

    /// <summary>Folders resolve through TryGetFolderFromPathAsync, not TryGetFileFromPathAsync.
    ///
    /// <para>Asking for a FILE at a directory path returns null, so a mixed selection would drag only
    /// its files and silently drop the folders — a half-complete drag being arguably worse than none,
    /// since the user sees it partly succeed.</para></summary>
    [Fact]
    public void Folders_resolve_through_the_folder_api()
    {
        var src = Source();
        Assert.Contains("TryGetFolderFromPathAsync", src);
        Assert.Contains("TryGetFileFromPathAsync", src);
    }

    /// <summary>The resolve must be kicked off on PRESS, not inside the drag.
    ///
    /// <para>DoDragDrop has to run synchronously within the pointer gesture; awaiting a resolve first
    /// yields and loses the drag. StackFlyoutView pre-resolves for exactly this reason, and its comment
    /// records that awaiting "would drop the OS drag gesture".</para></summary>
    [Fact]
    public void The_storage_resolve_starts_on_press_not_during_the_drag()
    {
        var src = Source();
        var assigned = src.IndexOf("_dragStorageTask = ", StringComparison.Ordinal);
        var dragDecl = src.IndexOf("Task StartDragAsync(", StringComparison.Ordinal);

        Assert.True(assigned >= 0, "_dragStorageTask is never assigned — the OS file list would never "
                                   + "be ready in time and the drag would silently fall back to "
                                   + "internal-only.");
        Assert.True(assigned < dragDecl,
            "_dragStorageTask must be assigned in the pointer-PRESS handler, which precedes "
            + "StartDragAsync in this file. Resolving inside the drag means awaiting inside the "
            + "gesture, which loses it.");
    }

    /// <summary>AllowDrop must be set on the ItemView — without it Avalonia never routes
    /// DragOver/Drop events to the control, so every drop is a silent no-op. This was the
    /// actual root cause of drops never working (bevel-u2x6).</summary>
    [Fact]
    public void The_view_accepts_drops()
    {
        var src = Source();
        Assert.Contains("DragDrop.SetAllowDrop(this, true)", src);
    }

    /// <summary>The drop side must also accept DataFormats.FileNames (plain path strings),
    /// not only DataFormats.Files (IStorageItem list) — some drag sources only carry strings.</summary>
    [Fact]
    public void The_drop_side_also_reads_file_names()
    {
        var src = Source();
        Assert.Contains("DataFormats.FileNames", src);
        Assert.Contains("GetFileNames()", src);
    }
}
