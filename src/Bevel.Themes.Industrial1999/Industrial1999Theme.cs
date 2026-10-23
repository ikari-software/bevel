namespace Bevel.Themes.Industrial1999;

/// <summary>
/// Marker for the Win2000 theme assembly. The theme itself is data
/// (<c>Industrial1999Theme.axaml</c>); this type just anchors the assembly reference and
/// documents the fork plan.
/// </summary>
/// <remarks>
/// TODO: fork Classic.Avalonia (MIT) into this project instead of referencing the
/// Classic.Avalonia.Theme NuGet package — resource-key refactor, metrics extraction
/// and DPI snapping are too invasive to layer over the package (05 §11). Tracked in beads.
/// </remarks>
public static class Industrial1999Theme
{
    public const string ResourceUri = "avares://Bevel.Themes.Industrial1999/Industrial1999Theme.axaml";
}
