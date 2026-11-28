using Bevel.Core.Updates;
using Velopack.Logging;
using VelopackSource = Velopack.Sources.IUpdateSource;

namespace Bevel.App.Updates;

/// <summary>
/// Velopack-backed <see cref="Bevel.Core.Updates.IUpdateSource"/> (bevel-ym0 / UPD-01).
///
/// Feed selection: a <c>github.com</c> host in the configured URL means "use Velopack's GitHub
/// release source" (zero infra — the release assets ARE the feed); a local directory path uses
/// SimpleFileSource (for tests); anything else is a simple web directory (keeps
/// <c>https://bevel.run/updates</c> usable later). An empty feed URL short-circuits to
/// <see cref="UpdateAvailability.None"/> without touching the network.
///
/// We read the feed directly via <see cref="Velopack.Sources.IUpdateSource.GetReleaseFeed"/> rather
/// than through <c>UpdateManager</c>, because UpdateManager requires a VelopackLocator (only set
/// after VelopackApp.Build().Run()) — the check runs in the core process where the locator may not
/// be initialized yet.
///
/// NEVER throws: any Velopack or network failure maps to <see cref="UpdateAvailability.None"/>,
/// because an unreachable feed is indistinguishable from "up to date" to the user.
/// </summary>
internal sealed class VelopackUpdateSource : Bevel.Core.Updates.IUpdateSource
{
    private const string AppId = "pl.ikari.bevel";

    public async ValueTask<UpdateAvailability> CheckAsync(string feedUrl, string currentVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return UpdateAvailability.None;

        try
        {
            VelopackSource source = CreateSource(feedUrl);
            var feed = await source.GetReleaseFeed(
                NullVelopackLogger.Instance, AppId, channel: null, stagingId: null, latestLocalRelease: null)
                .ConfigureAwait(false);
            if (feed?.Assets is not { Length: > 0 } assets)
                return UpdateAvailability.None;

            // The feed is ordered newest-first; the first asset is the latest release.
            var latest = assets[0];
            var feedVer = latest.Version?.Version;
            if (feedVer is not null && new Version(currentVersion) < feedVer)
            {
                return new UpdateAvailability(
                    Available: true,
                    Version: feedVer.ToString(),
                    Notes: latest.NotesMarkdown);
            }
            return UpdateAvailability.None;
        }
        catch (OperationCanceledException)
        {
            throw; // let the host's stopping token win
        }
        catch
        {
            // Network down, feed 404, malformed JSON — all mean "not now", never an error.
            return UpdateAvailability.None;
        }
    }

    /// <summary>Maps the configured feed URL to a Velopack source. GitHub release URLs use the
    /// GitHub source; a local directory path uses SimpleFileSource (for tests); anything else is a
    /// simple web directory.</summary>
    private static VelopackSource CreateSource(string feedUrl)
    {
        if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            // Velopack's GitHubSource takes a repo URL like https://github.com/owner/repo
            var repoUrl = $"https://github.com{uri.AbsolutePath.TrimEnd('/')}";
            return new Velopack.Sources.GithubSource(repoUrl, accessToken: null, prerelease: false, downloader: null);
        }
        if (Directory.Exists(feedUrl))
            return new Velopack.Sources.SimpleFileSource(new DirectoryInfo(feedUrl));
        return new Velopack.Sources.SimpleWebSource(feedUrl, downloader: null, timeout: 30);
    }
}
