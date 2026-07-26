using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;

// Renders the file-manager window to a PNG via headless Skia. No screen, no focus, no lock
// dependence — the captured frame is exactly what the theme draws, at 96 DPI (1 px = 1 DIP =
// 1 Win2000 px) for 1:1 layout measurement.

var outPath = args.Length > 0 ? args[0] : "fm.png";
var width = args.Length > 1 ? int.Parse(args[1]) : 760;
var height = args.Length > 2 ? int.Parse(args[2]) : 560;

AppBuilder.Configure<Bevel.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

// A deterministic sample folder so the listing has representative rows.
var dir = Path.Combine(Path.GetTempPath(), "bevel-shot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
foreach (var folder in new[] { "My Documents", "My Pictures", "Program Files", "WINNT" })
    Directory.CreateDirectory(Path.Combine(dir, folder));
File.WriteAllText(Path.Combine(dir, "autoexec.bat"), "@echo off");
File.WriteAllText(Path.Combine(dir, "boot.ini"), "[boot loader]");
File.WriteAllText(Path.Combine(dir, "readme.txt"), "hello world");
File.WriteAllText(Path.Combine(dir, "setup.exe"), new string('x', 4096));
File.WriteAllText(Path.Combine(dir, "holiday.png"), "x");
File.WriteAllText(Path.Combine(dir, "backup.zip"), "x");
File.WriteAllText(Path.Combine(dir, "theme song.mp3"), "x");
File.WriteAllText(Path.Combine(dir, "vacation.avi"), "x");
File.WriteAllText(Path.Combine(dir, "index.html"), "x");
File.WriteAllText(Path.Combine(dir, "kernel32.dll"), "x");
File.WriteAllText(Path.Combine(dir, "install.cmd"), "x");
File.WriteAllText(Path.Combine(dir, "Arial.ttf"), "x");
File.WriteAllText(Path.Combine(dir, "manual.pdf"), "x");
File.WriteAllText(Path.Combine(dir, "budget.xlsx"), "x");
File.WriteAllText(Path.Combine(dir, "letter.docx"), "x");
File.WriteAllText(Path.Combine(dir, "pitch.pptx"), "x");
File.WriteAllText(Path.Combine(dir, "contacts.db"), "x");
File.WriteAllText(Path.Combine(dir, "config.xml"), "x");
File.WriteAllText(Path.Combine(dir, "winxp.iso"), "x");

var vfs = new VfsRoot();
vfs.Register(new LocalFsProvider());
vfs.Register(new ComputerProvider());
var settings = new SettingsService();
var fileOps = new FileOperationService(vfs, new DefaultConflictHandler());
var controller = new FileManagerController(vfs, fileOps);

var win = new FileManagerWindow { Width = width, Height = height };
win.SetVfsRoot(vfs);
win.SetSettingsService(settings);
win.SetController(controller);
win.Show();

// Let the default (home) navigation settle first so its streamed rows don't bleed into the
// sample listing, then navigate to the deterministic sample folder.
static void Pump(int cycles)
{
    for (var i = 0; i < cycles; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(12);
    }
}

Pump(30);
controller.NavigateTo(new VfsPath("file", dir));
Pump(60);

// 4th arg "Glyph:<semanticId>" paints a single icon glyph at (width) px on a neutral card —
// e.g. `BevelShot out.png 256 256 Glyph:doc.iso`.
if (args.Length > 3 && args[3].StartsWith("Glyph:", StringComparison.OrdinalIgnoreCase))
{
    var semanticId = args[3]["Glyph:".Length..];
    var glyph = Bevel.UI.Glyphs.GlyphPreview(new IconKey(semanticId), width - 24);
    var host = new Avalonia.Controls.Window
    {
        Width = width,
        Height = height,
        SystemDecorations = Avalonia.Controls.SystemDecorations.None,
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ECECEC")),
        Content = new Avalonia.Controls.Border
        {
            Padding = new Avalonia.Thickness(12),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = glyph,
        },
    };
    host.Show();
    Pump(30);
    var gframe = host.CaptureRenderedFrame() ?? throw new Exception("glyph capture null");
    gframe.Save(outPath);
    Console.WriteLine($"saved {outPath} ({gframe.PixelSize.Width}x{gframe.PixelSize.Height})");
    try { Directory.Delete(dir, recursive: true); } catch { }
    Environment.Exit(0);
}

// 4th arg "CtxMenu" opens the folder-background right-click menu and captures it, to eyeball
// menu colors (bg / text / highlight) headlessly.
if (args.Length > 3 && string.Equals(args[3], "CtxMenu", StringComparison.OrdinalIgnoreCase))
{
    var noop = new Action(() => { });
    var actions = new Bevel.FileManager.Components.ContextMenuActions
    {
        Open = noop, OpenWith = noop, SendTo = noop, Cut = noop, Copy = noop,
        Paste = noop, PasteShortcut = noop, CreateShortcut = noop, Delete = noop,
        Rename = noop, Properties = noop, Undo = noop, Refresh = noop,
        CanPaste = () => true, CanUndo = () => true,
        ViewChanged = _ => { }, ArrangeIcons = _ => { }, NewItem = _ => { },
    };
    // Optional 6th arg "dark": force the OS-style Dark theme variant to reproduce a menu that
    // resolves light text (as macOS Dark mode would) — the Win2000 theme should stay light-locked.
    if (args.Length > 5 && string.Equals(args[5], "dark", StringComparison.OrdinalIgnoreCase))
    {
        // Simulate macOS Dark mode the way the OS does: at the Application level. (Note this
        // overrides an App.axaml Light pin, so it shows what dark WOULD look like, not the fix.)
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Pump(10);
    }
    Console.WriteLine($"App variant = {Avalonia.Application.Current!.ActualThemeVariant}");

    var menu = Bevel.FileManager.Components.ContextMenuBuilder.BuildFolderBackgroundMenu(actions);
    // Attach to the real ItemView (as the on-device path does), not the bare window — the popup
    // resolves DynamicResource colors up through its attach point, so the target matters.
    var target = win.GetVisualDescendants()
        .OfType<Bevel.FileManager.Components.ItemView>().FirstOrDefault() as Avalonia.Controls.Control ?? win;
    menu.Open(target);
    Pump(40);
    // Optional 5th arg: index of an item to force into the :selected (highlight) state so the
    // navy-bg / white-text hover colors are visible in the capture.
    if (args.Length > 4 && int.TryParse(args[4], out var hi) && menu.Items[hi] is Avalonia.Controls.Control mi)
    {
        ((Avalonia.Controls.IPseudoClasses)mi.Classes).Add(":selected");
        Pump(10);
    }
    // Popups render into their own PopupRoot top-level in headless; capture that, not the window.
    var popupRoot = (menu.GetVisualRoot() as Avalonia.Controls.TopLevel)
                    ?? throw new Exception("context menu popup root not found (menu did not open)");
    var cframe = popupRoot.CaptureRenderedFrame() ?? throw new Exception("ctx capture null");
    cframe.Save(outPath);
    Console.WriteLine($"saved {outPath} ({cframe.PixelSize.Width}x{cframe.PixelSize.Height})");
    try { Directory.Delete(dir, recursive: true); } catch { }
    Environment.Exit(0);
}

// 4th arg "Properties" renders the file Properties sheet instead of the main window.
if (args.Length > 3 && string.Equals(args[3], "Properties", StringComparison.OrdinalIgnoreCase))
{
    var nodeTask = vfs.ResolveAsync(new VfsPath("file", dir), default).AsTask();
    while (!nodeTask.IsCompleted) Pump(1);
    var dlg = new Bevel.FileManager.Components.PropertiesDialog(vfs, nodeTask.Result);
    dlg.Show();
    Pump(50);   // let layout settle and the async Contains/size scan finish
    var dframe = dlg.CaptureRenderedFrame() ?? throw new Exception("dialog capture null");
    dframe.Save(outPath);
    Console.WriteLine($"saved {outPath} ({dframe.PixelSize.Width}x{dframe.PixelSize.Height})");
    try { Directory.Delete(dir, recursive: true); } catch { }
    Environment.Exit(0);
}

// Optional view mode (4th arg): Details (default), LargeIcons, SmallIcons, List, Thumbnails.
if (args.Length > 3 && Enum.TryParse<Bevel.FileManager.Components.ViewMode>(args[3], true, out var vm))
{
    win.SetViewMode(vm);
    Pump(20);
}

// Preview the selection highlight (navy bar + white text) in the shot.
win.SelectInList(new VfsPath("file", Path.Combine(dir, "readme.txt")));
Pump(5);

var frame = win.CaptureRenderedFrame();
if (frame is null)
{
    Console.Error.WriteLine("capture returned null");
    Environment.Exit(1);
}

frame.Save(outPath);
Console.WriteLine($"saved {outPath} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");

try { Directory.Delete(dir, recursive: true); } catch { }
Environment.Exit(0);
