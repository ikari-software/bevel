using Bevel.Core.Search;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-6sk6: <see cref="SearchSession"/> semantics — generation-guarded publishes, inline providers,
/// debounce + budget for background providers, provider-ordered sections. Fake providers, post inline.
/// </summary>
public class SearchSessionTests
{
    private static SearchHit Hit(string title, int rank = 0) =>
        new(title, null, rank, () => Task.CompletedTask);

    /// <summary>A provider whose QueryAsync runs the supplied callback, synchronously or on a gate.</summary>
    private sealed class FakeProvider : ISearchProvider
    {
        public string SectionId { get; init; } = "s";
        public string SectionTitle { get; init; } = "S";
        public int Order { get; init; }
        public bool RunsInline { get; init; }
        public TimeSpan Debounce { get; init; } = TimeSpan.Zero;
        public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Invoked from QueryAsync. Return a completed task to publish synchronously.</summary>
        public Func<string, int, Action<IReadOnlyList<SearchHit>>, CancellationToken, Task> Run { get; init; }
            = (_, _, publish, _) => { publish(new[] { Hit("x") }); return Task.CompletedTask; };

        public Task QueryAsync(string query, int maxResults, Action<IReadOnlyList<SearchHit>> publish, CancellationToken ct)
            => Run(query, maxResults, publish, ct);
    }

    [Fact]
    public void A_newer_query_cancels_the_older_one_and_drops_its_late_results()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<SearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeProvider
        {
            SectionId = "slow",
            RunsInline = false,
            Debounce = TimeSpan.Zero,
            Run = (_, _, publish, ct) => gate.Task.ContinueWith(
                t => publish(t.Result), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default),
        };
        var published = new List<string>();
        var session = new SearchSession(new ISearchProvider[] { slow }, a => a());
        session.SectionChanged += (id, hits) => published.Add($"{id}:{hits.Count}");

        session.SetQuery("a");
        session.SetQuery("b");   // cancels the first query's token + generation
        gate.SetResult(new[] { Hit("late") });

        Assert.Empty(session.Section("slow"));      // the late publish was dropped by the generation check
        Assert.Empty(published);
        Assert.Equal("b", session.Query);
    }

    [Fact]
    public void Inline_providers_publish_before_SetQuery_returns()
    {
        var inline = new FakeProvider { SectionId = "apps", RunsInline = true };
        var session = new SearchSession(new ISearchProvider[] { inline }, a => a());

        session.SetQuery("cal");

        var hits = session.Section("apps");
        Assert.Single(hits);
        Assert.Equal("x", hits[0].Title);
        Assert.Equal("x", session.TopHit!.Title);
    }

    [Fact]
    public async Task A_provider_over_budget_is_cancelled_and_its_section_stays_empty()
    {
        var never = new FakeProvider
        {
            SectionId = "files",
            RunsInline = false,
            Debounce = TimeSpan.FromMilliseconds(10),
            Budget = TimeSpan.FromMilliseconds(50),
            Run = async (_, _, publish, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);   // blows the budget → cancelled
                publish(new[] { Hit("too late") });
            },
        };
        var session = new SearchSession(new ISearchProvider[] { never }, a => a());

        session.SetQuery("doc");
        await Task.Delay(300);   // debounce + budget elapse

        Assert.Empty(session.Section("files"));
        Assert.Null(session.TopHit);
    }

    [Fact]
    public void Sections_come_back_in_provider_order()
    {
        var a = new FakeProvider { SectionId = "a", Order = 2, RunsInline = true, Run = (_, _, p, _) => { p(new[] { Hit("a1", 0), Hit("a2", 1) }); return Task.CompletedTask; } };
        var b = new FakeProvider { SectionId = "b", Order = 0, RunsInline = true, Run = (_, _, p, _) => { p(new[] { Hit("b1") }); return Task.CompletedTask; } };
        var c = new FakeProvider { SectionId = "c", Order = 1, RunsInline = true, Run = (_, _, p, _) => { p(new[] { Hit("c1") }); return Task.CompletedTask; } };

        var session = new SearchSession(new ISearchProvider[] { a, b, c }, x => x());
        session.SetQuery("q");

        Assert.Equal(new[] { "b", "c", "a" }, session.Providers.Select(p => p.SectionId));
        Assert.Equal("b1", session.TopHit!.Title);   // first section in Order wins
    }

    [Fact]
    public void Clear_empties_every_section_and_notifies()
    {
        var inline = new FakeProvider { SectionId = "apps", RunsInline = true };
        var session = new SearchSession(new ISearchProvider[] { inline }, a => a());
        var notified = new List<string>();
        session.SectionChanged += (id, _) => notified.Add(id);

        session.SetQuery("cal");
        Assert.Single(session.Section("apps"));

        session.Clear();

        Assert.Empty(session.Section("apps"));
        Assert.Null(session.TopHit);
        Assert.Equal("", session.Query);
        Assert.Equal(new[] { "apps", "apps" }, notified);   // once with hits, once emptied by Clear
    }
}
