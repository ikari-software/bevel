using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>End-to-end Folder Options: a real FileManagerWindow listing a real temp dir must reflect Show
/// Hidden + Hide Extensions when ApplyFolderOptions() runs (the exact call the dialog-close and cross-window
/// fan-out make). Uses an ISOLATED settings dir so it never touches the user's real settings DB.</summary>
public sealed class FolderOptionsLiveTest : IDisposable
{
    private readonly string _dir;
    private readonly string _cfg;

    public FolderOptionsLiveTest()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-fo-{Guid.NewGuid():N}");
        _cfg = Path.Combine(Path.GetTempPath(), $"bevel-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_cfg);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, ".secret"), "x");   // dotfile → hidden by convention
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        try { Directory.Delete(_cfg, true); } catch { }
    }

    // ── Mechanism-level: the provider's hidden/dotfile filter ─────────────────────────
    private async Task<List<string>> Enumerate(bool includeHidden)
    {
        var provider = new LocalFsProvider();
        var names = new List<string>();
        await foreach (var n in provider.EnumerateAsync(new VfsPath("file", _dir),
                                    new EnumerateOptions { IncludeHidden = includeHidden }, default))
            names.Add(n.DisplayName);
        return names;
    }

    [Fact]
    public async Task Show_hidden_filters_dotfiles_at_the_provider()
    {
        var off = await Enumerate(includeHidden: false);
        Assert.Contains("notes.txt", off);
        Assert.DoesNotContain(".secret", off);

        var on = await Enumerate(includeHidden: true);
        Assert.Contains(".secret", on);
    }

    // ── End-to-end: the window applies the settings live via ApplyFolderOptions() ──────
    private static IReadOnlyList<ItemViewModel> Vms(FileManagerWindow win)
    {
        var itemView = win.GetType().GetField("ItemView", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        var list = (IEnumerable)itemView.GetType().GetField("_viewModels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(itemView)!;
        return list.Cast<ItemViewModel>().ToList();
    }

    private static void Pump(int ms = 800)
    {
        var end = Environment.TickCount + ms;
        while (Environment.TickCount < end) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Window_applies_show_hidden_and_hide_extensions_live()
    {
        ItemViewModel.HideKnownExtensions = false;
        var settings = new SettingsService(_cfg);   // isolated DB — defaults: hidden off, extensions off
        await settings.LoadAsync();

        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.SetSettingsService(settings);
        win.SetController(new FileManagerController(root, new FileOperationService(root, new DefaultConflictHandler())));
        win.Show();
        ((FileManagerController)win.ActiveController!).NavigateTo(new VfsPath("file", _dir));
        Pump();

        var n0 = Vms(win).Select(v => v.DisplayName).ToList();
        Assert.True(n0.Contains("notes.txt") && !n0.Contains(".secret"), $"initial: [{string.Join(",", n0)}]");

        await settings.UpdateAsync(c => c.ShowHiddenFiles = true);
        win.ApplyFolderOptions();
        Pump();
        var n1 = Vms(win).Select(v => v.DisplayName).ToList();
        Assert.True(n1.Contains(".secret"), $"after ShowHidden: [{string.Join(",", n1)}]");

        await settings.UpdateAsync(c => c.HideKnownExtensions = true);
        win.ApplyFolderOptions();
        Pump();
        var n2 = Vms(win).Select(v => v.DisplayName).ToList();
        Assert.True(n2.Contains("notes") && !n2.Contains("notes.txt"), $"after HideExt: [{string.Join(",", n2)}]");

        ItemViewModel.HideKnownExtensions = false;
    }
}
