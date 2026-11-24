namespace Bevel.Core.Search;

/// <summary>
/// One source of search results (bevel-6sk6): apps, later files/settings/tabs. Implementations live in
/// whatever layer owns the data; this seam is in Bevel.Core so it stays Avalonia-free (ARCH-02) and
/// unit-testable without a UI thread.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable key for the section (results replace per section, never interleave).</summary>
    string SectionId { get; }

    /// <summary>Header shown above the section's hits.</summary>
    string SectionTitle { get; }

    /// <summary>Sections render in ascending <see cref="Order"/>.</summary>
    int Order { get; }

    /// <summary>True when <see cref="QueryAsync"/> completes synchronously on the caller's thread (in-memory
    /// data owned by the UI thread) — <see cref="SearchSession"/> invokes these inline, without a debounce.</summary>
    bool RunsInline { get; }

    /// <summary>How long a non-inline provider's query waits after the last keystroke before starting.</summary>
    TimeSpan Debounce { get; }

    /// <summary>How long a non-inline provider's query may run before it is cancelled.</summary>
    TimeSpan Budget { get; }

    /// <summary>
    /// Runs the query. <paramref name="publish"/> REPLACES the section's hits and may be called more than
    /// once (cache first, fresher later); every call must be safe to marshal. Return without publishing to
    /// leave the section empty. Never throw for "no results" / "cancelled" — those are normal outcomes.
    /// </summary>
    Task QueryAsync(string query, int maxResults, Action<IReadOnlyList<SearchHit>> publish, CancellationToken ct);
}
