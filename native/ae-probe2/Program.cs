using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Bevel.Pal.MacOS;

internal static class Program
{
    internal static readonly string Log =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ae-avalonia.log");

    internal static void Trace(string s) => File.AppendAllText(Log, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");

    [STAThread]
    private static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("BEVEL_AE_TRACE", "1");   // harness: enable AppleEventInbound trace
        Trace($"=== main (pid {Environment.ProcessId}) ===");
        AppBuilder.Configure<TestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class TestApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        AppleEventInbound.Handler = req => Program.Trace(
            $"HANDLER FIRED: {req.Verb} paths=[{string.Join(", ", req.Paths)}] " +
            $"specs=[{string.Join(" | ", req.Specifiers)}] container={req.Container} name={req.Name}");
        AppleEventInbound.Install();
        Program.Trace("installed AE handlers; entering Avalonia [NSApp run] loop");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window { Width = 200, Height = 70, Title = "Bevel AE Test" };

        base.OnFrameworkInitializationCompleted();
    }
}
