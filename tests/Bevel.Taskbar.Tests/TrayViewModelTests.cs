using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The notification-area tray view-model (bevel-m3.1): pulls the initial item set from
/// <see cref="ISystemTrayHost"/> and reconciles the add/remove/update events in place (keyed by id),
/// marshalling each onto the UI thread.
///
/// Icon sizing reads the shared <see cref="TaskbarTheme"/> metrics (the row-height cap, bevel-xpfl),
/// hence the serial theme collection.
/// </summary>
[Collection("TaskbarTheme")]
public class TrayViewModelTests
{
    private static TrayItem Item(string id, string tooltip) => new(new TrayItemId(id), tooltip);

    [AvaloniaFact]
    public void Pulls_initial_snapshot_and_reconciles_events_in_place()
    {
        var alpha = Item("1:10", "Alpha");
        var beta = Item("2:20", "Beta");
        var host = new StubTray(alpha, beta);
        var vm = new TrayViewModel(host);

        vm.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Items.Count);

        // A new item arrives → appended.
        host.RaiseAdded(Item("3:30", "Gamma"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Items.Count);

        // Update to an existing id → no duplicate, tooltip refreshed.
        host.RaiseUpdated(Item("1:10", "Alpha!"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.Tooltip == "Alpha!");

        // Remove → gone.
        host.RaiseRemoved(alpha);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.Id.Value == "1:10");
    }

    [AvaloniaFact]
    public void Null_host_yields_an_empty_tray_without_faulting()
    {
        var vm = new TrayViewModel(null);
        vm.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Items);
    }

    [AvaloniaFact]
    public async Task Forward_passes_the_item_button_and_modifiers_to_the_host()
    {
        var host = new StubTray(Item("9:90", "Clock"));
        var vm = new TrayViewModel(host);

        var ok = await vm.Forward(new TrayItemId("9:90"), TrayButton.Right,
            TrayModifiers.Option | TrayModifiers.Command);

        Assert.True(ok);
        Assert.Equal("9:90", host.LastForward?.Id.Value);
        Assert.Equal(TrayButton.Right, host.LastForward?.Button);
        Assert.Equal(TrayModifiers.Option | TrayModifiers.Command, host.LastForward?.Modifiers);
    }

    [AvaloniaFact]
    public async Task Forward_with_null_host_is_a_safe_false()
        => Assert.False(await new TrayViewModel(null).Forward(new TrayItemId("x"), TrayButton.Left, TrayModifiers.None));

    [AvaloniaFact]
    public void Overflow_caps_the_visible_strip_and_the_flyout_holds_the_rest()
    {
        var host = new StubTray(Enumerable.Range(0, 10).Select(i => Item($"{i}:{i}0", $"T{i}")).ToArray());
        var vm = new TrayViewModel(host);
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10, vm.Items.Count);
        Assert.Equal(TrayViewModel.DefaultVisibleCap, vm.VisibleItems.Count);
        Assert.Equal(10 - TrayViewModel.DefaultVisibleCap, vm.OverflowItems.Count);
        Assert.True(vm.HasOverflow);
    }

    [AvaloniaFact]
    public async Task Using_an_overflowed_item_promotes_it_into_the_visible_set()
    {
        var host = new StubTray(Enumerable.Range(0, 10).Select(i => Item($"{i}:{i}0", $"T{i}")).ToArray());
        var vm = new TrayViewModel(host);
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        var overflowId = vm.OverflowItems.Last().Id;
        Assert.DoesNotContain(vm.VisibleItems, i => i.Id.Equals(overflowId));

        await vm.Forward(overflowId, TrayButton.Left, TrayModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.VisibleItems, i => i.Id.Equals(overflowId));      // promoted in
        Assert.Equal(TrayViewModel.DefaultVisibleCap, vm.VisibleItems.Count);       // still capped
    }

    [AvaloniaFact]
    public void Configure_changes_the_overflow_cap_and_icon_size_live()
    {
        var host = new StubTray(Enumerable.Range(0, 10).Select(i => Item($"{i}:{i}0", $"T{i}")).ToArray());
        var vm = new TrayViewModel(host);
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        vm.Configure(overflowCap: 3, iconSize: 24);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, vm.VisibleItems.Count);
        Assert.Equal(7, vm.OverflowItems.Count);
        // Icon size is a uniform scale of the NATIVE size (16 == native): iconSize 24 → 1.5× → a bounds-less
        // test item (native 24×22pt) wants 36×33pt. Uniform, so no per-item drift — but bounded by the row
        // height, so on the default single-row bar it lands at the 24pt cap, aspect intact (bevel-xpfl).
        Assert.All(vm.VisibleItems, i => Assert.Equal(TrayViewModel.MaxIconHeight(1), i.IconH, 3));
        Assert.All(vm.VisibleItems, i => Assert.Equal(24.0 / 22.0, i.IconW / i.IconH, 3));
    }

    // ── Icon-or-placeholder (bevel-yduf) ─────────────────────────────────

    /// <summary>The icon decode + tint runs off the UI thread and Posts back; pump until it lands.</summary>
    private static async Task PumpUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task An_item_without_icon_bytes_is_a_placeholder_slot_not_a_blank()
    {
        // The helper's limited mode sends NO bytes for a status item whose owner has no bundle id; the
        // slot used to be a blank cell at native width — indistinguishable from "nothing mirrored".
        var vm = new TrayViewModel(new StubTray(new TrayItem(new TrayItemId("1:10"), "Item-0", IconPng: null)));
        vm.Start();
        await PumpUntil(() => vm.Items.Count == 1);
        var item = vm.Items.Single();

        Assert.Null(item.IconSource);
        Assert.False(item.HasIcon);   // → the view draws the placeholder
        Assert.False(item.IsLive);
    }

    [AvaloniaFact]
    public async Task An_item_with_a_decodable_icon_shows_it_and_reports_live()
    {
        var png = TestPng.Solid(16, 200, 40, 40);
        var vm = new TrayViewModel(new StubTray(new TrayItem(new TrayItemId("2:20"), "Live", IconPng: png, IsLive: true)));
        vm.Start();
        await PumpUntil(() => vm.Items.Count == 1 && vm.Items[0].HasIcon);
        var item = vm.Items.Single();

        Assert.NotNull(item.IconSource);
        Assert.True(item.HasIcon);
        Assert.True(item.IsLive);
    }

    [AvaloniaFact]
    public async Task Undecodable_icon_bytes_fall_back_to_the_placeholder()
    {
        // Bytes arrived but they are not a PNG (or a corrupt one): still a visible placeholder, never a
        // silent empty cell. (The why is logged once per payload — see TrayItemViewModel.Apply.)
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var host = new StubTray(new TrayItem(new TrayItemId("3:30"), "Broken", IconPng: garbage));
        var vm = new TrayViewModel(host);
        vm.Start();
        await PumpUntil(() => vm.Items.Count == 1);
        await PumpUntil(() => false);   // let the off-thread decode attempt settle
        var item = vm.Items.Single();

        Assert.Null(item.IconSource);
        Assert.False(item.HasIcon);

        // A later update that DOES decode replaces the placeholder in place.
        host.RaiseUpdated(new TrayItem(new TrayItemId("3:30"), "Broken", IconPng: TestPng.Solid(16, 0, 0, 200)));
        await PumpUntil(() => item.HasIcon);
        Assert.True(item.HasIcon);
        Assert.NotNull(item.IconSource);
    }

    [Fact]
    public void Icon_state_summary_names_the_missing_grant_when_nothing_is_live()
    {
        // The one log line that separates "mirroring works" from "the helper has no Screen Recording
        // grant" — both look like app icons (or nothing) in the bar.
        Assert.Null(TrayViewModel.DescribeIconState(total: 0, live: 0, appIcons: 0, placeholders: 0));

        var allLive = TrayViewModel.DescribeIconState(total: 3, live: 3, appIcons: 0, placeholders: 0)!;
        Assert.Contains("3 live capture", allLive);
        Assert.DoesNotContain("Screen Recording", allLive);

        var noneLive = TrayViewModel.DescribeIconState(total: 4, live: 0, appIcons: 3, placeholders: 1)!;
        Assert.Contains("0 live capture", noneLive);
        Assert.Contains("3 app-icon fallback", noneLive);
        Assert.Contains("1 placeholder", noneLive);
        Assert.Contains("Screen Recording grant", noneLive);
        Assert.Contains("dev-sign.sh", noneLive);
    }

    [AvaloniaFact]
    public void SetConsolidated_dedups_unchanged_values()
    {
        // The dedup lives in SetConsolidated itself so BOTH callers (settings poll + live settings)
        // benefit — an unchanged value must not re-fire the native hide (ce-review: maintainability).
        var host = new StubTray();
        var vm = new TrayViewModel(host);

        vm.SetConsolidated(true);
        vm.SetConsolidated(true);    // unchanged — no second native call
        vm.SetConsolidated(false);
        vm.SetConsolidated(false);   // unchanged
        vm.SetConsolidated(true);

        Assert.Equal(new[] { true, false, true }, host.HiddenCalls);
    }

    private sealed class StubTray : ISystemTrayHost
    {
        private readonly List<TrayItem> _items;
        public StubTray(params TrayItem[] items) => _items = items.ToList();

        public (TrayItemId Id, TrayButton Button, TrayModifiers Modifiers)? LastForward { get; private set; }

        public List<bool> HiddenCalls { get; } = new();

        public Capabilities Capabilities => Capabilities.None;
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(_items.ToArray());
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default)
        {
            HiddenCalls.Add(hidden);
            return Task.CompletedTask;
        }
        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers, CancellationToken ct = default)
        {
            LastForward = (id, button, modifiers);
            return Task.FromResult(true);
        }

        public event EventHandler<TrayItem>? ItemAdded;
        public event EventHandler<TrayItem>? ItemRemoved;
        public event EventHandler<TrayItem>? ItemUpdated;

        public void RaiseAdded(TrayItem i) => ItemAdded?.Invoke(this, i);
        public void RaiseRemoved(TrayItem i) => ItemRemoved?.Invoke(this, i);
        public void RaiseUpdated(TrayItem i) => ItemUpdated?.Invoke(this, i);
    }
}
