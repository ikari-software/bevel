using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real in-process macOS IAppEnvironment implementation using NSWorkspace / LaunchServices
/// for app enumeration and launching. No helper process, no elevated permissions.
///
/// <list type="bullet">
///   <item>Installed apps: enumerated from /Applications, /System/Applications, and ~/Applications.</item>
///   <item>Running apps: queried via [NSWorkspace runningApplications].</item>
///   <item>Launching: uses LSOpenCFURLRef from LaunchServices for activation and single-instance semantics.</item>
///   <item>Live updates: FileSystemWatcher on the three application roots triggers re-enumeration.</item>
/// </list>
/// </summary>
public sealed class MacOSAppEnvironment : IAppEnvironment, IDisposable
{
    private readonly string[] _appRoots;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Lock _lock = new();

    private IReadOnlyList<InstalledApp> _cachedInstalled = Array.Empty<InstalledApp>();
    private IReadOnlyList<RunningApp> _cachedRunning = Array.Empty<RunningApp>();

    private bool _disposed;

    // Debounce + single-flight for filesystem-driven refreshes (see OnAppRootChanged).
    private readonly Lock _refreshLock = new();
    private System.Threading.Timer? _debounce;
    private bool _refreshRunning;
    private bool _refreshQueued;

    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;

    /// <summary>
    /// Creates the environment with the default macOS application roots.
    /// </summary>
    public MacOSAppEnvironment()
        : this(DefaultAppRoots())
    {
    }

    /// <summary>
    /// Test seam: supply custom application roots (e.g. temp directories for tests).
    /// </summary>
    internal MacOSAppEnvironment(string[] appRoots)
    {
        _appRoots = appRoots;

        // Pre-load AppKit on macOS so that NSWorkspace classes are available.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            AppKitInterop.EnsureAppKitLoaded();

        foreach (var root in _appRoots)
        {
            if (!Directory.Exists(root))
                continue;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = false, // start in GetRunningAppsAsync / EnumerateInstalledAppsAsync
            };

            watcher.Created += OnAppRootChanged;
            watcher.Deleted += OnAppRootChanged;
            watcher.Renamed += OnAppRootChanged;
            watcher.Changed += OnAppRootChanged;

            _watchers.Add(watcher);
        }
    }

    // ------------------------------------------------------------------
    //  IAppEnvironment
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

        // Enable watchers on first query.
        EnableWatchers();

        try
        {
            var apps = QueryRunningApps();
            lock (_lock)
            {
                _cachedRunning = apps;
            }
            return ValueTask.FromResult(apps);
        }
        catch
        {
            // If NSWorkspace is unavailable (sandbox, etc.), return cached or empty.
            lock (_lock)
            {
                return ValueTask.FromResult(_cachedRunning);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return ValueTask.FromResult<IReadOnlyList<InstalledApp>>(Array.Empty<InstalledApp>());

        // Enable watchers on first query.
        EnableWatchers();

        var apps = EnumerateInstalledApps();
        lock (_lock)
        {
            _cachedInstalled = apps;
        }
        return ValueTask.FromResult(apps);
    }

    /// <inheritdoc />
    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MacOSAppEnvironment));

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("App launching is only supported on macOS.");

        if (string.IsNullOrWhiteSpace(appIdOrPath))
            throw new ArgumentException("App ID or path must not be empty.", nameof(appIdOrPath));

        // Resolve the path: if it looks like a filesystem path, use it directly;
        // otherwise treat it as a bundle identifier and look up the app path.
        string resolvedPath;
        if (Path.IsPathRooted(appIdOrPath) && (Directory.Exists(appIdOrPath) || appIdOrPath.EndsWith(".app")))
        {
            resolvedPath = appIdOrPath;
            if (!Directory.Exists(resolvedPath))
                throw new FileNotFoundException($"Application not found at '{resolvedPath}'.");
        }
        else
        {
            var maybePath = ResolveBundlePath(appIdOrPath);
            if (maybePath is null)
                throw new FileNotFoundException($"Could not find application for '{appIdOrPath}'.");
            resolvedPath = maybePath;
        }

        var success = AppKitInterop.LaunchApplication(resolvedPath);
        if (!success)
            throw new InvalidOperationException($"Failed to launch application at '{resolvedPath}'.");

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    //  IDisposable
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        System.Threading.Timer? debounce;
        lock (_refreshLock)
        {
            debounce = _debounce;
            _debounce = null;
        }
        debounce?.Dispose();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    // ------------------------------------------------------------------
    //  Implementation
    // ------------------------------------------------------------------

    private static string[] DefaultAppRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            "/Applications",
            "/System/Applications",
            Path.Combine(home, "Applications"),
        };
    }

    private bool _watchersEnabled;

    private void EnableWatchers()
    {
        if (_watchersEnabled)
            return;

        _watchersEnabled = true;
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Watcher may fail on directories that don't exist or are inaccessible.
            }
        }
    }

    private void OnAppRootChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed)
            return;

        // Coalesce a burst of filesystem events (installing an app writes many files across its bundle) into
        // a single trailing-edge refresh: (re)arm a 250ms timer that fires once the writes go quiet, instead
        // of spawning one delayed full re-enumeration per event (review: adversarial — watcher storm).
        lock (_refreshLock)
        {
            _debounce ??= new System.Threading.Timer(_ => TriggerRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(250, Timeout.Infinite);
        }
    }

    // Run the refresh, but never two at once: overlapping refreshes would both read the same _cachedRunning,
    // diff it, and write it back — emitting duplicate or torn launch/terminate events. If one is already
    // running, remember to run exactly one more pass afterwards for changes that landed mid-refresh.
    private void TriggerRefresh()
    {
        lock (_refreshLock)
        {
            if (_refreshRunning) { _refreshQueued = true; return; }
            _refreshRunning = true;
        }
        _ = Task.Run(RunRefreshLoop);
    }

    private void RunRefreshLoop()
    {
        while (true)
        {
            RefreshAndRaiseEvents();
            lock (_refreshLock)
            {
                if (!_refreshQueued || _disposed) { _refreshRunning = false; return; }
                _refreshQueued = false;
            }
        }
    }

    private void RefreshAndRaiseEvents()
    {
        if (_disposed)
            return;

        try
        {
            // The watcher is on the application folders, so a change means the INSTALLED set may have
            // changed: re-enumerate and publish the fresh list (bevel). Consumers diff it in place, so a
            // no-op change is harmless.
            InstalledAppsChanged?.Invoke(this, EnumerateInstalledApps());

            IReadOnlyList<RunningApp> previousRunning;
            lock (_lock)
            {
                previousRunning = _cachedRunning;
            }

            var currentRunning = RunningAppQueryOrDefault();
            var previousSet = new HashSet<string>(previousRunning.Select(a => a.AppId));
            var currentSet = new HashSet<string>(currentRunning.Select(a => a.AppId));

            // Detect newly launched apps.
            foreach (var app in currentRunning)
            {
                if (!previousSet.Contains(app.AppId))
                {
                    AppLaunched?.Invoke(this, app);
                }
            }

            // Detect terminated apps.
            foreach (var app in previousRunning)
            {
                if (!currentSet.Contains(app.AppId))
                {
                    AppTerminated?.Invoke(this, app);
                }
            }

            lock (_lock)
            {
                _cachedRunning = currentRunning;
            }
        }
        catch
        {
            // Best-effort; failures in event raising are swallowed.
        }
    }

    private static IReadOnlyList<RunningApp> QueryRunningApps()
    {
        var workspace = AppKitInterop.SharedWorkspace();
        var appsArray = AppKitInterop.RunningApplications(workspace);
        if (appsArray == IntPtr.Zero)
            return Array.Empty<RunningApp>();

        var count = AppKitInterop.NSArrayCount(appsArray);
        var result = new List<RunningApp>(count);

        for (int i = 0; i < count; i++)
        {
            var app = AppKitInterop.NSArrayObjectAtIndex(appsArray, i);
            if (app == IntPtr.Zero)
                continue;

            var bundleId = AppKitInterop.NSStringToString(AppKitInterop.RunningAppBundleIdentifier(app));
            var displayName = AppKitInterop.NSStringToString(AppKitInterop.RunningAppLocalizedName(app));
            var pid = AppKitInterop.RunningAppProcessIdentifier(app);

            // An app must have at least a bundle ID or display name to be useful.
            if (string.IsNullOrEmpty(bundleId) && string.IsNullOrEmpty(displayName))
                continue;

            result.Add(new RunningApp(
                AppId: bundleId ?? displayName!,
                DisplayName: displayName ?? bundleId!,
                ProcessId: pid));
        }

        return result;
    }

    private IReadOnlyList<RunningApp> RunningAppQueryOrDefault()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return QueryRunningApps();
        }
        catch { }
        return Array.Empty<RunningApp>();
    }

    private IReadOnlyList<InstalledApp> EnumerateInstalledApps()
    {
        var result = new List<InstalledApp>();

        foreach (var root in _appRoots)
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                EnumerateAppsInDirectory(root, result);
            }
            catch
            {
                // Directory may be inaccessible (sandbox, permissions).
            }
        }

        // Sort by display name for a stable, user-friendly order.
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void EnumerateAppsInDirectory(string directory, List<InstalledApp> result)
    {
        // Recurse one level deep: /Applications/Utilities/Terminal.app is common,
        // but we don't want to go too deep (e.g. into .app bundle internals).
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (entry.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(entry);
                if (string.IsNullOrEmpty(name))
                    continue;

                // Skip hidden apps (e.g. ".hidden.app") unless they're in /System/Applications.
                if (name.StartsWith('.') && !directory.StartsWith("/System", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new InstalledApp(
                    AppId: entry,
                    DisplayName: name,
                    IconPath: entry,
                    Subtitle: ReadAppCategory(entry)));
            }
            else if (Directory.Exists(entry))
            {
                // Recurse into subdirectories (e.g., /Applications/Utilities/).
                try
                {
                    EnumerateAppsInDirectory(entry, result);
                }
                catch
                {
                    // Inaccessible subdirectory — skip.
                }
            }
        }
    }

    /// <summary>Reads the app's LSApplicationCategoryType from its Info.plist and maps it to a short,
    /// user-friendly label ("Developer Tools"), or null if it declares none. The value is a literal
    /// "public.app-category.X" string in the plist (XML or binary alike), so a byte-scan reads it without a
    /// plist parser or native interop; Info.plists are small, so the per-app read is cheap.</summary>
    private static string? ReadAppCategory(string appPath)
    {
        try
        {
            var plist = Path.Combine(appPath, "Contents", "Info.plist");
            if (!File.Exists(plist)) return null;
            // ASCII suffices — the category token is ASCII; any other bytes decode to harmless chars.
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(plist));
            var m = System.Text.RegularExpressions.Regex.Match(text, @"public\.app-category\.([a-z-]+)");
            return m.Success ? FriendlyCategory(m.Groups[1].Value) : null;
        }
        catch
        {
            return null; // unreadable plist → no subtitle, never break enumeration
        }
    }

    private static string FriendlyCategory(string token) => token switch
    {
        "developer-tools" => "Developer Tools",
        "utilities" => "Utilities",
        "productivity" => "Productivity",
        "graphics-design" => "Graphics & Design",
        "photography" => "Photography",
        "video" => "Video",
        "music" => "Music",
        "entertainment" => "Entertainment",
        "games" => "Games",
        "social-networking" => "Social Networking",
        "business" => "Business",
        "finance" => "Finance",
        "education" => "Education",
        "news" => "News",
        "reference" => "Reference",
        "lifestyle" => "Lifestyle",
        "travel" => "Travel",
        "weather" => "Weather",
        "healthcare-fitness" => "Health & Fitness",
        _ => CapitalizeWords(token.Replace('-', ' ')),
    };

    private static string CapitalizeWords(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Resolves a bundle identifier (e.g. "com.apple.Safari") to a path on disk.
    /// Uses NSWorkspace to find the app URL for the given bundle ID.
    /// </summary>
    private static string? ResolveBundlePath(string bundleId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return null;

        try
        {
            var workspace = AppKitInterop.SharedWorkspace();
            var url = AppKitInterop.URLForApplicationWithBundleIdentifier(workspace, bundleId);
            if (url == IntPtr.Zero)
                return null;

            try
            {
                var path = AppKitInterop.NSURLPath(url);
                return AppKitInterop.NSStringToString(path);
            }
            finally
            {
                AppKitInterop.SendVoid(url, AppKitInterop.Sel("release"));
            }
        }
        catch
        {
            return null;
        }
    }
}