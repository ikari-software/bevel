using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Fake;

/// <summary>Records opened paths instead of launching anything — deterministic for tests and the
/// Fake PAL (<c>--pal=fake</c>).</summary>
public sealed class FakeFileOpener : IFileOpener
{
    public List<string> Opened { get; } = new();
    public List<string> Previewed { get; } = new();

    public Task OpenPathAsync(string path, CancellationToken ct = default)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task PreviewAsync(string path, CancellationToken ct = default)
    {
        Previewed.Add(path);
        return Task.CompletedTask;
    }
}
