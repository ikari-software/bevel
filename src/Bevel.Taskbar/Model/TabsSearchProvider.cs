using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Search;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// The "Tabs" search section (bevel-vtc6): type-to-jump across every running <see cref="ITabProvider"/>
/// app (browsers, terminals). The section rides the Start menu's <see cref="SearchSession"/> next to
/// Programs/Files, ordered after them.
///
/// <para><b>One cache owner.</b> This provider never calls <see cref="ITabProvider.GetTabsAsync"/>
/// directly: every enumeration goes through the <paramref name="warm"/> delegate (the view's
/// <see cref="TabPrefetch"/>), so the favicons stay decoded once and the hover-warmed lists are reused.
/// An app the provider does not speak (and <see cref="TabPrefetch"/> agrees) is never queried at all —
/// no osascript, no Automation consent prompt for an app with no tab dialect.</para>
///
/// <para><b>Cache-first paint.</b> An osascript spawn is 100-650 ms per app (bevel-1fy8), far too slow
/// for a keystroke, so <see cref="QueryAsync"/> publishes FIRST from the rows of the last completed warm
/// (synchronously, before its first await — the instant first paint) and only then awaits all
/// <paramref name="warm"/> calls in parallel and publishes the fresher picture.</para>
///
/// <para><b>Activation re-resolves.</b> <see cref="AppTab"/> refs are positional, so the row the user
/// clicked may be stale by activation time. <see cref="ActivateAsync"/> re-warms the app and re-resolves
/// the tab by Url (else Title) against the FRESH rows before calling
/// <see cref="ITabProvider.ActivateAsync"/> — no match is a silent no-op, never a press on whatever tab
/// now occupies the remembered index.</para>
/// </summary>
internal sealed class TabsSearchProvider : ISearchProvider
{
    /// <summary>Rows shown per query — a browser can hold hundreds of tabs; the section stays scannable.</summary>
    private const int MaxRows = 8;

    /// <summary>Mirrors TaskButtonMenu's per-tab activation budget.</summary>
    private static readonly TimeSpan ActivateBudget = TimeSpan.FromSeconds(4);

    private readonly Func<IReadOnlyList<string>> _runningBundleIds;
    private readonly Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> _warm;
    private readonly ITabProvider _tabs;
    private readonly object _gate = new();

    /// <summary>The rows of the last completed warm, per app — the instant-first-paint source.</summary>
    private readonly Dictionary<string, IReadOnlyList<TaskButtonMenu.TabMenuRow>> _lastWarm = new(StringComparer.Ordinal);

    public TabsSearchProvider(
        Func<IReadOnlyList<string>> runningBundleIds,
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm,
        ITabProvider tabs)
    {
        _runningBundleIds = runningBundleIds;
        _warm = warm;
        _tabs = tabs;
    }

    public string SectionId => "tabs";
    public string SectionTitle => "Tabs";
    public int Order => 2;
    public bool RunsInline => false;
    public TimeSpan Debounce => TimeSpan.Zero;
    /// <summary>Sized like <see cref="TabPrefetch"/>'s budget: all supported apps are queried in
    /// PARALLEL, so 1500 ms covers even a ~0.65 s 100-tab Arc enumeration.</summary>
    public TimeSpan Budget => TimeSpan.FromMilliseconds(1500);

    public async Task QueryAsync(string query, int maxResults, Action<IReadOnlyList<SearchHit>> publish, CancellationToken ct)
    {
        // The SupportsApp gate runs HERE (not only inside TabPrefetch.Warm) so an unsupported running app
        // is never even asked for a warm.
        var ids = _runningBundleIds()
            .Where(id => !string.IsNullOrEmpty(id) && _tabs.SupportsApp(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Instant first paint: rank the last completed warm's rows. This runs before the first await,
        // so the publish is already out when QueryAsync returns its task.
        publish(BuildHits(query, ids, maxResults));

        // Then refresh every supported app in parallel and publish the fresher picture.
        var refreshes = new List<Task>(ids.Length);
        foreach (var id in ids)
        {
            var work = _warm(id);
            if (work is not null) refreshes.Add(RefreshAsync(id, work, ct));
        }
        await Task.WhenAll(refreshes).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        publish(BuildHits(query, ids, maxResults));
    }

    private async Task RefreshAsync(string bundleId, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>> work, CancellationToken ct)
    {
        try
        {
            var rows = await work.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate) _lastWarm[bundleId] = rows;
        }
        catch (OperationCanceledException) { throw; }   // budget/debounce cancel: the first paint stands
        catch { /* one app failing keeps its previous rows — the section never throws */ }
    }

    /// <summary>Ranks the cached rows against the query: title matches first, a Url match only as a
    /// fallback ranked below every title hit (rank+3), stable in row order, capped at
    /// <see cref="MaxRows"/> (and the session's maxResults). The row itself is the hit's
    /// <see cref="SearchHit.Tag"/>, so the results view binds its favicon straight off it.</summary>
    private IReadOnlyList<SearchHit> BuildHits(string query, IReadOnlyList<string> ids, int maxResults)
    {
        var cap = Math.Min(maxResults, MaxRows);
        var ranked = new List<(int Rank, int Order, SearchHit Hit)>();
        var order = 0;
        foreach (var id in ids)
        {
            IReadOnlyList<TaskButtonMenu.TabMenuRow> rows;
            lock (_gate)
            {
                if (!_lastWarm.TryGetValue(id, out rows!)) continue;
            }
            foreach (var row in rows)
            {
                var rank = SearchRanker.RankOf(row.Tab.Title, query);
                if (rank < 0 && row.Tab.Url is { Length: > 0 } url)
                {
                    var urlRank = SearchRanker.RankOf(url, query);
                    if (urlRank >= 0) rank = urlRank + 3;
                }
                if (rank < 0) continue;
                ranked.Add((rank, order++, new SearchHit(
                    row.Tab.Title,
                    row.Tab.Url is { Length: > 0 } subtitle ? subtitle : null,
                    rank,
                    () => ActivateAsync(id, row),
                    row)));
            }
        }
        ranked.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        return ranked.Take(cap).Select(static r => r.Hit).ToArray();
    }

    /// <summary>Re-warms <paramref name="bundleId"/>, re-resolves <paramref name="stale"/> by Url (else
    /// Title) against the fresh rows, and activates THAT row. No match = no-op.</summary>
    private async Task ActivateAsync(string bundleId, TaskButtonMenu.TabMenuRow stale)
    {
        try
        {
            var work = _warm(bundleId);
            if (work is null) return;
            var rows = await work.ConfigureAwait(false);
            lock (_gate) _lastWarm[bundleId] = rows;
            var fresh = rows.FirstOrDefault(r => r.Tab.Url is { Length: > 0 } u && u == stale.Tab.Url)
                     ?? rows.FirstOrDefault(r => r.Tab.Title == stale.Tab.Title);
            if (fresh is null) return;
            using var cts = new CancellationTokenSource(ActivateBudget);
            await _tabs.ActivateAsync(fresh.Tab, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TaskbarLog.Debug($"TABS search activation failed for {bundleId}: {ex.GetType().Name}");
        }
    }
}
