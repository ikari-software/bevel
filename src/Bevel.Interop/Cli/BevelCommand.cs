using Bevel.Core.Vfs;

namespace Bevel.Interop.Cli;

// ── Parsed automation command (08-os-interop.md §3.2 bevelctl + §3.3 bevel://) ────────────────
//
// Both surfaces parse into this one shape and execute through AutomationCommandRouter → the single
// IShellAutomation seam (INT-3), so `bevelctl reveal x` and `bevel://reveal?path=x` are identical.

/// <summary>The verbs both surfaces expose that map onto <see cref="IShellAutomation"/>. (theme /
/// register / permissions / doctor are separate subsystems, not automation verbs — parsed elsewhere.)</summary>
public enum BevelVerb { Reveal, Open, Select, Mkdir, Delete, Duplicate, Move, Query }

/// <summary>What <c>query</c> asks for.</summary>
public enum QueryKind { Windows, Selection, Version }

/// <summary>A fully-parsed, surface-agnostic command ready for <see cref="AutomationCommandRouter"/>.</summary>
public sealed record ParsedCommand
{
    public required BevelVerb Verb { get; init; }
    public IReadOnlyList<VfsPath> Paths { get; init; } = Array.Empty<VfsPath>();
    public bool NewWindow { get; init; }        // reveal --new-window
    public ViewMode? View { get; init; }        // open --view / ?view=
    public bool Json { get; init; }             // --json
    public bool Permanent { get; init; }        // delete --permanent
    public VfsPath? Target { get; init; }       // duplicate --to <dir>
    public QueryKind Query { get; init; }       // query <kind>
}

/// <summary>A command's outcome: a process/URL exit code plus the text (or JSON) to print.</summary>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>bevelctl exit codes (08-os-interop.md §3.2).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int BadArgs = 2;
    public const int ShellNotRunning = 3;
    public const int PermissionMissing = 4;
    public const int NotFound = 5;
}
