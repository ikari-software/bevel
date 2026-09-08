# Restoring the Windows shell after set-as-shell (U5 escape hatch)

If Bevel was set as the Windows shell (`IShellSession.RegisterAsShellAsync`) and you end up with no
usable desktop, recover this way — **Ctrl+Alt+Del still works even when the shell is broken**:

1. Press **Ctrl+Alt+Del → Task Manager**.
2. **File ▸ Run new task**.
3. To get a normal desktop immediately: run **`explorer.exe`**.
4. To make it permanent, run **`regedit`** and **delete** the value:
   - Key: `HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Winlogon`
   - Value: **`Shell`** — delete it (do *not* set it to `explorer.exe`).
   - Deleting it makes Winlogon fall back to the machine default (`HKLM\…\Winlogon\Shell` = `explorer.exe`).

Notes:
- Bevel only ever writes the **per-user** (`HKCU`) shell value, so another account on the box is never
  affected and can always fix yours.
- A machine policy `HKLM\…\Policies\System\Shell` overrides the per-user value; on a managed box the
  per-user set-as-shell may be ignored (by design).
- `UnregisterAsync()` performs the same safe restore (deletes the `HKCU` value) from within Bevel.
