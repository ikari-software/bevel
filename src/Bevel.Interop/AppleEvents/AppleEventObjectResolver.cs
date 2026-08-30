using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>Hard cap on the number of directory entries an inbound get/count/exists is allowed to
    /// walk on the UI thread (bevel-o3sa). The AE query handlers reply synchronously on Avalonia's AE
    /// dispatch thread (the UI thread), so an unbounded <c>count of every file of folder &lt;hugeDir&gt;</c>
    /// would freeze the whole shell for the length of the walk. Capping the enumeration keeps that walk
    /// bounded: <c>count</c> returns at most this many, <c>get</c> returns at most this many, and
    /// <c>exists</c> short-circuits (see <see cref="ExistsAsync"/>) so it never approaches the cap.
    /// 0 (the default on <see cref="ResolveAsync"/>) means unbounded — used by the async command path,
    /// which does not run on the UI thread.</summary>
    public const int UiThreadEnumerationCap = 4096;

    private readonly VfsRoot _vfs;
    private readonly IKnownFolders _known;

    public AppleEventObjectResolver(VfsRoot vfs, IKnownFolders? knownFolders = null)
    {
        _vfs = vfs;
        _known = knownFolders ?? SystemKnownFolders.Instance;
    }

    /// <param name="enumerationCap">Maximum directory entries to walk per container (0 = unbounded).
    /// Pass <see cref="UiThreadEnumerationCap"/> from any caller that resolves on the UI thread so a
    /// pathological directory can't freeze the shell (bevel-o3sa).</param>
    public async Task<IReadOnlyList<VfsPath>> ResolveAsync(ObjectSpecifier spec, CancellationToken ct = default, int enumerationCap = 0)
    {
        switch (spec)
        {
            case PropertySpecifier p:
                return new[] { KnownFolder(p.Property) };

            case PosixPathSpecifier pp:
                return new[] { new VfsPath("file", pp.Path) };

            case ElementByName e:
            {
                var container = await ResolveContainerAsync(e.Container, ct, enumerationCap);
                await foreach (var child in EnumerateChildrenAsync(container, e.Class, enumerationCap, ct))
                    if (NameMatches(child, e.Name)) return new[] { child.Path };   // stop at the first match — no full walk
                throw new AutomationException($"can't get {e.Class.ToString().ToLowerInvariant()} \"{e.Name}\": not found.");
            }

            case ElementByIndex e:
            {
                var container = await ResolveContainerAsync(e.Container, ct, enumerationCap);
                var children = await ChildrenAsync(container, e.Class, enumerationCap, ct);
                var index = e.Index == -1 ? children.Count - 1 : e.Index - 1;   // 1-based; -1 = last
                if (index < 0 || index >= children.Count)
                    throw new AutomationException($"index {e.Index} is out of range (container has {children.Count}).");
                return new[] { children[index].Path };
            }

            case EveryElement e:
            {
                var container = await ResolveContainerAsync(e.Container, ct, enumerationCap);
                var matches = new List<VfsPath>();
                await foreach (var child in EnumerateChildrenAsync(container, e.Class, enumerationCap, ct))
                    if (e.Filter is null || Matches(child, e.Filter!)) matches.Add(child.Path);
                return matches;
            }

            default:
                throw new AutomationException("unsupported object specifier.");
        }
    }

    /// <summary>Whether a specifier resolves to at least one object, WITHOUT walking the whole
    /// container. For element specifiers this stops at the first qualifying child (or, for a positive
    /// index, as soon as that index is reachable), so <c>exists</c> is O(first match) rather than
    /// O(directory) — the property the bevel-o3sa fix relies on to stay cheap on the UI thread.</summary>
    /// <param name="enumerationCap">See <see cref="ResolveAsync"/>. Bounds the worst case (a container
    /// with zero matches) so even a non-matching <c>exists</c> can't run away on the UI thread.</param>
    public async Task<bool> ExistsAsync(ObjectSpecifier spec, CancellationToken ct = default, int enumerationCap = 0)
    {
        switch (spec)
        {
            case PropertySpecifier:
            case PosixPathSpecifier:
                // No enumeration involved — a resolvable property/literal is treated as existing.
                return (await ResolveAsync(spec, ct, enumerationCap)).Count > 0;

            case ElementByName e:
            {
                var container = await ResolveContainerAsync(e.Container, ct, enumerationCap);
                await foreach (var child in EnumerateChildrenAsync(container, e.Class, enumerationCap, ct))
                    if (NameMatches(child, e.Name)) return true;
                return false;
            }

            case ElementByIndex e:
            {
                var container = await ResolveContainerAsync(e.Container, ct, enumerationCap);
                if (e.Index == 0) return false;
                var seen = 0;
                await foreach (var _ in EnumerateChildrenAsync(container, e.Class, enumerationCap, ct))
                {
                    seen++;
                    if (e.Index == -1) return true;          // "last" exists iff there is at least one
                    if (seen >= e.Index) return true;        // positive index is reachable
                }
                return false;
            }

            case EveryElement e:
            {
                var container = await ResolveContainerAsync(e.Container, ct, enumerationCap);
                await foreach (var child in EnumerateChildrenAsync(container, e.Class, enumerationCap, ct))
                    if (e.Filter is null || Matches(child, e.Filter!)) return true;
                return false;
            }

            default:
                throw new AutomationException("unsupported object specifier.");
        }
    }

    private static bool NameMatches(IVfsNode c, string name)
        => string.Equals(c.Path.FileName, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a container specifier to exactly one path; a null container defaults to home.</summary>
    private async Task<VfsPath> ResolveContainerAsync(ObjectSpecifier? container, CancellationToken ct, int enumerationCap)
    {
        if (container is null) return _known.Home;
        var resolved = await ResolveAsync(container, ct, enumerationCap);
        if (resolved.Count != 1)
            throw new AutomationException("a container must resolve to exactly one object.");
        return resolved[0];
    }

    /// <summary>Streams a container's children matching <paramref name="klass"/>, lazily, so callers
    /// that only need the first match (exists, name lookup) never force the whole walk. The
    /// <paramref name="enumerationCap"/> bounds how many raw entries the underlying provider yields.</summary>
    private async IAsyncEnumerable<IVfsNode> EnumerateChildrenAsync(
        VfsPath container, AeClass klass, int enumerationCap, [EnumeratorCancellation] CancellationToken ct)
    {
        var options = enumerationCap > 0
            ? new EnumerateOptions { IncludeHidden = true, IncludeSystem = true, Limit = enumerationCap }
            : AllItems;
        await foreach (var child in _vfs.EnumerateAsync(container, options, ct))
            if (ClassMatches(klass, child)) yield return child;
    }

    private async Task<IReadOnlyList<IVfsNode>> ChildrenAsync(VfsPath container, AeClass klass, int enumerationCap, CancellationToken ct)
    {
        var nodes = new List<IVfsNode>();
        await foreach (var child in EnumerateChildrenAsync(container, klass, enumerationCap, ct))
            nodes.Add(child);
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
