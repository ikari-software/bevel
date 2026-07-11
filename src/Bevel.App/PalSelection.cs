namespace Bevel.App;

using System.Runtime.InteropServices;

/// <summary>Which concrete PAL the composition root wires up (DI-02).</summary>
public enum PalKind
{
    /// <summary>Deterministic in-memory PAL. The M0 default and the demo/UI-test PAL.</summary>
    Fake,

    /// <summary>macOS PAL (stubs at M0; native integration lands M1+).</summary>
    MacOS,
}

/// <summary>
/// Resolves the PAL from the command line. <c>--pal=fake</c> / <c>--pal=macos</c>.
/// Defaults to <see cref="PalKind.Fake"/> so the scaffold boots on any OS (DI-02).
/// A future hook will default to <see cref="PalKind.MacOS"/> via a platform detector.
/// </summary>
public static class PalSelector
{
    public static PalKind FromArgs(string[] args)
    {
        foreach (var arg in args)
        {
            var value = arg switch
            {
                _ when arg.StartsWith("--pal=", StringComparison.OrdinalIgnoreCase) => arg["--pal=".Length..],
                _ => null,
            };

            if (value is null)
            {
                continue;
            }

            return value.ToLowerInvariant() switch
            {
                "macos" or "mac" or "osx" => PalKind.MacOS,
                _ => PalKind.Fake,
            };
        }

        // Default PAL: on macOS, prefer the real macOS PAL so the shell shows live
        // windows out of the box; elsewhere fall back to the deterministic Fake PAL
        // (the app must still boot on Linux/CI for tests and the UI previewer).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PalKind.MacOS;

        return PalKind.Fake;
    }
}
