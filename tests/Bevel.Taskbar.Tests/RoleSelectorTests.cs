using System.Linq;
using Bevel.App;
using Bevel.App.ShellCore;
using Bevel.Core;
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
    [InlineData("--role=core", ShellRole.Core)]
    [InlineData("--role=launcher", ShellRole.Launcher)]
    [InlineData("--role=nonsense", ShellRole.Launcher)]    // unknown value -> the split launcher
    public void Parses_known_roles(string arg, ShellRole expected) =>
        Assert.Equal(expected, RoleSelector.FromArgs(new[] { arg }));

    [Fact]
    public void Defaults_to_launcher_when_absent() =>
        Assert.Equal(ShellRole.Launcher, RoleSelector.FromArgs(new[] { "--pal=macos" }));

    [Fact]
    public void Picks_the_role_flag_out_of_a_mixed_argv() =>
        Assert.Equal(ShellRole.Explorer,
            RoleSelector.FromArgs(new[] { "--pal=macos", "--role=explorer", "--other" }));

    [Theory]
    [InlineData(ShellRole.Core, true)]       // the headless owner runs the helper
    [InlineData(ShellRole.Taskbar, false)]   // split taskbar is a shell-core CLIENT, no helper
    [InlineData(ShellRole.Explorer, false)]
    [InlineData(ShellRole.Desktop, false)]
    public void Helper_is_hosted_only_for_the_core_role(ShellRole role, bool expectHosted)
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

    // ── core-owns-settings role wiring (bevel-6nve) ──────────────────────────────────────────────

    [Theory]
    [InlineData(ShellRole.Core, "SettingsService")]       // the headless owner is the sole DB writer
    [InlineData(ShellRole.Taskbar, "RemoteSettingsService")]  // peers read/write through the core
    [InlineData(ShellRole.Explorer, "RemoteSettingsService")]
    [InlineData(ShellRole.Desktop, "RemoteSettingsService")]
    public void Settings_service_is_the_real_one_for_core_and_remote_for_peers(ShellRole role, string expectedImpl)
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, role);

        var settings = services.Single(d => d.ServiceType == typeof(ISettingsService));
        Assert.Equal(expectedImpl, settings.ImplementationType?.Name);
    }

    [Theory]
    [InlineData(ShellRole.Core, false)]       // the core is the server, never a client
    [InlineData(ShellRole.Taskbar, true)]     // shared window/app/tray client + a keyed "settings" client
    [InlineData(ShellRole.Explorer, true)]    // settings-only client (window/app/tray stay direct-PAL)
    [InlineData(ShellRole.Desktop, true)]
    public void Shell_core_client_is_registered_only_for_peer_roles(ShellRole role, bool expectClient)
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, role);

        var hasClient = services.Any(d => d.ServiceType == typeof(ShellCoreClient));
        Assert.Equal(expectClient, hasClient);
    }

    // bevel-6nve regression guard: the split taskbar's settings peer MUST get its own keyed "settings"
    // core client, separate from the shared window/app/tray client. Sharing one client made settings'
    // early startup connect eat the core's on-connect TraySnapshot (no tray reconcile backstop), so the
    // split taskbar came up with an empty tray.
    [Fact]
    public void Split_taskbar_settings_uses_a_dedicated_keyed_client_not_the_shared_tray_client()
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, ShellRole.Taskbar);

        var sharedClient = services.Any(d => d.ServiceType == typeof(ShellCoreClient) && d.ServiceKey is null);
        var settingsClient = services.Any(d => d.ServiceType == typeof(ShellCoreClient) && (d.ServiceKey as string) == "settings");
        Assert.True(sharedClient, "expected a shared (non-keyed) window/app/tray core client");
        Assert.True(settingsClient, "expected a dedicated keyed \"settings\" core client for RemoteSettingsService");
    }
}
