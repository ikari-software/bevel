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
    public Task OpenPathAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        Process.Start(psi);
        return Task.CompletedTask;
    }
}
