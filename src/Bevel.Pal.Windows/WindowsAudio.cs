using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>
/// U9 (bevel-ncfp.9): theme sounds via winmm <c>PlaySound</c>. Fire-and-forget (SND_ASYNC returns
/// immediately, so no UI-thread block), respects <see cref="Muted"/>, and degrades to silence for a
/// missing/empty path or off Windows — a theme sound must never crash or stall the shell.
/// </summary>
public sealed class WindowsAudioPlayback : IAudioPlayback
{
    private static readonly Capabilities Caps = new(
        Available: true, TrayMode: TrayCapability.Authoritative,
        Notes: new[] { "windows-audio: winmm PlaySound" });

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? Caps : Capabilities.None;

    public bool Muted { get; set; }

    public Task PlayAsync(string wavPath, CancellationToken ct = default)
    {
        if (Muted || string.IsNullOrEmpty(wavPath) || !OperatingSystem.IsWindows())
            return Task.CompletedTask;
        try { Play(wavPath); } catch { /* a bad codec / missing device must never surface as an error */ }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void Play(string wavPath)
    {
        // SND_FILENAME: pszSound is a file path. SND_ASYNC: return immediately. SND_NODEFAULT: no
        // fallback "ding" when the file is unusable. Stops any prior Bevel sound first (SND_PURGE-free
        // model: a new async play supersedes the last).
        PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
    }

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
}
