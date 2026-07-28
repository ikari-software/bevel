using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>Opens a document/path with its default macOS handler via <c>/usr/bin/open</c> — exactly
/// what Finder does on double-click. Local to the calling process; no helper or shell-core involvement.
/// The path is passed as a discrete argument (ArgumentList), so spaces and shell metacharacters are safe.</summary>
public sealed class MacOSFileOpener : IFileOpener
{
    private Process? _quickLook;

    public Task OpenPathAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        Process.Start(psi);
        return Task.CompletedTask;
    }

    /// <summary>Quick Look preview via <c>qlmanage -p</c> (the CLI entry point to Quick Look). Any
    /// existing preview is replaced, so pressing Space on a new file swaps the panel instead of stacking
    /// windows — closest to Finder's single shared Quick Look panel.</summary>
    public Task PreviewAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        try { if (_quickLook is { HasExited: false }) _quickLook.Kill(); } catch { /* already gone */ }

        var psi = new ProcessStartInfo("/usr/bin/qlmanage")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,   // qlmanage is chatty — swallow its diagnostics
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(path);
        try { _quickLook = Process.Start(psi); } catch { _quickLook = null; }
        return Task.CompletedTask;
    }

    /// <summary>Opens a document with a chosen app: <c>open -a &lt;app&gt; &lt;path&gt;</c>. Both are discrete
    /// arguments (ArgumentList), so spaces/metacharacters are safe.</summary>
    public Task OpenWithAsync(string path, string appPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(appPath))
            return Task.CompletedTask;

        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(appPath);
        psi.ArgumentList.Add(path);
        try { Process.Start(psi); } catch { /* app vanished / not launchable */ }
        return Task.CompletedTask;
    }

    /// <summary>Reveals + selects a path in Finder: <c>open -R &lt;path&gt;</c>.</summary>
    public Task RevealAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add(path);
        try { Process.Start(psi); } catch { /* nothing to reveal */ }
        return Task.CompletedTask;
    }

    /// <summary>Enumerates the apps that can open a path via <c>[NSWorkspace URLsForApplicationsToOpenURL:]</c>
    /// (macOS 12+), default handler first. Runs synchronously on the calling thread — meant to be called on
    /// the UI thread, which has an autorelease pool draining each event-loop turn, so the autoreleased
    /// NSURL/NSArray objects are neither leaked nor prematurely freed. Returns an empty list when AppKit is
    /// unavailable (headless).</summary>
    public ValueTask<IReadOnlyList<OpenWithHandler>> GetHandlersAsync(string path, CancellationToken ct = default)
    {
        var handlers = new List<OpenWithHandler>();
        if (string.IsNullOrWhiteSpace(path))
            return ValueTask.FromResult<IReadOnlyList<OpenWithHandler>>(handlers);

        var workspace = AppKitInterop.SharedWorkspace();
        var url = workspace == IntPtr.Zero ? IntPtr.Zero : AppKitInterop.FileUrl(path);
        if (url == IntPtr.Zero)
            return ValueTask.FromResult<IReadOnlyList<OpenWithHandler>>(handlers);

        // The default handler, so we can flag + float it to the top.
        var defaultUrl = AppKitInterop.URLForApplicationToOpenURL(workspace, url);
        var defaultPath = AppKitInterop.NSStringToString(AppKitInterop.NSURLPath(defaultUrl));

        var apps = AppKitInterop.URLsForApplicationsToOpenURL(workspace, url);
        var count = AppKitInterop.NSArrayCount(apps);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var appUrl = AppKitInterop.NSArrayObjectAtIndex(apps, i);
            var appPath = AppKitInterop.NSStringToString(AppKitInterop.NSURLPath(appUrl));
            if (string.IsNullOrEmpty(appPath) || !seen.Add(appPath))
                continue;

            var isDefault = defaultPath is not null && string.Equals(defaultPath, appPath, StringComparison.OrdinalIgnoreCase);
            handlers.Add(new OpenWithHandler(Path.GetFileNameWithoutExtension(appPath), appPath, BundleId: null, isDefault));
        }

        // Default first, then alphabetical — matches Finder's "Open With" ordering closely enough.
        handlers.Sort((a, b) =>
            a.IsDefault != b.IsDefault
                ? (a.IsDefault ? -1 : 1)
                : string.Compare(a.AppName, b.AppName, StringComparison.OrdinalIgnoreCase));

        return ValueTask.FromResult<IReadOnlyList<OpenWithHandler>>(handlers);
    }
}
