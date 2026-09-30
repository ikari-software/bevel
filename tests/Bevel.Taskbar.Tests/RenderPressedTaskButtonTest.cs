using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.UI.Luna;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The active window's task button renders PRESSED (bevel-zk4a): the sunken bevel under Win2000, the
/// darker inset face under Luna. Real pixels, both skins, and the pressed state must FOLLOW focus live —
/// including across a click on the button itself, which is where it used to die: the button's own
/// self-toggle on click divorced <c>IsChecked</c> from <see cref="TaskItemViewModel.IsFocused"/>.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderPressedTaskButtonTest
{
    private const int FaceInset = 5;     // skip the bevel / border when sampling a button's face
    private const int FaceSampleWidth = 40; // the label-free right end of a 150px button

    [AvaloniaFact]
    public void Win2000_focused_button_is_sunken_and_distinct_from_its_neighbour()
    {
        var (window, view, _, _) = BuildBar(
            Win("w0", "Window 1", focused: true),
            Win("w1", "Window 2"));

        var frame = CaptureStable(window);
        Save(frame, "BEVEL_PRESSED_WIN2000_OUT", "bevel-pressed-win2000.png");

        var (focused, other) = ButtonRects(view, window);

        // Sunken = the bevel's polarity flips (ClassicBorderStyle.RaiseReversed vs Raised): the raised
        // neighbour is lit on the left and shadowed on the right; the pressed button is the reverse.
        var raisedTilt = EdgeTilt(frame, other);
        var pressedTilt = EdgeTilt(frame, focused);
        Assert.True(raisedTilt > 20, $"raised neighbour should be light-left/dark-right (tilt {raisedTilt:F0})");
        Assert.True(pressedTilt < -20, $"pressed button should be dark-left/light-right (tilt {pressedTilt:F0})");

        // And the face itself reads differently (the classic hatched active-task fill vs flat grey).
        AssertFacesDiffer(frame, focused, other);
    }

    /// <summary>Left-edge luminance minus right-edge luminance, sampled mid-height: positive for a raised
    /// bevel, negative for a sunken one.</summary>
    private static double EdgeTilt(WriteableBitmap frame, Rect button)
    {
        var y = (int)(button.Y + button.Height / 2);
        return Luminance(frame, (int)button.X + 1, y) - Luminance(frame, (int)button.Right - 2, y);
    }

    /// <summary>
    /// Under Luna the active button's face is a distinct inset gradient with its own border. The live
    /// look is what the variant engine generates (luna-brushes-regenerated-by-variant) — the app always
    /// applies a variant — so the render is checked under the default Blue and the darkest axis, Purple,
    /// where a static-only colour would vanish.
    /// </summary>
    [AvaloniaFact]
    public void Luna_focused_button_is_distinct_under_the_default_and_darkest_variants()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            var (window, view, _, _) = BuildBar(
                Win("w0", "Window 1", focused: true),
                Win("w1", "Window 2"));

            foreach (var (color, env, file) in new[]
            {
                ("Blue", "BEVEL_PRESSED_LUNA_OUT", "bevel-pressed-luna.png"),
                ("Purple", "BEVEL_PRESSED_LUNA_PURPLE_OUT", "bevel-pressed-luna-purple.png"),
            })
            {
                LunaVariantService.Apply(color, "Hybrid");
                var frame = CaptureStable(window);
                Save(frame, env, file);
                var (focused, other) = ButtonRects(view, window);
                AssertFacesDiffer(frame, focused, other, $"Luna {color}");
                AssertBordersDiffer(frame, focused, other, $"Luna {color}");
                // PRESSED SEMANTICS (bevel-zk4a live regression): "distinct" once passed with the
                // checked face LIT brighter-than-hover, which read on screen as "hovered, never
                // pressed". The active button must read as pressed-in: DARKER than the resting face.
                AssertFaceIsDarker(frame, focused, other, $"Luna {color}");
            }
        }
        finally
        {
            LunaVariantService.Clear();
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }

    /// <summary>
    /// The pressed state is a projection of the shell's exclusive focus, so it must track focus as it
    /// moves — after the user has clicked a button (the gesture that used to sever the projection), away to
    /// an app that earns no button (nothing pressed), and back.
    /// </summary>
    [AvaloniaFact]
    public void Pressed_state_follows_focus_live_through_a_click_and_a_buttonless_app()
    {
        // Plain click = raise only (bevel-au94's Option-click mode): under the default click-to-minimize
        // policy a re-click on the active window legitimately un-presses it (optimistic minimize, covered
        // by TaskButtonReclickTests) — this test is about the button never diverging from focus on its own.
        var raiseOnly = new TaskButtonClickPolicy { Mode = TaskbarReclickMinimize.OptionClick, ModifierHeld = () => false };
        var (window, view, _, wm) = BuildBar(grouping: false, raiseOnly,
            Win("w0", "Window 1", focused: true),
            Win("w1", "Window 2"));

        var buttons = Buttons(view);
        Assert.Equal(new bool?[] { true, false }, buttons.Select(b => b.IsChecked));

        // A real pointer click on the unfocused button: optimistic exclusive press (bevel-c04q /
        // bevel-yslj) lights THAT button and clears the previous one — even though the stub WM
        // has not yet raised ForegroundChanged.
        Click(window, buttons[1]);
        Assert.Equal(new bool?[] { false, true }, buttons.Select(b => b.IsChecked));

        // Focus then genuinely lands on it: still exactly that button is pressed.
        wm.RaiseForeground(Win("w1", "Window 2", focused: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new bool?[] { false, true }, buttons.Select(b => b.IsChecked));

        // Re-click the pressed button (a toggle would un-press it); focus hasn't moved, so it stays pressed.
        Click(window, buttons[1]);
        Assert.Equal(new bool?[] { false, true }, buttons.Select(b => b.IsChecked));

        // Focus moves to something that earns no button (no title, no app): nothing is pressed.
        wm.RaiseForeground(new ForeignWindow(new ForeignWindowId("desktop"), "", null, false, true, default));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new bool?[] { false, false }, buttons.Select(b => b.IsChecked));

        // …and back.
        wm.RaiseForeground(Win("w0", "Window 1", focused: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new bool?[] { true, false }, buttons.Select(b => b.IsChecked));
    }

    /// <summary>A grouped button is pressed when ANY window of the group has focus, and never toggles on
    /// its own click (which opens the group's flyout).</summary>
    [AvaloniaFact]
    public void Grouped_button_is_pressed_when_any_member_window_is_focused()
    {
        var (window, view, _, wm) = BuildBar(grouping: true,
            Win("c1", "Gmail", "com.google.Chrome"),
            Win("c2", "Docs", "com.google.Chrome"),
            Win("f1", "Downloads", "com.apple.finder", focused: true));

        var buttons = Buttons(view);
        Assert.Equal(2, buttons.Count);                      // [Chrome group(2), Finder]
        Assert.Equal(new bool?[] { false, true }, buttons.Select(b => b.IsChecked));

        wm.RaiseForeground(Win("c2", "Docs", "com.google.Chrome", focused: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new bool?[] { true, false }, buttons.Select(b => b.IsChecked));

        Click(window, buttons[0]);                           // opens the flyout; must not un-press
        Assert.Equal(new bool?[] { true, false }, buttons.Select(b => b.IsChecked));

        wm.RaiseForeground(Win("f1", "Downloads", "com.apple.finder", focused: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new bool?[] { false, true }, buttons.Select(b => b.IsChecked));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private static (TaskbarWindow Window, TaskbarView View, ShellModel Model, StubWindowManager Wm) BuildBar(
        params ForeignWindow[] windows) => BuildBar(grouping: false, clicks: null, windows);

    private static (TaskbarWindow Window, TaskbarView View, ShellModel Model, StubWindowManager Wm) BuildBar(
        bool grouping, params ForeignWindow[] windows) => BuildBar(grouping, clicks: null, windows);

    private static (TaskbarWindow Window, TaskbarView View, ShellModel Model, StubWindowManager Wm) BuildBar(
        bool grouping, TaskButtonClickPolicy? clicks, params ForeignWindow[] windows)
    {
        var wm = new StubWindowManager { Live = windows };
        var model = new ShellModel(wm, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        view.Initialize(new BevelSettings
        {
            TaskbarButtonWidth = 150,
            TaskbarGrouping = grouping ? TaskbarGroupingMode.Always : TaskbarGroupingMode.Never,
        });
        var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Seed the buttons at their steady-state width/opacity so the render doesn't catch the grow-in
        // animation, then Start the model so the stub's focus events drive the exclusive-focus projection.
        // claimFocus wires optimistic clicks through ApplyExclusiveFocus (bevel-yslj).
        foreach (var w in windows)
            model.Windows.Add(new TaskItemViewModel(w, wm, clicks, claimFocus: model.ClaimFocus) { Width = 150, Opacity = 1 });
        model.Start();
        Dispatcher.UIThread.RunJobs();
        return (window, view, model, wm);
    }

    private static ForeignWindow Win(string id, string title, string? appId = "App", bool focused = false)
        => new(new ForeignWindowId(id), title, appId, false, focused, default);

    private static List<ToggleButton> Buttons(TaskbarView view) =>
        view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(c => c as ToggleButton ?? c.GetVisualDescendants().OfType<ToggleButton>().First())
            .ToList();

    /// <summary>The real gesture — pointer down + up over the button — so the ToggleButton's own click
    /// path runs exactly as it does for the user (not a synthetic Click event).</summary>
    private static void Click(TaskbarWindow window, Control button)
    {
        var tl = button.TranslatePoint(default, window)!.Value;
        var p = new Point(tl.X + button.Bounds.Width / 2, tl.Y + button.Bounds.Height / 2);
        window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        window.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static (Rect Focused, Rect Other) ButtonRects(TaskbarView view, TaskbarWindow window)
    {
        var buttons = Buttons(view);
        Rect RectOf(Control b)
        {
            var tl = b.TranslatePoint(default, window)!.Value;
            return new Rect(tl, b.Bounds.Size);
        }
        var focused = buttons.Single(b => b.IsChecked == true);
        var other = buttons.First(b => b.IsChecked != true);
        Assert.True(focused.Bounds.Width > 100, "buttons must be laid out at their steady-state width");
        return (RectOf(focused), RectOf(other));
    }

    /// <summary>Polled capture: the first frame after a resource/theme change can still be the previous
    /// composite, so capture until two consecutive frames agree (bounded).</summary>
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

    private static double Luminance(WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var px = new byte[4];
        Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, px, 0, 4);
        return 0.299 * px[2] + 0.587 * px[1] + 0.114 * px[0];   // BGRA
    }

    /// <summary>Mean luminance of the label-free right end of a button's face (inside the bevel).</summary>
    private static double FaceLuminance(WriteableBitmap frame, Rect button)
    {
        var x0 = (int)(button.Right - FaceInset - FaceSampleWidth);
        var x1 = (int)(button.Right - FaceInset);
        var y0 = (int)button.Y + FaceInset;
        var y1 = (int)button.Bottom - FaceInset;
        double sum = 0; var n = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++) { sum += Luminance(frame, x, y); n++; }
        return sum / n;
    }

    /// <summary>The pressed face must be DARKER than the resting one — the XP pressed-in semantics.
    /// Guards against the "distinct but lit" design regression that read as hover.</summary>
    private static void AssertFaceIsDarker(WriteableBitmap frame, Rect focused, Rect other, string skin)
    {
        var pressed = FaceLuminance(frame, focused);
        var resting = FaceLuminance(frame, other);
        Assert.True(resting - pressed > 8,
            $"{skin}: pressed face {pressed:F1} should be darker than the resting face {resting:F1} (pressed-in, not lit)");
    }

    private static void AssertFacesDiffer(WriteableBitmap frame, Rect focused, Rect other, string skin = "Win2000")
    {
        var a = FaceLuminance(frame, focused);
        var b = FaceLuminance(frame, other);
        Assert.True(Math.Abs(a - b) > 8, $"{skin}: pressed face {a:F1} vs neighbour {b:F1} — indistinguishable");
    }

    /// <summary>Luna's pressed button also swaps its 1px border brush; sample the top edge mid-width.</summary>
    private static void AssertBordersDiffer(WriteableBitmap frame, Rect focused, Rect other, string skin)
    {
        var a = Luminance(frame, (int)(focused.X + focused.Width / 2), (int)focused.Y);
        var b = Luminance(frame, (int)(other.X + other.Width / 2), (int)other.Y);
        Assert.True(Math.Abs(a - b) > 8, $"{skin}: pressed border {a:F1} vs neighbour {b:F1} — indistinguishable");
    }

    private static void Save(WriteableBitmap frame, string envVar, string fileName)
    {
        var outPath = Environment.GetEnvironmentVariable(envVar) ?? Path.Combine(Path.GetTempPath(), fileName);
        frame.Save(outPath);
    }

    /// <summary>Minimal window manager: a fixed live set plus a way to raise focus events.</summary>
    private sealed class StubWindowManager : IWindowManager
    {
        public IReadOnlyList<ForeignWindow> Live { get; set; } = [];

        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: [], SupportsReposition: true);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Live);

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged;

        public void RaiseForeground(ForeignWindow w) => ForegroundChanged?.Invoke(this, w);
    }
}
