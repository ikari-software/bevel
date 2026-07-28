using System.Diagnostics;
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
}
