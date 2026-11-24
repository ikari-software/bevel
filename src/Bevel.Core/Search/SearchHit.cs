namespace Bevel.Core.Search;

/// <summary>One search result row (bevel-6sk6). <paramref name="Rank"/> orders hits inside their section
/// (0 = best, matching <see cref="SearchRanker.RankOf"/>); <paramref name="ActivateAsync"/> runs the hit's
/// action; <paramref name="Tag"/> carries the source object (e.g. the view-model whose command activates)
/// so UI layers can bind icons without a lookup.</summary>
public sealed record SearchHit(string Title, string? Subtitle, int Rank, Func<Task> ActivateAsync, object? Tag = null);

/// <summary>A section header row in the results list (bevel-6sk6).</summary>
public sealed record SearchSectionHeader(string Title);
