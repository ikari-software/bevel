using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Registers/unregisters Bevel as a macOS login item via <c>SMAppService</c>
/// (ServiceManagement.framework, macOS 13+) — the modern replacement for the
/// deprecated <c>SMLoginItemSetEnabled</c> / login-items scripting (bevel-m2.12).
///
/// <para><b>Bundle requirement.</b> SMAppService only works from a properly signed
/// <c>.app</c> bundle; when Bevel is run as a bare <c>dotnet Bevel.App.dll</c>,
/// <c>registerAndReturnError:</c> fails and this reports <c>false</c> rather than
/// throwing. The SettingsService flag is still the source of truth for the toggle;
/// this just makes the OS actually honour it once Bevel runs from its bundle.</para>
/// </summary>
internal static class LoginItemRegistrar
{
    // SMAppServiceStatus (NSInteger)
    private const nint StatusNotRegistered = 0;
    private const nint StatusEnabled = 1;
    private const nint StatusRequiresApproval = 2;
    private const nint StatusNotFound = 3;

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr dlopen(string path, int mode);
    private const int RTLD_LAZY = 0x1;

    private static bool _frameworkLoaded;

    /// <summary>
    /// Resolves the <c>[SMAppService mainAppService]</c> singleton, loading
    /// ServiceManagement.framework first so <c>objc_getClass</c> can see the class.
    /// Returns <see cref="IntPtr.Zero"/> when the framework/class is unavailable
    /// (pre-13 OS, headless CI) — every public method then no-ops safely.
    /// </summary>
    private static IntPtr MainAppService()
    {
        if (!_frameworkLoaded)
        {
            // Must load BEFORE the first GetClass("SMAppService") — GetClass caches nil forever.
            dlopen("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", RTLD_LAZY);
            _frameworkLoaded = true;
        }

        var cls = AppKitInterop.GetClass("SMAppService");
        if (cls == IntPtr.Zero)
            return IntPtr.Zero;

        // Class method; the returned instance is autoreleased (fine on the UI thread's runloop pool).
        return AppKitInterop.SendIntPtr(cls, AppKitInterop.Sel("mainAppService"));
    }

    /// <summary>
    /// Registers (enabled) or unregisters (disabled) Bevel's main app as a login item.
    /// Returns true on success; false if ServiceManagement is unavailable or the call failed
    /// (e.g. not running from a signed bundle).
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        var service = MainAppService();
        if (service == IntPtr.Zero)
            return false;

        var sel = AppKitInterop.Sel(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:");
        // Pass NULL for the NSError** out-param — we surface success/failure via the BOOL only.
        return AppKitInterop.SendBool_IntPtr(service, sel, IntPtr.Zero);
    }

    /// <summary>
    /// True when the OS currently has Bevel registered as an enabled login item.
    /// (<c>requiresApproval</c> / <c>notRegistered</c> / <c>notFound</c> all read as false —
    /// the toggle should reflect what will actually happen at next login.)
    /// </summary>
    public static bool IsEnabled()
    {
        var service = MainAppService();
        if (service == IntPtr.Zero)
            return false;

        return AppKitInterop.SendNInt(service, AppKitInterop.Sel("status")) == StatusEnabled;
    }
}
