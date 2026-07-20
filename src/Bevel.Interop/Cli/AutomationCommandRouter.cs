using System.Linq;
using System.Text;
using Bevel.Core.Vfs;

namespace Bevel.Interop.Cli;

/// <summary>
/// Executes a <see cref="ParsedCommand"/> against the one <see cref="IShellAutomation"/> seam and
/// renders the result (08-os-interop.md §3.1/§3.2). This is where <c>bevelctl</c> and <c>bevel://</c>
/// converge (INT-3): the router never touches the filesystem or windows directly. A command-model
/// fault (<see cref="AutomationException"/>) becomes <see cref="ExitCodes.NotFound"/> with its
/// message; success is <see cref="ExitCodes.Ok"/>.
/// </summary>
public sealed class AutomationCommandRouter
{
    private readonly IShellAutomation _automation;

    public AutomationCommandRouter(IShellAutomation automation) => _automation = automation;

    public async Task<CommandResult> ExecuteAsync(ParsedCommand cmd, CancellationToken ct = default)
    {
        try
        {
            return cmd.Verb switch
            {
                BevelVerb.Reveal => await RevealAsync(cmd, ct),
                BevelVerb.Open => await OpenAsync(cmd, ct),
                BevelVerb.Select => await SelectAsync(cmd, ct),
                BevelVerb.Mkdir => await MkdirAsync(cmd, ct),
                BevelVerb.Delete => await DeleteAsync(cmd, ct),
                BevelVerb.Duplicate => await DuplicateAsync(cmd, ct),
                BevelVerb.Move => await MoveAsync(cmd, ct),
                BevelVerb.Query => await QueryAsync(cmd, ct),
                _ => new CommandResult(ExitCodes.BadArgs, $"unhandled verb {cmd.Verb}"),
            };
        }
        catch (AutomationException ex)
        {
            return new CommandResult(ExitCodes.NotFound, ex.Message);
        }
    }

    private async Task<CommandResult> RevealAsync(ParsedCommand cmd, CancellationToken ct)
    {
        var r = await _automation.RevealAsync(cmd.Paths, new RevealOptions(cmd.NewWindow), ct);
        return cmd.Json
            ? Ok($"{{\"window\":{r.Window.Id},\"selected\":{r.SelectedCount}}}")
            : Ok($"revealed {r.SelectedCount} item(s) in window {r.Window.Id}");
    }

    private async Task<CommandResult> OpenAsync(ParsedCommand cmd, CancellationToken ct)
    {
        var window = await _automation.OpenAsync(cmd.Paths[0], new OpenOptions(cmd.View), ct);
        return cmd.Json ? Ok($"{{\"window\":{window.Id}}}") : Ok($"opened window {window.Id}");
    }

    private async Task<CommandResult> SelectAsync(ParsedCommand cmd, CancellationToken ct)
    {
        // The CLI has no window handle, so select acts on the frontmost open window.
        var windows = await _automation.QueryAsync(AutomationQuery.Windows, ct);
        if (windows.Windows.Count == 0)
            return new CommandResult(ExitCodes.ShellNotRunning, "no open file-manager window to select in");

        await _automation.SelectAsync(windows.Windows[0], cmd.Paths, ct);
        return Ok($"selected {cmd.Paths.Count} item(s)");
    }

    private async Task<CommandResult> MkdirAsync(ParsedCommand cmd, CancellationToken ct)
    {
        var target = cmd.Paths[0];
        var made = await _automation.MakeAsync(target.Parent, NewItemKind.Folder, target.FileName, ct);
        return cmd.Json ? Ok($"{{\"path\":{JsonStr(Display(made))}}}") : Ok(Display(made));
    }

    private async Task<CommandResult> DeleteAsync(ParsedCommand cmd, CancellationToken ct)
    {
        await _automation.DeleteAsync(cmd.Paths, cmd.Permanent ? DeleteMode.Permanent : DeleteMode.Trash, ct);
        var how = cmd.Permanent ? "deleted" : "moved to Trash";
        return Ok($"{how} {cmd.Paths.Count} item(s)");
    }

    private async Task<CommandResult> DuplicateAsync(ParsedCommand cmd, CancellationToken ct)
    {
        var dups = await _automation.DuplicateAsync(cmd.Paths, cmd.Target, ct);
        return cmd.Json
            ? Ok($"{{\"created\":{JsonArray(dups.Select(Display))}}}")
            : Ok(string.Join('\n', dups.Select(Display)));
    }

    private async Task<CommandResult> MoveAsync(ParsedCommand cmd, CancellationToken ct)
    {
        if (cmd.Target is not { } destination)
            return new CommandResult(ExitCodes.BadArgs, "move needs a destination (--to)");
        var moved = await _automation.MoveAsync(cmd.Paths, destination, ct);
        return cmd.Json
            ? Ok($"{{\"moved\":{JsonArray(moved.Select(Display))}}}")
            : Ok(string.Join('\n', moved.Select(Display)));
    }

    private async Task<CommandResult> QueryAsync(ParsedCommand cmd, CancellationToken ct)
    {
        return cmd.Query switch
        {
            QueryKind.Version => await QueryVersion(cmd, ct),
            QueryKind.Windows => await QueryWindows(cmd, ct),
            QueryKind.Selection => await QuerySelection(cmd, ct),
            _ => new CommandResult(ExitCodes.BadArgs, "unknown query"),
        };
    }

    private async Task<CommandResult> QueryVersion(ParsedCommand cmd, CancellationToken ct)
    {
        var snap = await _automation.QueryAsync(AutomationQuery.Application, ct);
        var v = snap.Version ?? "unknown";
        return cmd.Json ? Ok($"{{\"version\":{JsonStr(v)}}}") : Ok(v);
    }

    private async Task<CommandResult> QueryWindows(ParsedCommand cmd, CancellationToken ct)
    {
        var snap = await _automation.QueryAsync(AutomationQuery.Windows, ct);
        var ids = snap.Windows.Select(w => w.Id).ToArray();
        return cmd.Json
            ? Ok($"{{\"windows\":[{string.Join(",", ids)}]}}")
            : Ok(ids.Length == 0 ? "(no open windows)" : string.Join('\n', ids.Select(i => $"window {i}")));
    }

    private async Task<CommandResult> QuerySelection(ParsedCommand cmd, CancellationToken ct)
    {
        var snap = await _automation.QueryAsync(AutomationQuery.Selection, ct);
        var items = snap.Selection.Select(Display).ToArray();
        return cmd.Json
            ? Ok($"{{\"selection\":{JsonArray(items)}}}")
            : Ok(items.Length == 0 ? "(no selection)" : string.Join('\n', items));
    }

    // ── Rendering helpers ────────────────────────────────────────────────────────────────────

    private static CommandResult Ok(string output) => new(ExitCodes.Ok, output);

    /// <summary>Human/CLI-friendly rendering: a <c>file</c> path shows its POSIX form, other schemes
    /// their canonical <c>vfs://</c> URI.</summary>
    private static string Display(VfsPath p) => p.Scheme == "file" ? p.Value : p.ToString();

    private static string JsonStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    private static string JsonArray(IEnumerable<string> values) => "[" + string.Join(",", values.Select(JsonStr)) + "]";
}
