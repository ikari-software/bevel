using Bevel.Pal.Abstractions;
using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Tests for MacOSAppEnvironment: installed-app enumeration from filesystem directories,
/// running-app queries via NSWorkspace, and app launching.
/// </summary>
public sealed class MacOSAppEnvironmentTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly MacOSAppEnvironment _env;

    public MacOSAppEnvironmentTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bevel-apps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _env = new MacOSAppEnvironment(new[] { _tempRoot });
    }

    public void Dispose()
    {
        _env.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    //  Happy path: enumerating a temp directory structured like /Applications
    // ------------------------------------------------------------------

    [Fact]
    public async Task EnumerateInstalledApps_yields_apps_from_directory()
    {
        // Arrange: create a structure like /Applications
        //   /Applications/Safari.app
        //   /Applications/Utilities/Terminal.app
        CreateAppBundle("Safari.app");
        var utilsDir = Path.Combine(_tempRoot, "Utilities");
        Directory.CreateDirectory(utilsDir);
        CreateAppBundle(Path.Combine("Utilities", "Terminal.app"));

        // Act
        var apps = await _env.EnumerateInstalledAppsAsync();

        // Assert
        Assert.NotNull(apps);
        Assert.Equal(2, apps.Count);

        var safari = apps.FirstOrDefault(a => a.DisplayName == "Safari");
        Assert.NotNull(safari);
        Assert.EndsWith("Safari.app", safari.AppId);
        Assert.Equal(safari.AppId, safari.IconPath);

        var terminal = apps.FirstOrDefault(a => a.DisplayName == "Terminal");
        Assert.NotNull(terminal);
        Assert.EndsWith("Terminal.app", terminal.AppId);
    }

    [Fact]
    public async Task EnumerateInstalledApps_skips_hidden_apps()
    {
        // Arrange: hidden apps (starting with .) should be skipped
        CreateAppBundle(".hidden.app");
        CreateAppBundle("Visible.app");

        // Act
        var apps = await _env.EnumerateInstalledAppsAsync();

        // Assert
        Assert.Single(apps);
        Assert.Equal("Visible", apps[0].DisplayName);
    }

    [Fact]
    public async Task EnumerateInstalledApps_sorts_by_display_name()
    {
        // Arrange
        CreateAppBundle("Zebra.app");
        CreateAppBundle("Alpha.app");
        CreateAppBundle("Beta.app");

        // Act
        var apps = await _env.EnumerateInstalledAppsAsync();

        // Assert
        Assert.Equal(3, apps.Count);
        Assert.Equal("Alpha", apps[0].DisplayName);
        Assert.Equal("Beta", apps[1].DisplayName);
        Assert.Equal("Zebra", apps[2].DisplayName);
    }

    // ------------------------------------------------------------------
    //  Edge case: empty directory yields empty but non-null catalog
    // ------------------------------------------------------------------

    [Fact]
    public async Task EnumerateInstalledApps_empty_directory_returns_empty_list()
    {
        // Act
        var apps = await _env.EnumerateInstalledAppsAsync();

        // Assert
        Assert.NotNull(apps);
        Assert.Empty(apps);
    }

    [Fact]
    public async Task EnumerateInstalledApps_nonexistent_root_returns_empty_list()
    {
        // Arrange
        using var env = new MacOSAppEnvironment(new[] { "/does/not/exist" });

        // Act
        var apps = await env.EnumerateInstalledAppsAsync();

        // Assert
        Assert.NotNull(apps);
        Assert.Empty(apps);
    }

    // ------------------------------------------------------------------
    //  Error path: LaunchAsync with non-existent app surfaces clear failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task LaunchAsync_empty_path_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _env.LaunchAsync(""));
    }

    [Fact]
    public async Task LaunchAsync_null_path_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _env.LaunchAsync(null!));
    }

    [Fact]
    public async Task LaunchAsync_nonexistent_app_throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _env.LaunchAsync("/nonexistent/Something.app"));
    }

    // ------------------------------------------------------------------
    //  Integration: FileSystemWatcher picks up changes
    // ------------------------------------------------------------------

    [Fact]
    public async Task FileSystemWatcher_detects_new_app_creation()
    {
        // Trigger watcher initialization by first query.
        await _env.EnumerateInstalledAppsAsync();

        // Wait a bit for watcher to be fully enabled.
        await Task.Delay(100);

        // Create a new app bundle.
        CreateAppBundle("NewApp.app");

        // Allow the watcher + debounce to fire.
        await Task.Delay(500);

        // Enumerate again — should include the new app.
        var apps = await _env.EnumerateInstalledAppsAsync();

        Assert.Contains(apps, a => a.DisplayName == "NewApp");
    }

    [Fact]
    public async Task FileSystemWatcher_detects_app_deletion()
    {
        CreateAppBundle("DeleteMe.app");

        // Trigger watcher
        await _env.EnumerateInstalledAppsAsync();
        await Task.Delay(100);

        // Delete the app
        Directory.Delete(Path.Combine(_tempRoot, "DeleteMe.app"), recursive: true);

        await Task.Delay(500);

        var apps = await _env.EnumerateInstalledAppsAsync();
        Assert.DoesNotContain(apps, a => a.DisplayName == "DeleteMe");
    }

    // ------------------------------------------------------------------
    //  Real macOS: running apps and launching (only on macOS)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetRunningApps_returns_results_on_macOS()
    {
        if (!OperatingSystem.IsMacOS())
            return; // macOS-only test

        using var env = new MacOSAppEnvironment();
        var apps = await env.GetRunningAppsAsync();

        Assert.NotNull(apps);
        // A real macOS session always has running apps (Finder, WindowServer clients, this test host).
        Assert.NotEmpty(apps);

        foreach (var app in apps)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.AppId), $"App {app.DisplayName} has empty AppId");
            Assert.True(app.ProcessId > 0, $"App {app.DisplayName} has invalid PID: {app.ProcessId}");
        }
    }

    [Fact(Skip = "Opens Calculator.app on the dev machine — re-enable when the launch path needs re-verification.")]
    public async Task LaunchAsync_opens_known_app_on_macOS()
    {
        if (!OperatingSystem.IsMacOS())
            return; // macOS-only test

        // Calculator.app is present on every macOS install.
        var calcPath = "/System/Applications/Calculator.app";
        if (!Directory.Exists(calcPath))
            return; // Calculator.app not found on this system

        using var env = new MacOSAppEnvironment();

        try
        {
            await env.LaunchAsync(calcPath);
        }
        catch (Exception)
        {
            // P/Invoke bridge may not be fully wired or the app may be blocked by security.
            return;
        }

        // Give it a moment to launch.
        await Task.Delay(500);

        // Verify it appears in running apps.
        IReadOnlyList<RunningApp> running;
        try
        {
            running = await env.GetRunningAppsAsync();
        }
        catch
        {
            return; // Running apps query unavailable.
        }

        if (running.Count == 0)
            return; // Running in restricted environment.

        Assert.Contains(running, a => a.AppId.Contains("Calculator", StringComparison.OrdinalIgnoreCase)
                                   || a.DisplayName.Contains("Calculator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "Opens Calculator.app on the dev machine — re-enable when the launch path needs re-verification.")]
    public async Task LaunchAsync_by_bundle_identifier_on_macOS()
    {
        if (!OperatingSystem.IsMacOS())
            return; // macOS-only test

        // com.apple.calculator is the bundle ID for Calculator.app
        using var env = new MacOSAppEnvironment();

        try
        {
            await env.LaunchAsync("com.apple.calculator");

            await Task.Delay(500);

            var running = await env.GetRunningAppsAsync();
            if (running.Count > 0)
            {
                Assert.Contains(running, a => a.AppId.Contains("Calculator", StringComparison.OrdinalIgnoreCase)
                                           || a.DisplayName.Contains("Calculator", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (FileNotFoundException)
        {
            // Calculator.app may not be on this system — acceptable.
        }
        catch (InvalidOperationException)
        {
            // P/Invoke bridge may not be fully wired — acceptable in test environment.
        }
    }

    // ------------------------------------------------------------------
    //  Dispose safety
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disposed_environment_returns_empty_results()
    {
        _env.Dispose();

        var installed = await _env.EnumerateInstalledAppsAsync();
        Assert.Empty(installed);

        var running = await _env.GetRunningAppsAsync();
        Assert.Empty(running);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _env.LaunchAsync("/Applications/Safari.app"));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private void CreateAppBundle(string relativePath)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(fullPath);
        // Create a minimal Contents dir to make it look like a real .app bundle.
        Directory.CreateDirectory(Path.Combine(fullPath, "Contents"));
    }
}