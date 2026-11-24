using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-ze3o: per-folder view/sort entries — MRU-last upsert, cap, and tolerance of
/// malformed entries.</summary>
public class FolderViewStatesTests
{
    [Fact]
    public void Upsert_moves_an_existing_folder_to_the_end_and_caps_at_200()
    {
        var entries = new List<string>();
        for (var i = 0; i < 200; i++)
            entries.Add($"Details|Name|A|/folder{i}");

        var state = new FolderViewStates.FolderViewState("List", "Size", false);
        var result = FolderViewStates.Upsert(entries.ToArray(), "/folder7", state);

        Assert.Equal(200, result.Length);                       // replace keeps the count at the cap
        Assert.Equal("Details|Name|A|/folder0", result[0]);
        Assert.Equal("List|Size|D|/folder7", result[^1]);        // moved to the MRU end with its new state
        Assert.Single(result, e => e.EndsWith("|/folder7", StringComparison.Ordinal));

        // Upserting a NEW folder past the cap drops the oldest (MRU is last, so drop from the front)
        var capped = FolderViewStates.Upsert(result, "/new", state);
        Assert.Equal(200, capped.Length);
        Assert.DoesNotContain(capped, e => e.EndsWith("|/folder0", StringComparison.Ordinal));
        Assert.Equal("List|Size|D|/new", capped[^1]);

        // Find returns the updated state for the moved folder
        var found = FolderViewStates.Find(result, "/folder7");
        Assert.Equal(state, found);
    }

    [Fact]
    public void Upsert_appends_a_new_folder_without_dropping_anything_under_the_cap()
    {
        var result = FolderViewStates.Upsert(
            new[] { "Details|Name|A|/a" }, "/b",
            new FolderViewStates.FolderViewState("List", "Modified", true));
        Assert.Equal(new[] { "Details|Name|A|/a", "List|Modified|A|/b" }, result);
    }

    [Fact]
    public void Find_ignores_malformed_entries()
    {
        var entries = new[]
        {
            "",                                  // empty
            "Details|Name|/x",                   // missing direction
            "Details|Name|X|/x",                 // bad direction token
            "Details|Name|A",                    // no path part
            "|Name|A|/x",                        // empty view mode
            "garbage",
            "Details|Name|A|/good",              // the only well-formed one
            "List|Size|D|/pipe|in|path",         // path containing '|' must parse whole
        };
        Assert.Equal(new FolderViewStates.FolderViewState("Details", "Name", true),
            FolderViewStates.Find(entries, "/good"));
        Assert.Equal(new FolderViewStates.FolderViewState("List", "Size", false),
            FolderViewStates.Find(entries, "/pipe|in|path"));
        Assert.Null(FolderViewStates.Find(entries, "/x"));
        Assert.Null(FolderViewStates.Find(entries, "/missing"));
    }

    [Fact]
    public void Find_returns_the_last_match_when_a_path_is_duplicated()
    {
        var entries = new[] { "Details|Name|A|/a", "List|Size|D|/a" };
        Assert.Equal(new FolderViewStates.FolderViewState("List", "Size", false),
            FolderViewStates.Find(entries, "/a"));
    }
}
