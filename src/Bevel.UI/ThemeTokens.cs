namespace Bevel.UI;

/// <summary>
/// The semantic theme-token keys (bevel-38y) — the contract between Bevel-owned controls and
/// whichever theme is active (Win2000 default; Luna/Win11 later). Values live in the theme
/// (Bevel.Themes.Win2000/Tokens.axaml for Win2000, per docs/spec/05-theming.md §3/§5/§8.1);
/// controls bind these keys via DynamicResource in AXAML or resource lookup in code-behind and
/// never hardcode palette hex or metric values. ThemeTokenTests keeps this list and the theme
/// dictionary in lockstep.
/// </summary>
public static class ThemeTokens
{
    // ── Palette (spec §8.1) — Bevel.Color.* is the Color, Bevel.Brush.* the brush ──
    public const string ColorButtonFace = "Bevel.Color.ButtonFace";
    public const string ColorButtonHighlight = "Bevel.Color.ButtonHighlight";
    public const string ColorButtonLight = "Bevel.Color.ButtonLight";
    public const string ColorButtonShadow = "Bevel.Color.ButtonShadow";
    public const string ColorButtonDkShadow = "Bevel.Color.ButtonDkShadow";
    public const string ColorWindow = "Bevel.Color.Window";
    public const string ColorWindowText = "Bevel.Color.WindowText";
    public const string ColorWindowFrame = "Bevel.Color.WindowFrame";
    public const string ColorGrayText = "Bevel.Color.GrayText";
    public const string ColorAppWorkspace = "Bevel.Color.AppWorkspace";
    public const string ColorDesktop = "Bevel.Color.Desktop";
    public const string ColorMenu = "Bevel.Color.Menu";
    public const string ColorMenuText = "Bevel.Color.MenuText";
    public const string ColorScrollbar = "Bevel.Color.Scrollbar";
    public const string ColorActiveTitle = "Bevel.Color.ActiveTitle";
    public const string ColorGradientActiveTitle = "Bevel.Color.GradientActiveTitle";
    public const string ColorInactiveTitle = "Bevel.Color.InactiveTitle";
    public const string ColorGradientInactiveTitle = "Bevel.Color.GradientInactiveTitle";
    public const string ColorActiveTitleText = "Bevel.Color.ActiveTitleText";
    public const string ColorInactiveTitleText = "Bevel.Color.InactiveTitleText";
    public const string ColorHighlight = "Bevel.Color.Highlight";
    public const string ColorHighlightText = "Bevel.Color.HighlightText";
    public const string ColorHotTracking = "Bevel.Color.HotTracking";
    public const string ColorInfoWindow = "Bevel.Color.InfoWindow";
    public const string ColorInfoText = "Bevel.Color.InfoText";

    public const string BrushButtonFace = "Bevel.Brush.ButtonFace";
    public const string BrushButtonHighlight = "Bevel.Brush.ButtonHighlight";
    public const string BrushButtonLight = "Bevel.Brush.ButtonLight";
    public const string BrushButtonShadow = "Bevel.Brush.ButtonShadow";
    public const string BrushButtonDkShadow = "Bevel.Brush.ButtonDkShadow";
    public const string BrushWindow = "Bevel.Brush.Window";
    public const string BrushWindowText = "Bevel.Brush.WindowText";
    public const string BrushWindowFrame = "Bevel.Brush.WindowFrame";
    public const string BrushGrayText = "Bevel.Brush.GrayText";
    public const string BrushAppWorkspace = "Bevel.Brush.AppWorkspace";
    public const string BrushDesktop = "Bevel.Brush.Desktop";
    public const string BrushMenu = "Bevel.Brush.Menu";
    public const string BrushMenuText = "Bevel.Brush.MenuText";
    public const string BrushScrollbar = "Bevel.Brush.Scrollbar";
    public const string BrushActiveTitle = "Bevel.Brush.ActiveTitle";
    public const string BrushGradientActiveTitle = "Bevel.Brush.GradientActiveTitle";
    public const string BrushInactiveTitle = "Bevel.Brush.InactiveTitle";
    public const string BrushGradientInactiveTitle = "Bevel.Brush.GradientInactiveTitle";
    public const string BrushActiveTitleText = "Bevel.Brush.ActiveTitleText";
    public const string BrushInactiveTitleText = "Bevel.Brush.InactiveTitleText";
    public const string BrushHighlight = "Bevel.Brush.Highlight";
    public const string BrushHighlightText = "Bevel.Brush.HighlightText";
    public const string BrushHotTracking = "Bevel.Brush.HotTracking";
    public const string BrushInfoWindow = "Bevel.Brush.InfoWindow";
    public const string BrushInfoText = "Bevel.Brush.InfoText";

    // ── Metrics (spec §3) — logical px at 1.0 scale ──
    public const string MetricCaptionHeight = "Bevel.Metric.CaptionHeight";
    public const string MetricCaptionButtonWidth = "Bevel.Metric.CaptionButtonWidth";
    public const string MetricCaptionButtonHeight = "Bevel.Metric.CaptionButtonHeight";
    public const string MetricResizeBorder = "Bevel.Metric.ResizeBorder";
    public const string MetricEdgeThickness = "Bevel.Metric.EdgeThickness";
    public const string MetricScrollBarSize = "Bevel.Metric.ScrollBarSize";
    public const string MetricMenuBarHeight = "Bevel.Metric.MenuBarHeight";
    public const string MetricMenuItemHeight = "Bevel.Metric.MenuItemHeight";
    public const string MetricTaskbarHeight = "Bevel.Metric.TaskbarHeight";
    public const string MetricIconGridCellWidth = "Bevel.Metric.IconGridCellWidth";
    public const string MetricIconGridCellHeight = "Bevel.Metric.IconGridCellHeight";
    public const string MetricFocusRectInset = "Bevel.Metric.FocusRectInset";
    public const string MetricCornerRadius = "Bevel.Metric.CornerRadius";
    public const string MetricButtonPadding = "Bevel.Metric.ButtonPadding";

    // ── Fonts (spec §5 FNT-01) ──
    public const string FontUI = "Bevel.Font.UI";
    public const string FontCaption = "Bevel.Font.Caption";
    public const string FontMono = "Bevel.Font.Mono";
}
