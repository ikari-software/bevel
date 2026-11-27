using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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

    private static int CountRaises(Action mutate)
    {
        var raises = 0;
        void Record() => raises++;
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            mutate();
            Dispatcher.UIThread.RunJobs();
        }
        finally { Glyphs.ThemeCacheInvalidated -= Record; }
        return raises;
    }

    [AvaloniaFact]
    public void Re_applying_the_look_already_in_place_is_silent_and_a_new_look_raises_again()
    {
        // ThemeVariants.Apply re-runs on EVERY settings change (font, folder options…); re-applying the active
        // colourway must not churn the resources and rebuild every glyph in the shell (review finding #1).
        ResetEngines();
        try
        {
            Assert.Equal(1, CountRaises(() => FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" })));
            Assert.Equal(0, CountRaises(() => FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" })));
            Assert.Equal(1, CountRaises(() => FlatVariantService.Apply(new BevelSettings { FlatVariant = "Silver" })));
            Assert.Equal(Color.Parse("#A0A0A0"), Resolve(Highlight));
            Assert.Equal(1, CountRaises(() => FlatVariantService.Apply(new BevelSettings { FlatVariant = "Blue" })));
            Assert.Equal(0, CountRaises(() => FlatVariantService.Apply(new BevelSettings { FlatVariant = "Blue" })));

            Assert.Equal(1, CountRaises(() => Blue2001VariantService.Apply("Silver", "Gloss")));
            Assert.Equal(0, CountRaises(() => Blue2001VariantService.Apply("Silver", "Gloss")));
            Assert.Equal(1, CountRaises(() => ColorSchemeService.Apply("Brick")));
            Assert.Equal(0, CountRaises(() => ColorSchemeService.Apply("Brick")));
        }
        finally { ResetEngines(); }
    }

    [AvaloniaFact]
    public void An_engine_applies_into_a_fresh_Application_even_if_a_previous_one_had_the_same_look()
    {
        // The engines' statics outlive an Application (every headless test gets a fresh one). An "already applied"
        // guard keyed on the id alone skipped the fresh Application, which then had none of the look (the stale
        // static class behind bevel-i145). Stand in for a fresh Application by swapping its resources.
        ResetEngines();
        var app = Application.Current!;
        var original = app.Resources;
        var fresh = new ResourceDictionary();
        try
        {
            FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" });
            Blue2001VariantService.Apply("Silver", "Gloss");
            ColorSchemeService.Apply("Brick");

            app.Resources = fresh;
            FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" });
            Assert.True(fresh.ContainsKey("Flat.Brush.TitleToneDark"), "Flat skipped the fresh resources");
            Blue2001VariantService.Apply("Silver", "Gloss");
            Assert.True(fresh.ContainsKey("Blue2001.Brush.Gloss"), "Blue2001 skipped the fresh resources");
            ColorSchemeService.Apply("Brick");
            Assert.True(fresh.MergedDictionaries.Count >= 2, "ColorScheme skipped the fresh resources (scheme + aliases)");

            // Clearing in the fresh Application removes what went into it.
            ResetEngines();
            Assert.False(fresh.ContainsKey("Flat.Brush.TitleToneDark"));
            Assert.False(fresh.ContainsKey("Blue2001.Brush.Gloss"));
        }
        finally
        {
            app.Resources = original;
            // The engines forgot the looks they put into `original` when they moved to `fresh`; re-adopt and clear
            // them so this test leaves the Application as it found it.
            FlatVariantService.Apply(new BevelSettings { FlatVariant = "Amber" });
            Blue2001VariantService.Apply("Silver", "Gloss");
            ColorSchemeService.Apply("Brick");
            ResetEngines();
        }
    }

    [AvaloniaFact]
    public void ThemeService_apply_raises_once_after_the_theme_swap()
    {
        ResetEngines();
        object? seen = null;
        var raises = 0;
        void Record() { raises++; seen = Resolve("Bevel.Color.Window"); }
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            Assert.True(ThemeService.Apply(ThemeIds.Pastel));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, raises);
            Assert.Equal(Color.Parse("#FBF7F1"), seen);   // the Pastel palette, not the one it replaced

            raises = 0;
            Assert.True(ThemeService.Apply(ThemeIds.Pastel));   // already in place
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, raises);

            Assert.True(ThemeService.Apply(ThemeIds.Industrial1999));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, raises);
            Assert.Equal(Resolve("Bevel.Color.Window"), seen);
        }
        finally
        {
            Glyphs.ThemeCacheInvalidated -= Record;
            ThemeService.Apply(ThemeIds.Industrial1999);
            ResetEngines();
        }
    }

    [AvaloniaFact]
    public void A_live_glyph_recolours_at_every_hop_of_a_theme_round_trip()
    {
        // The sequence reviewers traced by hand: Bevel 2001 Silver -> Flat Blue -> Industrial 1999 -> back. Each hop
        // runs exactly what App's settings handler runs, and must reach the glyph once, from the final palette.
        ResetEngines();
        var icon = new GlyphIcon { Glyph = "folder", Size = 16 };
        var window = new Window { Content = icon, Width = 64, Height = 64 };
        object? seen = null;
        var raises = 0;
        void Record() { raises++; seen = Resolve(Highlight); }
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var hops = new[]
            {
                new BevelSettings { ThemeId = ThemeIds.Blue2001, Blue2001Color = "Silver", Blue2001Gloss = "Gloss" },
                new BevelSettings { ThemeId = ThemeIds.Flat, FlatVariant = "Blue" },
                new BevelSettings { ThemeId = ThemeIds.Industrial1999, ColorScheme = "Brick" },
                new BevelSettings { ThemeId = ThemeIds.Blue2001, Blue2001Color = "Silver", Blue2001Gloss = "Gloss" },
            };
            foreach (var hop in hops)
            {
                var before = icon.Child;
                raises = 0;
                Assert.True(ThemeService.Apply(hop.ThemeId));
                ThemeVariants.Apply(hop);
                Dispatcher.UIThread.RunJobs();

                Assert.True(raises == 1, $"{hop.ThemeId}: raised {raises} times");
                Assert.Equal(Resolve(Highlight), seen);
                Assert.NotSame(before, icon.Child);
            }
        }
        finally
        {
            Glyphs.ThemeCacheInvalidated -= Record;
            window.Close();
            ThemeService.Apply(ThemeIds.Industrial1999);
            ResetEngines();
        }
    }

    [AvaloniaFact]
    public void A_throwing_subscriber_neither_silences_the_others_nor_latches_the_signal()
    {
        var raises = 0;
        void Throw() => throw new InvalidOperationException("boom");
        void Record() => raises++;
        Glyphs.ThemeCacheInvalidated += Throw;
        Glyphs.ThemeCacheInvalidated += Record;
        try
        {
            Glyphs.InvalidateThemeCache();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, raises);

            Glyphs.InvalidateThemeCache();   // and the next change still gets through
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, raises);
        }
        finally
        {
            Glyphs.ThemeCacheInvalidated -= Throw;
            Glyphs.ThemeCacheInvalidated -= Record;
        }
    }
}
