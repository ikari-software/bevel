using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Bevel.Core.Vfs;

namespace Bevel.UI;

/// <summary>
/// A <see cref="Glyphs"/> icon as a XAML element: <c>&lt;ui:GlyphIcon Glyph="folder" Size="16"/&gt;</c>.
///
/// The ONE way to put a shared glyph in markup. The alternative — transcribing the glyph's paths and
/// colours into the .axaml — is how the address bar's folder drifted twice: first its geometry (a copy
/// of the path data that kept the old rectangular back panel, c98431d), then its colours (three hex
/// literals that never followed a skin, bevel-i9iu). Binding <see cref="Glyphs"/>' brushes with
/// <c>x:Static</c> would not fix the second: those brushes are resolved once and CACHED, the cache is
/// dropped on every theme/scheme/variant switch, and an <c>x:Static</c> captures the object at parse
/// time — the bound Path would keep the stale, evicted brush forever. So this hosts the built icon and
/// REBUILDS it on <see cref="Glyphs.ThemeCacheInvalidated"/>, which every recolour engine raises.
/// </summary>
public sealed class GlyphIcon : Decorator
{
    /// <summary>The semantic icon id, as <see cref="IconKey.SemanticId"/> ("folder", "doc.txt", "drive.fixed", …).</summary>
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<GlyphIcon, string?>(nameof(Glyph));

    /// <summary>Rendered size in DIPs (the glyph is authored in a 16-unit space and vector-scaled).</summary>
    public static readonly StyledProperty<int> SizeProperty =
        AvaloniaProperty.Register<GlyphIcon, int>(nameof(Size), 16);

    static GlyphIcon()
    {
        GlyphProperty.Changed.AddClassHandler<GlyphIcon>((c, _) => c.Rebuild());
        SizeProperty.Changed.AddClassHandler<GlyphIcon>((c, _) => c.Rebuild());
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public int Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Glyphs.ThemeCacheInvalidated += OnThemeCacheInvalidated;
        Rebuild();   // the tokens may have changed while detached
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Glyphs.ThemeCacheInvalidated -= OnThemeCacheInvalidated;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeCacheInvalidated()
    {
        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    private void Rebuild()
        => Child = Glyph is { Length: > 0 } id ? Glyphs.Icon(Size, new IconKey(id)) : null;
}
