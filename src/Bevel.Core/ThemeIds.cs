namespace Bevel.Core;

/// <summary>
/// The persisted theme identifiers.
///
/// These replaced the vendor-derived ids an earlier build wrote ("luna" is Microsoft's codename for the
/// XP visual style; "win2000" is a product name). There is deliberately NO back-compatibility mapping.
/// v0.1.0 was the only build that ever wrote the old ids, it had six downloads across all three
/// platforms, and an unrecognised id resolves to <see cref="Default"/> through the ordinary, already
/// tested path — so the whole blast radius was one wrong theme on first launch, fixable with a click,
/// with each orphaned "theme:&lt;old&gt;" override key left sitting intact in the blob rather than
/// destroyed. That did not justify a permanent compatibility surface spelling out the very names this
/// rename existed to remove.
///
/// A legacy id is therefore simply UNKNOWN here, and <see cref="OrDefault"/> only supplies the default
/// for a missing value. Should a later rename have real installs behind it, map it — but map it where
/// the value leaves the DATABASE, which is why this type lives in Core: ARCH-02 forbids Core from
/// referencing Avalonia, so the UI-side theme service cannot be reached from the read path at all.
/// </summary>
public static class ThemeIds
{
    public const string Industrial1999 = "industrial1999";
    public const string Blue2001 = "blue2001";
    public const string Flat = "flat";

    /// <summary>The id an empty or missing value resolves to.</summary>
    public const string Default = Industrial1999;

    /// <summary>Supplies <see cref="Default"/> for a missing value. An id that is merely UNRECOGNISED is
    /// handed back as-is, so the caller's own IsKnown check decides — a typo stays visible instead of
    /// being laundered into a valid theme.</summary>
    public static string OrDefault(string? id)
        => string.IsNullOrWhiteSpace(id) ? Default : id!;
}
