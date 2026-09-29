using System;
using System.Linq;
using System.Reflection;

/// <summary>
/// The repository root, injected at build time (bevel-eabc).
///
/// This replaces four hand-rolled walk-ups from <c>AppContext.BaseDirectory</c> that searched for a
/// marker directory — with three different markers (<c>proofs/</c>, <c>src/Bevel.Pal.MacOS</c>,
/// <c>Bevel.sln</c>) and three different failure modes (throw, return null, assert). Each behaved
/// differently if the bin layout moved, and the newest was keyed to a PROJECT directory, so it would have
/// broken on a project rename while the others kept working.
///
/// The value comes from an <c>AssemblyMetadata</c> item in the root <c>Directory.Build.props</c>, gated on
/// <c>BevelNeedsRepoPaths</c> so only dev-time projects (tests, tools) embed an absolute build path —
/// shipped assemblies must not carry one.
///
/// Deliberately no walk-up fallback: a missing value means the project did not opt in, and silently
/// guessing the root is how the four originals diverged in the first place.
/// </summary>
internal static class RepoPaths
{
    private const string Key = "BevelRepoRoot";

    /// <summary>Absolute path to the repository root, without a trailing separator.</summary>
    public static string Root { get; } = Read();

    private static string Read()
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == Key)?.Value;

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Assembly metadata '{Key}' is missing. It is injected by the root Directory.Build.props " +
                "for projects that set <BevelNeedsRepoPaths>true</BevelNeedsRepoPaths>; add that property " +
                "to this project rather than walking up from AppContext.BaseDirectory.");

        return value!.TrimEnd('/', '\\');
    }
}
