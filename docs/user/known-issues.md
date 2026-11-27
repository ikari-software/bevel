# Known Issues

Sourced from open bug beads only. Each entry cites its bead id for tracking.

## Taskbar / Start menu

- **bevel-cezo** — Start menu: type-to-search while open does not filter the
  program list yet.
- **bevel-xpfl** — Two-row taskbar: tray icons can be taller than the available
  row height.
- **bevel-yv2m** — macOS Zoom ignores the Bevel taskbar height (windows can
  slide underneath it).

## System tray

- **bevel-qcd9** — The tray self-test does not re-verify live capture if Screen
  Recording is granted mid-session (restart Bevel to pick it up).
- **bevel-yduf** — The tray mirrors the correct items but sometimes fails to
  render their icons.

## Internal / test-only

- **bevel-79r** — WindowSpawner close-enumeration test is flaky (test
  hardening, not user-visible).

---

Not a bug? Check the [user guide](guide.md) or file an issue on GitHub.
