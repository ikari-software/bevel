using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Guards the exact "apply-then-subscribe" ordering bug that was found and fixed in
/// App.axaml.cs's OnFrameworkInitializationCompleted (settings.Changed subscribed BEFORE the
/// startup theme read) and, in the same audit pass, found lurking a second time in
/// <see cref="TaskbarViewModel"/>'s constructor for BOTH its connection-status read and its
/// settings read. A value landing in the gap between "read the current value" and "subscribe to
/// its change notification" is silently lost until some LATER, unrelated change happens to fire
/// the event again — which for a cross-process push (settings) or a transport reconnect
/// (connection) is not a theoretical race, it is a real IPC round-trip window.
///
/// These tests assert the ORDER of operations directly (subscribe call observed before the first
/// read call) rather than trying to simulate the race's end state, because the failure mode is
/// "the notification for a change that already happened is missed entirely" — a property of
/// *when* the subscription is armed relative to the read, not of what value the read returns.
/// </summary>
public class TaskbarViewModelSubscriptionOrderTests
{
    private sealed class OrderTrackingSettings : ISettingsService
    {
        public readonly List<string> CallOrder = new();
        private readonly BevelSettings _current = new();

        public event Action? Changed
        {
            add { CallOrder.Add("subscribe"); }
            remove { }
        }

        public BevelSettings Current { get { CallOrder.Add("read"); return _current; } }
        public int Version => 0;
        public ThemeOverrides ThemeOverridesFor(string themeId) => new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ReloadIfChangedAsync(CancellationToken ct = default) => Task.FromResult(false);
        public string SnapshotJson() => "{}";
        public Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class OrderTrackingLink : IShellConnectionStatus
    {
        public readonly List<string> CallOrder = new();
        public bool IsConnected { get { CallOrder.Add("read"); return true; } }
        public event EventHandler<bool>? ConnectionChanged
        {
            add { CallOrder.Add("subscribe"); }
            remove { }
        }
    }

    [Fact]
    public void Settings_Changed_is_subscribed_before_the_startup_Current_read()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var settings = new OrderTrackingSettings();

        _ = new TaskbarViewModel(model, new StartMenuViewModel(model), settings: settings);

        // The FIRST read is StacksViewModel's one-shot folder seed (settings?.Current.TaskbarStacks) —
        // whole-branch review Fix 1 restored the stacks region to its pre-component-list hand-built
        // ItemsControl (TaskbarView.axaml bound to Stacks.Stacks), since no component type has a
        // renderer yet and ComponentBarHost's generic Button regressed the shipped feature. That read
        // is a one-shot startup seed, not a live subscription, so there is nothing to race: Stacks
        // never changes again from this value.
        //
        // The guarded property is about the PAIR that follows: "subscribe" must land before the
        // SECOND read — the one that feeds ApplyConsolidation. If that read came first instead, a
        // settings snapshot landing in the gap (e.g. the core's on-connect broadcast racing DI
        // construction) would be lost until some later, unrelated settings edit happens to fire
        // Changed again.
        Assert.Equal(new[] { "read", "subscribe", "read" }, settings.CallOrder);
    }

    [Fact]
    public void ConnectionChanged_is_subscribed_before_the_startup_IsConnected_read()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var link = new OrderTrackingLink();

        _ = new TaskbarViewModel(model, new StartMenuViewModel(model), link);

        // Same discipline as above: a connection flip landing between the read and the subscribe
        // would otherwise never invoke the handler for that transition, leaving IsDisconnected wrong
        // until the NEXT flip (which may not come for a long time on a healthy link).
        Assert.Equal("subscribe", link.CallOrder[0]);
        Assert.Contains("read", link.CallOrder);
    }
}
