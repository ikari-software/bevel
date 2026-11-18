using Avalonia;
using Avalonia.Media;
using Bevel.Core;

namespace Bevel.UI;

/// <summary>Source-skin variation axis for Flat/Whistler Watercolor. These are deliberately not Luna
/// variants: each look preserves Flat's warm-gray shell and changes only the source skin pigments.</summary>
public static class FlatVariantService
{
    public static readonly IReadOnlyList<(string Id, string Display)> Variants = new[]
    {
        ("Blue", "Blue"),
        ("Ergonomic", "Ergonomic"),
        ("Silver", "Silver"),
        ("Amber", "Amber"),
    };

    public const string Default = "Blue";
    private static readonly List<object> Injected = new();
    // What is applied, and INTO WHICH resource dictionary. The id alone is not enough: these statics outlive an
    // Application (every headless test gets a fresh one), and a guard keyed on the id alone would skip applying to
    // a fresh Application that has none of it (bevel-yfkw review; the stale-static class behind bevel-i145).
    private static string? _appliedId;
    private static Avalonia.Controls.IResourceDictionary? _owner;

    public static bool IsKnown(string? id) => Variants.Any(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    public static void Apply(BevelSettings settings)
    {
        if (Application.Current?.Resources is not { } resources) return;
        var id = IsKnown(settings.FlatVariant) ? settings.FlatVariant : Default;
        // ThemeVariants.Apply re-runs on EVERY settings change; re-applying the variation already in place must
        // not churn the resources or make every glyph in the shell rebuild.
        if (ReferenceEquals(_owner, resources) && string.Equals(id, _appliedId, StringComparison.OrdinalIgnoreCase))
            return;
        Clear();
        _owner = resources;
        _appliedId = id;
        // "Blue" is the theme-native look: FlatTheme.axaml already defines every token injected
        // below with the correct source-led pigments, so re-injecting them here would only shadow
        // the theme with stale values (and the 2-stop Gradient() cannot express the 3-stop caption ramp).
        // The palette change of this exit is Clear()'s removal, which raises the invalidation itself (and stays
        // silent when nothing was injected, so re-applying Blue on every settings change rebuilds nothing).
        if (string.Equals(id, Default, StringComparison.OrdinalIgnoreCase))
            return;
        var (caption, captionEnd, taskbar, taskbarEnd, select, selectEnd, places, start, startEnd, placeText, toneDark, frame, btn, btnBorder, btnHover, btnPress, progress) = id switch
        {
            "Ergonomic" => ("#C7E3DC", "#75B6A7", "#ECE9D8", "#ECE9D8", "#6B936A", "#527B51", "#75B6A7", "#C7E3DC", "#75B6A7", "#FFFFFF", "#527B51", "#75B6A7", "#75B6A7", "#A4CDC4", "#88C1B3", "#5C998B", "#75B6A7"),
            "Silver" => ("#EDF0F1", "#BFC0C0", "#ECE9D8", "#ECE9D8", "#A0A0A0", "#818181", "#BFC0C0", "#EDF0F1", "#BFC0C0", "#1A202C", "#818181", "#BFC0C0", "#A0A0A0", "#D0D3D3", "#B5B8B8", "#787B7B", "#A0A0A0"),
            "Amber" => ("#F0A83A", "#C27514", "#F4F2E8", "#ECE9D8", "#C87512", "#9E5608", "#D97E14", "#ECE9D8", "#DCD7C8", "#FFFFFF", "#9E5608", "#C27514", "#D97E14", "#F6C57E", "#E69324", "#A35C0A", "#D97E14"),
            _ => ("#5E9CF9", "#4A8CEF", "#F4F2E8", "#ECE9D8", "#316AC5", "#244F9C", "#3570D6", "#ECE9D8", "#DCD7C8", "#FFFFFF", "#1E4EAA", "#3567CB", "#2862C8", "#7AA4EB", "#3B7AE6", "#163F94", "#316AC5"),
        };
        Put(resources, "Bevel.Brush.CaptionActive", Gradient(caption, captionEnd));
        Put(resources, "Bevel.Brush.TaskbarBackground", Gradient(taskbar, taskbarEnd));
        Put(resources, "Bevel.Brush.Highlight", Gradient(select, selectEnd));
        Put(resources, "Bevel.Color.Highlight", Color.Parse(select));
        Put(resources, "Bevel.Brush.HotTracking", new SolidColorBrush(Color.Parse(select)));
        Put(resources, "Bevel.Color.HotTracking", Color.Parse(select));
        Put(resources, "Bevel.Brush.WindowFrame", new SolidColorBrush(Color.Parse(frame)));
        Put(resources, "Bevel.Color.WindowFrame", Color.Parse(frame));
        Put(resources, "Flat.Brush.CaptionButton", new SolidColorBrush(Color.Parse(btn)));
        Put(resources, "Flat.Brush.CaptionClose", new SolidColorBrush(Color.Parse(btn)));
        Put(resources, "Flat.Brush.CaptionButtonBorder", new SolidColorBrush(Color.Parse(btnBorder)));
        Put(resources, "Flat.Brush.CaptionButtonHover", new SolidColorBrush(Color.Parse(btnHover)));
        Put(resources, "Flat.Brush.CaptionButtonPressed", new SolidColorBrush(Color.Parse(btnPress)));
        Put(resources, "Flat.Brush.Check", new SolidColorBrush(Color.Parse(select)));
        Put(resources, "Flat.Brush.Focus", new SolidColorBrush(Color.Parse(select)));
        Put(resources, "Flat.Brush.Progress", Gradient(caption, captionEnd));
        Put(resources, "Flat.Brush.TaskButtonChecked", Gradient(caption, captionEnd));
        Put(resources, "Blue2001.Brush.StartMenuHeader", Gradient(caption, captionEnd));
        Put(resources, "Blue2001.Brush.StartMenuPlacesColumn", Gradient(places, places));
        Put(resources, "Blue2001.Brush.StartMenuFooter", Gradient(start, startEnd));
        Put(resources, "Blue2001.Brush.StartMenuPlaceText", new SolidColorBrush(Color.Parse(placeText)));
        Put(resources, "Flat.Brush.TitleToneDark", new SolidColorBrush(Color.Parse(toneDark)));
        Glyphs.InvalidateThemeCache();   // AFTER the writes (bevel-yfkw); coalesces with Clear()'s above
    }

    public static void Clear()
    {
        if (Application.Current?.Resources is not { } resources) return;
        var owned = ReferenceEquals(_owner, resources);
        _owner = null;
        _appliedId = null;
        if (Injected.Count == 0) return;   // nothing of ours in the resources — no palette change to announce
        // Keys injected into a previous Application's resources are not in this one: forget them, touch nothing.
        if (owned)
            foreach (var key in Injected) resources.Remove(key);
        Injected.Clear();
        if (owned) Glyphs.InvalidateThemeCache();   // the variation's colours just left (bevel-yfkw)
    }

    private static void Put(Avalonia.Controls.IResourceDictionary resources, object key, object value)
    {
        resources[key] = value;
        Injected.Add(key);
    }

    private static LinearGradientBrush Gradient(string start, string end) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops { new(Color.Parse(start), 0), new(Color.Parse(end), 1) },
    };
}
