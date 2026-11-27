using Bevel.Core;

namespace Bevel.Taskbar.Tests;

/// <summary>Settings for view tests. <see cref="BevelSettings"/> defaults to a real stack on the user's
/// ~/Downloads; a test must never touch the real home folder (a watcher there can raise a macOS
/// Files-and-Folders prompt and has hung the shell), so every test that feeds settings to a
/// <c>TaskbarView</c> goes through this.</summary>
internal static class TestSettings
{
    /// <summary>The same settings with no stacks configured.</summary>
    public static BevelSettings NoStacks(BevelSettings s)
    {
        s.TaskbarStacks = Array.Empty<string>();
        return s;
    }
}
