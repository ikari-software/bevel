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
/// <para><c>ItemView.StartDragAsync</c> sets <c>DataFormats.FileNames</c> (plain path strings,
/// which DataObjectToDataTransferWrapper converts to BclStorageItem → DataFormat.File → native
/// public.file-url on macOS), <c>DataFormats.Text</c> (for text-only targets like iTerm),
/// <c>DataFormats.Files</c> (IStorageItem list when the press-time resolve finished), and the
/// internal <c>FileDropPayload</c>. These source guards pin those properties.</para>
///
/// <para><b>Why these are source assertions.</b> <c>DoDragDrop</c> only runs inside a live
/// pointer gesture and blocks on a platform drag session, so there is no headless way to start one and
/// read back the data. These guards pin the key properties and are honest about being
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
        return string.Join('\n', lines.Skip(decl).Take(60));
    }

    /// <summary>FileNames (plain path strings) is the reliable path to native file URLs:
    /// DataObjectToDataTransferWrapper converts each string to a BclStorageItem → DataFormat.File
    /// → public.file-url on macOS. Without it Finder sees a drag with no files.</summary>
    [Fact]
    public void The_drag_offers_file_names()
    {
        Assert.Contains("DataFormats.FileNames", StartDragBody());
    }

    /// <summary>Text format must also be set — iTerm and other text-only targets accept this.</summary>
    [Fact]
    public void The_drag_always_offers_plain_text_paths()
    {
        Assert.Contains("DataFormats.Text", StartDragBody());
    }

    /// <summary>IStorageItem list under "Files" when the press-time resolve finished.</summary>
    [Fact]
    public void The_drag_offers_storage_items_when_ready()
    {
        Assert.Contains("DataFormats.Files", StartDragBody());
    }

    /// <summary>The internal payload must stay — it preserves Bevel's own move/copy semantics
    /// on an internal drop.</summary>
    [Fact]
    public void The_drag_still_offers_the_internal_payload()
    {
        Assert.Contains("FileDropPayload.DataFormat", StartDragBody());
    }

    /// <summary>Leading slashes must be collapsed — VfsPath.Combine(root, "x") yields "//x"
    /// which makes a malformed file:// URI that Finder rejects.</summary>
    [Fact]
    public void The_drag_normalizes_double_leading_slashes()
    {
        Assert.Contains("NormalizeFsPath", StartDragBody());
    }

    /// <summary>The drop side must use ResolveDropPaths with BOTH the new IDataTransfer and
    /// the old IDataObject, covering drags from both old-API and new-API sources.</summary>
    [Fact]
    public void The_drop_uses_resolve_drop_paths_with_both_apis()
    {
        var src = Source();
        Assert.Contains("ResolveDropPaths(e.DataTransfer)", src);
        Assert.Contains("ResolveDropPaths(e.Data)", src);
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
