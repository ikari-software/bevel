using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-wqus: activating a FILE (double-click / Enter / context Open) opens it with the OS
/// default handler via the injected IFileOpener, instead of doing nothing.</summary>
public class OpenFileTest
{
    private sealed class RecordingOpener : IFileOpener
    {
        public List<string> Opened { get; } = new();
        public List<string> Previewed { get; } = new();
        public Task OpenPathAsync(string path, CancellationToken ct = default) { Opened.Add(path); return Task.CompletedTask; }
        public Task PreviewAsync(string path, CancellationToken ct = default) { Previewed.Add(path); return Task.CompletedTask; }
    }

    [AvaloniaFact]
    public async Task Activating_a_file_opens_it_via_the_opener()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bevel-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "hello.txt").Replace('\\', '/');   // VfsPath canonicalises to '/'; matches the opener's recorded path cross-platform
        File.WriteAllText(file, "x");

        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var opener = new RecordingOpener();
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.SetFileOpener(opener);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        IVfsNode? node = null;
        await foreach (var n in root.EnumerateAsync(new VfsPath("file", dir), new EnumerateOptions(), default))
            if (n.DisplayName == "hello.txt") node = n;
        Assert.NotNull(node);

        var onActivated = typeof(FileManagerWindow).GetMethod("OnItemActivated", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onActivated.Invoke(win, new object?[] { win, new ItemActivatedEventArgs(new ItemViewModel(node!)) });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(file, opener.Opened);
        try { Directory.Delete(dir, true); } catch { }
    }

    [AvaloniaFact]
    public async Task Space_previews_the_file_without_opening_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bevel-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "hello.txt").Replace('\\', '/');   // VfsPath canonicalises to '/'; matches the opener's recorded path cross-platform
        File.WriteAllText(file, "x");

        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var opener = new RecordingOpener();
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.SetFileOpener(opener);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        IVfsNode? node = null;
        await foreach (var n in root.EnumerateAsync(new VfsPath("file", dir), new EnumerateOptions(), default))
            if (n.DisplayName == "hello.txt") node = n;
        Assert.NotNull(node);

        var onPreview = typeof(FileManagerWindow).GetMethod("OnItemPreview", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onPreview.Invoke(win, new object?[] { win, new ItemActivatedEventArgs(new ItemViewModel(node!)) });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(file, opener.Previewed);   // previewed…
        Assert.Empty(opener.Opened);               // …NOT opened
        try { Directory.Delete(dir, true); } catch { }
    }
}
