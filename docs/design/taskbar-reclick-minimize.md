# Click-again-to-minimize on task buttons (bevel-au94)

## The gesture

Clicking the taskbar button of the window that is already active minimizes it. Windows 95 through 11
behave this way; so does Bevel, in `TaskItemViewModel.ToggleAsync`:

| button state | click does |
| --- | --- |
| minimized | restore + raise (one atomic op, bevel-nxic) |
| focused | **minimize** — the configurable arm |
| otherwise | activate |

## The decision: classic stays the default

`taskbarReclickMinimize` defaults to `Click` — the classic toggle, on, for everyone, out of the box.

The north star is "Win2000 as if designed in 2026", and fidelity here means *function*, not only
palette: minimize-on-reclick is one of the two things a Windows taskbar button does. A user who has
ever used Windows reaches for it without thinking — it is the cheapest way to get a window out of the
way without travelling to its titlebar. Defaulting it off to match the macOS Dock (where a click only
ever raises) would quietly delete a classic behaviour on a shell whose entire promise is that the
classic behaviours are there and work. That is the failure mode the project's design rules already
name: extend, don't apologise.

The counter-argument — "a click that minimizes surprises Mac users, and it is worse when focus
tracking is briefly wrong, because the click meant to raise the window buries it instead" — was the
origin of this request. But the reported instance of that was bevel-hx63: the menu-close key-focus
handback re-activated the previously-frontmost app and stomped the activation the taskbar had just
driven. That is fixed, and it was not a minimize at all. Changing a default to work around a bug
would have left the bug and lost the behaviour.

So the modern expectation is served as a *choice*, not as a new default.

## The three modes

`BevelSettings.TaskbarReclickMinimize` (`taskbarReclickMinimize`, enum, default `Click`):

| mode | a click on the active window's button |
| --- | --- |
| `Click` | minimizes it (classic Win2000 — **default**) |
| `OptionClick` | raises it; **Option** (macOS) / **Alt** (Windows) + click minimizes it |
| `Never` | raises it; the button never minimizes |

In every mode the minimized→restore and unfocused→activate arms are unchanged, and `Minimize` stays
on the button's right-click menu — turning the gesture off never makes the verb unreachable.

Surfaced in Settings → Taskbar → *Window buttons* → **Re-click**, next to the middle-click-closes
checkbox. Applies live (no restart): the taskbar pushes the mode into `TaskButtonClickPolicy.Shared`
on every settings-changed apply, and every button — including ones `ShellModel` creates later — reads
that one object at click time.

The button's accessible `StatusText` follows the mode ("Active (click to minimize)" /
"Active (Option-click to minimize)" / plain "Active"), so it never advertises a gesture the click
will not honour.

### Why the chord is Option/Alt, and why it is read live

Middle-click is already taken (`taskbarMiddleClickCloses`), so the alternate gesture is a modifier.
Option is Bevel's existing "the other verb" modifier on this control — the task-button menu already
swaps Quit for Force Quit while it is held.

The modifier is queried *at click time* from the platform's live, focus-independent state
(`TaskbarNative.OptionKeyDown` on macOS via `[NSEvent modifierFlags]`, `WindowsTaskbarNative.AltKeyDown`
via `GetAsyncKeyState` on Windows) — never taken from an input event. The taskbar is deliberately
non-activating, so keyboard events do not route to it and (verified, bevel-ww71) pointer events over
its popups carry no modifier flags at all; a live query is also the only source that answers for a
keyboard Space/Enter activation of the button. Platforms with no live query wired yet report "not
held", so `OptionClick` is a macOS/Windows mode.

## Known adjacent risk: sticky focus (NOT addressed here)

`TaskItemViewModel.IsFocused` is not set from per-window event flags (those went stale and left several
buttons pressed); it is projected exclusively by `ShellModel.ApplyExclusiveFocus`. That projection is
deliberately *sticky*: `ApplyFocusFromSnapshot` keeps the previous focused id when a reconcile snapshot
names no focused window, so a transient all-false enumeration cannot unpress every button. The cost is
that if the foreground moves to something the helper never reports as a focused window, the projection
can stay `true` for a window that is no longer front — and in `Click` mode a click meant to raise that
window would minimize it.

No freshness gate was added for this: the only correct confirmation is a live-foreground probe
(a new per-platform frontmost-app query plus a pid/bundle match on the VM) or a full enumeration
round-trip per reclick, both of which are machinery for a symptom that has not been reproduced once
bevel-hx63 was fixed. The three modes above are the user-level escape hatch in the meantime. If it is
ever reproduced on device, gate the `minimize` decision in `ToggleAsync` on the live foreground —
the same evidence-based shape as `TaskbarWindow.ShouldHandBackKeyFocus`.
