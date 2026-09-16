using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests for the "Properties" General-tab sheet backing Alt+Enter / File&gt;Properties / the
/// toolbar Properties button. Drives the dialog directly (constructed, never shown modally) and
/// awaits <see cref="PropertiesDialog.ContainsScanTask"/> for the async folder walk, mirroring how
/// FolderPickerDialogTests drives that dialog's internals rather than a live modal loop.
/// </summary>
public sealed class PropertiesDialogTests : IDisposable
{
    private readonly string _dir;
    private readonly VfsRoot _vfsRoot = new();

    public PropertiesDialogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-properties-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _vfsRoot.Register(new LocalFsProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Minimal hand-built IVfsNode for a file, so single-file tests don't depend on
    /// real filesystem stat timing (size/modified are supplied directly).</summary>
    private sealed class FakeFileNode : IVfsNode
    {
        public required VfsPath Path { get; init; }
        public required string DisplayName { get; init; }
        public VfsNodeKind Kind { get; init; } = VfsNodeKind.File;
        public bool MightHaveChildren { get; init; }
        public long? Size { get; init; }
        public DateTimeOffset? Modified { get; init; }
        public DateTimeOffset? Created { get; init; }
        public DateTimeOffset? Accessed { get; init; }
        public VfsNodeAttributes Attributes { get; init; }
        public string TypeDescription { get; init; } = "";
        public IconKey IconKey { get; init; }
        public VfsCapabilities Caps { get; init; }
    }

    // ── Single file ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Single_file_shows_type_location_size_and_modified_date()
    {
        var modified = new DateTimeOffset(2025, 3, 14, 9, 30, 0, TimeSpan.Zero);
        var node = new FakeFileNode
        {
            Path = new VfsPath("file", "/Users/test/Documents/report.txt"),
            DisplayName = "report.txt",
            TypeDescription = "Text Document",
            Size = 12345,
            Modified = modified,
        };

        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.Equal("report.txt Properties", dialog.Title);
        Assert.Equal("report.txt", dialog.NameField.Text);
        Assert.Equal("Text Document", dialog.TypeValue.Text);
        Assert.Equal("/Users/test/Documents", dialog.LocationValue.Text);
        Assert.Equal("12.1 KB (12,345 bytes)", dialog.SizeValue.Text);
        Assert.Equal("16.0 KB (16,384 bytes)", dialog.SizeOnDiskValue.Text); // rounded up to a 4 KiB cluster
        Assert.False(dialog.ContainsRowGrid.IsVisible);
        Assert.Equal(modified.ToString("g"), dialog.ModifiedValue.Text);
        Assert.Equal("-", dialog.CreatedValue.Text);   // not exposed by IVfsNode
        Assert.Equal("-", dialog.AccessedValue.Text);  // not exposed by IVfsNode
        Assert.Null(dialog.ContainsScanTask);           // only folders scan
    }

    [AvaloniaFact]
    public void Single_file_surfaces_created_accessed_and_attribute_state()
    {
        var created = new DateTimeOffset(2024, 1, 2, 3, 4, 0, TimeSpan.Zero);
        var accessed = new DateTimeOffset(2025, 6, 7, 8, 9, 0, TimeSpan.Zero);
        var node = new FakeFileNode
        {
            Path = new VfsPath("file", "/x/a.txt"),
            DisplayName = "a.txt",
            Created = created,
            Accessed = accessed,
            Attributes = VfsNodeAttributes.ReadOnly | VfsNodeAttributes.Hidden,
        };

        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.Equal(created.ToString("g"), dialog.CreatedValue.Text);
        Assert.Equal(accessed.ToString("g"), dialog.AccessedValue.Text);
        Assert.True(dialog.ReadOnlyCheck.IsChecked);   // reflects real state...
        Assert.True(dialog.HiddenCheck.IsChecked);
        Assert.True(dialog.ReadOnlyCheck.IsEnabled);   // ...and is now editable/committable (bevel-iuh)
        Assert.True(dialog.HiddenCheck.IsEnabled);
    }

    [AvaloniaFact]
    public void Single_file_attributes_are_editable_and_default_unchecked()
    {
        var node = new FakeFileNode
        {
            Path = new VfsPath("file", "/Users/test/a.txt"),
            DisplayName = "a.txt",
        };

        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.False(dialog.ReadOnlyCheck.IsChecked);
        Assert.True(dialog.ReadOnlyCheck.IsEnabled);
        Assert.False(dialog.HiddenCheck.IsChecked);
        Assert.True(dialog.HiddenCheck.IsEnabled);
        Assert.False(dialog.NameField.IsReadOnly); // single selection: name is editable too
    }

    [AvaloniaFact]
    public void Ok_cancel_and_apply_buttons_exist_and_apply_starts_disabled_when_clean()
    {
        var node = new FakeFileNode { Path = new VfsPath("file", "/x/a.txt"), DisplayName = "a.txt" };
        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.NotNull(dialog.Ok);
        Assert.NotNull(dialog.Cancel);
        Assert.NotNull(dialog.Apply);
        Assert.False(dialog.Apply.IsEnabled); // nothing edited yet, so nothing to commit
    }

    // ── Single folder (async Contains/size scan) ───────────────────────

    [AvaloniaFact]
    public async Task Single_folder_computes_size_and_contents_via_background_scan()
    {
        if (OperatingSystem.IsWindows()) return;   // Avalonia headless can't PushFrame a nested dispatcher loop on Windows (framework limit); passes on Linux+macOS
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "one.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "two.bin"), new byte[200]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "three.bin"), new byte[300]);

        var folderPath = new VfsPath("file", _dir);
        var node = await _vfsRoot.ResolveAsync(folderPath, default);

        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.True(dialog.ContainsRowGrid.IsVisible);
        Assert.NotNull(dialog.ContainsScanTask);
        await dialog.ContainsScanTask!;

        // 2 files directly inside + 1 folder + 1 file inside that folder = 3 files, 1 folder.
        Assert.Equal("3 Files, 1 Folders", dialog.ContainsValue.Text);
        Assert.Contains("600 bytes", dialog.SizeValue.Text); // 100 + 200 + 300
        Assert.DoesNotContain("Calculating", dialog.SizeValue.Text);
    }

    // ── Multi-selection (simplified sheet) ─────────────────────────────

    [AvaloniaFact]
    public void Multi_selection_shows_combined_size_and_a_simplified_sheet()
    {
        var a = new FakeFileNode { Path = new VfsPath("file", "/Users/test/a.txt"), DisplayName = "a.txt", Size = 1000 };
        var b = new FakeFileNode { Path = new VfsPath("file", "/Users/test/b.txt"), DisplayName = "b.txt", Size = 2000 };

        var dialog = new PropertiesDialog(_vfsRoot, new IVfsNode[] { a, b });

        Assert.Equal("2 Items Properties", dialog.Title);
        Assert.Equal("2 items selected", dialog.NameField.Text);
        Assert.True(dialog.NameField.IsReadOnly);
        Assert.Equal("Files", dialog.TypeValue.Text);
        Assert.Equal("/Users/test", dialog.LocationValue.Text); // shared parent
        Assert.Equal("2.9 KB (3,000 bytes)", dialog.SizeValue.Text);
        Assert.False(dialog.ContainsRowGrid.IsVisible);
        Assert.Null(dialog.ContainsScanTask);
    }

    [AvaloniaFact]
    public void Multi_selection_with_different_locations_reports_various_locations()
    {
        var a = new FakeFileNode { Path = new VfsPath("file", "/Users/test/one/a.txt"), DisplayName = "a.txt" };
        var b = new FakeFileNode { Path = new VfsPath("file", "/Users/test/two/b.txt"), DisplayName = "b.txt" };

        var dialog = new PropertiesDialog(_vfsRoot, new IVfsNode[] { a, b });

        Assert.Equal("(various locations)", dialog.LocationValue.Text);
    }

    [AvaloniaFact]
    public void Multi_selection_size_on_disk_sums_each_files_own_cluster_rounding()
    {
        // Two files whose sizes each round up to a different cluster count: summing the raw total
        // first (1000 + 4097 = 5097) would round to a single 8 KiB cluster, but per-file rounding
        // (4096 + 8192) must yield 12 KiB — this pins that per-file behavior for the multi-select sheet.
        var a = new FakeFileNode { Path = new VfsPath("file", "/Users/test/a.txt"), DisplayName = "a.txt", Size = 1000 };
        var b = new FakeFileNode { Path = new VfsPath("file", "/Users/test/b.txt"), DisplayName = "b.txt", Size = 4097 };

        var dialog = new PropertiesDialog(_vfsRoot, new IVfsNode[] { a, b });

        Assert.Equal("5.0 KB (5,097 bytes)", dialog.SizeValue.Text);
        Assert.Equal("12.0 KB (12,288 bytes)", dialog.SizeOnDiskValue.Text); // 4096 + 8192, not RoundUp(5097)
        Assert.Null(dialog.MultiScanTask); // no folders in the selection — resolves synchronously
    }

    [AvaloniaFact]
    public async Task Multi_selection_with_a_folder_recurses_and_combines_sizes()
    {
        if (OperatingSystem.IsWindows()) return;   // Avalonia headless can't PushFrame a nested dispatcher loop on Windows (framework limit); passes on Linux+macOS
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "sub", "inner.bin"), new byte[500]);

        var folderPath = new VfsPath("file", _dir);
        var folderNode = await _vfsRoot.ResolveAsync(folderPath, default);

        var topFile = new FakeFileNode { Path = new VfsPath("file", "/Users/test/loose.txt"), DisplayName = "loose.txt", Size = 250 };

        var dialog = new PropertiesDialog(_vfsRoot, new IVfsNode[] { topFile, folderNode });

        Assert.NotNull(dialog.MultiScanTask);
        Assert.Equal("Calculating...", dialog.SizeValue.Text);
        await dialog.MultiScanTask!;

        // 250 (loose top-level file) + 500 (file inside the selected folder) = 750 bytes.
        Assert.Contains("750 bytes", dialog.SizeValue.Text);
        Assert.DoesNotContain("Calculating", dialog.SizeValue.Text);
        // Size on disk: 250 -> one 4 KiB cluster (4096) + 500 -> one 4 KiB cluster (4096) = 8192.
        Assert.Contains("8,192 bytes", dialog.SizeOnDiskValue.Text);
    }

    // ── Empty selection ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void Empty_selection_falls_back_to_a_generic_title()
    {
        var dialog = new PropertiesDialog(_vfsRoot, Array.Empty<IVfsNode>());

        Assert.Equal("Properties", dialog.Title);
        Assert.False(dialog.ContainsRowGrid.IsVisible);
        Assert.False(dialog.ReadOnlyCheck.IsEnabled);
        Assert.False(dialog.HiddenCheck.IsEnabled);
    }

    // ── Actionable Properties: rename + attribute commit (bevel-iuh) ───

    [AvaloniaFact]
    public async Task Apply_button_tracks_dirty_state_for_name_and_attribute_edits()
    {
        if (OperatingSystem.IsWindows()) return;   // Avalonia headless can't PushFrame a nested dispatcher loop on Windows (framework limit); passes on Linux+macOS
        var path = Path.Combine(_dir, "dirty.txt");
        File.WriteAllText(path, "x");
        var node = await _vfsRoot.ResolveAsync(new VfsPath("file", path), default);

        var dialog = new PropertiesDialog(_vfsRoot, node);
        Assert.False(dialog.Apply.IsEnabled);

        dialog.NameField.Text = "renamed.txt";
        Assert.True(dialog.Apply.IsEnabled);

        dialog.NameField.Text = "dirty.txt"; // revert to the original name
        Assert.False(dialog.Apply.IsEnabled);

        dialog.HiddenCheck.IsChecked = true;
        Assert.True(dialog.Apply.IsEnabled);

        dialog.HiddenCheck.IsChecked = false; // revert
        Assert.False(dialog.Apply.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Ok_commits_a_pending_rename_for_a_single_file()
    {
        if (OperatingSystem.IsWindows()) return;   // Avalonia headless can't PushFrame a nested dispatcher loop on Windows (framework limit); passes on Linux+macOS
        var original = Path.Combine(_dir, "old-name.txt");
        File.WriteAllText(original, "hello");
        var node = await _vfsRoot.ResolveAsync(new VfsPath("file", original), default);

        var dialog = new PropertiesDialog(_vfsRoot, node);
        dialog.NameField.Text = "new-name.txt";

        await dialog.OkAsync();

        Assert.True(dialog.CommittedChange);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(Path.Combine(_dir, "new-name.txt")));
    }

    [AvaloniaFact]
    public async Task Apply_commits_an_attribute_toggle_and_stays_open()
    {
        if (OperatingSystem.IsWindows()) return;   // Avalonia headless can't PushFrame a nested dispatcher loop on Windows (framework limit); passes on Linux+macOS
        var path = Path.Combine(_dir, "toggle.txt");
        File.WriteAllText(path, "x");
        var node = await _vfsRoot.ResolveAsync(new VfsPath("file", path), default);

        var dialog = new PropertiesDialog(_vfsRoot, node);
        Assert.False(dialog.Apply.IsEnabled);

        dialog.ReadOnlyCheck.IsChecked = true;
        Assert.True(dialog.Apply.IsEnabled);

        await dialog.ApplyAsync();

        Assert.True(dialog.CommittedChange);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.False(dialog.Apply.IsEnabled); // dirty cleared — dialog stayed open (this is Apply, not OK)

        // Clear the read-only bit back off so the test fixture's recursive Dispose() delete
        // of _dir doesn't have to fight file permissions on the way out.
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [AvaloniaFact]
    public async Task Ok_with_no_edits_closes_without_committing()
    {
        var path = Path.Combine(_dir, "untouched.txt");
        File.WriteAllText(path, "x");
        var node = await _vfsRoot.ResolveAsync(new VfsPath("file", path), default);

        var dialog = new PropertiesDialog(_vfsRoot, node);

        await dialog.OkAsync();

        Assert.False(dialog.CommittedChange); // nothing was dirty, so nothing to commit
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task Cancel_discards_pending_edits_without_committing()
    {
        var path = Path.Combine(_dir, "cancel-me.txt");
        File.WriteAllText(path, "x");
        var node = await _vfsRoot.ResolveAsync(new VfsPath("file", path), default);

        var dialog = new PropertiesDialog(_vfsRoot, node);
        dialog.NameField.Text = "should-not-exist.txt";
        dialog.ReadOnlyCheck.IsChecked = true;

        dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(dialog.CommittedChange);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_dir, "should-not-exist.txt")));
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }
}
