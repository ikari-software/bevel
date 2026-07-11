# WindowSpawner — a scripted-window test rig

A tiny AppKit app that opens, renames, and closes real windows on command, so the
window-manager path can be tested against a **known** set of windows instead of whatever
apps happen to be open on the dev/CI machine.

Plan unit **U13**, requirements **R20** (repeatable AX window-manager coverage) and **R21**
(contract test the real macOS `IWindowManager`). It is deliberately smaller than the
specced `WindowZoo.app` / M-INFRA rig — it exists to give this milestone's own tests
deterministic windows, not to stand up a self-hosted-CI fleet.

## Why it has to be a real app

`BevelHelper`'s `WindowServiceImpl` only enumerates windows whose owning app has a
`regular` activation policy, a standard-window subrole, a non-empty title, and a non-zero
on-screen frame. A bare command-line tool's windows get filtered out before any test could
see them, so the rig is a proper `.regular` AppKit app with titled `NSWindow`s.

## Protocol

One command per line on **stdin**; one ack per command on **stdout** (flushed immediately,
so a driver can wait on the window actually existing instead of sleeping):

| Command | Ack |
|---|---|
| `open <key> <x> <y> <w> <h> <title…>` | `ok open <key> id=<cgWindowNumber>` |
| `rename <key> <title…>` | `ok rename <key>` |
| `close <key>` | `ok close <key>` |
| `list` | `window <key> id=<n> title=<title>` (×N), then `ok list <count>` |
| `quit` | `ok quit` (then the process exits) |

`id=` is `NSWindow.windowNumber`, which **is** the `kCGWindowNumber` the helper reports, so
a driver can correlate on the exact window. `<title>` is the tail of the line, so it may
contain spaces without quoting. Unknown key → `err nokey <key>`; unparseable line →
`err parse <line>`. On startup the rig prints `ready`. Closing stdin is treated as `quit`,
so a driver that dies never leaks the rig.

```console
$ printf 'open a 200 200 420 320 Hello World\nlist\nclose a\nquit\n' | .build/debug/WindowSpawner
ready
ok open a id=17486
window a id=17486 title=Hello World
ok list 1
ok close a
ok quit
```

## Build & run

```bash
swift build --package-path tests/rigs/macos-lite/WindowSpawner
# binary → tests/rigs/macos-lite/WindowSpawner/.build/debug/WindowSpawner
```

## Who drives it

- **`native/helper-macos/…/WindowServiceSpawnerTests.swift`** (`SpawnerHarness`) — asserts
  `WindowServiceImpl.enumerateWindows()` finds the window by its exact CGWindowID, reflects
  a rename, and drops it on close. In-process; the real correctness coverage (R20).
- **`tests/Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests.cs`** (`SpawnerProcess`) —
  drives the real `MacOSWindowManager` over a live helper, backed by this rig (R21,
  env-gated by `BEVEL_LIVE_HELPER_TESTS=1`).

Both resolve the binary from the prebuilt `.build/debug/WindowSpawner` (building it once if
absent), or from `BEVEL_WINDOW_SPAWNER` if set.

## Permissions

Reading another process's window title needs **Accessibility** (AX titles) or **Screen
Recording** (CGWindowList titles). Without one, the helper can't title the window and
correctly drops it, so the driving tests **skip cleanly** rather than assert nothing —
window management genuinely cannot work without the grant. Grant your terminal (or Xcode)
Accessibility in System Settings ▸ Privacy & Security ▸ Accessibility to run them.
