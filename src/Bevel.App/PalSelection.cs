namespace Bevel.App;

using System.Runtime.InteropServices;

/// <summary>Which concrete PAL the composition root wires up (DI-02).</summary>
public enum PalKind
{
    /// <summary>Deterministic in-memory PAL. The M0 default and the demo/UI-test PAL.</summary>
    Fake,

    /// <summary>macOS PAL (stubs at M0; native integration lands M1+).</summary>
    MacOS,

    /// <summary>Windows PAL (bootstrap stubs at bevel-ncfp.1; Win32 integration lands per unit).</summary>
    Windows,
}

/// <summary>
/// Resolves the PAL from the command line. <c>--pal=fake</c> / <c>--pal=macos</c> /
/// <c>--pal=windows</c> (aliases <c>mac</c>/<c>osx</c> and <c>win</c>).
/// Defaults to the host OS PAL on macOS and Windows so a packaged launch shows live windows;
/// Linux/CI fall back to <see cref="PalKind.Fake"/> (DI-02).
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
                "windows" or "win" => PalKind.Windows,
                _ => PalKind.Fake,
            };
        }

        // Default PAL: on macOS/Windows, prefer that OS's real PAL so the shell shows live windows
        // out of the box; elsewhere (Linux/CI) fall back to the deterministic Fake PAL (the app must
        // still boot for tests and the UI previewer).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PalKind.MacOS;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PalKind.Windows;

        return PalKind.Fake;
    }
}
