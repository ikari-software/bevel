namespace Bevel.Themes.Win2000;

/// <summary>
/// Marker for the Win2000 theme assembly. The theme itself is data
/// (<c>Win2000Theme.axaml</c>); this type just anchors the assembly reference and
/// documents the fork plan.
/// </summary>
/// <remarks>
/// TODO: fork Classic.Avalonia (MIT) into this project instead of referencing the
/// Classic.Avalonia.Theme NuGet package — resource-key refactor, metrics extraction
/// and DPI snapping are too invasive to layer over the package (05 §11). Tracked in beads.
/// </remarks>
public static class Win2000Theme
{
    public const string ResourceUri = "avares://Bevel.Themes.Win2000/Win2000Theme.axaml";
}
