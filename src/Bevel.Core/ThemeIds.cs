namespace Bevel.Core;

/// <summary>
/// The persisted theme identifiers, and the mapping from the vendor-derived ids earlier builds wrote.
///
/// Lives in Core rather than next to ThemeService because the migration has to happen where the value
/// leaves the DATABASE — Core must not reference Avalonia (ARCH-02), so the UI-side theme service
/// cannot be reached from here. Canonicalising at the single read point means the rest of the shell
/// only ever sees a current id.
/// </summary>
public static class ThemeIds
{
    public const string Industrial1999 = "industrial1999";
    public const string Blue2001 = "blue2001";
    public const string Flat = "flat";

    /// <summary>The id an empty, missing or unknown value resolves to.</summary>
    public const string Default = Industrial1999;

    /// <summary>
    /// Ids written by builds that used the old vendor-derived names.
    ///
    /// "luna" is Microsoft's codename for the XP visual style and "win2000" is a product name; neither
    /// belongs in a public repo or a shipped assembly, so both were renamed. But the OLD value is
    /// already sitting in every existing install's settings.db, and an id the loader does not recognise
    /// resolves silently to the default — every user of the 2001 Blue skin would have opened the shell
    /// to find it reset, with nothing to explain why. This map is what makes the rename invisible.
    ///
    /// Applied on every read, so an old value keeps working indefinitely and is rewritten the next time
    /// settings are saved. Do not remove these entries: a settings.db can be arbitrarily old.
    /// </summary>
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["win2000"] = Industrial1999,
        ["luna"] = Blue2001,
    };

    /// <summary>Maps a persisted id to the one in use today; current ids pass through unchanged.</summary>
    public static string Canonical(string? id)
        => string.IsNullOrWhiteSpace(id) ? Default
         : Legacy.TryGetValue(id!, out var current) ? current
         : id!;
}
