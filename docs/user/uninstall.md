# Uninstalling Bevel

Bevel ships a built-in uninstaller that cleanly removes every trace of the app.

## Quick uninstall

```bash
/Applications/Bevel.app/Contents/MacOS/Bevel --uninstall
```

This will, in order:

1. **Stop the running shell** (SIGTERM the launcher — never SIGKILL, which can
   strand the Dock).
2. **Restore the macOS Dock** autohide value Bevel captured at launch.
3. **Remove Bevel from Login Items.**
4. **Deregister `bevel://` and folder-viewer** LaunchServices handlers
   (`lsregister -u`).
5. **Reset TCC permissions** for both Bevel and its helper
   (`tccutil reset All`).
6. **Delete config and runtime data:** `~/.config/bevel`, `~/.bevel`, and the
   runtime dirs under `~/Library/Application Support/bevel/` (sockets, tokens,
   icon pool, logs).

The app bundle itself is **never deleted** — drag `/Applications/Bevel.app` to
the Trash afterwards.

## Options

| Flag | Effect |
|---|---|
| `--dry-run` | Print the full plan without changing anything. |
| `--keep-settings` | Skip deleting `~/.config/bevel` (keeps your settings for a future reinstall). |

Example dry run:

```bash
/Applications/Bevel.app/Contents/MacOS/Bevel --uninstall --dry-run
```

## Manual cleanup (if you used `--keep-settings`)

```bash
rm -rf ~/.config/bevel ~/.bevel
```

---

## See also

- [User guide](guide.md)
- [Known issues](known-issues.md)
