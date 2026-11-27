# Bevel Desktop — User Guide

Bevel Desktop is a cross-platform desktop shell replacement: a themable taskbar
(window list, Start menu, system tray, clock) and an integrated classic-style
file manager. This guide covers installing, running, and removing Bevel on macOS.

---

## 1. Install

Download the latest DMG from [bevel.run](https://bevel.run/) (or the GitHub
Releases page), open it, and drag **Bevel.app** into your **Applications** folder.

> Bevel is code-signed and notarized. If macOS warns about an unidentified
> developer, right-click the app and choose **Open**.

## 2. First run and permissions

Launch Bevel from Applications. On first run, Bevel asks for several macOS
permissions. **Bevel and its helper are separate TCC entries** — you may need to
grant each one individually in **System Settings ▸ Privacy & Security**:

| Permission | Why Bevel needs it |
|---|---|
| **Accessibility** | Window enumeration, focus, minimize/restore |
| **Screen Recording** | Tray icon capture for mirrored menu-bar items |
| **Apple Events** | Scripting other apps (Start menu verbs, automation) |
| **Input Monitoring** | Synthetic input for window verbs |
| **Full Disk Access** | Reading files outside the sandbox for the Filer |

Grant what you are comfortable with. The taskbar works with Accessibility alone;
tray mirroring needs Screen Recording.

## 3. The taskbar

- **Window list** — every open window gets a button. Click to focus, right-click
  for the window menu (Activate / Minimize / Restore / Maximize / Close / Quit).
- **Start menu** — click the Start button (or press the Windows key if mapped)
  for programs, recent files, and session actions.
- **System tray** — mirrored macOS menu-bar status items appear here.
- **Clock** — click for the calendar.

Right-click the taskbar edge for **Taskbar settings** (icon size, grouping,
themes, tray behaviour, and more).

## 4. The Filer (file manager)

Open the Filer from the Start menu or with `bevelctl open ~`. Features: tabs,
icons/list/details views, drag-and-drop, rename, search, and an info panel.

## 5. Themes

Switch themes in **Taskbar settings ▸ Appearance**. Built-in themes:
**Bevel 1999 Industrial** (default), **Bevel 2001 Blue**.

## 6. Automation: `bevelctl` and `bevel://`

Bevel ships a CLI tool and a URL scheme for automation.

### bevelctl

```
usage: bevelctl <reveal|open|select|mkdir|delete|duplicate|move|launch|query|quit> ...
  reveal <path>... [--new-window]
  open <path> [--view icons|list|details]
  select <path>...
  mkdir <path>
  delete <path>... [--permanent]
  duplicate <path>... [--to <dir>]
  move <path>... --to <dir>
  launch <app-id>
  query <windows|selection|version|programs> [--json]
  quit
```

### bevel:// URL scheme

`bevel://` accepts **non-destructive verbs only** (web pages can fire URLs):

- `bevel://reveal?path=/Users/you/Documents`
- `bevel://open?path=/Users/you&view=details`

Destructive verbs (`delete`, `move`, `mkdir`, …) are refused from URLs — use
`bevelctl` for those.

## 7. Quitting Bevel

Use **Start menu ▸ Turn Off…** (or `bevelctl quit`). **Never `kill -9` the Bevel
process** — it strands the macOS Dock (Bevel hides the Dock while running and
restores it on clean exit).

## 8. Recovering a half-dead shell

If Bevel crashes or is killed, orphaned processes can leave the Dock hidden.
Run the recovery script inside the app bundle:

```bash
/Applications/Bevel.app/Contents/Resources/bevel-recover.sh
```

It diagnoses the state, then gently recovers (SIGTERM). Use `--force` only if
the process is wedged.

## 9. Exporting crash logs

If Bevel crashes, durable logs are kept in `~/Library/Logs/Bevel`. To export
them as a zip for a bug report: open **Taskbar settings ▸ Diagnostics ▸ Export
crash logs…**.

---

## See also

- [Known issues](known-issues.md)
- [Uninstalling Bevel](uninstall.md)
