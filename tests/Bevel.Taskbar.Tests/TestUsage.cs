using Bevel.Taskbar;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Throwaway <see cref="ProgramUsageStore"/> for tests. <c>ShellModel</c>'s <c>usage</c> parameter is
/// optional and used to fall back to <c>new ProgramUsageStore()</c> — the developer's REAL
/// <c>~/.config/bevel/program-usage.json</c> — so 32 tests were quietly rewriting the live Start-menu
/// most-used ordering on every run. <c>BevelConfigDir</c> now refuses that resolution inside a test host;
/// this is what the call sites pass instead (bevel-cfg-guard).
/// </summary>
internal static class TestUsage
{
    public static ProgramUsageStore Scratch() => new(Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "bevel-tests", Guid.NewGuid().ToString("n"))).FullName);
}
