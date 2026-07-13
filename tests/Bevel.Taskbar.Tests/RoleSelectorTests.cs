using System.Linq;
using Bevel.App;
using Bevel.Pal.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Covers the <c>--role</c> split's two load-bearing pieces: argument parsing (which surface a
/// process hosts) and the composition-root gating that keeps the eagerly-started helper out of
/// non-taskbar roles (so an explorer/desktop process doesn't spawn a redundant helper).
/// </summary>
public sealed class RoleSelectorTests
{
    [Theory]
    [InlineData("--role=taskbar", ShellRole.Taskbar)]
    [InlineData("--role=bar", ShellRole.Taskbar)]
    [InlineData("--role=explorer", ShellRole.Explorer)]
    [InlineData("--role=files", ShellRole.Explorer)]
    [InlineData("--role=filemanager", ShellRole.Explorer)]
    [InlineData("--role=desktop", ShellRole.Desktop)]
    [InlineData("--role=TASKBAR", ShellRole.Taskbar)]      // case-insensitive
    [InlineData("--role=nonsense", ShellRole.All)]         // unknown value -> all-in-one
    public void Parses_known_roles(string arg, ShellRole expected) =>
        Assert.Equal(expected, RoleSelector.FromArgs(new[] { arg }));

    [Fact]
    public void Defaults_to_all_when_absent() =>
        Assert.Equal(ShellRole.All, RoleSelector.FromArgs(new[] { "--pal=macos" }));

    [Fact]
    public void Picks_the_role_flag_out_of_a_mixed_argv() =>
        Assert.Equal(ShellRole.Explorer,
            RoleSelector.FromArgs(new[] { "--pal=macos", "--role=explorer", "--other" }));

    [Theory]
    [InlineData(ShellRole.All, true)]        // all-in-one owns window management directly
    [InlineData(ShellRole.Core, true)]       // the headless owner runs the helper
    [InlineData(ShellRole.Taskbar, false)]   // split taskbar is a shell-core CLIENT, no helper
    [InlineData(ShellRole.Explorer, false)]
    [InlineData(ShellRole.Desktop, false)]
    public void Helper_is_hosted_only_for_core_and_all_roles(ShellRole role, bool expectHosted)
    {
        // Registration doesn't instantiate the macOS types, so this is safe to assert on any OS.
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, role);

        var hostsAService = services.Any(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(expectHosted, hostsAService);
    }

    [Fact]
    public void Split_taskbar_uses_shell_core_client_window_manager()
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, ShellRole.Taskbar);

        // The taskbar role must bind IWindowManager/IAppEnvironment to the shell-core clients (not the
        // direct macOS impls), so it never touches the helper or NSWorkspace watchers itself.
        var wm = services.Single(d => d.ServiceType == typeof(IWindowManager));
        var ae = services.Single(d => d.ServiceType == typeof(IAppEnvironment));
        Assert.Equal("ShellCoreWindowManager", wm.ImplementationType?.Name);
        Assert.Equal("ShellCoreAppEnvironment", ae.ImplementationType?.Name);
    }

    [Fact]
    public void Core_role_uses_direct_macos_window_manager()
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, ShellRole.Core);

        var wm = services.Single(d => d.ServiceType == typeof(IWindowManager));
        Assert.Equal("MacOSWindowManager", wm.ImplementationType?.Name);
    }
}
