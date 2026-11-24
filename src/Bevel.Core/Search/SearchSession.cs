namespace Bevel.Core.Search;

/// <summary>
/// Drives one search box over a set of <see cref="ISearchProvider"/>s (bevel-6sk6). Thread-affinity-free:
/// <paramref name="post"/> marshals every UI-bound notification onto the caller's chosen thread (the
/// view passes <c>Dispatcher.UIThread.Post</c>, tests pass <c>a => a()</c>).
///
/// Semantics: <see cref="SetQuery"/> cancels the previous query's token source, bumps a generation counter,
/// runs <see cref="ISearchProvider.RunsInline"/> providers synchronously, and for the rest starts a
/// debounced, budget-capped background query. Every publish is generation-checked twice (before and after
/// <paramref name="post"/>) so a late answer from a superseded query is dropped, never shown.
/// </summary>
public sealed class SearchSession
{
    /// <summary>Max hits a provider is asked for per section.</summary>
    public const int DefaultMaxResults = 20;

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly Action<Action> _post;
    private readonly int _maxResults;
    private readonly Dictionary<string, IReadOnlyList<SearchHit>> _sections = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private int _generation;

    public SearchSession(IReadOnlyList<ISearchProvider> providers, Action<Action> post, int maxResults = DefaultMaxResults)
    {
        _providers = providers.OrderBy(p => p.Order).ToArray();
        _post = post;
        _maxResults = maxResults;
        foreach (var p in _providers) _sections[p.SectionId] = Array.Empty<SearchHit>();
    }

    /// <summary>The providers, in display order (ascending <see cref="ISearchProvider.Order"/>).</summary>
    public IReadOnlyList<ISearchProvider> Providers => _providers;

    /// <summary>Raised (via <paramref name="post"/>) whenever a section's hits are replaced.</summary>
    public event Action<string, IReadOnlyList<SearchHit>>? SectionChanged;

    /// <summary>The current query, empty when cleared.</summary>
    public string Query { get; private set; } = "";

    /// <summary>The best hit overall: first hit of the first non-empty section in provider order.</summary>
    public SearchHit? TopHit
    {
        get
        {
            foreach (var p in _providers)
                if (_sections.TryGetValue(p.SectionId, out var hits) && hits.Count > 0)
                    return hits.OrderBy(h => h.Rank).First();
            return null;
        }
    }

    /// <summary>Current hits of one section (empty when none/unknown).</summary>
    public IReadOnlyList<SearchHit> Section(string sectionId)
        => _sections.TryGetValue(sectionId, out var hits) ? hits : Array.Empty<SearchHit>();

    /// <summary>Runs a new query. An empty query behaves as <see cref="Clear"/>.</summary>
    public void SetQuery(string query)
    {
        CancelPending();
        Query = query;
        if (query.Length == 0) { Clear(); return; }
        var gen = _generation;
        var ct = _cts!.Token;
        foreach (var p in _providers)
        {
            if (p.RunsInline)
            {
                _ = RunProviderAsync(p, query, gen, ct);
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(p.Budget);
                    try { await Task.Delay(p.Debounce, linked.Token); }
                    catch (OperationCanceledException) { return; }
                    await RunProviderAsync(p, query, gen, linked.Token);
                });
            }
        }
    }

    /// <summary>Drops the query and empties every section (notified through <see cref="SectionChanged"/>).</summary>
    public void Clear()
    {
        CancelPending();
        Query = "";
        foreach (var p in _providers)
            Publish(p, _generation, Array.Empty<SearchHit>());
    }

    private void CancelPending()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _generation++;
    }

    private async Task RunProviderAsync(ISearchProvider p, string query, int gen, CancellationToken ct)
    {
        try
        {
            await p.QueryAsync(query, _maxResults, hits => Publish(p, gen, hits), ct);
        }
        catch (OperationCanceledException) { /* budget/debounce cancel — the section just stays as it was */ }
        catch { /* a broken provider must not kill the session; its section stays empty */ }
    }

    private void Publish(ISearchProvider p, int gen, IReadOnlyList<SearchHit> hits)
    {
        if (gen != _generation) return;   // superseded before we even marshaled
        _post(() =>
        {
            if (gen != _generation) return;   // superseded while marshaling
            _sections[p.SectionId] = hits;
            SectionChanged?.Invoke(p.SectionId, hits);
        });
    }
}
