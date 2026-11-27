using System.Text.RegularExpressions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The shared icon pool must have a writer that actually renders icons (bevel-3gad).
///
/// <para>MmfBgraPool had 13 passing tests and had never cached a single icon. The pool itself was
/// correct; the COMPOSITION was not. <c>poolIsWriter</c> was <c>role is ShellRole.Core</c>, but
/// <c>RunShellCore</c> resolves only IWindowManager / IAppEnvironment / ISystemTrayHost /
/// ISettingsService and never IIconProvider. DI singletons are lazy, so the core never constructed
/// PooledIconProvider and never called TryAdd. Every role was therefore a reader; readers never create
/// the file; and a reader with no file degrades silently to a detached, always-misses pool. No
/// icons.pool existed on disk at all.</para>
///
/// <para>That is why these are wiring assertions rather than more pool-mechanism tests: the mechanism
/// was never the problem, and thirteen green tests over it said nothing about whether anything was
/// plugged in. Measured cost of the gap on this machine: 11.8 ms per cold icon render versus 0.006 ms
/// per pool hit — about 4 s of redundant render work per taskbar start at 343 installed apps.</para>
/// </summary>
public sealed class IconPoolWiringTests
{
    private static string CompositionRootSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bevel.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "src", "Bevel.App", "CompositionRoot.cs");
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join('\n', text.Split('\n').Select(l => Regex.Replace(l, @"//.*$", string.Empty)));
    }

    /// <summary>Every <c>poolIsWriter</c> binding — there is one per PAL branch — must name the same
    /// role, or the single-writer invariant the pool depends on is not actually single.</summary>
    [Fact]
    public void Both_pal_branches_designate_the_same_single_writer_role()
    {
        var bindings = Regex.Matches(CompositionRootSource(),
                @"poolIsWriter\s*=\s*role\s+is\s+ShellRole\.(?<role>\w+)")
            .Select(m => m.Groups["role"].Value)
            .ToList();

        Assert.Equal(2, bindings.Count);          // macOS + Windows branches
        Assert.Single(bindings.Distinct());       // both naming one role
    }

    /// <summary>The writer must be a role that actually resolves IIconProvider. Core does not — that is
    /// the whole bug — so designating it writer makes the cache inert again, silently.</summary>
    [Fact]
    public void The_pool_writer_is_a_role_that_renders_icons()
    {
        var writer = Regex.Match(CompositionRootSource(),
            @"poolIsWriter\s*=\s*role\s+is\s+ShellRole\.(?<role>\w+)").Groups["role"].Value;

        Assert.False(writer == "Core",
            "ShellRole.Core cannot be the icon-pool writer: RunShellCore never resolves IIconProvider, "
            + "so PooledIconProvider is never constructed and TryAdd is never called. That is bevel-3gad "
            + "— the pool silently caches nothing. Pick a role that renders icons, or make the core "
            + "resolve and drive an icon provider first.");

        Assert.Equal("Taskbar", writer);
    }

    /// <summary>A reader that attaches before any writer has created the file is detached FOREVER —
    /// the pool is a DI singleton, opened once. Pinned here because it constrains the design: the
    /// writer must be a long-lived process that starts before the readers it serves, which the
    /// supervised taskbar is and an on-demand Filer is not.</summary>
    [Fact]
    public void Writer_choice_respects_the_detach_once_semantics()
    {
        var src = CompositionRootSource();

        Assert.Contains("MmfBgraPool.CreateOrOpen", src);
        Assert.DoesNotContain("poolIsWriter = role is ShellRole.Filer", src);
        Assert.DoesNotContain("poolIsWriter = role is ShellRole.Desktop", src);
    }
}
