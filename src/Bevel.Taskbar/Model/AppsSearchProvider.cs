using Bevel.Core.Search;

namespace Bevel.Taskbar;

/// <summary>
/// The "Programs" search section (bevel-6sk6): ranks <see cref="ShellModel.Programs"/> inline — that
/// collection is UI-thread-owned, so the provider runs on the keystroke with no debounce and no I/O.
/// Each hit activates through the program's own <see cref="ProgramItemViewModel.LaunchCommand"/> and
/// carries the VM as <see cref="SearchHit.Tag"/> so the results view can bind its icon.
/// </summary>
public sealed class AppsSearchProvider : ISearchProvider
{
    private readonly ShellModel _model;

    public AppsSearchProvider(ShellModel model) => _model = model;

    public string SectionId => "programs";
    public string SectionTitle => "Programs";
    public int Order => 0;
    public bool RunsInline => true;
    public TimeSpan Debounce => TimeSpan.Zero;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(100);

    public Task QueryAsync(string query, int maxResults, Action<IReadOnlyList<SearchHit>> publish, CancellationToken ct)
    {
        var ranked = SearchRanker.Rank(_model.Programs, static p => p.DisplayName, query);
        var hits = new List<SearchHit>(Math.Min(ranked.Count, maxResults));
        foreach (var p in ranked)
        {
            if (hits.Count >= maxResults) break;
            p.EnsureIcon();   // lazy, off-thread, idempotent
            hits.Add(new SearchHit(
                p.DisplayName,
                p.SubLabel is { Length: > 0 } sub ? sub : null,
                SearchRanker.RankOf(p.DisplayName, query),
                () => { if (p.LaunchCommand.CanExecute(null)) p.LaunchCommand.Execute(null); return Task.CompletedTask; },
                p));
        }
        publish(hits);
        return Task.CompletedTask;
    }
}
