using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Reclick-minimize policy on task buttons (bevel-au94). The classic Win2000 toggle stays the default;
/// the other two modes turn a click on the ACTIVE window's button into a plain raise, either always
/// (<see cref="TaskbarReclickMinimize.Never"/>) or unless the Option/Alt chord is held
/// (<see cref="TaskbarReclickMinimize.OptionClick"/>). The minimized→restore and unfocused→activate arms
/// of the toggle must be untouched by all of it.
///
/// <c>RecordingWm</c> completes every op synchronously, so <c>Execute(null)</c> on the async command runs
/// the whole toggle before it returns — the same pattern <c>AppPresenceTests</c> uses.
/// </summary>
public class TaskButtonReclickTests
{
    private static TaskButtonClickPolicy Policy(TaskbarReclickMinimize mode, bool modifierHeld = false) =>
        new() { Mode = mode, ModifierHeld = () => modifierHeld };

    private static (TaskItemViewModel Vm, RecordingWm Wm) Focused(TaskButtonClickPolicy policy)
    {
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Arc", "Arc", false, true, default), wm, policy);
        vm.SetFocused(true);
        return (vm, wm);
    }

    [Fact]
    public void Classic_is_the_default_mode()
    {
        Assert.Equal(TaskbarReclickMinimize.Click, new TaskButtonClickPolicy().Mode);
        Assert.Equal(TaskbarReclickMinimize.Click, new BevelSettings().TaskbarReclickMinimize);
    }

    [Fact]
    public void Classic_mode_minimizes_the_active_window_on_reclick()
    {
        var (vm, wm) = Focused(Policy(TaskbarReclickMinimize.Click));
        vm.ActivateCommand.Execute(null);

        Assert.Equal(new[] { "w1" }, wm.Minimized);
        Assert.Empty(wm.Activated);
        Assert.True(vm.IsMinimized);
    }

    [Fact]
    public void Never_mode_activates_instead_of_minimizing()
    {
        var (vm, wm) = Focused(Policy(TaskbarReclickMinimize.Never));
        vm.ActivateCommand.Execute(null);

        Assert.Empty(wm.Minimized);
        Assert.Equal(new[] { "w1" }, wm.Activated);   // click-to-activate is kept
        Assert.False(vm.IsMinimized);
        Assert.True(vm.IsFocused);                    // and no optimistic un-press
    }

    [Fact]
    public void Modifier_mode_raises_on_a_plain_click()
    {
        var (vm, wm) = Focused(Policy(TaskbarReclickMinimize.OptionClick, modifierHeld: false));
        vm.ActivateCommand.Execute(null);

        Assert.Empty(wm.Minimized);
        Assert.Equal(new[] { "w1" }, wm.Activated);
    }

    [Fact]
    public void Modifier_mode_minimizes_when_the_chord_is_held()
    {
        var (vm, wm) = Focused(Policy(TaskbarReclickMinimize.OptionClick, modifierHeld: true));
        vm.ActivateCommand.Execute(null);

        Assert.Equal(new[] { "w1" }, wm.Minimized);
        Assert.Empty(wm.Activated);
    }

    [Fact]
    public void The_chord_is_read_live_at_click_time_not_cached()
    {
        var held = false;
        var policy = new TaskButtonClickPolicy
        {
            Mode = TaskbarReclickMinimize.OptionClick,
            ModifierHeld = () => held,
        };
        var (vm, wm) = Focused(policy);

        vm.ActivateCommand.Execute(null);   // no modifier → raise
        held = true;
        vm.SetFocused(true);
        vm.ActivateCommand.Execute(null);   // modifier now down → minimize

        Assert.Equal(new[] { "w1" }, wm.Activated);
        Assert.Equal(new[] { "w1" }, wm.Minimized);
    }

    [Theory]
    [InlineData(TaskbarReclickMinimize.Click)]
    [InlineData(TaskbarReclickMinimize.OptionClick)]
    [InlineData(TaskbarReclickMinimize.Never)]
    public void A_minimized_window_still_restores_in_every_mode(TaskbarReclickMinimize mode)
    {
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w2"), "Notes", "Notes", true, false, default),
            wm, Policy(mode, modifierHeld: true));

        vm.ActivateCommand.Execute(null);

        Assert.Equal(new[] { "w2" }, wm.RestoredAndActivated);
        Assert.Empty(wm.Minimized);
    }

    [Theory]
    [InlineData(TaskbarReclickMinimize.Click)]
    [InlineData(TaskbarReclickMinimize.OptionClick)]
    [InlineData(TaskbarReclickMinimize.Never)]
    public void An_unfocused_window_still_activates_in_every_mode(TaskbarReclickMinimize mode)
    {
        var wm = new RecordingWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w3"), "Mail", "Mail", false, false, default),
            wm, Policy(mode, modifierHeld: true));

        vm.ActivateCommand.Execute(null);

        Assert.Equal(new[] { "w3" }, wm.Activated);
        Assert.Empty(wm.Minimized);
    }

    [Fact]
    public void Status_text_advertises_only_the_gesture_the_mode_honours()
    {
        Assert.Equal("Arc — Active (click to minimize)",
            Focused(Policy(TaskbarReclickMinimize.Click)).Vm.StatusText);
        Assert.Equal("Arc — Active", Focused(Policy(TaskbarReclickMinimize.Never)).Vm.StatusText);
        Assert.Equal($"Arc — Active ({TaskButtonClickPolicy.ModifierName}-click to minimize)",
            Focused(Policy(TaskbarReclickMinimize.OptionClick)).Vm.StatusText);
    }

    [Fact]
    public void Buttons_built_without_a_policy_follow_the_shared_one()
    {
        // ShellModel creates buttons with no settings of its own, so the fallback must be the live shared
        // policy — otherwise a mode change would only reach buttons that existed when it was made.
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w4"), "Arc", "Arc", false, true, default), new RecordingWm());
        vm.SetFocused(true);
        Assert.Equal($"Arc — Active ({TaskButtonClickPolicy.Shared.MinimizeGestureHint})", vm.StatusText);
    }

    private sealed class RecordingWm : IWindowManager
    {
        public List<string> Activated { get; } = new();
        public List<string> Minimized { get; } = new();
        public List<string> RestoredAndActivated { get; } = new();
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) { Activated.Add(id.Value); return Task.CompletedTask; }
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) { Minimized.Add(id.Value); return Task.CompletedTask; }
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default) { RestoredAndActivated.Add(id.Value); return Task.CompletedTask; }
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
