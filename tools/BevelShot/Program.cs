using Avalonia;
using Avalonia.Headless;
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
