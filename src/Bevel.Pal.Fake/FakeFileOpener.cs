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
    public List<(string Path, string App)> OpenedWith { get; } = new();
    public List<string> Revealed { get; } = new();

    /// <summary>Handlers returned by <see cref="GetHandlersAsync"/> — settable so tests are deterministic.</summary>
    public List<OpenWithHandler> Handlers { get; } = new();

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

    public Task OpenWithAsync(string path, string appPath, CancellationToken ct = default)
    {
        OpenedWith.Add((path, appPath));
        return Task.CompletedTask;
    }

    public Task RevealAsync(string path, CancellationToken ct = default)
    {
        Revealed.Add(path);
        return Task.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OpenWithHandler>> GetHandlersAsync(string path, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<OpenWithHandler>>(Handlers);
}
