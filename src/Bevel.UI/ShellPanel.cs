using Avalonia.Controls;

namespace Bevel.UI;

/// <summary>
/// Placeholder base for Bevel's owner-drawn chrome primitives. Real classic-look
/// controls (ClassicBorderDecorator-based panels, title bars, sunken/raised bevels)
/// land with the theming track. Exists at M0 so feature modules have something to
/// reference from <c>Bevel.UI</c>.
/// </summary>
public class ShellPanel : Panel
{
}
