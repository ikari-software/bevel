using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// The shell's background model service (bevel-d2z). Owns the live, observable projections the
/// taskbar and Start menu bind to — the open windows and the installed programs — and keeps
/// them current off the UI thread. PAL events (window opened/closed/changed) arrive on gRPC /
/// poll worker threads; the heavy work (enumeration, icon rendering/decoding) runs on the
/// thread pool; only the cheap collection mutation and the finished-bitmap assignment marshal
/// onto the dispatcher. Core rule: never block the UI thread.
///
/// The view is now a thin data-bound reflection of these collections — no hand-rolled
/// UpsertButton / Items.Add / re-layout in reaction to each event.
/// </summary>
public sealed class ShellModel : IDisposable
{
    private readonly IWindowManager? _windows;
    private readonly IAppEnvironment? _appEnv;
    private readonly IconLoader _icons;
    private readonly IShellConnectionStatus? _connection;
    private CancellationTokenSource? _cts;
    /// <summary>Live token source of the reconcile loop's between-passes wait, cancelled to wake it
    /// early (a reconnect pokes it so the strip re-syncs at once, not after the next interval).</summary>
    private CancellationTokenSource? _reconcileWait;
    private bool _started;
    private bool _disposed;
    /// <summary>Last known focused window; reconcile keeps this when the snapshot omits focus.</summary>
    private string? _focusedWindowId;

    /// <summary>Decoded window-icon bitmaps keyed by a content hash of the PNG bytes (bevel-gww.8),
    /// so byte-identical app icons across many windows are decoded once and shared.</summary>
    private readonly ConcurrentDictionary<long, Bitmap> _windowIconCache = new();

    /// <summary>Open foreign windows, one entry per taskbar button. UI-thread-owned.</summary>
    public ObservableCollection<TaskItemViewModel> Windows { get; } = new();

    /// <summary>Installed applications for the Start menu's Programs cascade. UI-thread-owned.</summary>
    public ObservableCollection<ProgramItemViewModel> Programs { get; } = new();

    /// <summary>
    /// False until the first installed-apps enumeration finishes (success, failure, or no app
    /// environment), then latched true. Lets the Start menu tell "still loading" (empty + not yet
    /// loaded → "(Loading…)") apart from "genuinely no apps" (empty + loaded → "(No programs
    /// found)"), rather than showing a bare empty cascade during the brief enumeration window.
    /// </summary>
    public bool ProgramsLoaded { get; private set; }

    /// <summary>Raised on the UI thread the moment <see cref="ProgramsLoaded"/> latches true.</summary>
    public event Action? ProgramsLoadedChanged;

    public ShellModel(IWindowManager? windows, IAppEnvironment? appEnv, IIconProvider? icons,
        IShellConnectionStatus? connection = null)
    {
        _windows = windows;
        _appEnv = appEnv;
        _icons = new IconLoader(icons);
        _connection = connection;
    }

    /// <summary>The shared off-thread icon loader, for view-models that render their own icons.</summary>
    public IconLoader Icons => _icons;

    /// <summary>
    /// Subscribes to PAL window events and kicks the off-thread enumeration of installed apps.
    /// Idempotent. Call before starting the window manager's stream/poll so the initial snapshot
    /// is captured; existing windows are also seeded directly in case the stream races.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        if (_windows is not null)
        {
            _windows.WindowOpened += OnWindowOpened;
            _windows.WindowChanged += OnWindowChanged;
            _windows.ForegroundChanged += OnForegroundChanged;
            _windows.WindowClosed += OnWindowClosed;
            _cts = new CancellationTokenSource();
            _ = ReconcileLoopAsync(_cts.Token);

            // Inbound reconnect recovery: the core re-pushes its snapshot on reconnect, but a window
            // that CLOSED during the gap isn't in it, so it would linger until the next reconcile.
            // Poke the loop the moment the link is back so the full add/remove reconcile runs at once.
            if (_connection is not null)
                _connection.ConnectionChanged += OnConnectionChanged;
        }

        _ = LoadProgramsAsync();
    }

    // ── Windows ─────────────────────────────────────────────────────────

    /// <summary>
    /// Authoritative backstop for the event stream: seeds the initial list, then every
    /// <see cref="ReconcileInterval"/> enumerates the live windows off-thread and — on the UI
    /// thread — prunes any button whose window is gone and upserts the rest. Events keep the
    /// strip instant; this guarantees a stale button (a missed Close for a transient dialog like
    /// "Open" or "Profiles") can't linger, which pure event-handling can't promise.
    /// </summary>
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var live = await _windows!.EnumerateAsync(ct).ConfigureAwait(false);
                Post(() => Reconcile(live));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { TaskbarLog.Swallowed("ReconcileLoop", ex); } // helper not up yet — retry next tick

            // Interruptible wait: OnConnectionChanged cancels this to force an immediate re-enumerate
            // on reconnect. A linked source keeps real shutdown (ct) distinct from a poke.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _reconcileWait, wait);
            try { await Task.Delay(ReconcileInterval, wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* poked by a reconnect — reconcile now */ }
            finally { Volatile.Write(ref _reconcileWait, null); }
        }
    }

    /// <summary>Reconnect handler: on link-up, wake the reconcile loop so it re-enumerates and prunes
    /// anything that changed during the outage immediately. Fires off a transport thread.</summary>
    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected) return;
        var wait = Volatile.Read(ref _reconcileWait);
        if (wait is null) return;
        try { wait.Cancel(); } catch (ObjectDisposedException) { /* loop advanced past this wait */ }
    }

    /// <summary>UI thread. Prune windows absent from the fresh enumeration; upsert the present.</summary>
    private void Reconcile(IReadOnlyList<ForeignWindow> live)
    {
        if (_disposed) return;

        // Desired button set, ordered to hold the strip STABLE. The helper enumerates in CG
        // z-order — which reshuffles on every focus change — so we must not let the reconciler
        // reorder buttons to match it: rank surviving buttons by their current slot (a stable
        // OrderBy keeps them put) and append genuinely new windows in enumeration order. A window
        // earns a slot if it has a title/app id, or if it already owns a button and is still alive
        // (so a transient empty-title rename doesn't yank an existing button).
        var slotOf = new Dictionary<string, int>(Windows.Count);
        for (var i = 0; i < Windows.Count; i++)
            slotOf[Windows[i].Id.Value] = i;

        var desired = live
            .Where(w => EarnsButton(w) || slotOf.ContainsKey(w.Id.Value))
            .OrderBy(w => slotOf.TryGetValue(w.Id.Value, out var s) ? s : int.MaxValue)
            .ToList();

        // Reconcile is the 2s backstop; log only when it actually changes the button SET (add/remove),
        // not on every no-op tick — a set delta here is what reflows the strip.
        var desiredIds = new HashSet<string>(desired.Select(w => w.Id.Value));
        var added = desired.Where(w => !slotOf.ContainsKey(w.Id.Value)).ToList();
        var removed = Windows.Where(vm => !vm.IsClosing && !desiredIds.Contains(vm.Id.Value)).ToList();
        if (added.Count > 0 || removed.Count > 0)
        {
            TaskbarLog.Debug($"RECONCILE (2s backstop) live={live.Count} desired={desired.Count} " +
                $"+[{string.Join(", ", added.Select(w => $"'{w.Title}'/{w.Id.Value}"))}] " +
                $"-[{string.Join(", ", removed.Select(vm => $"'{vm.Title}'/{vm.Id.Value}"))}]");
        }

        // Fold onto the shared keyed in-place diff. onRemove: BeginExit gives vanished windows the
        // XP exit animation and defers the actual removal; update: revive + refresh a survivor in
        // place (a re-add of a closing id revives its exact VM/container instead of rebuilding).
        ObservableReconciler.Reconcile(
            Windows, desired,
            keyOf: vm => vm.Id.Value,
            sourceKeyOf: w => w.Id.Value,
            create: CreateItem,
            update: (vm, w) => { if (EarnsButton(w)) ApplyUpdate(vm, w); },
            onRemove: BeginExit);

        var liveIds = new HashSet<string>(live.Select(w => w.Id.Value));
        ApplyFocusFromSnapshot(live, liveIds);
    }

    /// <summary>
    /// Reconcile-time focus: apply a snapshot when it names a focused window; otherwise keep
    /// the sticky projection so a transient all-false enumeration does not clear every button.
    /// </summary>
    private void ApplyFocusFromSnapshot(IReadOnlyList<ForeignWindow> live, HashSet<string> liveIds)
    {
        if (TaskbarLog.IsEnabled)
        {
            var focusedInSnap = live.Where(w => w.IsFocused).Select(w => $"'{w.Title}'").ToList();
            var minimizedInSnap = live.Where(w => w.IsMinimized).Select(w => $"'{w.Title}'").ToList();
            TaskbarLog.Debug($"SNAPSHOT focus=[{string.Join(", ", focusedInSnap)}] " +
                $"minimized=[{string.Join(", ", minimizedInSnap)}]");
        }
        var snapshotFocus = live.FirstOrDefault(w => w.IsFocused)?.Id.Value;
        if (snapshotFocus is not null)
        {
            ApplyExclusiveFocus(snapshotFocus);
            return;
        }

        if (_focusedWindowId is not null && liveIds.Contains(_focusedWindowId))
            ApplyExclusiveFocus(_focusedWindowId);
        else if (_focusedWindowId is not null)
            ApplyExclusiveFocus(null);
    }

    private void OnWindowOpened(object? sender, ForeignWindow w) => Post(() =>
    {
        TaskbarLog.Debug($"event=WindowOpened {Meta(w)}");
        Upsert(w);
        if (w.IsFocused)
            ApplyExclusiveFocus(w.Id.Value);
    });

    /// <summary>Title/minimize/frame updates only — focus is not applied here.</summary>
    private void OnWindowChanged(object? sender, ForeignWindow w) => Post(() =>
    {
        TaskbarLog.Debug($"event=WindowChanged {Meta(w)}");
        Upsert(w);
    });

    private void OnForegroundChanged(object? sender, ForeignWindow w) => Post(() =>
    {
        TaskbarLog.Debug($"event=ForegroundChanged {Meta(w)}");
        Upsert(w);
        if (w.IsFocused)
            ApplyExclusiveFocus(w.Id.Value);
    });
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    /// <summary>UI thread. Exactly one taskbar button may appear pressed at a time.</summary>
    private void ApplyExclusiveFocus(string? focusedId)
    {
        if (_focusedWindowId != focusedId)
        {
            var title = Find(focusedId ?? "")?.Title ?? "(none)";
            TaskbarLog.Debug($"FOCUS -> id={focusedId ?? "(null)"} title='{title}'");
        }
        _focusedWindowId = focusedId;
        foreach (var vm in Windows)
            vm.SetFocused(vm.Id.Value == focusedId);
    }

    private void OnWindowClosed(object? sender, ForeignWindow w) => Post(() =>
    {
        TaskbarLog.Debug($"event=WindowClosed {Meta(w)}");
        if (_focusedWindowId == w.Id.Value)
            ApplyExclusiveFocus(null);
        Remove(w.Id.Value);
    });

    /// <summary>Single-window upsert for the instant event path (open/change/foreground). The
    /// full-set backstop goes through <see cref="Reconcile"/> and the shared reconciler instead.</summary>
    private void Upsert(ForeignWindow w)
    {
        if (_disposed || !EarnsButton(w)) return;

        var vm = Find(w.Id.Value);
        if (vm is not null)
            ApplyUpdate(vm, w);
        else if (_windows is not null)
        {
            TaskbarLog.Debug($"ADD button (Upsert) {Meta(w)} -> count {Windows.Count + 1}");
            Windows.Add(CreateItem(w));
        }
    }

    /// <summary>Single-window remove for the instant event path — hands the button to the same
    /// deferred exit animation the reconciler's onRemove hook uses.</summary>
    private void Remove(string id)
    {
        if (Find(id) is { } vm)
        {
            TaskbarLog.Debug($"REMOVE button (event) id={id} title='{vm.Title}' -> exit-anim");
            BeginExit(vm, () => Windows.Remove(vm));
        }
    }

    /// <summary>A window earns a button once it has at least a title or an app id.</summary>
    private static bool EarnsButton(ForeignWindow w) =>
        !(string.IsNullOrEmpty(w.Title) && string.IsNullOrEmpty(w.AppId));

    /// <summary>Linear scan by stable native id — the collection is taskbar-sized.</summary>
    private TaskItemViewModel? Find(string id)
    {
        foreach (var vm in Windows)
            if (vm.Id.Value == id) return vm;
        return null;
    }

    /// <summary>Builds a button for a newly-seen window. It enters at Width/Opacity 0 (the VM
    /// defaults); the view's next layout pass pushes the target width and Opacity 1 so the
    /// template's transition grows it in (XP-style). Icon is decoded off-thread.</summary>
    private TaskItemViewModel CreateItem(ForeignWindow w)
    {
        var vm = new TaskItemViewModel(w, _windows!);
        LoadWindowIcon(vm, w.IconPng); // decode off-thread, assign on UI thread
        return vm;
    }

    /// <summary>Refreshes a surviving button in place. Revive first: if this exact id reappeared
    /// while its button was animating out, cancel the pending exit and keep the VM/container.</summary>
    private static void ApplyUpdate(TaskItemViewModel vm, ForeignWindow w)
    {
        vm.Revive();
        vm.Update(w);
    }

    /// <summary>
    /// The deferred-remove hook shared by the event path and the reconciler. XP-style exit: mark
    /// closing (the layout pass now skips it), animate Width/Opacity to 0 via the template
    /// transition, then run <paramref name="commit"/> after the animation. The button stays in the
    /// collection during the grace period so a reappearing native ID revives this exact VM/container
    /// instead of manufacturing a replacement. No-op if already animating out.
    /// </summary>
    private void BeginExit(TaskItemViewModel vm, Action commit)
    {
        if (vm.IsClosing) return; // already animating out — don't restart or double-schedule
        vm.IsClosing = true;
        vm.Width = 0;
        vm.Opacity = 0;
        var epoch = vm.BeginCloseEpoch();
        DispatcherTimer.RunOnce(() =>
        {
            // A Revive may have cancelled this pending exit — IsClosing cleared, or (for a
            // close -> revive -> close inside the grace window) a newer close epoch that this stale
            // timer must not act on. The commit removes by identity, so it can't hit the wrong VM.
            if (!vm.IsClosing || vm.CloseGeneration != epoch)
                return;
            commit();
        }, ExitAnimation);
    }

    private static readonly TimeSpan ExitAnimation = TimeSpan.FromMilliseconds(160);

    /// <summary>
    /// Decodes the window's PNG icon off the UI thread, then assigns it on the UI thread — but only
    /// once per distinct icon. Many windows of the same app carry byte-identical <c>IconPng</c>, so we
    /// key decoded bitmaps by a content hash of the PNG and reuse the result: N windows of an app now
    /// cost ONE decode and share one immutable image source (Avalonia bitmaps are safe to share across
    /// Image controls). A dedicated in-process cache — not the shared MMF icon pool — because window
    /// icons are process-local and transient, and the pool is single-writer (the taskbar is a reader in
    /// split mode, so writing window icons there would be a second writer). See bevel-gww.8.
    /// </summary>
    private void LoadWindowIcon(TaskItemViewModel vm, byte[]? png)
    {
        if (png is not { Length: > 0 }) return;

        var key = Fnv1a64(png);
        if (_windowIconCache.TryGetValue(key, out var cached))
        {
            Dispatcher.UIThread.Post(() => vm.IconSource = cached); // already decoded — no thread hop needed
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var bmp = new Bitmap(new MemoryStream(png));
                // A near-simultaneous second window with the same icon may also be decoding; TryAdd keeps
                // the first published bitmap and the loser's copy is simply dropped — both assign a valid image.
                var winner = _windowIconCache.GetOrAdd(key, bmp);
                Dispatcher.UIThread.Post(() => vm.IconSource = winner);
            }
            catch (Exception ex) { TaskbarLog.Swallowed("LoadWindowIcon", ex); } // invalid PNG — icon-less
        });
    }

    /// <summary>FNV-1a 64-bit over the PNG bytes — a cheap, stable content key for the icon cache.</summary>
    internal static long Fnv1a64(byte[] data)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        foreach (var b in data) { h ^= b; h *= prime; }
        return unchecked((long)h);
    }

    // ── Programs ────────────────────────────────────────────────────────

    private async Task LoadProgramsAsync()
    {
        // No app environment (e.g. tests/headless): nothing to enumerate, but still latch loaded so
        // the menu resolves "(Loading…)" to "(No programs found)". Runs on the caller's UI thread.
        if (_appEnv is null) { MarkProgramsLoaded(); return; }
        try
        {
            // EnumerateInstalledAppsAsync walks /Applications synchronously — push it to the
            // thread pool so the UI thread never waits on filesystem I/O.
            var apps = await Task.Run(() => _appEnv.EnumerateInstalledAppsAsync().AsTask()).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                // Diff in place, keyed by bundle id: a re-enumeration (app installed/removed) keeps
                // every surviving item's view-model — and therefore its menu container, its loaded
                // icon and the menu's scroll position — instead of a Clear() that rebuilds them all.
                ObservableReconciler.Reconcile(
                    Programs, apps,
                    keyOf: vm => vm.AppId,
                    sourceKeyOf: a => a.AppId,
                    create: a => new ProgramItemViewModel(a, _appEnv, _icons));
            });
        }
        catch (Exception ex) { TaskbarLog.Swallowed("LoadPrograms", ex); } // enumeration failed — Programs stays empty
        finally
        {
            // Latch loaded whether enumeration populated, found nothing, or threw — the menu must
            // stop showing "(Loading…)" once the one-shot startup enumeration has run its course.
            await Dispatcher.UIThread.InvokeAsync(MarkProgramsLoaded);
        }
    }

    private void MarkProgramsLoaded()
    {
        if (_disposed || ProgramsLoaded) return;
        ProgramsLoaded = true;
        ProgramsLoadedChanged?.Invoke();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Compact one-line metadata for a window event/mutation (BEVEL_DEBUG_TASKBAR trace).</summary>
    private static string Meta(ForeignWindow w) =>
        $"id={w.Id.Value} title='{w.Title}' app='{w.AppId}' min={w.IsMinimized} focus={w.IsFocused} earns={EarnsButton(w)}";

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        if (_windows is not null)
        {
            _windows.WindowOpened -= OnWindowOpened;
            _windows.WindowChanged -= OnWindowChanged;
            _windows.ForegroundChanged -= OnForegroundChanged;
            _windows.WindowClosed -= OnWindowClosed;
        }

        if (_connection is not null)
            _connection.ConnectionChanged -= OnConnectionChanged;
    }
}
