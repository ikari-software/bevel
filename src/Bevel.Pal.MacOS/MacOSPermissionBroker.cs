using System.Diagnostics;
using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real macOS IPermissionBroker using AXIsProcessTrustedWithOptions for Accessibility
/// and deep-linking to System Settings for the grant flow.
/// </summary>
public sealed class MacOSPermissionBroker : IPermissionBroker
{
    public event EventHandler<ShellPermission>? PermissionChanged;

    public ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default)
    {
        return permission switch
        {
            ShellPermission.Accessibility => ValueTask.FromResult(
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && AXIsProcessTrusted()
                    ? PermissionState.Granted
                    : PermissionState.Denied),
            _ => ValueTask.FromResult(PermissionState.NotApplicable),
        };
    }

    public Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default)
    {
        if (permission == ShellPermission.Accessibility && OperatingSystem.IsMacOS())
        {
            // Deep-link to Security & Privacy → Accessibility.
            // On Sequoia/Tahoe, the system no longer shows a modal from AXIsProcessTrustedWithOptions;
            // the deep-link is the only reliable path.
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility",
                UseShellExecute = true,
            });
        }

        return Task.FromResult(PermissionState.Denied);
    }

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern bool AXIsProcessTrusted();
}