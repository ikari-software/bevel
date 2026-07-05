using Classic.Avalonia.Theme;

namespace Bevel.UI;

/// <summary>
/// Base class for every Bevel window. Inherits Classic.Avalonia's <see cref="ClassicWindow"/>
/// so windows wear a client-drawn Windows 2000 title bar and beveled frame — replacing the
/// host OS's native decorations (e.g. the macOS system title bar and traffic lights) — on all
/// platforms. When Classic.Avalonia is forked into Bevel.Themes.Win2000 (bevel-m0-classic),
/// this base retargets to the fork and gains Bevel.Metric.* / Bevel.Color.* theming hooks.
/// </summary>
public class BevelWindow : ClassicWindow
{
}
