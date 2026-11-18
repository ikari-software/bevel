using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// <see cref="GlyphIcon"/> build discipline (bevel-yfkw): it builds only while attached, once per palette
/// generation — not once per property set plus once on attach — and rebuilds on attach if the palette changed
/// while it was detached (a recycled row, a closed menu).
/// </summary>
[Collection("TaskbarTheme")]   // raises the app-wide palette-changed signal
public class GlyphIconTests
{
    [AvaloniaFact]
    public void Builds_only_while_attached_and_once_per_generation()
    {
        var icon = GlyphIcon.For(IconKey.Folder(), 32);
        Assert.Equal("folder", icon.Glyph);
        Assert.Equal(32, icon.Size);
        Assert.Null(icon.Child);   // property sets before attach build nothing

        var host = new Border();
        var window = new Window { Content = host };
        window.Show();
        host.Child = icon;
        Dispatcher.UIThread.RunJobs();
        var built = Assert.IsAssignableFrom<Control>(icon.Child);

        // Detach + re-attach with the palette unchanged: keep the built icon.
        host.Child = null;
        host.Child = icon;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(built, icon.Child);

        // Palette changes while detached: the next attach rebuilds.
        host.Child = null;
        Glyphs.InvalidateThemeCache();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(built, icon.Child);   // detached hosts don't build
        host.Child = icon;
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(built, icon.Child);

        // A property change while attached rebuilds even within one generation.
        var beforeResize = icon.Child;
        icon.Size = 16;
        Assert.NotSame(beforeResize, icon.Child);
        window.Close();
    }

    [AvaloniaFact]
    public void For_maps_a_key_without_a_semantic_id_to_the_generic_document()
        => Assert.Equal("doc.generic", GlyphIcon.For(default, 16).Glyph);
}
