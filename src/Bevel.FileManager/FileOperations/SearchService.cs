using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// A single "find files/folders" request: where to look, what to match on the display name,
/// and how far/wide the walk is allowed to go before it gives up.
/// </summary>
/// <param name="Root">The folder to search (its subtree is walked recursively).</param>
/// <param name="NameQuery">Case-insensitive substring to match against each node's DisplayName.</param>
/// <param name="MaxDepth">
/// How many folder levels below <see cref="Root"/> to recurse into. 0 searches only the direct
/// children of <see cref="Root"/>; 1 additionally searches one level of subfolders, and so on.
/// </param>
/// <param name="MaxResults">Stop yielding once this many matches have been found (0 = unlimited).</param>
public sealed record SearchQuery(VfsPath Root, string NameQuery, int MaxDepth = 6, int MaxResults = 500);

/// <summary>
/// Walks a VFS subtree looking for nodes whose display name contains a query string
/// (case-insensitive). Depth- and result-capped so a search of a huge tree can't run away.
/// Folders that fail to enumerate (permission errors, unsupported schemes, races with a
/// concurrent delete, etc.) are skipped rather than aborting the whole search.
/// </summary>
public sealed class SearchService
{
    private readonly VfsRoot _vfsRoot;

    public SearchService(VfsRoot vfsRoot)
    {
        _vfsRoot = vfsRoot ?? throw new ArgumentNullException(nameof(vfsRoot));
    }

    /// <summary>Search <paramref name="root"/>'s subtree for nodes whose name contains <paramref name="nameQuery"/>.</summary>
    public IAsyncEnumerable<IVfsNode> SearchAsync(
        VfsPath root, string nameQuery, int maxDepth = 6, int maxResults = 500, CancellationToken ct = default)
        => SearchAsync(new SearchQuery(root, nameQuery, maxDepth, maxResults), ct);

    /// <summary>Search using a pre-built <see cref="SearchQuery"/>.</summary>
    public async IAsyncEnumerable<IVfsNode> SearchAsync(
        SearchQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pattern = (query.NameQuery ?? string.Empty).Trim();
        if (pattern.Length == 0)
            yield break;

        var maxResults = query.MaxResults <= 0 ? int.MaxValue : query.MaxResults;
        var options = new EnumerateOptions { IncludeHidden = true, IncludeSystem = false };

        // Breadth-first by depth level so MaxDepth is a simple per-level cutoff, and so shallow
        // matches surface before the walk spends time deep in the tree.
        var currentLevel = new List<VfsPath> { query.Root };
        var depth = 0;
        var found = 0;

        while (currentLevel.Count > 0 && found < maxResults)
        {
            var nextLevel = new List<VfsPath>();

            foreach (var folder in currentLevel)
            {
                ct.ThrowIfCancellationRequested();

                List<IVfsNode> children;
                try
                {
                    children = await CollectChildrenAsync(folder, options, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Inaccessible / unsupported / raced-away folder — skip it, keep searching.
                    continue;
                }

                foreach (var node in children)
                {
                    if (node.Kind == VfsNodeKind.Folder && depth < query.MaxDepth)
                        nextLevel.Add(node.Path);

                    if (!NameMatches(node.DisplayName, pattern))
                        continue;

                    found++;
                    yield return node;

                    if (found >= maxResults)
                        yield break;
                }
            }

            currentLevel = nextLevel;
            depth++;
        }
    }

    private async Task<List<IVfsNode>> CollectChildrenAsync(VfsPath folder, EnumerateOptions options, CancellationToken ct)
    {
        var list = new List<IVfsNode>();
        await foreach (var node in _vfsRoot.EnumerateAsync(folder, options, ct).ConfigureAwait(false))
            list.Add(node);
        return list;
    }

    private static bool NameMatches(string displayName, string pattern)
        => displayName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
}
