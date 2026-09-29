namespace Bevel.Core.Input;

/// <summary>
/// Shared "type to find" (type-ahead) matcher: feed it the characters the user types and it returns the
/// index of the item to select. Pure logic — no UI, no <c>Key</c> decoding — so every list surface
/// (Filer items, the folder tree, the Start menu, the folder picker, the taskbar, the desktop grid)
/// can share ONE correct implementation instead of re-deriving a buggy one each time.
///
/// Behaviour (classic Filer):
///   • Characters typed within the reset window accumulate into a prefix ("a", "p" → "ap…").
///   • A pause longer than the reset window starts a fresh prefix.
///   • Repeating the SAME single character ("c", "c", "c") does NOT search for "cc" — it CYCLES forward
///     through every item beginning with that character.
///   • Search runs FORWARD from the current selection and wraps around exactly once.
///
/// Because it consumes real typed text (from the platform's text-input event), it handles digits, symbols
/// and accented / non-Latin characters for free — none of which a <c>Key</c>-enum decode can do. Timing is
/// injected (<c>nowMs</c>) so behaviour is deterministically unit-testable.
/// </summary>
public sealed class TypeToFind
{
    private readonly Func<long> _nowMs;
    private readonly long _resetMs;
    private string _buffer = "";
    private long _lastMs;

    /// <param name="nowMs">Monotonic millisecond clock (e.g. a running <c>Stopwatch.ElapsedMilliseconds</c>).</param>
    /// <param name="resetMs">Idle gap after which the accumulated prefix is dropped.</param>
    public TypeToFind(Func<long> nowMs, long resetMs = 700)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        _resetMs = resetMs;
    }

    /// <summary>The prefix accumulated so far (callers may surface it, e.g. a search hint).</summary>
    public string Buffer => _buffer;

    /// <summary>Forget the accumulated prefix — call on focus loss or when the list is reloaded.</summary>
    public void Reset() => _buffer = "";

    /// <summary>String convenience overload — the labels are themselves the match text.</summary>
    public int Match(string typed, IReadOnlyList<string> labels, int currentIndex)
        => Match(typed, labels, static s => s, currentIndex);

    /// <summary>
    /// Feed the just-typed text and get the index in <paramref name="items"/> to select, or -1 for no match
    /// (the caller should then keep the current selection). <paramref name="label"/> projects an item to its
    /// match text; <paramref name="currentIndex"/> is the current selection, or -1 if nothing is selected.
    /// </summary>
    public int Match<T>(string typed, IReadOnlyList<T> items, Func<T, string> label, int currentIndex)
    {
        if (string.IsNullOrEmpty(typed) || items.Count == 0) return -1;

        var now = _nowMs();
        if (now - _lastMs > _resetMs) _buffer = "";   // idle gap → fresh prefix (no-op when already empty)
        _lastMs = now;

        // Repeating the same single character cycles (prefix stays one char); anything else extends it.
        bool sameCharRepeat = _buffer.Length > 0 && typed.Length == 1 && AllEqualIgnoreCase(_buffer, typed[0]);
        _buffer = sameCharRepeat ? typed : _buffer + typed;

        // A single-character prefix (fresh or collapsed from a repeat) advances PAST the current item so
        // pressing the same letter walks the matches; a longer prefix refines in place from the current item.
        bool cycle = _buffer.Length == 1;
        int from = (currentIndex < 0 ? -1 : currentIndex) + (cycle ? 1 : 0);
        if (from < 0) from = 0;

        for (int n = 0; n < items.Count; n++)
        {
            int i = (from + n) % items.Count;
            if ((label(items[i]) ?? "").StartsWith(_buffer, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static bool AllEqualIgnoreCase(string buffer, char c)
    {
        var upper = char.ToUpperInvariant(c);
        foreach (var b in buffer)
            if (char.ToUpperInvariant(b) != upper) return false;
        return true;
    }
}
