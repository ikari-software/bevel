using System.Text;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// The rules that decide whether a possibly stale menu row still names a live tab (bevel-6asm).
/// The generated activation AppleScript (<see cref="MacOSTabProvider.ActivationScript"/>) runs the
/// SAME decision inside the target app's process; this pure C# twin exists so the rules can be
/// table-tested without an app — keep the two in step (the script's <c>isMatch</c> handler and
/// <c>hit*</c> logic are the mirror of <see cref="Matches"/> / <see cref="Resolve"/>).
/// <list type="bullet">
/// <item>Browsers (Chromium, Safari): identity is the URL when the row has one, else the title.
/// A title that changed since the fetch (unread counts) must not defeat activation.</item>
/// <item>iTerm2: identity is the session's <c>unique id</c> (<see cref="AppTab.SessionId"/>); job
/// names change constantly. A row without one cannot be identified — never guessed.</item>
/// <item>Resolution: the remembered position wins when it still matches; otherwise EXACTLY ONE
/// match across every window is used; zero or several is a no-op.</item>
/// </list>
/// </summary>
internal static class TabIdentity
{
    /// <summary>The one whitespace set trimmed on BOTH sides (C# <see cref="Normalize"/> and the
    /// script's generated <c>trimmed</c> handler), so a non-breaking space cannot make the two disagree.</summary>
    internal static readonly char[] TrimChars =
        Enumerable.Range(0, 0x10000).Select(i => (char)i).Where(char.IsWhiteSpace).ToArray();

    internal static string Normalize(string? s) => (s ?? "").Trim(TrimChars);

    /// <summary>AppleScript list literal of <see cref="TrimChars"/>, e.g. <c>{character id 9, ...}</c>.</summary>
    internal static string TrimCharsAppleScriptList()
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < TrimChars.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("character id ").Append((int)TrimChars[i]);
        }
        return sb.Append('}').ToString();
    }

    /// <summary>A tab as the app reports it right now.</summary>
    internal readonly record struct Live(string WindowRef, int TabIndex, string Title, string Url, string? SessionId);

    internal static bool Matches(Live live, AppTab want)
    {
        if (want.SessionId is not null)   // iTerm: the session id is the identity
            return want.SessionId.Length > 0 && live.SessionId == want.SessionId;
        if (!string.IsNullOrEmpty(want.Url))
            return Normalize(live.Url) == want.Url;
        var t = Normalize(live.Title);
        if (t.Length == 0) t = Normalize(live.Url);   // same fallback as MacOSTabProvider.Parse
        return t == want.Title;
    }

    internal static (string WindowRef, int TabIndex)? Resolve(IReadOnlyList<Live> live, AppTab want)
    {
        // iTerm rows without a session id (empty / null) can never be identified.
        if (want.BundleId == "com.googlecode.iterm2" && string.IsNullOrEmpty(want.SessionId)) return null;

        foreach (var l in live)
            if (l.WindowRef == want.WindowRef && l.TabIndex == want.TabIndex && Matches(l, want))
                return (l.WindowRef, l.TabIndex);

        (string, int)? only = null;
        foreach (var l in live)
        {
            if (!Matches(l, want)) continue;
            if (only is not null) return null;   // ambiguous
            only = (l.WindowRef, l.TabIndex);
        }
        return only;
    }
}
