using System.Diagnostics;
using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;
using static Bevel.Pal.MacOS.AccessibilityInterop;
using static Bevel.Pal.MacOS.CoreGraphicsInterop;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real macOS IPermissionBroker using AXIsProcessTrustedWithOptions for Accessibility
/// and deep-linking to System Settings for the grant flow.
/// </summary>
public sealed class MacOSPermissionBroker : IPermissionBroker
{
    private readonly object _gate = new();
    private EventHandler<ShellPermission>? _permissionChanged;
    private System.Threading.Timer? _pollTimer;
    private readonly PermissionChangeTracker _tracker = new();

    /// <summary>bevel-sdss: raised when a polled permission flips. TCC has no change notification,
    /// so a 2 s timer runs ONLY while there is at least one subscriber (started on first add,
    /// stopped on last remove) and each tick raises the event for the permissions whose state
    /// changed since the previous tick.</summary>
    public event EventHandler<ShellPermission>? PermissionChanged
    {
        add
        {
            lock (_gate)
            {
                _permissionChanged += value;
                _pollTimer ??= new System.Threading.Timer(
                    _ => _ = PollAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
        }
        remove
        {
            lock (_gate)
            {
                _permissionChanged -= value;
                if (_permissionChanged is null)
                {
                    _pollTimer?.Dispose();
                    _pollTimer = null;
                }
            }
        }
    }

    private async Task PollAsync()
    {
        EventHandler<ShellPermission>? handler;
        lock (_gate) handler = _permissionChanged;
        if (handler is null) return;

        var accessibility = await GetStateAsync(ShellPermission.Accessibility).ConfigureAwait(false);
        var screenRecording = await GetStateAsync(ShellPermission.ScreenRecording).ConfigureAwait(false);
        foreach (var permission in _tracker.Observe(accessibility, screenRecording))
            handler.Invoke(this, permission);
    }

    public ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ValueTask.FromResult(PermissionState.NotApplicable);

        return permission switch
        {
            ShellPermission.Accessibility => ValueTask.FromResult(
                AXIsProcessTrusted() ? PermissionState.Granted : PermissionState.Denied),
            // Screen Recording gates live status-item pixel capture (spec §5.3). Preflight checks the
            // grant WITHOUT prompting — the tray falls back to limited mode (§5.5) until it's granted.
            ShellPermission.ScreenRecording => ValueTask.FromResult(
                CGPreflightScreenCaptureAccess() ? PermissionState.Granted : PermissionState.Denied),
            _ => ValueTask.FromResult(PermissionState.NotApplicable),
        };
    }

    public Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult(PermissionState.NotApplicable);

        switch (permission)
        {
            case ShellPermission.Accessibility:
                // Deep-link to Security & Privacy → Accessibility.
                // On Sequoia/Tahoe, the system no longer shows a modal from AXIsProcessTrustedWithOptions;
                // the deep-link is the only reliable path.
                OpenPrivacyPane("Privacy_Accessibility");
                return Task.FromResult(PermissionState.Denied);

            case ShellPermission.ScreenRecording:
                // First-run: CGRequestScreenCaptureAccess() shows the system prompt (once); afterwards
                // the toggle only changes in System Settings, so deep-link there too. Returns the
                // preflight state — the grant usually takes effect only after the app is relaunched.
                var granted = CGRequestScreenCaptureAccess();
                if (!granted) OpenPrivacyPane("Privacy_ScreenCapture");
                return Task.FromResult(granted ? PermissionState.Granted : PermissionState.Denied);

            default:
                return Task.FromResult(PermissionState.NotApplicable);
        }
    }

    private static void OpenPrivacyPane(string anchor)
        => Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            Arguments = $"x-apple.systempreferences:com.apple.preference.security?{anchor}",
            UseShellExecute = true,
        });


    // Screen Recording grant checks (CoreGraphics). Preflight never prompts; Request prompts once.

}