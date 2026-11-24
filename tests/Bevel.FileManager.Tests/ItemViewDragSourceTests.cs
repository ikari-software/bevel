using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// A drag out of the Filer must carry real files (bevel-u2x6).
///
/// <para><c>ItemView.StartDragAsync</c> uses <c>DoDragDropAsync</c> with a <c>DataTransfer</c>
/// containing only <c>SetFile()</c> items, which maps to native <c>public.file-url</c> on macOS.
/// The drop side resolves via <c>ResolveDropPaths(IDataTransfer)</c>. These source guards pin
/// those properties.</para>
///
/// <para><b>Why these are source assertions.</b> <c>DoDragDropAsync</c> only runs inside a live
/// pointer gesture and blocks on a platform drag session, so there is no headless way to start one
/// and read back the data. These guards pin the key properties and are honest about being
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

    /// <summary>Extracts the body of a method by brace-matching from its declaration line.</summary>
    private static string MethodBody(string source, string declarationFragment)
    {
        var lines = source.Split('\n');
        var decl = Array.FindIndex(lines, l => l.Contains(declarationFragment));
        Assert.True(decl >= 0, $"{declarationFragment} not found in ItemView.axaml.cs — if it was renamed, "
                               + "update this guard rather than deleting it.");
        // Find the opening brace, then brace-match to the closing one.
        var text = string.Join('\n', lines.Skip(decl));
        var open = text.IndexOf('{');
        Assert.True(open >= 0, "no opening brace found");
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { depth--; if (depth == 0) return text[..(i + 1)]; }
        }
        Assert.Fail("unbalanced braces in method body");
        return "";
    }

    private static string StartDragBody() => MethodBody(Source(), "Task StartDragAsync(");

    /// <summary>Uses DoDragDropAsync with a DataTransfer — the non-obsolete API that gives
    /// direct control over DataFormat.File items → native public.file-url on macOS.</summary>
    [Fact]
    public void The_drag_uses_drag_drop_async_with_data_transfer()
    {
        var body = StartDragBody();
        Assert.Contains("DoDragDropAsync", body);
        Assert.Contains("new DataTransfer()", body);
    }

    /// <summary>Each resolved IStorageItem must be added via SetFile() — this creates a direct
    /// DataFormat.File item that the macOS backend maps to public.file-url.</summary>
    [Fact]
    public void The_drag_adds_storage_items_via_set_file()
    {
        var body = StartDragBody();
        Assert.Contains("SetFile(", body);
        Assert.Contains("DataTransferItem", body);
    }

    /// <summary>No text/payload items — adding anything beyond SetFile hangs the native
    /// drag session on macOS (bevel-u2x6).</summary>
    [Fact]
    public void The_drag_does_not_add_text_or_payload_items()
    {
        var body = StartDragBody();
        Assert.DoesNotContain("SetText(", body);
        Assert.DoesNotContain("FileDropPayload.DataFormat", body);
    }

    /// <summary>The drop side must use ResolveDropPaths with the IDataTransfer API.</summary>
    [Fact]
    public void The_drop_uses_resolve_drop_paths()
    {
        var src = Source();
        Assert.Contains("ResolveDropPaths(e.DataTransfer)", src);
    }

    /// <summary>The drop side must resolve OS files via TryGetFiles() (IStorageItem list).</summary>
    [Fact]
    public void The_drop_resolves_os_files()
    {
        var src = Source();
        Assert.Contains("TryGetFiles()", src);
    }

    /// <summary>Plain text must NOT be accepted as file paths — dropping selected text from
    /// another app would fire DropRequested with garbage paths.</summary>
    [Fact]
    public void The_drop_does_not_accept_plain_text_as_paths()
    {
        var src = Source();
        Assert.DoesNotContain("TryGetText()", src);
        Assert.DoesNotContain("GetText()", src);
    }

    /// <summary>OnDragOver must only accept DataFormat.File and the internal JSON payload —
    /// not DataFormat.Text.</summary>
    [Fact]
    public void The_drag_over_does_not_accept_plain_text()
    {
        var body = MethodBody(Source(), "void OnDragOver(");
        Assert.DoesNotContain("DataFormat.Text", body);
        Assert.Contains("DataFormat.File", body);
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
    /// <para>DoDragDropAsync has to run synchronously within the pointer gesture; awaiting a resolve first
    /// yields and loses the drag. StackFlyoutView pre-resolves for exactly this reason.</para></summary>
    [Fact]
    public void The_storage_resolve_starts_on_press_not_during_the_drag()
    {
        var src = Source();
        var assigned = src.IndexOf("_dragStorageTask = ", StringComparison.Ordinal);
        var dragDecl = src.IndexOf("Task StartDragAsync(", StringComparison.Ordinal);

        Assert.True(assigned >= 0, "_dragStorageTask is never assigned — the OS file list would never "
                                   + "be ready in time and the drag would carry nothing.");
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

    /// <summary>No debug file I/O in the drag path — it runs on the UI thread and leaks paths.</summary>
    [Fact]
    public void The_drag_path_has_no_debug_file_logging()
    {
        var src = Source();
        Assert.DoesNotContain("bevel_drag_debug", src);
        Assert.DoesNotContain("AppendAllText", src);
    }
}
