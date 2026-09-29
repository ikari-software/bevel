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
/// non-taskbar roles (so a filer/desktop process doesn't spawn a redundant helper).
/// </summary>
public sealed class RoleSelectorTests
{
    [Theory]
    [InlineData("--role=taskbar", ShellRole.Taskbar)]
    [InlineData("--role=bar", ShellRole.Taskbar)]
    [InlineData("--role=filer", ShellRole.Filer)]
    [InlineData("--role=files", ShellRole.Filer)]
    [InlineData("--role=filemanager", ShellRole.Filer)]
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
        Assert.Equal(ShellRole.Filer,
            RoleSelector.FromArgs(new[] { "--pal=macos", "--role=filer", "--other" }));

    [Theory]
    [InlineData(ShellRole.Core, true)]       // the headless owner runs the helper
    [InlineData(ShellRole.Taskbar, false)]   // split taskbar is a shell-core CLIENT, no helper
    [InlineData(ShellRole.Filer, false)]
    [InlineData(ShellRole.Desktop, false)]
    public void Helper_is_hosted_only_for_the_core_role(ShellRole role, bool expectHosted)
    {
        // Registration doesn't instantiate the macOS types, so this is safe to assert on any OS.
        //
        // Matches the HELPER registration specifically. "Hosts any IHostedService" was an accurate proxy
        // only while the helper was the sole hosted thing; the Core role now also hosts the update
        // checker (bevel-rkxb), so the broad form would stay green even if the helper registration were
        // removed — which is the exact regression this test exists to catch.
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, role);

        var hostsHelper = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Any(d => d.ImplementationType is null    // the helper is factory-registered; nothing else is
                      || d.ImplementationType.Name.Contains("Helper", StringComparison.Ordinal));
        Assert.Equal(expectHosted, hostsHelper);
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
    [InlineData(ShellRole.Filer, "RemoteSettingsService")]
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
    [InlineData(ShellRole.Filer, true)]    // settings-only client (window/app/tray stay direct-PAL)
    [InlineData(ShellRole.Desktop, true)]
    public void Shell_core_client_is_registered_only_for_peer_roles(ShellRole role, bool expectClient)
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, role);

        var hasClient = services.Any(d => d.ServiceType == typeof(ShellCoreClient));
        Assert.Equal(expectClient, hasClient);
    }

    // bevel-4zfs regression guard: the split taskbar's settings peer SHARES the one client now. The old
    // keyed "settings" client existed only because the core pushed its snapshot on CONNECT — the settings
    // peer's early LoadAsync connect would eat the TraySnapshot before the tray adapter subscribed. The
    // session protocol gated that push behind Hello, so exactly ONE non-keyed ShellCoreClient must be
    // registered for the whole process, and the settings peer uses it.
    [Fact]
    public void Split_taskbar_settings_shares_the_one_core_client()
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.MacOS, ShellRole.Taskbar);

        var clients = services.Where(d => d.ServiceType == typeof(ShellCoreClient)).ToList();
        Assert.Single(clients);
        Assert.True(clients[0].ServiceKey is null, "the process's single core client must be non-keyed (shared)");
    }

    // ── Windows PAL wiring (bevel-ncfp.1 / U1) ───────────────────────────────────────────────────

    [Theory]
    [InlineData("--pal=windows", PalKind.Windows)]
    [InlineData("--pal=win", PalKind.Windows)]        // short alias
    [InlineData("--pal=WINDOWS", PalKind.Windows)]    // case-insensitive
    [InlineData("--pal=macos", PalKind.MacOS)]
    [InlineData("--pal=nonsense", PalKind.Fake)]      // unknown -> Fake scaffold
    public void PalSelector_parses_windows(string arg, PalKind expected) =>
        Assert.Equal(expected, PalSelector.FromArgs(new[] { arg }));

    [Fact]
    public void Windows_pal_registers_all_capability_interfaces()
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.Windows, ShellRole.Core);

        // Every PAL surface the Fake PAL registers must be bound for the Windows PAL too.
        foreach (var t in new[]
        {
            typeof(IWindowManager), typeof(ISystemTrayHost), typeof(IDesktopEnvironment),
            typeof(IShellSession), typeof(IFileOperations), typeof(IIconProvider),
            typeof(IAppEnvironment), typeof(IFileOpener), typeof(IPermissionBroker),
            typeof(IAudioPlayback), typeof(IDockController), typeof(IShellConnectionStatus),
            typeof(ITabProvider), typeof(IVolumeLabelSource),
        })
            Assert.True(services.Any(d => d.ServiceType == t), $"Windows PAL missing {t.Name}");
    }

    [Theory]
    [InlineData(ShellRole.Core)]
    [InlineData(ShellRole.Taskbar)]
    [InlineData(ShellRole.Filer)]
    [InlineData(ShellRole.Desktop)]
    public void Windows_pal_hosts_no_helper_service_in_any_role(ShellRole role)
    {
        // Unlike macOS (Core hosts the Swift helper), Windows discovery is in-process — NO role
        // registers a HELPER hosted service.
        //
        // Asserted against helper registrations specifically rather than "no IHostedService at all".
        // The broader form was an accurate proxy only while the helper was the sole thing that would
        // ever be hosted; the Core role now also hosts the update checker (bevel-rkxb), which is not a
        // helper and is deliberately registered on both platforms. The invariant this test names is
        // unchanged.
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.Windows, role);
        var helperHosts = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Where(d => (d.ImplementationType?.Name ?? "").Contains("Helper", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(helperHosts);
    }

    [Theory]
    [InlineData(ShellRole.Core, "WindowsWindowManager")]     // direct in-process discovery owner
    [InlineData(ShellRole.Taskbar, "ShellCoreWindowManager")] // peer: shell-core client
    public void Windows_window_manager_binds_direct_for_core_and_client_for_taskbar(ShellRole role, string expected)
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.Windows, role);
        var wm = services.Single(d => d.ServiceType == typeof(IWindowManager));
        Assert.Equal(expected, wm.ImplementationType?.Name);
    }

    [Theory]
    [InlineData(ShellRole.Core, "SettingsService")]           // sole DB writer
    [InlineData(ShellRole.Taskbar, "RemoteSettingsService")]  // peers read/write through the core
    [InlineData(ShellRole.Filer, "RemoteSettingsService")]
    [InlineData(ShellRole.Desktop, "RemoteSettingsService")]
    public void Windows_settings_service_is_real_for_core_and_remote_for_peers(ShellRole role, string expectedImpl)
    {
        var services = new ServiceCollection();
        services.AddBevelPlatform(PalKind.Windows, role);
        var settings = services.Single(d => d.ServiceType == typeof(ISettingsService));
        Assert.Equal(expectedImpl, settings.ImplementationType?.Name);
    }
}
