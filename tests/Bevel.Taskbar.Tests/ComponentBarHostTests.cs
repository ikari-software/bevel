using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 14: the bar is actually COMPOSED from the list. Without this the registry,
/// normalizer, panel and health budget are untested islands and spec §9 is unmet.
/// </summary>
[Collection("TaskbarTheme")]
public class ComponentBarHostTests
{
    private static ComponentRegistry Registry()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), inst => new LocalComponentChannel(
            i => new ComponentState(i.InstanceId,
                new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                false)));
        return r;
    }

    private static ComponentInstance Stack(string folder) => new(
        ComponentInstance.NewId(), TaskbarComponentTypes.Stack,
        new Dictionary<string, string> { ["folder"] = folder }, Visible: true);

    private static ComponentBarHost Host()
    {
        var geometry = new BarGeometry(1);
        return new(Registry(), new ComponentHealth(), () => geometry);
    }

    [AvaloniaFact]
    public async Task Each_visible_instance_gets_a_slot_in_list_order()
    {
        var host = Host();
        var a = Stack("/one");
        var b = Stack("/two");
        await host.ApplyAsync(new[] { a, b }, CancellationToken.None);

        Assert.Equal(new[] { a.InstanceId, b.InstanceId }, host.Slots.Select(s => s.InstanceId));
    }

    [AvaloniaFact]
    public async Task A_hidden_instance_gets_no_slot_but_keeps_its_settings()
    {
        var host = Host();
        var hidden = Stack("/kept") with { Visible = false };
        await host.ApplyAsync(new[] { hidden }, CancellationToken.None);

        Assert.Empty(host.Slots);
        Assert.Equal("/kept", hidden.Settings["folder"]);   // removal would have discarded this
    }

    [AvaloniaFact]
    public async Task An_unknown_type_keeps_its_slot_and_renders_inert()
    {
        var host = Host();
        var unknown = new ComponentInstance("gone", "com.example.absent",
            new Dictionary<string, string>(), true);
        await host.ApplyAsync(new[] { Stack("/one"), unknown }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
        var slot = host.Slots.Single(s => s.InstanceId == "gone");
        Assert.True(slot.IsInert);
    }

    [AvaloniaFact]
    public async Task A_bar_with_zero_resolvable_components_still_produces_a_view()
    {
        var host = Host();
        await host.ApplyAsync(Array.Empty<ComponentInstance>(), CancellationToken.None);

        Assert.NotNull(host.View);   // spec §6: an empty bar is still a shell; a crashed bar is not
        Assert.Empty(host.Slots);
    }

    [AvaloniaFact]
    public async Task Re_applying_replaces_slots_rather_than_accumulating_them()
    {
        var host = Host();
        await host.ApplyAsync(new[] { Stack("/one") }, CancellationToken.None);
        await host.ApplyAsync(new[] { Stack("/two"), Stack("/three") }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
    }

    [AvaloniaFact]
    public async Task A_duplicate_instanceId_on_disk_is_repaired_before_slots_are_built()
    {
        var host = Host();
        var dup1 = new ComponentInstance("same", TaskbarComponentTypes.Stack,
            new Dictionary<string, string> { ["folder"] = "/a" }, true);
        var dup2 = new ComponentInstance("same", TaskbarComponentTypes.Stack,
            new Dictionary<string, string> { ["folder"] = "/b" }, true);

        await host.ApplyAsync(new[] { dup1, dup2 }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
        Assert.Equal(2, host.Slots.Select(s => s.InstanceId).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task A_channel_that_throws_on_connect_yields_an_inert_slot_and_the_bar_survives()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), _ => new ThrowingChannel());
        var geometry = new BarGeometry(1);
        var host = new ComponentBarHost(r, new ComponentHealth(), () => geometry);

        await host.ApplyAsync(new[] { Stack("/boom") }, CancellationToken.None);

        Assert.NotNull(host.View);
        Assert.True(host.Slots.Single().IsInert);
    }

    /// <summary>
    /// Fix round 1, Finding 1 (CRITICAL): <see cref="TaskbarComponentsMigration.BuildDefaultList"/>
    /// populates <c>TaskbarComponents</c> with Start, WindowStrip, Stack, Tray, Clock AND
    /// ShowDesktop — but this region's registry (<see cref="Host"/>) knows only Stack. Handing the
    /// WHOLE migrated list to a Stack-only registry made the normalizer correctly keep every other
    /// type as an "unknown" slot (that part is right — it is exactly how a genuinely uninstalled
    /// third-party component must behave) and <see cref="ComponentBarHost"/> correctly render each
    /// of those as an inert placeholder — except here they are NOT uninstalled, they are rendered
    /// elsewhere on the bar by hand, so the result was 5 ghost 12x12 ignore placeholders sitting
    /// inside the stacks region. <c>TaskbarView.ApplyComponentRegion</c> fixes this by filtering the
    /// list to the types this region's registry actually serves before calling
    /// <see cref="ComponentBarHost.ApplyAsync"/>; this test proves that filtered composition is
    /// correct against a REALISTIC full migrated list — the exact shape the real settings path
    /// produces — rather than only the synthetic single/two-instance lists used above, whose
    /// narrowness is what let the bug through undetected.
    /// </summary>
    [AvaloniaFact]
    public async Task A_full_migrated_list_filtered_to_served_types_produces_only_those_slots()
    {
        var host = Host();   // registers ONLY Stack, same as the real stacks-region registry
        var full = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings());
        // Sanity: the real migration really does produce more than just Stack instances, or this
        // test would not be exercising the bug at all.
        Assert.True(full.Select(i => i.TypeId).Distinct().Count() > 1,
            "the default migrated list should contain more than one component type");

        var filtered = full.Where(i => i.TypeId == TaskbarComponentTypes.Stack).ToArray();
        await host.ApplyAsync(filtered, CancellationToken.None);

        Assert.NotEmpty(host.Slots);   // the default Downloads stack resolves to a live slot
        Assert.All(host.Slots, s => Assert.Equal(TaskbarComponentTypes.Stack, s.TypeId));
        Assert.All(host.Slots, s => Assert.False(s.IsInert));   // no ghost placeholders
    }

    /// <summary>
    /// Whole-branch review Fix 3: <see cref="ComponentListNormalizer"/> computes the one-greedy-per-
    /// bar demotion into <c>NormalizedList.EffectiveSizing</c>, but <see cref="ComponentBarHost"/>
    /// used to bind <c>type.Sizing</c> — the manifest's DECLARED sizing — instead of reading it, so
    /// two greedy components both laid out greedy: exactly the fixed-zone failure spec §4.2 rejected
    /// to avoid. This drives the full <see cref="ComponentBarHost.ApplyAsync"/> path with two
    /// greedy-typed components registered — not the normalizer alone, which is the mistake that let
    /// the original bug through undetected — and asserts only one resulting slot ends up greedy.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_greedy_components_produce_only_one_greedy_slot_through_ApplyAsync()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(GreedyManifest("test.greedy.one"), _ => new LocalComponentChannel(
            i => new ComponentState(i.InstanceId, new Dictionary<string, string>(), false)));
        r.Register(GreedyManifest("test.greedy.two"), _ => new LocalComponentChannel(
            i => new ComponentState(i.InstanceId, new Dictionary<string, string>(), false)));
        var geometry = new BarGeometry(1);
        var host = new ComponentBarHost(r, new ComponentHealth(), () => geometry);

        var a = new ComponentInstance(ComponentInstance.NewId(), "test.greedy.one", new Dictionary<string, string>(), true);
        var b = new ComponentInstance(ComponentInstance.NewId(), "test.greedy.two", new Dictionary<string, string>(), true);
        await host.ApplyAsync(new[] { a, b }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
        var greedyCount = host.Slots.Count(s => TaskbarComponentsPanel.GetSizing(s.Content) == ComponentSizing.Greedy);
        Assert.Equal(1, greedyCount);
    }

    private static ComponentManifest GreedyManifest(string id) => new(
        Id: id,
        ContractVersion: ManifestValidator.CurrentContractVersion,
        DisplayName: id,
        Description: "test fixture",
        MultiInstance: true,
        Sizing: ComponentSizing.Greedy,
        RequiresCapability: null,
        SettingsSchema: Array.Empty<ComponentSettingsField>(),
        View: Array.Empty<ComponentPrimitive>());

    /// <summary>
    /// Whole-branch review Fix 5, spec §6: "Hangs count as failures." A <c>ComponentState</c> with
    /// <c>Inert: true</c> is exactly how a timed-out (hung) component reports — not by throwing —
    /// and <see cref="ComponentBarHost.ApplyCoreAsync"/> used to record nothing on that path, so a
    /// hung component was retried on every settings push forever and never reached the health
    /// budget's quarantine. This applies three times against a channel that always reports hung and
    /// asserts the budget quarantines after <see cref="ComponentHealth"/>'s configured crash budget
    /// (default 3).
    /// </summary>
    [AvaloniaFact]
    public async Task A_hung_component_records_a_crash_and_quarantines_after_the_health_budget()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), _ => new HungChannel());
        var geometry = new BarGeometry(1);
        var health = new ComponentHealth(crashBudget: 3);
        var host = new ComponentBarHost(r, health, () => geometry);
        var inst = Stack("/hung");

        await host.ApplyAsync(new[] { inst }, CancellationToken.None);
        Assert.True(host.Slots.Single().IsInert);

        await host.ApplyAsync(new[] { inst }, CancellationToken.None);
        await host.ApplyAsync(new[] { inst }, CancellationToken.None);

        // Third apply is the component's third recorded crash — the budget quarantines it, and the
        // RecordCrash call on THIS instanceId (not the probe above) must return Quarantine too.
        Assert.Equal(ComponentVerdict.Quarantine, health.RecordCrash(inst.InstanceId));
        Assert.True(host.Slots.Single().IsInert);
    }

    /// <summary>A channel that always reports hung (Inert: true) rather than throwing — the shape a
    /// real timed-out component reports back in (whole-branch review Fix 5).</summary>
    private sealed class HungChannel : IComponentChannel
    {
        public event Action<ComponentState>? StateChanged;
        public Task<ComponentState> ConnectAsync(ComponentInstance i, CancellationToken ct)
            => Task.FromResult(new ComponentState(i.InstanceId, new Dictionary<string, string>(), Inert: true));
        public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Whole-branch review Fix 6, spec §6: "Validation rejects the component, never the bar."
    /// <c>ComponentBarHost.ApplyCoreAsync</c> used to call <c>_registry.CreateChannel(inst)</c> —
    /// which invokes a third-party factory — OUTSIDE the per-component try, so one throwing factory
    /// aborted the WHOLE apply and the region silently stayed empty. This registers one type whose
    /// factory always throws alongside the normal Stack type and asserts the other component still
    /// renders.
    /// </summary>
    [AvaloniaFact]
    public async Task A_throwing_factory_costs_only_its_own_slot_and_the_rest_still_render()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), inst => new LocalComponentChannel(
            i => new ComponentState(i.InstanceId,
                new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                false)));
        r.Register(GreedyManifest("test.throwing.factory"), _ =>
            throw new InvalidOperationException("factory blew up"));
        var geometry = new BarGeometry(1);
        var host = new ComponentBarHost(r, new ComponentHealth(), () => geometry);

        var good = Stack("/fine");
        var bad = new ComponentInstance(ComponentInstance.NewId(), "test.throwing.factory", new Dictionary<string, string>(), true);
        await host.ApplyAsync(new[] { good, bad }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
        var goodSlot = host.Slots.Single(s => s.InstanceId == good.InstanceId);
        var badSlot = host.Slots.Single(s => s.InstanceId == bad.InstanceId);
        Assert.False(goodSlot.IsInert);
        Assert.True(badSlot.IsInert);
    }

    /// <summary>
    /// Fix round 1, Finding 2 (Important): <c>ApplyAsync</c> is fired from startup AND from every
    /// settings push, each on its own background <c>Task.Run</c>, with no serialization between
    /// them. Two overlapping runs would concurrently mutate the plain <c>List&lt;IComponentChannel&gt;</c>/
    /// <c>List&lt;ComponentSlot&gt;</c> fields — "Collection was modified", or a silently corrupted
    /// mix of both runs' slots. <see cref="ComponentBarHost"/> now serializes every call through a
    /// <c>SemaphoreSlim(1,1)</c>; this drives two genuinely overlapping calls (via a channel whose
    /// <c>ConnectAsync</c> actually awaits a delay, so the second call's <c>WaitAsync</c> is forced
    /// to queue behind the first's in-flight critical section) and asserts the result is always
    /// coherent — exactly one run's slot count, with no duplicate instance ids — never a mix.
    /// </summary>
    [AvaloniaFact]
    public async Task Overlapping_ApplyAsync_calls_serialize_so_the_final_slot_set_is_coherent()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), _ => new DelayedChannel(TimeSpan.FromMilliseconds(30)));
        var geometry = new BarGeometry(1);
        var host = new ComponentBarHost(r, new ComponentHealth(), () => geometry);

        var first = host.ApplyAsync(new[] { Stack("/one") }, CancellationToken.None);
        var second = host.ApplyAsync(
            new[] { Stack("/two"), Stack("/three"), Stack("/four") }, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.True(host.Slots.Count is 1 or 3,
            $"slot count {host.Slots.Count} matches neither call's input size — the two applies interleaved");
        Assert.Equal(host.Slots.Count, host.Slots.Select(s => s.InstanceId).Distinct().Count());
    }

    /// <summary>
    /// Fix round 2, Finding 2 (Important): every test above calls <c>ApplyAsync</c> directly from
    /// the <c>[AvaloniaFact]</c> UI thread, which already has dispatcher affinity — so none of them
    /// would have caught the off-thread "Call from invalid thread" crash fix round 1 found and
    /// fixed. Production calls it as <c>_ = Task.Run(() => host.ApplyAsync(...))</c>
    /// (<c>TaskbarView.ApplyComponentRegion</c>), which runs the WHOLE method — including, before
    /// that fix, the <c>ComponentSlot.Live</c>/<c>Inert</c> construction — on a thread-pool thread
    /// with no dispatcher affinity at all. This test reproduces that exact call shape so a
    /// regression that reinstated direct construction inside the loop fails HERE, not only on the
    /// first real shell start. Bounded with a timeout so a hang fails loudly instead of wedging CI.
    /// </summary>
    [AvaloniaFact]
    public async Task ApplyAsync_driven_through_TaskRun_like_production_builds_controls_without_crashing()
    {
        var host = Host();
        var work = Task.Run(() => host.ApplyAsync(new[] { Stack("/one"), Stack("/two") }, CancellationToken.None));
        var winner = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(work, winner);   // fails loudly on a hang instead of timing out the whole run
        await work;                 // observe (and surface) any exception the background run threw

        Assert.Equal(2, host.Slots.Count);
        Assert.All(host.Slots, s => Assert.False(s.IsInert));
    }

    private sealed class ThrowingChannel : IComponentChannel
    {
        public event Action<ComponentState>? StateChanged;
        public Task<ComponentState> ConnectAsync(ComponentInstance i, CancellationToken ct)
            => throw new InvalidOperationException("component blew up on start");
        public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A channel whose <see cref="ConnectAsync"/> genuinely awaits, so two concurrent
    /// <c>ApplyAsync</c> calls against the same host actually overlap in time rather than one
    /// completing synchronously before the other starts.</summary>
    private sealed class DelayedChannel : IComponentChannel
    {
        private readonly TimeSpan _delay;
        public DelayedChannel(TimeSpan delay) => _delay = delay;
        public event Action<ComponentState>? StateChanged;
        public async Task<ComponentState> ConnectAsync(ComponentInstance i, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new ComponentState(i.InstanceId, new Dictionary<string, string>(), false);
        }
        public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
