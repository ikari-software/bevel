using System.Text;
using System.Text.Json;

// ThemeGen (MET-01 / bevel-51t): regenerates the semantic theme-token artifacts from
// theme.json — the single source of truth. Deterministic output (document order, LF,
// invariant culture) so CI can diff the committed files against a fresh run.
//
//   dotnet run --project tools/ThemeGen                 (from the repo root)
//   dotnet run --project tools/ThemeGen -- <theme.json> <Tokens.axaml> <ThemeTokens.cs>

var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
    ?? throw new InvalidOperationException("Bevel.sln not found above the tool — pass explicit paths.");

var jsonPath = args.Length > 0 ? args[0] : Path.Combine(repoRoot, "src", "Bevel.Themes.Win2000", "theme.json");
var axamlPath = args.Length > 1 ? args[1] : Path.Combine(repoRoot, "src", "Bevel.Themes.Win2000", "Tokens.axaml");
var csPath = args.Length > 2 ? args[2] : Path.Combine(repoRoot, "src", "Bevel.UI", "ThemeTokens.cs");
var schemesJsonPath = Path.Combine(repoRoot, "src", "Bevel.Themes.Win2000", "schemes.json");
var schemesOutDir = Path.Combine(repoRoot, "third_party", "classic-avalonia", "Classic.Avalonia.Theme", "Colors");

using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
var root = doc.RootElement;
var palette = root.GetProperty("palette");
var metrics = root.GetProperty("metrics");
var fonts = root.GetProperty("fonts");
var edgeRendering = root.GetProperty("edgeRendering").GetString()!;

File.WriteAllText(axamlPath, EmitAxaml(palette, metrics, fonts, edgeRendering), new UTF8Encoding(false));
File.WriteAllText(csPath, EmitCs(palette, metrics, fonts), new UTF8Encoding(false));
Console.WriteLine($"themegen: wrote {axamlPath}");
Console.WriteLine($"themegen: wrote {csPath}");

// Classic color schemes (W2K-01 / bevel-c75): one SystemColors dictionary per scheme in the
// vendored fork, generated from schemes.json.
using var schemesDoc = JsonDocument.Parse(File.ReadAllText(schemesJsonPath));
foreach (var scheme in schemesDoc.RootElement.GetProperty("schemes").EnumerateObject())
{
    var outPath = Path.Combine(schemesOutDir, scheme.Name + ".axaml");
    File.WriteAllText(outPath, EmitScheme(scheme.Value, metrics), new UTF8Encoding(false));
    Console.WriteLine($"themegen: wrote {outPath}");
}

static string MetricDouble(JsonElement metrics, string name)
    => metrics.GetProperty(name).GetProperty("double").GetDouble()
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

static string EmitScheme(JsonElement colors, JsonElement metrics)
{
    // The vendored SystemParameters keys must agree with Bevel.Metric.* — both derive from
    // theme.json here so they cannot drift (bevel-zhf; upstream shipped MenuBarHeight 18
    // where the Win2000 'Windows Standard' value is 19, spec 05-theming.md §3).
    var menuBarHeight = MetricDouble(metrics, "MenuBarHeight");
    var captionHeight = MetricDouble(metrics, "CaptionHeight");
    var scrollBarSize = MetricDouble(metrics, "ScrollBarSize");
    var b = new StringBuilder();
    b.Append("""
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:common="clr-namespace:Classic.CommonControls;assembly=Classic.CommonControls.Avalonia"
                    xmlns:system="clr-namespace:System;assembly=netstandard">
  <!--
    auto-generated: do NOT edit by hand.
    Classic color scheme (W2K-01 / bevel-c75), generated from
    src/Bevel.Themes.Win2000/schemes.json by tools/ThemeGen. Edit the JSON and run
    tools/ThemeGen (dotnet run); CI fails when this file drifts from a fresh generation.
  -->
""");
    b.Append('\n');
    foreach (var c in colors.EnumerateObject())
        b.Append($"  <Color x:Key=\"{{x:Static common:SystemColors.{c.Name}ColorKey}}\">{c.Value.GetString()}</Color>\n");

    // Shared non-color tail — identical across schemes, owned here (upstream had it verbatim
    // in every scheme file); metric values interpolated from theme.json above.
    b.Append($$"""

  <system:Double x:Key="{x:Static common:SystemParameters.HorizontalScrollBarHeightKey}">{{scrollBarSize}}</system:Double>
  <system:Double x:Key="{x:Static common:SystemParameters.VerticalScrollBarWidthKey}">{{scrollBarSize}}</system:Double>
  <system:Double x:Key="{x:Static common:SystemParameters.HorizontalScrollBarButtonWidthKey}">{{scrollBarSize}}</system:Double>
  <system:Double x:Key="{x:Static common:SystemParameters.VerticalScrollBarButtonHeightKey}">{{scrollBarSize}}</system:Double>
  <system:Double x:Key="{x:Static common:SystemParameters.HorizontalScrollBarThumbWidthKey}">{{scrollBarSize}}</system:Double>
  <system:Double x:Key="{x:Static common:SystemParameters.MenuBarHeightKey}">{{menuBarHeight}}</system:Double>

  <system:Double x:Key="{x:Static common:SystemParameters.WindowCaptionHeightKey}">{{captionHeight}}</system:Double>
  <system:Double x:Key="{x:Static common:NonClientMetrics.CaptionFontSizeKey}">11</system:Double>
  <FontFamily x:Key="{x:Static common:NonClientMetrics.CaptionFontKey}">fonts:Tahoma#Tahoma, Tahoma, $Default</FontFamily>
  <FontFamily x:Key="ContentControlThemeFontFamily">fonts:Tahoma#Tahoma, Tahoma, $Default</FontFamily>
  <system:Double x:Key="FontSizeSmall">9</system:Double>
  <system:Double x:Key="FontSizeNormal">11</system:Double>
  <system:Double x:Key="FontSizeLarge">13</system:Double>
</ResourceDictionary>
""");
    b.Append('\n');
    return b.ToString();
}

static string EmitAxaml(JsonElement palette, JsonElement metrics, JsonElement fonts, string edgeRendering)
{
    var b = new StringBuilder();
    b.Append("""
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:classic="using:Classic.Avalonia.Theme">
    <!--
      auto-generated: do NOT edit by hand.
      Bevel semantic theme tokens, generated from theme.json by tools/ThemeGen
      (MET-01 / bevel-51t). Edit theme.json and run tools/ThemeGen (dotnet run);
      CI fails when this file drifts from a fresh generation. (XML comments cannot
      contain double hyphens, so no flag spellings here.)

      Values: docs/spec/05-theming.md §8.1 (palette), §3 (metrics), §5 FNT-01 (fonts).
      Controls bind Bevel.Brush.* / Bevel.Metric.* / Bevel.Font.* via DynamicResource so a
      future theme or classic color scheme (W2K-01) swaps values without touching templates.
      Roles that share a value in this scheme still get their OWN keys — schemes may recolor
      them independently.
    -->

    <!-- ── Palette ────────────────────────────────────────────────────────── -->
""");
    b.Append("\n\n");
    foreach (var p in palette.EnumerateObject())
        b.Append($"    <Color x:Key=\"Bevel.Color.{p.Name}\">{p.Value.GetString()}</Color>\n");
    b.Append('\n');
    foreach (var p in palette.EnumerateObject())
        b.Append($"    <SolidColorBrush x:Key=\"Bevel.Brush.{p.Name}\" Color=\"{{StaticResource Bevel.Color.{p.Name}}}\" />\n");

    b.Append("\n    <!-- ── Metrics (logical px at 1.0 scale) ──────────────────────────────── -->\n\n");
    foreach (var m in metrics.EnumerateObject())
    {
        var (tag, text) = MetricValue(m.Value);
        b.Append($"    <{tag} x:Key=\"Bevel.Metric.{m.Name}\">{text}</{tag}>\n");
    }

    b.Append("\n    <!-- ── Fonts (families only; faces swap with the asset work) ──────────── -->\n\n");
    foreach (var f in fonts.EnumerateObject())
        b.Append($"    <FontFamily x:Key=\"Bevel.Font.{f.Name}\">{f.Value.GetString()}</FontFamily>\n");

    b.Append("\n    <!-- ── Edge rendering default (chrome spec §8; user override: Display settings) ── -->\n\n");
    b.Append($"    <classic:EdgeRendering x:Key=\"Bevel.Edge.Rendering\">{edgeRendering}</classic:EdgeRendering>\n");

    b.Append("</ResourceDictionary>\n");
    return b.ToString();
}

static (string Tag, string Text) MetricValue(JsonElement metric)
{
    if (metric.TryGetProperty("double", out var d))
        return ("x:Double", d.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
    if (metric.TryGetProperty("thickness", out var t))
        return ("Thickness", t.GetString()!);
    if (metric.TryGetProperty("cornerRadius", out var c))
        return ("CornerRadius", c.GetString()!);
    throw new InvalidOperationException($"metric must declare one of double|thickness|cornerRadius: {metric}");
}

static string EmitCs(JsonElement palette, JsonElement metrics, JsonElement fonts)
{
    var b = new StringBuilder();
    b.Append("""
// <auto-generated> — do NOT edit by hand.
// Generated from src/Bevel.Themes.Win2000/theme.json by tools/ThemeGen (MET-01 / bevel-51t).
// Edit theme.json and run `dotnet run --project tools/ThemeGen`; CI fails on drift.

namespace Bevel.UI;

/// <summary>
/// The semantic theme-token keys (bevel-38y) — the contract between Bevel-owned controls and
/// whichever theme is active (Win2000 default; Luna/Win11 later). Values live in the theme
/// (generated Tokens.axaml for Win2000, per docs/spec/05-theming.md §3/§5/§8.1); controls bind
/// these keys via DynamicResource in AXAML or resource lookup in code-behind and never
/// hardcode palette hex or metric values. ThemeTokenTests keeps keys and theme in lockstep.
/// </summary>
public static class ThemeTokens
{
""");
    b.Append('\n');
    foreach (var p in palette.EnumerateObject())
        b.Append($"    public const string Color{p.Name} = \"Bevel.Color.{p.Name}\";\n");
    b.Append('\n');
    foreach (var p in palette.EnumerateObject())
        b.Append($"    public const string Brush{p.Name} = \"Bevel.Brush.{p.Name}\";\n");
    b.Append('\n');
    foreach (var m in metrics.EnumerateObject())
        b.Append($"    public const string Metric{m.Name} = \"Bevel.Metric.{m.Name}\";\n");
    b.Append('\n');
    foreach (var f in fonts.EnumerateObject())
        b.Append($"    public const string Font{f.Name} = \"Bevel.Font.{f.Name}\";\n");
    b.Append('\n');
    b.Append("    public const string EdgeRendering = \"Bevel.Edge.Rendering\";\n");
    b.Append("}\n");
    return b.ToString();
}

static string? FindRepoRoot(string startDir)
{
    var dir = startDir;
    for (var i = 0; i < 12 && dir is not null; i++)
    {
        if (File.Exists(Path.Combine(dir, "Bevel.sln")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}
