using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.UI;
using Bevel.UI.Blue2001;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The palette-changed contract (bevel-yfkw): <see cref="Glyphs.ThemeCacheInvalidated"/> is the ONE
/// app-wide signal that a recolour engine changed the tokens icons are drawn from. Every engine mutation
/// must raise it, exactly once per dispatcher pass, and only AFTER the new values are in
/// Application.Resources — a subscriber that rebuilds from the old palette re-fills the brush cache with
/// stale brushes and the icon never recolours.
/// </summary>
[Collection("TaskbarTheme")]   // mutates Application.Current resources + the engines' process-wide statics
public class ThemeInvalidationContractTests
{
    private const string Highlight = "Bevel.Color.Highlight";
    private static readonly object SchemeHighlight = Classic.CommonControls.SystemColors.HighlightColorKey;

    private static object? Resolve(object key)
    {
        Application.Current!.TryGetResource(key, null, out var value);
        return value;
    }

    private static void ResetEngines()
    {
        Blue2001VariantService.Clear();
        FlatVariantService.Clear();
        ColorSchemeService.Clear();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Every_engine_mutation_raises_once_after_resources_are_written()
    {
        ResetEngines();
        var seen = new List<(object? Highlight, object? SchemeHighlight)>();
        void Record() => seen.Add((Resolve(Highlight), Resolve(SchemeHighlight)));
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            void Step(string name, Action mutate)
            {
                seen.Clear();
                mutate();
                Dispatcher.UIThread.RunJobs();
                Assert.True(seen.Count == 1, $"{name}: raised {seen.Count} times, expected exactly 1");
                // The subscriber must observe the palette the engine just WROTE, not the one it replaced.
                Assert.True(Equals(Resolve(Highlight), seen[0].Highlight),
                    $"{name}: {Highlight} at raise was {seen[0].Highlight}, after apply {Resolve(Highlight)}");
                Assert.True(Equals(Resolve(SchemeHighlight), seen[0].SchemeHighlight),
                    $"{name}: SystemColors.Highlight at raise was {seen[0].SchemeHighlight}, after apply {Resolve(SchemeHighlight)}");
            }

            Step("Blue2001VariantService.Apply(Silver, Gloss)", () => Blue2001VariantService.Apply("Silver", "Gloss"));
            Step("Blue2001VariantService.Clear()", Blue2001VariantService.Clear);
            Step("FlatVariantService.Apply(Amber)", () => FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" }));
            Step("FlatVariantService.Clear()", FlatVariantService.Clear);
            Step("ColorSchemeService.Apply(Brick)", () => ColorSchemeService.Apply("Brick"));
            Step("ColorSchemeService.Clear()", ColorSchemeService.Clear);
        }
        finally
        {
            Glyphs.ThemeCacheInvalidated -= Record;
            ResetEngines();
        }
    }

    [AvaloniaFact]
    public void Engines_run_back_to_back_coalesce_into_one_raise_with_the_final_palette()
    {
        ResetEngines();
        var raises = 0;
        object? seenHighlight = null;
        void Record() { raises++; seenHighlight = Resolve(Highlight); }
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            // One settings change runs several engines in ONE handler (ThemeVariants.Apply clears the inactive
            // engines, then applies the active one). Subscribers must hear about it once, after the last write.
            var generation = Glyphs.ThemeGeneration;
            FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" });
            Blue2001VariantService.Apply("Purple", "Matte");
            ColorSchemeService.Apply("Brick");
            Assert.Equal(0, raises);   // posted, not raised inline
            Assert.True(Glyphs.ThemeGeneration > generation, "ThemeGeneration must advance synchronously on invalidation");

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, raises);
            Assert.Equal(Resolve(Highlight), seenHighlight);
        }
        finally
        {
            Glyphs.ThemeCacheInvalidated -= Record;
            ResetEngines();
        }
    }

    [AvaloniaFact]
    public void Clearing_an_engine_that_holds_nothing_does_not_raise()
    {
        // ThemeVariants.Apply clears every inactive engine on EVERY settings change (font, folder options…);
        // a no-op Clear must not make every glyph host in the shell rebuild.
        ResetEngines();
        var raises = 0;
        void Record() => raises++;
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            Blue2001VariantService.Clear();
            FlatVariantService.Clear();
            ColorSchemeService.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, raises);
        }
        finally
        {
            Glyphs.ThemeCacheInvalidated -= Record;
            ResetEngines();
        }
    }
}
