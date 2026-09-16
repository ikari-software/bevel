using System.Reflection;

namespace Bevel.Core;

/// <summary>
/// Single source of truth for Bevel's per-user CONFIG directory (<c>~/.config/bevel</c>) — the home of
/// <c>settings.db</c>, <c>settings.json</c>, <c>program-usage.json</c> and <c>bevelctl.sock</c>. The
/// runtime/socket counterpart is <c>BevelRuntimeDir</c> in Bevel.App; this one owns the files that are
/// the USER'S OWN STATE and must survive everything a developer does.
///
/// <para><b>Why this type exists.</b> Three call sites independently built this path with a literal
/// <c>Path.Combine(UserProfile, ".config", "bevel")</c>, each exposed as an implicit default —
/// <c>new SettingsService()</c>, <c>new ProgramUsageStore(null)</c>, <c>AutomationSocket</c>'s default.
/// A test that forgot to pass a temp directory therefore pointed straight at the developer's live
/// config and overwrote it. The mitigation in the tree was a COMMENT ("pointing that at the real
/// ~/.config/bevel/settings.db would clobber the user's live settings"). Comments do not survive
/// copy-paste, and this destroyed a real user's settings more than once.</para>
///
/// <para><b>The enforcement.</b> <see cref="Path"/> THROWS when resolved inside a test host. Not a
/// redirect to a temp dir — a redirect would silently "work", leaving a test that believes it exercised
/// the default path while quietly depending on a lie. A throw makes the accident impossible to write:
/// the only way a test reaches a config directory is to name one, and the only directory it can name is
/// one it created. Production is unaffected — a shipped Bevel process is not a test host.</para>
/// </summary>
public static class BevelConfigDir
{
    /// <summary>The real per-user config directory. Throws in a test host — see the type docs.</summary>
    public static string Path
    {
        get
        {
            if (IsTestHost())
                throw new InvalidOperationException(
                    "BevelConfigDir.Path resolves the developer's REAL config directory (~/.config/bevel) " +
                    "and must never be touched from a test — it holds live user settings. " +
                    "Pass an explicit directory instead, e.g. `new SettingsService(tempDir)` / " +
                    "`new ProgramUsageStore(tempDir)`, rooted at a per-test folder under Path.GetTempPath(). " +
                    "This throw is the enforcement for bevel-cfg-guard; do not weaken it.");

            return Real;
        }
    }

    /// <summary>
    /// The real path with NO guard. Exists solely for the composition root and the guard's own test,
    /// which must be able to name the production location without resolving it. Never call this to get
    /// a directory to read or write in a test.
    /// </summary>
    internal static string Real => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel");

    /// <summary>
    /// True when this process is a test runner. Deliberately NOT cached: assemblies load lazily, so a
    /// value computed at type-init could be captured before xunit is in the domain, and the guard would
    /// silently stop guarding. Resolution happens a handful of times per process, so the scan is free.
    /// </summary>
    internal static bool IsTestHost()
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name is { } entry
            && entry.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name is not { } name) continue;
            if (name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
