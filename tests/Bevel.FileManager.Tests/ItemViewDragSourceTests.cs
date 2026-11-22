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
/// <para><c>ItemView.StartDragAsync</c> now uses the non-obsolete <c>DragDrop.DoDragDropAsync</c>
/// with a <c>DataTransfer</c> containing <c>DataTransferItem.SetFile()</c> items (→ native
/// <c>public.file-url</c> on macOS), a text item with newline-separated paths, and a JSON
/// internal payload. These source guards pin the structural properties that were wrong.</para>
///
/// <para><b>Why these are source assertions.</b> <c>DoDragDrop</c> only runs inside a live
/// pointer gesture and blocks on a platform drag session, so there is no headless way to start one and
/// read back the <c>DataTransfer</c>. These guards pin the key properties and are honest about being
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
        return string.Join('\n', lines.Skip(decl).Take(80));
    }

    /// <summary>The drag must use DoDragDropAsync with a DataTransfer (not the obsolete
    /// DoDragDrop with IDataObject) so the macOS backend properly writes public.file-url.</summary>
    [Fact]
    public void The_drag_uses_the_new_drag_drop_async_api()
    {
        var body = StartDragBody();
        Assert.Contains("DoDragDropAsync", body);
        Assert.Contains("new DataTransfer()", body);
    }

    /// <summary>Each resolved IStorageItem must be added as a DataTransferItem with SetFile —
    /// this is what creates DataFormat.File → public.file-url on the native pasteboard.</summary>
    [Fact]
    public void The_drag_adds_storage_items_via_set_file()
    {
        var body = StartDragBody();
        Assert.Contains("SetFile(", body);
        Assert.Contains("DataTransferItem", body);
    }

    /// <summary>A text item with newline-separated paths must always be added — iTerm and other
    /// text targets accept this even when file-url isn't available.</summary>
    [Fact]
    public void The_drag_always_offers_plain_text_paths()
    {
        var body = StartDragBody();
        Assert.Contains("SetText(", body);
    }

    /// <summary>The internal payload (JSON under a custom app format) must stay, because it is
    /// what preserves Bevel's own move/copy semantics on an internal drop.</summary>
    [Fact]
    public void The_drag_still_offers_the_internal_payload()
    {
        var body = StartDragBody();
        Assert.Contains("FileDropPayload.DataFormat", body);
        Assert.Contains("JsonSerializer.Serialize", body);
    }

    /// <summary>Leading slashes must be collapsed — VfsPath.Combine(root, "x") yields "//x"
    /// which makes a malformed file:// URI that Finder rejects.</summary>
    [Fact]
    public void The_drag_normalizes_double_leading_slashes()
    {
        var body = StartDragBody();
        Assert.Contains("NormalizeFsPath", body);
    }

    /// <summary>The drop side must use ResolveDropPaths with BOTH the new IDataTransfer and
    /// the old IDataObject, covering drags from both old-API and new-API sources.</summary>
    [Fact]
    public void The_drop_uses_resolve_drop_paths_with_both_apis()
    {
        var src = Source();
        Assert.Contains("ResolveDropPaths(e.DataTransfer)", src);
        Assert.Contains("ResolveDropPaths(e.Data)", src);
        Assert.Contains("internal static List<VfsPath>? ResolveDropPaths(IDataTransfer? transfer)", src);
        Assert.Contains("internal static List<VfsPath>? ResolveDropPaths(IDataObject data)", src);
    }

    /// <summary>The drop side must fall back through: internal payload → IStorageItem files →
    /// FileNames strings → plain text.</summary>
    [Fact]
    public void The_drop_falls_back_through_all_formats()
    {
        var src = Source();
        Assert.Contains("TryGetFiles()", src);
        Assert.Contains("GetFileNames()", src);
        Assert.Contains("TryGetText()", src);
        Assert.Contains("GetText()", src);
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
}
