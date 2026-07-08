using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
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
        public string TypeDescription { get; init; } = "";
        public IconKey IconKey { get; init; }
        public VfsCapabilities Caps { get; init; }
        public IReadOnlyDictionary<string, object?> ExtraColumns { get; init; } = new Dictionary<string, object?>();
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
    public void Single_file_attributes_are_display_only_and_unchecked()
    {
        var node = new FakeFileNode
        {
            Path = new VfsPath("file", "/Users/test/a.txt"),
            DisplayName = "a.txt",
        };

        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.False(dialog.ReadOnlyCheck.IsChecked);
        Assert.False(dialog.ReadOnlyCheck.IsEnabled);
        Assert.False(dialog.HiddenCheck.IsChecked);
        Assert.False(dialog.HiddenCheck.IsEnabled);
    }

    [AvaloniaFact]
    public void Ok_cancel_and_apply_buttons_exist_and_apply_is_a_noop_placeholder()
    {
        var node = new FakeFileNode { Path = new VfsPath("file", "/x/a.txt"), DisplayName = "a.txt" };
        var dialog = new PropertiesDialog(_vfsRoot, node);

        Assert.NotNull(dialog.Ok);
        Assert.NotNull(dialog.Cancel);
        Assert.NotNull(dialog.Apply);
        Assert.False(dialog.Apply.IsEnabled); // Apply has nothing to commit yet (see class remarks)
    }

    // ── Single folder (async Contains/size scan) ───────────────────────

    [AvaloniaFact]
    public async Task Single_folder_computes_size_and_contents_via_background_scan()
    {
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

    // ── Empty selection ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void Empty_selection_falls_back_to_a_generic_title()
    {
        var dialog = new PropertiesDialog(_vfsRoot, Array.Empty<IVfsNode>());

        Assert.Equal("Properties", dialog.Title);
        Assert.False(dialog.ContainsRowGrid.IsVisible);
    }
}
