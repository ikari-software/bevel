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
        // Fake query answers to exercise the native reply-descriptor plumbing (bevel-3i4).
        AppleEventInbound.QueryHandler = q =>
        {
            Program.Trace($"QUERY: {q.Kind} op={q.Op} spec={q.Specifier}");
            if (q.Op == QueryOp.Exists) return new AeBool(q.Specifier is not null);   // fake: exists iff a specifier
            return q.Kind switch
            {
                AeQueryKind.Version => new AeText("9.9-probe"),
                AeQueryKind.Home => new AePath("/Users/ikari"),
                AeQueryKind.Desktop => new AePath("/Users/ikari/Desktop"),
                AeQueryKind.WindowCount => new AeCount(3),
                AeQueryKind.Selection => new AePaths(new[] { "/tmp/a.txt", "/tmp/b.txt" }),
                _ => null,
            };
        };
        AppleEventInbound.Install();
        Program.Trace("installed AE handlers; entering Avalonia [NSApp run] loop");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window { Width = 200, Height = 70, Title = "Bevel AE Test" };

        base.OnFrameworkInitializationCompleted();
    }
}
