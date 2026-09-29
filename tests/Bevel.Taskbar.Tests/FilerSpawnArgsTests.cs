using Bevel.App;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The spawn-command builders (bevel-t48y Task 1): ONE argv builder for filer children
/// (<see cref="Program.BuildFilerArgs"/>) and ONE start-info builder for supervised children
/// (<see cref="Program.CreateRoleStartInfo"/>). These tests pin the exact argv each produces so the
/// supervised-spawn feature (Tasks 2–4) can build identical children through the same seams without
/// silently drifting from today's in-process <c>SpawnFiler</c> behaviour.
/// </summary>
public class FilerSpawnArgsTests
{
    /// <summary>Under a `dotnet` host, <see cref="Program.CreateRestartStartInfo"/> legitimately prepends
    /// the entry-assembly path — assert the argv as a suffix so the test is host-shape independent.</summary>
    private static void AssertArgsSuffix(System.Diagnostics.ProcessStartInfo si, string[] expected)
        => Assert.Equal(expected, si.ArgumentList.Skip(si.ArgumentList.Count - expected.Length));

    // The parent's filer-scoped switches must be REPLACED, not duplicated: a launcher that was itself
    // started with an --open-path must not leak it into the child.
    [Fact]
    public void BuildFilerArgs_replaces_the_parents_filer_scoped_switches_and_appends_the_target()
    {
        var parent = new[]
        {
            "--pal=macos", "--role=launcher", "--open-path=/old/path", "--select=/old/other", "--search",
        };

        var args = Program.BuildFilerArgs(parent, "/Users/ikari/Documents", search: false, selectPath: null);

        Assert.Equal(new[]
        {
            "--pal=macos",
            "--role=filer",
            "--open-path=/Users/ikari/Documents",
        }, args);
    }

    [Fact]
    public void BuildFilerArgs_passes_search_and_select_through_when_given()
    {
        var args = Program.BuildFilerArgs(
            new[] { "--pal=fake" }, "/tmp", search: true, selectPath: "/tmp/a-file.txt");

        Assert.Equal(new[]
        {
            "--pal=fake",
            "--role=filer",
            "--open-path=/tmp",
            "--search",
            "--select=/tmp/a-file.txt",
        }, args);
    }

    [Fact]
    public void CreateRoleStartInfo_replaces_the_role_and_appends_extras_after_it()
    {
        var parent = new[] { "--pal=macos", "--role=launcher" };
        var env = new Dictionary<string, string> { ["BEVEL_TEST_ENV"] = "1" };

        var startInfo = Program.CreateRoleStartInfo(ShellRole.Taskbar, parent, env);
        AssertArgsSuffix(startInfo, new[] { "--pal=macos", "--role=taskbar" });
        Assert.Equal("1", startInfo.Environment["BEVEL_TEST_ENV"]);

        // The supervised filer spawn (bevel-t48y): extras land after the role arg, in order.
        var filer = Program.CreateRoleStartInfo(ShellRole.Filer, parent, env,
            extraChildArgs: new[] { "--open-path=/tmp", "--select=/tmp/x" });
        AssertArgsSuffix(filer,
            new[] { "--pal=macos", "--role=filer", "--open-path=/tmp", "--select=/tmp/x" });
    }
}
