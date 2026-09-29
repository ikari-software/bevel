using System;
using System.Globalization;
using Avalonia.Headless.XUnit;
using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// Regression pin for a live shell crash (2026-08-17): Glyphs built computed path data with
/// culture-sensitive interpolation, so under a comma-decimal locale (pl_PL) "2.5" rendered as
/// "2,5", PathMarkupParser threw on the UI thread, and ONE video file in a Filer folder
/// aborted the whole process. Every glyph must build under a comma-decimal culture.
/// </summary>
public class GlyphCultureTests
{
    [AvaloniaFact]
    public void Every_file_glyph_builds_under_a_comma_decimal_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            // One extension per glyph family Glyphs routes on — the video filmstrip (the crasher)
            // plus its siblings, so a future culture-sensitive interpolation in ANY family fails here.
            // One extension per glyph family Glyphs routes on — incl. "iso" (disc, concentric-arc
            // computed geometry) and "html" (DocGlyph, which had a culture-sensitive path escape a
            // review missed until it was grepped out). A future comma-decimal regression in ANY
            // family fails here.
            foreach (var ext in new[] { "mp4", "mp3", "jpg", "html", "htm", "zip", "exe", "txt", "pdf", "xls", "dll", "iso", "dmg", "unknownext" })
            {
                var control = Bevel.UI.Glyphs.Icon(16, IconKey.File(ext));
                Assert.NotNull(control);
            }
            // Non-file glyph families with computed geometry (drives, Start-menu icons).
            foreach (var key in new[]
            {
                IconKey.Folder(), IconKey.FolderOpen(), IconKey.Computer(), IconKey.Network(),
                IconKey.FixedDrive(), IconKey.CdDrive(), IconKey.NetDrive(), IconKey.RemovableDrive(),
                IconKey.Trash(), IconKey.Trash(full: true),
            })
                Assert.NotNull(Bevel.UI.Glyphs.Icon(16, key));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
