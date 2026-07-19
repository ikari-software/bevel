using System.Linq;
using Bevel.Core.Vfs;

namespace Bevel.Interop.Cli;

/// <summary>
/// Parses <c>bevel://</c> URLs (08-os-interop.md §3.3) into the same <see cref="ParsedCommand"/> as
/// the CLI, so a URL invocation and a CLI invocation are behaviourally identical (INT-3). v1 maps
/// the automation verbs <c>reveal</c>/<c>open</c>; <c>search</c>/<c>settings</c>/<c>theme</c> are
/// other subsystems and report a clear "not supported yet" rather than silently no-op'ing.
///
/// <para>Grammar: <c>bevel://reveal?path=&lt;posix&gt;[&amp;path=...]</c>,
/// <c>bevel://open?path=&lt;posix&gt;[&amp;view=details]</c>. Query values are percent-decoded.</para>
/// </summary>
public static class BevelUrlParser
{
    public static (ParsedCommand? Command, string? Error) Parse(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Parse(uri)
            : (null, $"not a valid URL: {url}");

    public static (ParsedCommand? Command, string? Error) Parse(Uri uri)
    {
        if (!uri.Scheme.Equals("bevel", StringComparison.OrdinalIgnoreCase))
            return (null, $"not a bevel:// URL: {uri}");

        // bevel://reveal?path=… → Host is the verb. Fall back to the pre-query segment for the
        // opaque bevel:reveal?… form.
        var verb = (string.IsNullOrEmpty(uri.Host) ? uri.AbsolutePath.Trim('/') : uri.Host).ToLowerInvariant();
        var query = ParseQuery(uri.Query);

        switch (verb)
        {
            case "reveal":
                var revealPaths = query.Values("path");
                if (revealPaths.Count == 0) return (null, "bevel://reveal needs a path= parameter");
                return (new ParsedCommand { Verb = BevelVerb.Reveal, Paths = revealPaths.Select(UrlPath).ToArray() }, null);

            case "open":
                var openPath = query.Single("path");
                if (openPath is null) return (null, "bevel://open needs a path= parameter");
                var view = query.Single("view");
                if (view is not null && BevelCtlParser.ParseView(view) is null)
                    return (null, "view= must be icons|list|details");
                return (new ParsedCommand { Verb = BevelVerb.Open, Paths = new[] { UrlPath(openPath) }, View = BevelCtlParser.ParseView(view) }, null);

            case "search" or "settings" or "theme":
                return (null, $"bevel://{verb} is not wired to automation yet (M4 covers reveal/open).");

            default:
                return (null, $"unknown bevel:// verb '{verb}'");
        }
    }

    private static VfsPath UrlPath(string posix) => new("file", posix);

    // ── Minimal query-string parser (multi-value; percent-decoded) ───────────────────────────

    private static QueryBag ParseQuery(string rawQuery)
    {
        var bag = new QueryBag();
        var q = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        if (q.Length == 0) return bag;

        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            bag.Add(Uri.UnescapeDataString(key), Uri.UnescapeDataString(value.Replace('+', ' ')));
        }
        return bag;
    }

    private sealed class QueryBag
    {
        private readonly List<(string Key, string Value)> _pairs = new();
        public void Add(string key, string value) => _pairs.Add((key, value));
        public IReadOnlyList<string> Values(string key) =>
            _pairs.Where(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray();
        public string? Single(string key) => Values(key).FirstOrDefault();
    }
}
