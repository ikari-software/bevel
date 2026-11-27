using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// A failed settings.json export must never fail the settings SAVE.
///
/// <para>The export is a human-readable mirror; the SQLite row is the source of truth and nothing reads
/// the JSON at runtime. It is written temp-then-replace precisely because two instances can race, and
/// a lost race is by design harmless. But the catch was typed to <see cref="IOException"/>, and the way
/// that race actually loses on Windows is <see cref="UnauthorizedAccessException"/> — <c>MoveFile</c>
/// onto a destination another writer holds open. That is a sibling of IOException, not a subclass, so
/// it escaped straight out of <c>SaveAsync</c>. POSIX's rename never fails that way, so the bug was
/// invisible on a macOS dev machine and surfaced as an intermittent windows-latest CI failure in
/// <c>SettingsServiceSqliteTests.Concurrent_readers_and_writers_never_hit_database_is_locked</c>.</para>
///
/// <para>These tests inject the failure through the writer's seam instead of relying on the platform,
/// so they have the same teeth everywhere — a test that can only fail on the runner that happened to
/// catch it once is not much of a guard.</para>
/// </summary>
public sealed class PassiveExportResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "bevel-export-" + Guid.NewGuid().ToString("N")[..8]);

    public PassiveExportResilienceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_ => System.IO.Path.Combine(_dir, "settings.json");

    private static string[] TempsIn(string dir) => Directory.GetFiles(dir, "*.tmp");

    /// <summary>The exact Windows failure: the replace is refused because the destination is open.</summary>
    [Fact]
    public async Task An_access_denied_replace_does_not_escape()
    {
        await SettingsService.WriteBestEffortExportAsync(Path_, "{}",
            move: (_, _) => throw new UnauthorizedAccessException("Access to the path is denied."),
            CancellationToken.None);

        Assert.Empty(TempsIn(_dir));   // and it cleaned up after itself
    }

    /// <summary>...and so does any other failure, because the contract is "best effort", not "best
    /// effort against the exception types we have seen so far".</summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task No_replace_failure_escapes(Type failure)
    {
        await SettingsService.WriteBestEffortExportAsync(Path_, "{}",
            move: (_, _) => throw (Exception)Activator.CreateInstance(failure)!,
            CancellationToken.None);

        Assert.Empty(TempsIn(_dir));
    }

    /// <summary>Cancellation is NOT swallowed — a shutdown must still read as cancelled rather than as
    /// a quietly skipped write.</summary>
    [Fact]
    public async Task Cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SettingsService.WriteBestEffortExportAsync(Path_, "{}", move: null, cts.Token));
    }

    /// <summary>The happy path still writes the file and leaves no temp behind.</summary>
    [Fact]
    public async Task A_successful_export_lands_and_leaves_no_temp()
    {
        await SettingsService.WriteBestEffortExportAsync(Path_, "{\"a\":1}", move: null, CancellationToken.None);

        Assert.Equal("{\"a\":1}", await File.ReadAllTextAsync(Path_));
        Assert.Empty(TempsIn(_dir));
    }

    /// <summary>And it replaces an existing export rather than failing on it.</summary>
    [Fact]
    public async Task An_existing_export_is_replaced()
    {
        await File.WriteAllTextAsync(Path_, "stale");

        await SettingsService.WriteBestEffortExportAsync(Path_, "fresh", move: null, CancellationToken.None);

        Assert.Equal("fresh", await File.ReadAllTextAsync(Path_));
        Assert.Empty(TempsIn(_dir));
    }
}
