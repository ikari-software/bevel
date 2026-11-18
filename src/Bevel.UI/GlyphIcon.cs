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
///
/// <para>Also the ONE way to put a shared glyph in code (bevel-yfkw): <see cref="For"/>. A bare
/// <c>Glyphs.Icon()</c> is a snapshot that keeps its build-time brushes forever, which is why a live theme
/// switch left every Filer row, Start-menu item and Properties icon in the old palette.</para>
///
/// <para>Builds only while attached, and only when it has no child or the palette moved on since its build
/// (<see cref="Glyphs.ThemeGeneration"/>) — setting Glyph and Size then attaching is one build, not three, and a
/// host re-attached after the palette changed while it was detached (a recycled row, a closed menu) catches up.</para>
/// </summary>
public sealed class GlyphIcon : Decorator
{
    /// <summary>The semantic icon id, as <see cref="IconKey.SemanticId"/> ("folder", "doc.txt", "drive.fixed", …).</summary>
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<GlyphIcon, string?>(nameof(Glyph));

    /// <summary>Rendered size in DIPs (the glyph is authored in a 16-unit space and vector-scaled).</summary>
    public static readonly StyledProperty<int> SizeProperty =
        AvaloniaProperty.Register<GlyphIcon, int>(nameof(Size), 16);

    // The Glyphs.ThemeGeneration the current Child was built at; -1 forces the next EnsureBuilt to build.
    private int _builtGeneration = -1;
    private bool _attached;

    static GlyphIcon()
    {
        GlyphProperty.Changed.AddClassHandler<GlyphIcon>((c, _) => c.Invalidate());
        SizeProperty.Changed.AddClassHandler<GlyphIcon>((c, _) => c.Invalidate());
    }

    /// <summary>A host for <paramref name="key"/>'s glyph at <paramref name="size"/> DIPs — the code-side twin
    /// of <c>&lt;ui:GlyphIcon Glyph=".." Size=".."/&gt;</c>; same id mapping as <see cref="Glyphs.Icon"/>.</summary>
    public static GlyphIcon For(IconKey key, int size)
        => new() { Glyph = key.SemanticId ?? "doc.generic", Size = size };

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
        _attached = true;
        Glyphs.ThemeCacheInvalidated += OnThemeCacheInvalidated;
        EnsureBuilt();   // the tokens may have changed while detached
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Glyphs.ThemeCacheInvalidated -= OnThemeCacheInvalidated;
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    // Raised on the UI thread (Glyphs posts it); the guard only covers a direct invoke from elsewhere.
    private void OnThemeCacheInvalidated()
    {
        if (Dispatcher.UIThread.CheckAccess()) EnsureBuilt();
        else Dispatcher.UIThread.Post(EnsureBuilt);
    }

    private void Invalidate()
    {
        _builtGeneration = -1;
        EnsureBuilt();
    }

    private void EnsureBuilt()
    {
        if (!_attached) return;
        var generation = Glyphs.ThemeGeneration;
        if (Child is not null && _builtGeneration == generation) return;
        Child = Glyph is { Length: > 0 } id ? Glyphs.Icon(Size, new IconKey(id)) : null;
        _builtGeneration = generation;
    }
}
