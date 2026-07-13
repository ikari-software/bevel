using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bevel.App;

internal static class Program
{
    // Avalonia needs an STA thread on Windows; harmless elsewhere.
    [STAThread]
    public static void Main(string[] args)
    {
        var pal = PalSelector.FromArgs(args);

        // Compose via Microsoft.Extensions.Hosting (DI-01). The PAL is selected here,
        // at the composition root, and nowhere else (DI-02).
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services
            .AddBevelPlatform(pal)
            .AddBevelModules();

        using var host = builder.Build();
        App.Services = host.Services;

        // Let a termination signal (SIGTERM / SIGINT / Ctrl-C) drive a clean Avalonia
        // shutdown so window OnClosed handlers and hosted-service Dispose run (e.g. the
        // Dock controller restores the user's Dock preference). .NET on macOS does NOT
        // surface SIGTERM via Console.CancelKeyPress/AppDomain.ProcessExit, so register a
        // POSIX signal handler explicitly (bevel-3kz).
        var appBuilder = BuildAvaloniaApp();
        // Keep the registrations rooted for the whole process lifetime: PosixSignalRegistration
        // unregisters its handler once the instance is garbage-collected.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => App.RequestExit());
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => App.RequestExit());

        // Start the host so IHostedServices run (e.g. the macOS HelperLifecycle). This is
        // non-blocking — hosted services degrade gracefully rather than aborting boot.
        host.Start();

        // Load persisted settings on THIS thread, before StartWithClassicDesktopLifetime turns it
        // into the Avalonia UI thread. A synchronous read here blocks nothing (no dispatcher is
        // pumping yet); doing it inside OnFrameworkInitializationCompleted blocked — and once
        // deadlocked — the UI thread instead (core rule: never block the UI thread).
        host.Services.GetRequiredService<Bevel.Core.SettingsService>()
            .LoadAsync().GetAwaiter().GetResult();

        try
        {
            appBuilder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop hosted services cleanly on exit (kills the helper, restores the Dock).
            // Run on the thread pool, NOT this thread: after the desktop lifetime returns,
            // Avalonia's SynchronizationContext is still installed here, so a plain
            // .GetResult() would post StopAsync's await-continuations back to this blocked
            // main thread and deadlock — the helper never stops and the process hangs on
            // SIGTERM (only kill -9 works). Task.Run detaches from that context (bevel-fu5).
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        }

        if (App.RestartRequested)
        {
            // Let the old helper socket and NSWindows tear down before the child connects.
            Thread.Sleep(400);
            Relaunch(args);
        }
    }

    private static void Relaunch(IReadOnlyList<string> args)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return;

        try
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location ?? "";
            var startInfo = CreateRestartStartInfo(processPath, entryAssemblyPath, args);
            startInfo.WorkingDirectory = !string.IsNullOrEmpty(entryAssemblyPath)
                ? Path.GetDirectoryName(entryAssemblyPath) ?? startInfo.WorkingDirectory
                : Path.GetDirectoryName(processPath) ?? startInfo.WorkingDirectory;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                startInfo = CreateDetachedMacOSStartInfo(startInfo);

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "bevel-restart.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:u}] Bevel restart failed: {ex}\n");
            Console.Error.WriteLine($"Bevel restart failed: {ex.Message} (see {logPath})");
        }
    }

    /// <summary>
    /// Reconstructs the current launch for both a published app executable and a framework-
    /// dependent <c>dotnet Bevel.App.dll</c> invocation, preserving the original arguments.
    /// </summary>
    internal static ProcessStartInfo CreateRestartStartInfo(
        string processPath,
        string entryAssemblyPath,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entryAssemblyPath))
        {
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    /// <summary>
    /// Wraps a launch in <c>nohup … &amp;</c> so the relaunched shell survives the exiting
    /// parent process (macOS process-group / terminal session teardown).
    /// </summary>
    internal static ProcessStartInfo CreateDetachedMacOSStartInfo(ProcessStartInfo inner)
    {
        var command = new System.Text.StringBuilder("nohup ");
        command.Append(QuoteShellArgument(inner.FileName));
        foreach (var arg in inner.ArgumentList)
        {
            command.Append(' ');
            command.Append(QuoteShellArgument(arg));
        }
        var logPath = Path.Combine(Path.GetTempPath(), "bevel-restart.log");
        command.Append($" >>{logPath} 2>&1 &");

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            WorkingDirectory = inner.WorkingDirectory,
            ArgumentList = { "-c", command.ToString() },
        };
    }

    internal static string QuoteShellArgument(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // Referenced by name by the Avalonia XAML previewer/designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
