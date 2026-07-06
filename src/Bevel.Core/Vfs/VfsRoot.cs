namespace Bevel.Core.Vfs;

/// <summary>
/// Routes VFS paths to the appropriate provider by scheme.
/// This is the single entry point for all file manager UI.
/// </summary>
public sealed class VfsRoot
{
    private readonly Dictionary<string, IVfsProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Schemes => _providers.Keys;

    public void Register(IVfsProvider provider)
    {
        _providers[provider.Scheme] = provider;
    }

    public IVfsProvider GetProvider(string scheme)
    {
        if (_providers.TryGetValue(scheme, out var provider))
            return provider;
        throw new KeyNotFoundException($"No VFS provider registered for scheme '{scheme}'.");
    }

    public IVfsProvider GetProvider(VfsPath path) => GetProvider(path.Scheme);

    public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
        => GetProvider(path).ResolveAsync(path, ct);

    public IAsyncEnumerable<IVfsNode> EnumerateAsync(VfsPath folder, EnumerateOptions options, CancellationToken ct)
        => GetProvider(folder).EnumerateAsync(folder, options, ct);

    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
        => GetProvider(file).OpenReadAsync(file, ct);

    public IDirectoryWatcher? CreateWatcher(VfsPath folder)
        => GetProvider(folder).CreateWatcher(folder);
}
