using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U3 (bevel-ncfp.3) contract for the real Win32 <see cref="WindowsWindowManager"/>. The portable
/// facts run on every runner (macOS/Linux CI included): off Windows the whole P/Invoke surface is
/// guarded, so it constructs cleanly, reports unavailable, enumerates empty, and every action is an
/// inert no-op rather than a throw. The Windows-only facts are gated with an early return so the file
/// still compiles and passes off-Windows; on the box they assert real behavior (no crash, HWND ids
/// round-trip through <see cref="ForeignWindowId"/>).
/// </summary>
public class WindowsWindowManagerTests
{
    [Fact]
    public async Task Off_windows_is_unavailable_and_enumerates_empty()
    {
        if (OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        Assert.False(wm.Capabilities.Available);
        Assert.Empty(await wm.EnumerateAsync());
    }

    [Fact]
    public async Task Off_windows_every_action_is_an_inert_no_op()
    {
        if (OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        var id = new ForeignWindowId("12345");

        // None of these touch Win32 off-Windows — they must complete without throwing.
        await wm.ActivateAsync(id);
        await wm.MinimizeAsync(id);
        await wm.RestoreAsync(id);
        await wm.RestoreAndActivateAsync(id);
        await wm.CloseAsync(id);
        await wm.RepositionAsync(id, new PalRect(0, 0, 100, 100));
        await wm.TerminateAppAsync("C:\\Windows\\notepad.exe", force: false);
        Assert.Null(await wm.CaptureWindowAsync(id, 320, 240));
    }

    [Fact]
    public void Off_windows_subscribing_to_events_does_not_start_a_pump_or_throw()
    {
        if (OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        // The lazy pump start is Windows-guarded; wiring handlers is a pure no-op off Windows.
        void H(object? s, ForeignWindow w) { }
        wm.WindowOpened += H;
        wm.WindowClosed += H;
        wm.WindowChanged += H;
        wm.ForegroundChanged += H;
        wm.WindowOpened -= H;
        wm.WindowClosed -= H;
        wm.WindowChanged -= H;
        wm.ForegroundChanged -= H;
    }

    [Fact]
    public void Dispose_is_idempotent_on_any_os()
    {
        var wm = new WindowsWindowManager();
        wm.Dispose();
        wm.Dispose();   // second dispose must be a no-op, never throw
    }

    // ── Windows-only runtime facts ──────────────────────────────────────

    [Fact]
    public async Task Windows_is_available_and_supports_reposition()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        Assert.True(wm.Capabilities.Available);
        Assert.True(wm.Capabilities.SupportsReposition);
        // Enumerating the live desktop must not crash, and every id must round-trip to an HWND long.
        var windows = await wm.EnumerateAsync();
        foreach (var w in windows)
        {
            Assert.True(long.TryParse(w.Id.Value, out _), $"HWND id did not round-trip: '{w.Id.Value}'");
            Assert.NotNull(w.Id.Value);
        }
    }

    [Fact]
    public async Task Windows_actions_on_a_bogus_hwnd_do_not_throw()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var wm = new WindowsWindowManager();
        // A never-valid HWND: IsWindow gates every action, so these resolve to harmless no-ops.
        var id = new ForeignWindowId("999999999");
        await wm.ActivateAsync(id);
        await wm.MinimizeAsync(id);
        await wm.CloseAsync(id);
        await wm.RestoreAndActivateAsync(id);
        Assert.Null(await wm.CaptureWindowAsync(id, 200, 200));
    }

    [Fact]
    public void NormalizeCaptureMax_treats_zero_as_the_240x160_hover_default()
    {
        Assert.Equal((240, 160), WindowsWindowManager.NormalizeCaptureMax(0, 0));
        Assert.Equal((240, 160), WindowsWindowManager.NormalizeCaptureMax(-1, 0));
        Assert.Equal((320, 200), WindowsWindowManager.NormalizeCaptureMax(320, 200));
    }

    [Fact]
    public void EncodePng_zero_cap_keeps_source_dimensions()
    {
        var png = WindowsWindowManager.EncodePng(OpaqueBgra(400, 300), 400, 300, 0, 0);
        Assert.Equal((400, 300), PngSize(png));
    }

    [Fact]
    public void EncodePng_fits_a_4k_buffer_inside_the_hover_cap()
    {
        var (capW, capH) = WindowsWindowManager.NormalizeCaptureMax(0, 0);
        var png = WindowsWindowManager.EncodePng(OpaqueBgra(3840, 2160), 3840, 2160, capW, capH);
        var (w, h) = PngSize(png);
        Assert.True(w <= capW && h <= capH, $"hover PNG {w}x{h} exceeded {capW}x{capH}");
        Assert.True(w > 0 && h > 0);
    }

    [Fact]
    public async Task Windows_real_hwnd_geometry_capture_minimize_restore_close()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!TryLaunchNotepad(out var proc, out var hwnd)) return;

        using var wm = new WindowsWindowManager();
        var id = new ForeignWindowId(hwnd.ToInt64().ToString());
        try
        {
            Assert.True(WindowsWindowManager.IsRealAppWindow(hwnd));
            var child = GetWindow(hwnd, GW_CHILD);
            if (child != IntPtr.Zero)
                Assert.False(WindowsWindowManager.IsRealAppWindow(child), "child HWNDs must not become taskbar buttons");

            Assert.Equal((uint)proc.Id, WindowsWindowManager.TerminationPid(hwnd));

            var png = await wm.CaptureWindowAsync(id, 0, 0);
            Assert.NotNull(png);
            var (w, h) = PngSize(png);
            Assert.True(w <= 240 && h <= 160, $"0/0 capture encoded {w}x{h}, expected ≤240×160");

            await wm.MinimizeAsync(id);
            await Task.Delay(200);
            var minimized = (await wm.EnumerateAsync()).FirstOrDefault(x => x.Id.Value == id.Value);
            if (minimized is not null)
                Assert.True(minimized.IsMinimized);

            await wm.RestoreAsync(id);
            await wm.CloseAsync(id);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            proc.Dispose();
        }
    }

    [Fact]
    public void EncodePng_forces_opaque_by_default_but_preserves_real_alpha_on_request()
    {
        // Window CAPTURE wants opacity forced (PrintWindow's alpha is unreliable); a window ICON must
        // keep its alpha or every taskbar glyph renders as a solid square.
        var bgra = new byte[] { 0x10, 0x20, 0x30, 0x00, 0x40, 0x50, 0x60, 0x80 }; // 2×1: a=0, a=128

        var opaque = Decode(WindowsWindowManager.EncodePng(bgra, 2, 1, 0, 0));
        Assert.Equal(255, opaque.Rgba[3]);
        Assert.Equal(255, opaque.Rgba[7]);

        var kept = Decode(WindowsWindowManager.EncodePng(bgra, 2, 1, 0, 0, preserveAlpha: true));
        Assert.Equal(0, kept.Rgba[3]);
        Assert.Equal(0x80, kept.Rgba[7]);
        // BGRA in, RGBA out — the swizzle must survive the alpha change.
        Assert.Equal(0x30, kept.Rgba[0]);
        Assert.Equal(0x10, kept.Rgba[2]);
    }

    /// <summary>The regression this pins: BuildForeignWindow used to hardcode <c>IconPng: null</c>, so
    /// every Windows task button rendered label-only even though bevel-ncfp.3 scoped the icon.</summary>
    [Fact]
    public async Task Windows_enumerated_window_carries_a_usable_icon_png()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!TryLaunchNotepad(out var proc, out var hwnd)) return;

        using var wm = new WindowsWindowManager();
        try
        {
            var w = (await wm.EnumerateAsync()).FirstOrDefault(x => x.Id.Value == hwnd.ToInt64().ToString());
            if (w is null) return;   // window vanished / session has no desktop — nothing to assert

            Assert.NotNull(w.IconPng);
            var icon = Decode(w.IconPng!);
            Assert.True(icon.W >= 16 && icon.H >= 16, $"icon decoded {icon.W}x{icon.H}, expected ≥16px");
            // A fully-transparent icon is the legacy-mask bug (and an invisible button glyph).
            Assert.True(Enumerable.Range(0, icon.W * icon.H).Any(i => icon.Rgba[i * 4 + 3] != 0),
                "icon decoded fully transparent");

            // Icons are cached by source handle/exe, so a second enumeration hands back the very same
            // array rather than re-converting and re-encoding on every 2s poll (bevel-tnii).
            var again = (await wm.EnumerateAsync()).FirstOrDefault(x => x.Id.Value == w.Id.Value);
            if (again?.IconPng is not null)
                Assert.Same(w.IconPng, again.IconPng);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            proc.Dispose();
        }
    }

    /// <summary>Minimal PNG reader for what <c>EncodePng</c> writes: 8-bit RGBA, filter None rows.</summary>
    private static (int W, int H, byte[] Rgba) Decode(byte[] png)
    {
        var (w, h) = PngSize(png);
        using var idat = new MemoryStream();
        for (int o = 8; o + 8 <= png.Length;)
        {
            int len = (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
            var type = Encoding.ASCII.GetString(png, o + 4, 4);
            if (type == "IDAT") idat.Write(png, o + 8, len);
            o += 12 + len;   // length + type + data + CRC
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();

        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int row = y * (w * 4 + 1);
            Assert.Equal(0, bytes[row]);   // filter: None
            Array.Copy(bytes, row + 1, rgba, y * w * 4, w * 4);
        }
        return (w, h, rgba);
    }

    /// <summary>
    /// Activation must beat the Win32 foreground lock (bevel-upm6). The lock is what made this
    /// intermittent in the wild: a bare SwitchToThisWindow from a process without foreground rights
    /// is a SILENT no-op, so a task-button click left focus where it was and the button's optimistic
    /// press snapped back seconds later.
    ///
    /// The precondition is armed deliberately: this process takes the foreground itself immediately
    /// before activating, which is exactly the state ("someone else just changed the foreground")
    /// where the old code failed. Skips rather than fails when the desktop can't host the fixture —
    /// a headless/locked session has no meaningful foreground to contest.
    /// </summary>
    [Fact]
    public async Task Windows_activate_takes_the_foreground_against_the_lock()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!TryLaunchNotepad(out var proc, out var hwnd)) return;

        using var wm = new WindowsWindowManager();
        try
        {
            // Arm the lock: hand the foreground to someone else (our own test-host window if we can
            // find one, else notepad's owner) right before the call under test.
            var other = GetForegroundWindow();
            if (other == IntPtr.Zero || other == hwnd) return;   // nothing to contest; not a useful run
            SwitchToThisWindow(other, true);
            await Task.Delay(300);
            if (GetForegroundWindow() != other) return;          // couldn't arm it; skip rather than lie

            await wm.ActivateAsync(new ForeignWindowId(hwnd.ToInt64().ToString()));
            await Task.Delay(500);

            Assert.Equal(hwnd, GetForegroundWindow());
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            proc.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private static bool TryLaunchNotepad(out Process proc, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("notepad.exe failed to start");
        for (var i = 0; i < 50; i++)
        {
            proc.Refresh();
            hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero) return true;
            Thread.Sleep(100);
        }
        try { proc.Kill(entireProcessTree: true); } catch { /* headless session */ }
        proc.Dispose();
        proc = null!;
        return false;
    }

    private static byte[] OpaqueBgra(int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = 0x40;     // B
            buf[i + 1] = 0x80; // G
            buf[i + 2] = 0xC0; // R
            buf[i + 3] = 0xFF;
        }
        return buf;
    }

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length >= 24, "PNG too short to contain IHDR");
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    private const uint GW_CHILD = 5;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
