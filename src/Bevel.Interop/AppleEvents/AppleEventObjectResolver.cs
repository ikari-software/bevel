using System.Linq;
using Bevel.Core.Vfs;

namespace Bevel.Interop.AppleEvents;

/// <summary>
/// Resolves a parsed <see cref="ObjectSpecifier"/> to VFS path(s) against the shared
/// <see cref="VfsRoot"/> and known folders (08-os-interop.md §2.1.2). This is the hand-written AE
/// object-model resolver the spec calls out as owned scope — pure managed code over the command
/// model, so a resolver fault throws <see cref="AutomationException"/> and the native handler turns
/// that into <c>errAEEventNotHandled</c> rather than crashing the app. Single specifiers resolve to
/// one path; <c>every …</c> / <c>whose …</c> to many.
/// </summary>
public sealed class AppleEventObjectResolver
{
    private static readonly EnumerateOptions AllItems = new() { IncludeHidden = true, IncludeSystem = true };

    private readonly VfsRoot _vfs;
    private readonly IKnownFolders _known;

    public AppleEventObjectResolver(VfsRoot vfs, IKnownFolders? knownFolders = null)
    {
        _vfs = vfs;
        _known = knownFolders ?? SystemKnownFolders.Instance;
    }

    public async Task<IReadOnlyList<VfsPath>> ResolveAsync(ObjectSpecifier spec, CancellationToken ct = default)
    {
        switch (spec)
        {
            case PropertySpecifier p:
                return new[] { KnownFolder(p.Property) };

            case PosixPathSpecifier pp:
                return new[] { new VfsPath("file", pp.Path) };

            case ElementByName e:
            {
                var container = await ResolveContainerAsync(e.Container, ct);
                var children = await ChildrenAsync(container, e.Class, ct);
                var match = children.FirstOrDefault(c =>
                    string.Equals(c.Path.FileName, e.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.DisplayName, e.Name, StringComparison.OrdinalIgnoreCase));
                if (match is null) throw new AutomationException($"can't get {e.Class.ToString().ToLowerInvariant()} \"{e.Name}\": not found.");
                return new[] { match.Path };
            }

            case ElementByIndex e:
            {
                var container = await ResolveContainerAsync(e.Container, ct);
                var children = await ChildrenAsync(container, e.Class, ct);
                var index = e.Index == -1 ? children.Count - 1 : e.Index - 1;   // 1-based; -1 = last
                if (index < 0 || index >= children.Count)
                    throw new AutomationException($"index {e.Index} is out of range (container has {children.Count}).");
                return new[] { children[index].Path };
            }

            case EveryElement e:
            {
                var container = await ResolveContainerAsync(e.Container, ct);
                var children = await ChildrenAsync(container, e.Class, ct);
                var filtered = e.Filter is null ? children : children.Where(c => Matches(c, e.Filter!));
                return filtered.Select(c => c.Path).ToArray();
            }

            default:
                throw new AutomationException("unsupported object specifier.");
        }
    }

    /// <summary>Resolves a container specifier to exactly one path; a null container defaults to home.</summary>
    private async Task<VfsPath> ResolveContainerAsync(ObjectSpecifier? container, CancellationToken ct)
    {
        if (container is null) return _known.Home;
        var resolved = await ResolveAsync(container, ct);
        if (resolved.Count != 1)
            throw new AutomationException("a container must resolve to exactly one object.");
        return resolved[0];
    }

    private async Task<IReadOnlyList<IVfsNode>> ChildrenAsync(VfsPath container, AeClass klass, CancellationToken ct)
    {
        var nodes = new List<IVfsNode>();
        await foreach (var child in _vfs.EnumerateAsync(container, AllItems, ct))
            if (ClassMatches(klass, child)) nodes.Add(child);
        return nodes;
    }

    private VfsPath KnownFolder(string property) => property.ToLowerInvariant() switch
    {
        "home" => _known.Home,
        "desktop" => _known.Desktop,
        "trash" => _known.Trash,
        "startup disk" or "startupdisk" => _known.StartupDisk,
        _ => throw new AutomationException($"unknown property \"{property}\"."),
    };

    private static bool ClassMatches(AeClass klass, IVfsNode node) => klass switch
    {
        AeClass.Item => true,
        AeClass.File => node.Kind is VfsNodeKind.File or VfsNodeKind.Link,
        AeClass.Folder => node.Kind == VfsNodeKind.Folder,
        AeClass.Disk => node.Kind == VfsNodeKind.Volume,
        _ => false,
    };

    private static bool Matches(IVfsNode node, WhoseFilter filter)
    {
        var actual = filter.Key switch
        {
            WhoseKey.Name => node.Path.FileName,
            WhoseKey.NameExtension => Extension(node.Path.FileName),
            WhoseKey.Kind => KindOf(node),
            _ => "",
        };
        return filter.Op switch
        {
            WhoseOp.Equals => actual.Equals(filter.Value, StringComparison.OrdinalIgnoreCase),
            WhoseOp.BeginsWith => actual.StartsWith(filter.Value, StringComparison.OrdinalIgnoreCase),
            WhoseOp.EndsWith => actual.EndsWith(filter.Value, StringComparison.OrdinalIgnoreCase),
            WhoseOp.Contains => actual.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..];
    }

    private static string KindOf(IVfsNode node) => node.Kind switch
    {
        VfsNodeKind.Folder => "folder",
        VfsNodeKind.Volume => "disk",
        VfsNodeKind.Link => "alias file",
        _ => "document file",
    };
}
