using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-qb19: render proof that the attention overlay (a) is visible and ANIMATING while a
/// window's attention request is active, (b) settles into a static tint in bounded mode, and (c) is
/// gone when idle/off — in BOTH skins (Blue2001 Luna gloss and Industrial1999 Win2000 flat grey), so
/// the token-bound glow reads on both. Drives the overlay's deterministic TickAt so no wall-clock
/// animation is needed. Pixel assertions are scoped to the button's own rect (the strip's other
/// widgets settle asynchronously and would pollute a whole-frame diff).
/// </summary>
[Collection("TaskbarTheme")] // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderTaskAttentionTest
{
    private sealed class ManualAttentionTimer : IAttentionTimer
    {
        public Action? Callback { get; private set; }
        public void Start(TimeSpan dueTime, Action callback) => Callback = callback;
        public void Stop() => Callback = null;
        public void Fire() { var cb = Callback; Callback = null; cb?.Invoke(); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeIds.Blue2001, "Luna/Blue2001")]
    [InlineData(ThemeIds.Industrial1999, "Win2000/Industrial1999")]
    public void Attention_overlay_animates_then_settles_and_disappears_in_both_skins(string skinId, string skinName)
    {
        try
        {
            Bevel.UI.ThemeService.Apply(skinId);
            var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
            var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
            var wm = new StubWindowManager();
            var view = new TaskbarView { DataContext = vm };
            // Pin the strip's button width like RenderPressedTaskButtonTest does, so the layout pass
            // can't drift the button between captures.
            view.Initialize(TestSettings.NoStacks(new BevelSettings
            {
                TaskbarButtonWidth = 150,
                TaskbarGrouping = TaskbarGroupingMode.Never,
            }));
            var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Own policy + manual timer: the render test must not depend on the process-wide Shared.
            var policy = new TaskAttentionPolicy
            {
                Animation = TaskbarAttentionAnimation.Pulse,
                Duration = TaskbarAttentionDuration.Bounded,
            };
            var timer = new ManualAttentionTimer();
            var item = new TaskItemViewModel(
                new ForeignWindow(new ForeignWindowId("w-att"), "Doc", "App", false, false, default),
                wm, attention: policy, attentionTimer: timer) { Width = 150, Opacity = 1 };
            model.Windows.Add(item);
            Dispatcher.UIThread.RunJobs();

            var overlay = view.GetVisualDescendants().OfType<TaskAttentionOverlay>()
                .Single(o => ReferenceEquals(o.DataContext, item));
            overlay.AutoTick = false;   // deterministic frames: drive TickAt manually below
            // The overlay is IsVisible=false while idle (hence zero bounds) — take the rect from the
            // button, which carries the same Margin="1,2,0,2" and so covers the identical face area.
            var btn = view.GetVisualDescendants().OfType<TaskButton>().First(b => ReferenceEquals(b.DataContext, item));
            // Inset past the bevel edges (the checked/resting styles differ only in the outer 1-3px
            // bevel, exactly like RenderPressedTaskButtonTest's FaceInset) — the glow covers the face.
            var rect = new Rect(RectOf(btn, window).Position + new Vector(4, 3),
                RectOf(btn, window).Size - new Size(8, 6));
            Assert.True(rect.Width > 100, $"{skinName}: button should be laid out at steady width, got {rect}");

            // Idle: overlay hidden.
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TaskAttentionVisual.None, overlay.Mode);
            Assert.False(overlay.IsVisible);
            var idle = RegionBytes(CaptureStable(window), rect);

            // Active pulse at two phases of the 1.2s cycle: opacity must differ (animating), and both
            // frames must differ from idle inside the button rect (overlay visible and painting).
            item.Update(new ForeignWindow(new ForeignWindowId("w-att"), "Doc", "App", false, false, default)
            { AttentionAt = 1000 });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TaskAttentionVisual.Pulse, overlay.Mode);
            Assert.True(overlay.IsVisible, $"{skinName}: overlay should be visible while active");

            overlay.TickAt(0);
            Dispatcher.UIThread.RunJobs();
            var pulseA = overlay.GlowOpacityForTest;
            var frameA = RegionBytes(CaptureStable(window), rect);

            overlay.TickAt(TaskAttentionPolicy.PulsePeriod.TotalMilliseconds / 2);
            Dispatcher.UIThread.RunJobs();
            var pulseB = overlay.GlowOpacityForTest;
            var frameB = RegionBytes(CaptureStable(window), rect);

            Assert.True(Math.Abs(pulseA - pulseB) > 0.05,
                $"{skinName}: pulse opacity should breathe (phase0={pulseA:F2}, phase½={pulseB:F2})");
            Assert.False(frameA.AsSpan().SequenceEqual(idle),
                $"{skinName}: active overlay must change the rendered button");
            Assert.False(frameA.AsSpan().SequenceEqual(frameB),
                $"{skinName}: two pulse phases must render differently (it animates)");

            // Bounded settle: after the pulse budget the VM settles → static tint (still visible, but
            // differs from both idle and the animated pulse frame).
            timer.Fire();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TaskAttentionVisual.Tint, overlay.Mode);
            Assert.True(overlay.IsVisible, $"{skinName}: settled tint should stay visible");
            var settled = RegionBytes(CaptureStable(window), rect);
            Assert.False(settled.AsSpan().SequenceEqual(idle),
                $"{skinName}: settled tint must still paint over the idle button");
            Assert.False(settled.AsSpan().SequenceEqual(frameA),
                $"{skinName}: settled tint differs from the animated pulse frame");

            // Acknowledged (activated): overlay hidden; unfocused again, the button renders exactly
            // as it did when idle (the pressed look while focused is the checked style, not the overlay).
            item.SetFocused(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TaskAttentionVisual.None, overlay.Mode);
            Assert.False(overlay.IsVisible, $"{skinName}: activation must hide the overlay");
            item.SetFocused(false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(RegionBytes(CaptureStable(window), rect).AsSpan().SequenceEqual(idle),
                $"{skinName}: once acknowledged and unfocused, the button's face must render exactly as idle");
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeIds.Blue2001, "Luna/Blue2001")]
    [InlineData(ThemeIds.Industrial1999, "Win2000/Industrial1999")]
    public void Attention_overlay_off_mode_renders_nothing(string skinId, string skinName)
    {
        try
        {
            Bevel.UI.ThemeService.Apply(skinId);
            var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
            var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
            var wm = new StubWindowManager();
            var view = new TaskbarView { DataContext = vm };
            // Pin the strip's button width like RenderPressedTaskButtonTest does, so the layout pass
            // can't drift the button between captures.
            view.Initialize(TestSettings.NoStacks(new BevelSettings
            {
                TaskbarButtonWidth = 150,
                TaskbarGrouping = TaskbarGroupingMode.Never,
            }));
            var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var policy = new TaskAttentionPolicy { Animation = TaskbarAttentionAnimation.Off };
            var item = new TaskItemViewModel(
                new ForeignWindow(new ForeignWindowId("w-off"), "Doc", "App", false, false, default),
                wm, attention: policy, attentionTimer: new ManualAttentionTimer()) { Width = 150, Opacity = 1 };
            model.Windows.Add(item);
            Dispatcher.UIThread.RunJobs();

            var overlay = view.GetVisualDescendants().OfType<TaskAttentionOverlay>()
                .Single(o => ReferenceEquals(o.DataContext, item));
            overlay.AutoTick = false;
            var btn = view.GetVisualDescendants().OfType<TaskButton>().First(b => ReferenceEquals(b.DataContext, item));
            // Inset past the bevel edges (the checked/resting styles differ only in the outer 1-3px
            // bevel, exactly like RenderPressedTaskButtonTest's FaceInset) — the glow covers the face.
            var rect = new Rect(RectOf(btn, window).Position + new Vector(4, 3),
                RectOf(btn, window).Size - new Size(8, 6));
            Assert.True(rect.Width > 100, $"{skinName}: button should be laid out at steady width, got {rect}");
            var idle = RegionBytes(CaptureStable(window), rect);

            item.Update(new ForeignWindow(new ForeignWindowId("w-off"), "Doc", "App", false, false, default)
            { AttentionAt = 1000 });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(TaskAttentionVisual.None, overlay.Mode);
            Assert.False(overlay.IsVisible, $"{skinName}: off must hide the overlay");
            Assert.True(RegionBytes(CaptureStable(window), rect).AsSpan().SequenceEqual(idle),
                $"{skinName}: off must render pixel-identical to idle inside the button's face");
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Attention_overlay_timer_pauses_while_the_taskbar_window_is_hidden()
    {
        // bevel-qb19 review: hiding the taskbar window does NOT detach the visual tree, so the
        // until-activated 25fps DispatcherTimer used to keep running on a hidden bar. It must pause
        // on top-level IsVisible=false and resume on show.
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
            var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
            var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
            var wm = new StubWindowManager();
            var view = new TaskbarView { DataContext = vm };
            view.Initialize(TestSettings.NoStacks(new BevelSettings
            {
                TaskbarButtonWidth = 150,
                TaskbarGrouping = TaskbarGroupingMode.Never,
                // until-activated: no settle timer — the overlay's own animation timer is the
                // perpetual one this test is about.
                TaskbarAttentionDuration = TaskbarAttentionDuration.UntilActivated,
            }));
            var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var policy = new TaskAttentionPolicy
            {
                Animation = TaskbarAttentionAnimation.Pulse,
                Duration = TaskbarAttentionDuration.UntilActivated,
            };
            var item = new TaskItemViewModel(
                new ForeignWindow(new ForeignWindowId("w-hide"), "Doc", "App", false, false, default),
                wm, attention: policy, attentionTimer: new ManualAttentionTimer()) { Width = 150, Opacity = 1 };
            model.Windows.Add(item);
            Dispatcher.UIThread.RunJobs();

            var overlay = view.GetVisualDescendants().OfType<TaskAttentionOverlay>()
                .Single(o => ReferenceEquals(o.DataContext, item));
            // AutoTick left true: the production timer path. Drive the stamp through the VM.
            item.Update(new ForeignWindow(new ForeignWindowId("w-hide"), "Doc", "App", false, false, default)
            { AttentionAt = 1000 });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(TaskAttentionVisual.Pulse, overlay.Mode);
            Assert.True(overlay.TimerRunningForTest, "visible bar: the animation timer runs");

            window.IsVisible = false;   // hidden bar — visual tree stays attached
            Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.TimerRunningForTest, "hidden bar: the 25fps timer must pause");

            window.IsVisible = true;    // shown again
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.TimerRunningForTest, "shown bar: the timer resumes");
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    // ── capture helpers (same polled-stable pattern as RenderPressedTaskButtonTest) ──

    private static Rect RectOf(Control c, TaskbarWindow window)
    {
        var tl = c.TranslatePoint(default, window)!.Value;
        return new Rect(tl, c.Bounds.Size);
    }

    private static WriteableBitmap CaptureStable(TaskbarWindow window)
    {
        byte[]? previous = null;
        WriteableBitmap? frame = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var bytes = Bytes(frame!);
            if (previous is not null && bytes.AsSpan().SequenceEqual(previous))
                return frame!;
            previous = bytes;
        }
        return frame!;
    }

    private static byte[] Bytes(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * frame.PixelSize.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Raw bytes of just the button's rect (BGRA rows, tightly packed).</summary>
    private static byte[] RegionBytes(WriteableBitmap frame, Rect rect)
    {
        using var buffer = frame.Lock();
        var x0 = Math.Max(0, (int)rect.X);
        var y0 = Math.Max(0, (int)rect.Y);
        var x1 = Math.Min(frame.PixelSize.Width, (int)rect.Right);
        var y1 = Math.Min(frame.PixelSize.Height, (int)rect.Bottom);
        var w = Math.Max(0, x1 - x0);
        var bytes = new byte[w * Math.Max(0, y1 - y0) * 4];
        for (var y = y0; y < y1; y++)
            Marshal.Copy(buffer.Address + y * buffer.RowBytes + x0 * 4,
                bytes, (y - y0) * w * 4, w * 4);
        return bytes;
    }
}
