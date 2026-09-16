using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Bevel.App.Supervision;

/// <summary>
/// Identity of the binary a role process is actually running (bevel-9h7n). The launcher
/// compares each child's heartbeat stamp to <see cref="Current"/> so a rebuilt/replaced
/// binary, or an IPC protocol bump, is a version-skew rather than a mystery disconnect.
///
/// <para>Shape: <c>protocol|informational-version|latest-mtime-ticks</c>. Ticks come from
/// <see cref="Environment.ProcessPath"/> and the entry assembly (the latter is what
/// changes under <c>dotnet run</c>, where ProcessPath is the shared <c>dotnet</c> host).
/// Re-read every call so a file replaced while the launcher is up is visible on the next
/// monitor tick.</para>
/// </summary>
internal static class BuildStamp
{
    public const string EnvVar = "BEVEL_BUILD_STAMP";

    /// <summary>Bump when the shell-core / launcher-control wire format is incompatible.
    /// Landed in the stamp string, so a protocol bump looks like skew and heals the same way.</summary>
    public const int Protocol = 1;

    public static string Current()
    {
        var ver = VersionString();
        long ticks = 0;
        ticks = MaxTicks(ticks, Environment.ProcessPath);
        ticks = MaxTicks(ticks, EntryLocation());
        return $"{Protocol}|{ver}|{ticks}";
    }

    private static string VersionString()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info;
            return asm.GetName().Version?.ToString() ?? "0";
        }
        catch { return "0"; }
    }

    private static long MaxTicks(long current, string? path)
    {
        if (string.IsNullOrEmpty(path)) return current;
        try
        {
            if (File.Exists(path))
                return Math.Max(current, File.GetLastWriteTimeUtc(path).Ticks);
        }
        catch { /* a missing path just contributes 0 ticks */ }
        return current;
    }

    // Empty Location under single-file/AOT is handled: ticks stay 0 and ProcessPath is used.
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Empty Location under single-file/AOT is handled: ticks stay 0 and ProcessPath is used.")]
    private static string EntryLocation() => Assembly.GetEntryAssembly()?.Location ?? "";
}
