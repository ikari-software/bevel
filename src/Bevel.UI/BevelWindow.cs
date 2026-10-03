using System;
using Avalonia.Controls;
using Avalonia.Platform;
using Classic.Avalonia.Theme;

namespace Bevel.UI;

/// <summary>
/// Base class for every Bevel window. Inherits Classic.Avalonia's <see cref="ClassicWindow"/>
/// so windows wear a client-drawn Windows 2000 title bar and beveled frame — replacing the
/// host OS's native decorations (e.g. the macOS system title bar and traffic lights) — on all
/// platforms. When Classic.Avalonia is forked into Bevel.Themes.Industrial1999 (bevel-m0-classic),
/// this base retargets to the fork and gains Bevel.Metric.* / Bevel.Color.* theming hooks.
/// </summary>
public class BevelWindow : ClassicWindow
{
    public BevelWindow()
    {
        // Strip native macOS chrome (title bar + traffic lights) UP FRONT, in the ctor, before the
        // NSWindow is realized — so the client-drawn Classic/Luna frame is deterministic. The Classic
        // theme also flips SystemDecorations->None, but only via a Loaded-time '__classic_theme_is_mac'
        // class handler that runs AFTER the native window exists (ClassicTheme.axaml.cs) — too late for
        // a freshly shown window, which then keeps native decorations. This mirrors what TaskbarWindow /
        // DesktopWindow already do in their own constructors. (Not TransparencyLevelHint/transparent
        // Background — those suit only the borderless overlay windows; framed windows want the opaque frame.)
        if (OperatingSystem.IsMacOS())
        {
            SystemDecorations = SystemDecorations.None;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        }
    }
}
