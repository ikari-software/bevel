using System.IO;
using System.Linq;
using Bevel.Core.Vfs;

namespace Bevel.Interop.Cli;

/// <summary>
/// Parses <c>bevelctl</c> argument vectors into a <see cref="ParsedCommand"/> (08-os-interop.md
/// §3.2). Pure: no I/O, no automation calls — a bad-args result carries the usage message the CLI
/// prints with <see cref="ExitCodes.BadArgs"/>. Relative and <c>~</c> paths resolve against the
/// caller's home / working directory here so the router sees absolute file paths.
/// </summary>
public static class BevelCtlParser
{
    public const string Usage =
        "usage: bevelctl <reveal|open|select|mkdir|delete|duplicate|query> ...\n" +
        "  reveal <path>... [--new-window]\n" +
        "  open <path> [--view icons|list|details]\n" +
        "  select <path>...\n" +
        "  mkdir <path>\n" +
        "  delete <path>... [--permanent]\n" +
        "  duplicate <path>... [--to <dir>]\n" +
        "  query <windows|selection|version> [--json]";

    private static readonly string Home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    /// <summary>Returns the parsed command, or an error message (→ <see cref="ExitCodes.BadArgs"/>).</summary>
    public static (ParsedCommand? Command, string? Error) Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return (null, Usage);

        var verb = args[0].ToLowerInvariant();
        var operands = new List<string>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? view = null, to = null;

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { operands.Add(a); continue; }

            var key = a[2..].ToLowerInvariant();
            switch (key)
            {
                case "view":
                    if (i + 1 >= args.Count) return Err("--view needs a value (icons|list|details)");
                    view = args[++i];
                    break;
                case "to":
                    if (i + 1 >= args.Count) return Err("--to needs a directory");
                    to = args[++i];
                    break;
                case "new-window" or "permanent" or "json":
                    flags.Add(key);
                    break;
                default:
                    return Err($"unknown option --{key}");
            }
        }

        var json = flags.Contains("json");

        switch (verb)
        {
            case "reveal":
                if (operands.Count == 0) return Err("reveal needs at least one path");
                return Ok(new ParsedCommand { Verb = BevelVerb.Reveal, Paths = Paths(operands), NewWindow = flags.Contains("new-window"), Json = json });

            case "open":
                if (operands.Count != 1) return Err("open needs exactly one path");
                if (view is not null && ParseView(view) is null) return Err("--view must be icons|list|details");
                return Ok(new ParsedCommand { Verb = BevelVerb.Open, Paths = Paths(operands), View = ParseView(view), Json = json });

            case "select":
                if (operands.Count == 0) return Err("select needs at least one path");
                return Ok(new ParsedCommand { Verb = BevelVerb.Select, Paths = Paths(operands), Json = json });

            case "mkdir":
                if (operands.Count != 1) return Err("mkdir needs exactly one path");
                return Ok(new ParsedCommand { Verb = BevelVerb.Mkdir, Paths = Paths(operands), Json = json });

            case "delete":
                if (operands.Count == 0) return Err("delete needs at least one path");
                return Ok(new ParsedCommand { Verb = BevelVerb.Delete, Paths = Paths(operands), Permanent = flags.Contains("permanent"), Json = json });

            case "duplicate":
                if (operands.Count == 0) return Err("duplicate needs at least one path");
                return Ok(new ParsedCommand { Verb = BevelVerb.Duplicate, Paths = Paths(operands), Target = to is null ? null : PathArg(to), Json = json });

            case "query":
                if (operands.Count != 1) return Err("query needs one of: windows|selection|version");
                return operands[0].ToLowerInvariant() switch
                {
                    "windows" => Ok(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Windows, Json = json }),
                    "selection" => Ok(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Selection, Json = json }),
                    "version" => Ok(new ParsedCommand { Verb = BevelVerb.Query, Query = QueryKind.Version, Json = json }),
                    _ => Err("query target must be windows|selection|version"),
                };

            default:
                return Err($"unknown command '{verb}'\n{Usage}");
        }
    }

    internal static ViewMode? ParseView(string? v) => v?.ToLowerInvariant() switch
    {
        "icons" => ViewMode.Icons,
        "list" => ViewMode.List,
        "details" => ViewMode.Details,
        _ => null,
    };

    private static IReadOnlyList<VfsPath> Paths(IEnumerable<string> operands) => operands.Select(PathArg).ToArray();

    private static VfsPath PathArg(string s)
    {
        if (s == "~") s = Home;
        else if (s.StartsWith("~/", StringComparison.Ordinal)) s = Path.Combine(Home, s[2..]);
        return new VfsPath("file", Path.GetFullPath(s));
    }

    private static (ParsedCommand?, string?) Ok(ParsedCommand cmd) => (cmd, null);
    private static (ParsedCommand?, string?) Err(string message) => (null, message);
}
