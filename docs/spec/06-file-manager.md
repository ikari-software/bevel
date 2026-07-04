# File Manager (Explorer Clone)

## Summary

The file manager ("Files" internally; presents as "Exploring - <folder>" in the default Win2000 theme) is a faithful recreation of Windows 2000 Explorer built entirely in Avalonia on top of a virtual file system (VFS) abstraction. One window class serves both "folder window" mode (no tree) and "Explore" mode (tree pane on), exactly as Win2000 did. All file system access goes through `IVfsProvider` implementations — local disk, Trash, network mounts, a per-platform "My Computer" virtual root, and read-only ZIP archives in v1. A queued file-operations engine executes copy/move/delete/rename jobs off the UI thread with Win2000-style progress dialogs (including the flying-file animation, recreated), conflict resolution, multi-level undo, and cancellation. Change watching is a single `IDirectoryWatcher` interface backed by FSEvents, inotify/fanotify, and ReadDirectoryChangesW. Icons come from a two-layer pipeline: theme-supplied recreated icons matched by semantic key first, native platform icons as fallback; thumbnails come from platform thumbnailers (QuickLook / IThumbnailProvider / freedesktop thumbnailers) behind a shared disk cache. The details view must open a 100k-entry directory with first paint under 250 ms.

Cross-references: process/IPC architecture and the PAL live in `01-architecture.md`; theme package format and asset override rules in `05-theming.md` (this chapter only defines the icon *keys* themes can override); desktop/taskbar integration points (e.g. "Explore" on Start menu, desktop icons are a borderless file-manager view) in `07-shell-ux.md`; Finder-compat Apple Events surface that maps onto this VFS in `08-os-interop.md`; milestones in `09-engineering-plan.md`.

---

## 1. Window anatomy

A single `FileManagerWindow` (Avalonia `Window`) with this fixed vertical stack, top to bottom:

```
+--------------------------------------------------------------+
| Title bar: "<folder name>" or "Exploring - <folder name>"    |
| Menu bar:  File  Edit  View  Go  Favorites  Tools  Help      |
| Toolbar:   [Back v][Fwd v][Up] | [Search][Folders][History]  |
|            | [Move To][Copy To] | [Cut][Copy][Paste]         |
|            | [Undo] | [Delete][Properties] | [Views v]       |
| Address:   Address [ C:\WINNT           v ]  [Go]            |
+---------------+----------------------------------------------+
| Folders pane  |  Item view (icons / list / details)          |
| (TreeView)    |                                              |
|               |                                              |
+---------------+----------------------------------------------+
| Status bar: "42 object(s)  |  1.24 MB  |  My Computer"       |
+--------------------------------------------------------------+
```

**FM-001** The window SHALL support two modes: *folder mode* (no left pane) and *explore mode* (Folders tree pane visible). `View > Explorer Bar > Folders` (and the Folders toolbar button) toggles between them at runtime. Double-clicking a folder from the desktop opens folder mode; "Explore" context verb opens explore mode.

**FM-002** The left pane is an *Explorer Bar* host that can show exactly one of: Folders (tree), Search, History, Favorites — matching Win2000's `View > Explorer Bar` submenu. v1 ships Folders and Search; History and Favorites panes are stubs wired to the same menu but may slip to v1.1 (see 09-engineering-plan.md).

**FM-003** Every chrome element (menu bar, toolbar with etched separators and flat hover-raised buttons, address combo, status bar with sunken panels, tree lines, selection colors) SHALL be drawn by the active theme. The Win2000 theme starts from Classic.Avalonia's control themes (`ClassicWindowDecorations`, 3D border brushes) and extends them with `ToolBar`, `StatusBar`, and rebar-band styles that Classic.Avalonia does not yet cover — budget for writing these ourselves (see `05-theming.md`).

### 1.1 Menu bar

Full Win2000 menu tree, verbatim where the action exists cross-platform:

| Menu | Items (v1) |
|---|---|
| File | *(selection-dependent verbs: Open, Explore, Search…, Open With ▸)*, separator, Send To ▸, separator, New ▸ (Folder, Shortcut/Alias, per-type templates), separator, Create Shortcut, Delete, Rename, Properties, separator, Close |
| Edit | Undo <verb> (Ctrl+Z), separator, Cut (Ctrl+X), Copy (Ctrl+C), Paste (Ctrl+V), Paste Shortcut, separator, Copy To Folder…, Move To Folder…, separator, Select All (Ctrl+A), Invert Selection |
| View | Toolbars ▸ (Standard Buttons, Address Bar, Links; Customize…), Status Bar, Explorer Bar ▸ (Search, Favorites, History, Folders), separator, Large Icons / Small Icons / List / Details / Thumbnails (radio group), separator, Arrange Icons ▸ (by Name/Type/Size/Date; Auto Arrange), Line Up Icons, separator, Refresh (F5) |
| Go | Back, Forward, Up One Level, separator, Home Folder, My Computer analog, separator, MRU folder list |
| Favorites | Add to Favorites…, Organize Favorites…, separator, pinned items |
| Tools | Map Network Drive…, Disconnect Network Drive… (platform-gated), separator, Folder Options… |
| Help | Help Topics, About |

**FM-010** "Folder Options…" opens the Win2000 dialog (General / View / File Types tabs). The View tab hosts the classic checkbox tree: show hidden files, hide extensions for known types, show full path in title bar, etc. Settings persist in the shell's settings store (`01-architecture.md`) under `fileManager.folderOptions.*`.

**FM-011** "Hide file extensions for known file types" defaults to **follow the host OS setting** — we mirror the platform file manager's own show-extensions preference (Finder's "Show all filename extensions", Explorer's "Hide extensions for known file types") at first run. The user-facing toggle overrides in either direction and persists thereafter. Rationale: least surprise — extensions appear exactly as the user's OS already shows them. This is a settings default, not theme-coupled behavior and not a fixed platform-tied policy.

### 1.2 Toolbar

**FM-020** Standard Buttons band with Win2000 iconography (recreated assets, see §6): Back (split dropdown of history), Forward (split dropdown), Up, Search, Folders, History, Move To, Copy To, Cut, Copy, Paste, Undo, Delete, Properties, Views (split dropdown cycling Large Icons → Small Icons → List → Details → Thumbnails). Buttons are flat, raise on hover, and honor "text labels" option from Customize.

**FM-021** Back/Forward maintain a per-window navigation stack of VFS paths (not view states). Depth cap 50. Up navigates to the VFS parent (which for a drive root is My Computer, not the FS parent).

### 1.3 Address bar

**FM-030** The address bar is a flat editable `ComboBox` (no breadcrumbs — breadcrumbs are XP/Vista-era). Dropdown shows the virtual namespace tree flattened one level deep (Desktop, My Computer analog, drives/volumes, Home, Network, Trash) plus MRU typed paths. Displayed text is the full native path for FS locations (`/Users/cezar/Documents`, `C:\WINNT`) and the display name for virtual roots ("My Computer").

**FM-031** Typing accepts: absolute native paths, `~` expansion, environment variables (`$HOME`, `%USERPROFILE%` per platform), UNC/`smb://`/`afp://` URLs (mounts on demand via the network provider), and `vfs:` URIs (internal, accepted but never displayed). Autocomplete queries the VFS with a 150 ms debounce, inline-completion style (Win2000 behavior), Escape restores the previous text.

### 1.4 Folders tree pane

**FM-040** The tree is rooted at **Desktop**, whose children are: user home ("My Documents" analog → the home folder `~` on macOS, platform home elsewhere, per the VFS map, §2.3), the My Computer analog, Network, Trash, plus actual desktop folder contents that are folders. This mirrors the Win2000 shell namespace shape.

**FM-041** Tree nodes populate lazily on expand; a node shows the `+` expander if `IVfsNode.MightHaveChildren` (cheap heuristic: is a container) and removes it after an expansion finds none — matching Explorer's optimistic-plus behavior. Expansion enumerations run off the UI thread; a node whose enumeration exceeds 500 ms shows an hourglass cursor, never blocks the tree.

**FM-042** Tree and item view stay synchronized: navigating the view selects+reveals the tree node (expanding ancestors); selecting a tree node navigates the view. Sync suppresses re-entrancy via a navigation token.

### 1.5 Item view modes

Implemented as one virtualized `ItemsControl` with swappable panels/templates; the item source is a `FolderViewModel` shared by all modes.

| Mode | Layout | Notes |
|---|---|---|
| Large Icons | 32 px icons (theme metric), wrap grid, label below, auto-arrange optional | Free placement (drag anywhere, positions persisted per-folder) when Auto Arrange off — required for desktop reuse (`07-shell-ux.md`) |
| Small Icons | 16 px, wrap grid, label right | |
| List | 16 px, top-to-bottom columns, horizontal scroll | |
| Details | 16 px, column headers: Name, Size, Type, Modified (+ provider extras: attributes, trash origin, archive ratio) | Click header sorts, click again reverses; drag to reorder; right-click header for column picker |
| Thumbnails | 96 px thumbs in bordered tiles | Win2000 had this (folder-level option); we expose it as a normal view mode |

**FM-050** Per-folder view state (mode, sort, column widths/order, icon positions) persists in a local LiteDB/SQLite store keyed by VFS path hash — never as `desktop.ini` droppings in user folders. Global default configurable; "Reset All Folders" in Folder Options.

**FM-051** Selection semantics: single click selects, Ctrl+click toggles, Shift+click ranges, rubber-band from empty space (marquee drawn in theme selection style), type-ahead selects the next name-prefix match with a 700 ms reset timer. Double-click (or Enter) invokes the default verb. Single-click-to-open honored when Folder Options "web style" is chosen (hover-select after 400 ms, underlined labels).

**FM-052** Label edit (F2 / two slow clicks) is an in-place TextBox that pre-selects the basename excluding extension. Renaming validates against the provider (`IVfsProvider.ValidateName`) so illegal characters per backend (`/` everywhere; `\:*?"<>|` on NTFS-backed paths; leading-dot warnings on macOS) are rejected with the classic error dialog.

### 1.6 Status bar

**FM-060** Three sunken panels: (1) "N object(s)" plus "(+M hidden)" when applicable, or "N object(s) selected"; (2) total size of folder/selection (computed lazily, blank until known; folders excluded like Win2000); (3) current namespace zone icon+label ("My Computer" / "Network"). Left panel shows menu-item hint text while menus are open.

### 1.7 Keyboard parity

**FM-070** The following shortcuts SHALL work identically across platforms, with Cmd substituted for Ctrl on macOS *in addition to* (not instead of) the F-key bindings:

| Keys | Action |
|---|---|
| Ctrl+X / C / V | Cut / Copy / Paste |
| Ctrl+Z | Undo last file op |
| Ctrl+A | Select all |
| Delete | Delete to Trash |
| Shift+Delete | Delete permanently (with confirm) |
| F2 | Rename |
| F3 | Open Search bar |
| F4 | Drop down address bar |
| F5 | Refresh |
| F6 / Tab | Cycle focus: tree → view → address |
| Backspace | Up one level (Win2000 semantics: Up, not Back) |
| Alt+Left / Alt+Right | Back / Forward |
| Alt+Enter | Properties |
| Alt+F4 / Cmd+W | Close window |
| Ctrl+Shift+N | New folder (concession to modernity; also on File > New) |
| Enter / Cmd+Down (macOS) | Open |
| Apps key / Shift+F10 | Context menu on selection |

### 1.8 Context menus

**FM-080** Item context menu order (Win2000 canonical): default verb bold (Open/Explore), other static verbs, **Open With ▸**, separator, Send To ▸, separator, Cut, Copy, separator, Create Shortcut, Delete, Rename, separator, Properties. Folder-background menu: View ▸, Arrange Icons ▸, Refresh, separator, Paste, Paste Shortcut, Undo, separator, New ▸, separator, Properties.

**FM-081** *Open With* is populated by the PAL (`IAppRegistry` in `01-architecture.md`):
- macOS: `NSWorkspace.shared.urlsForApplications(toOpen:)` (12+) enumerates candidate apps; launch via `NSWorkspace.open(_:withApplicationAt:configuration:)`; "Choose Program…" shows an app-picker rooted at `/Applications`. Executed in the C# process via P/Invoke to objc_msgSend bindings, not the helper — these are safe AppKit calls.
- Windows: `SHAssocEnumHandlers` / `IEnumAssocHandlers` for the list; `IAssocHandler::Invoke`; "Choose Program…" → `SHOpenWithDialog`.
- Linux: `g_app_info_get_recommended_for_type()` from the file's MIME type (via `xdg-mime`/GIO); launch with `g_app_info_launch_uris()`; default changes via `xdg-mime default`.

**FM-082** Native context-menu *extensions* (Finder Sync/Action extensions, Windows IContextMenu shell extensions, Nautilus scripts) are explicitly **out of scope for v1**. Rationale: each is a large compatibility surface (IContextMenu alone is a tar pit) with crash risk inside our process; the 90% case is verbs + Open With + Send To. Revisit per-platform post-v1; Windows IContextMenu hosting is the most feasible and most demanded. Rejected alternative: hosting them in v1 via the native helper — deferred, not discarded.

**FM-083** Send To ▸ is populated from a user folder of launchers (`~/Library/Application Support/Bevel/SendTo`, `%APPDATA%\Bevel\SendTo`, `~/.config/bevel/sendto`) seeded with: Desktop (create shortcut), Mail Recipient (opens `mailto:` with attachment via platform), Compressed Folder (creates .zip via the archive provider).

### 1.9 Properties dialogs

**FM-090** File/folder Properties is the classic tabbed dialog. Tabs:
- **General**: big icon, name (editable), Type, Location, Size ("1.24 MB (1,302,528 bytes)"), Size on disk, Created/Modified/Accessed, attribute checkboxes mapped per platform (Read-only → POSIX write bit / NTFS R; Hidden → dot-file or `chflags hidden` on macOS, FILE_ATTRIBUTE_HIDDEN on Windows).
- **Security/Permissions** (platform-gated): POSIX owner/group/mode editor with chmod-style checkbox grid on macOS/Linux; NTFS ACL read-only summary on Windows v1.
- **Details** (provider extras: UTI/MIME type, volume, symlink target).
Folder size computes asynchronously with a live-updating count, cancellable by closing. Multi-selection shows combined size and count. Drive properties show the pie chart (used/free, drawn in theme colors) — the beloved Win2000 disk pie is a requirement, not decoration.

---

## 2. VFS abstraction

### 2.1 Model

All browsing UI binds to the VFS; nothing in the UI layer touches `System.IO` directly.

```csharp
public readonly record struct VfsPath(string Scheme, string Value); // "file", "trash", "computer", "net", "zip"
// Canonical string form: "vfs://<scheme>/<value>", e.g. vfs://zip//Users/x/a.zip!/inner/dir

public interface IVfsNode
{
    VfsPath Path { get; }
    string DisplayName { get; }
    VfsNodeKind Kind { get; }              // File, Folder, Volume, VirtualRoot, Link
    bool MightHaveChildren { get; }
    long? Size { get; }
    DateTimeOffset? Modified { get; }
    string TypeDescription { get; }        // "Text Document", "File Folder"
    IconKey IconKey { get; }               // §6
    VfsCapabilities Caps { get; }          // flags: Rename, Delete, Trash, CopySource, MoveTarget, Watchable, Properties
    IReadOnlyDictionary<string, object?> ExtraColumns { get; }
}

public interface IVfsProvider
{
    string Scheme { get; }
    ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct);
    IAsyncEnumerable<IVfsNode> EnumerateAsync(VfsPath folder, EnumerateOptions o, CancellationToken ct);
    ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct);
    ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct); // null => read-only
    IDirectoryWatcher? CreateWatcher(VfsPath folder);                              // null => poll
    NameValidationResult ValidateName(VfsPath folder, string proposedName);
}
```

`EnumerateAsync` MUST stream (`IAsyncEnumerable`) so the view can render incrementally (§8). `IVfsMutator` exposes `CreateFolder`, `Rename`, `Delete(toTrash: bool)`, `SetAttributes`, and `OpenWrite` for the operations engine.

### 2.2 Providers (v1)

| Scheme | Provider | Notes |
|---|---|---|
| `file` | LocalFsProvider | .NET `Directory.Enumerate*` + P/Invoke for attributes .NET misses (macOS `chflags`, birthtime via `stat`) |
| `computer` | ComputerProvider | Virtual root; children = volumes (§2.3) |
| `trash` | TrashProvider | §2.4 |
| `net` | NetworkProvider | Browsing = mounted shares only in v1 (macOS `/Volumes`, Linux `gio mount -l`, Windows mapped drives + `WNetOpenEnum`). Discovery (Bonjour/SMB browse) deferred to v1.1 |
| `zip` | ZipProvider | Read-only, §2.5 |

### 2.3 "My Computer" analog per platform

**FM-100** Display name and contents are platform-mapped:
- **macOS**: named after the machine (`SCDynamicStoreCopyComputerName`), children = mounted volumes from `NSFileManager.mountedVolumeURLs(includingResourceValuesForKeys:options:)` excluding hidden system volumes (`volumeIsBrowsableKey`), each with volume icon, capacity columns. Ejectable volumes get an Eject context verb (`NSWorkspace.unmountAndEjectDevice(atPath:)`).
- **Windows**: drive letters via `GetLogicalDrives` + `GetDriveType`, display names via `SHGetFileInfo`.
- **Linux**: mounts from `GVolumeMonitor` (via helper or libmount parsing of `/proc/self/mountinfo` filtered to user-relevant mounts), plus `~` as "Home".
The Win2000 theme labels it "My Computer" regardless of platform; themes may override the label (theme string resource `Vfs.ComputerRootName`), the VFS provides the neutral machine name as default.

### 2.4 Trash / Recycle Bin

**FM-110** Trash is a first-class VFS location with extra Details columns *Original Location* and *Date Deleted*, verbs *Restore*, *Delete permanently*, and folder-background verb *Empty Trash* (with the classic confirmation).
- macOS: send-to-trash via `NSFileManager.trashItem(at:resultingItemURL:)`; enumeration reads `~/.Trash` (and per-volume `.Trashes/<uid>`); original path recovered from the `.DS_Store`-independent put-back info is **not** publicly available, so we store our own sidecar map (`~/.Trash` inode → origin) for items we trashed, and show "unknown" origin for items trashed by Finder. Restore for unknown-origin items disabled. This asymmetry is accepted; Apple gives no API for put-back metadata.
- Linux: freedesktop.org Trash spec v1.0 (`~/.local/share/Trash/{files,info}` + per-volume `.Trash-<uid>`); `.trashinfo` files give us Path and DeletionDate natively.
- Windows: `IFileOperation` with `FOF_ALLOWUNDO` to trash; enumerate via shell item `::{645FF040-5081-101B-9F08-00AA002F954E}`; restore via the shell verb.

### 2.5 Archive browsing — DECISION

**FM-120** v1 ships **read-only ZIP browsing** (double-click a `.zip` navigates into it like a folder — Win2000's "Compressed Folders" behavior) via `System.IO.Compression.ZipArchive` behind `ZipProvider`, plus "Extract All…" wizard and Send To > Compressed Folder for creation. Copy-out works (archive → real folder through the ops engine); copy-*into* archives, and other formats (7z, tar.*, rar) are post-v1 via SharpCompress (MIT). Rationale: zip read is nearly free with the BCL, exercises the VFS abstraction early, and matches the era feature. Writing into archives complicates the operations engine (no partial-failure atomicity) for marginal value. Rejected: full archive RW in v1 (scope), no archives at all (misses an easy, high-signal VFS proof).

---

## 3. File operations engine

### 3.1 Job model

**FM-130** All mutations (copy, move, delete, trash, rename batches, extract) run as jobs in a per-user-session `FileOperationService` (in the main shell process, own thread pool lane — not the native helper; this is pure managed I/O). Jobs are FIFO-queued per *target volume* and run in parallel across volumes (serializing same-disk jobs avoids seek thrash and matches user intuition; parallel cross-volume keeps a USB copy from blocking a local one).

```csharp
public sealed class FileOpRequest
{
    public FileOpKind Kind;                 // Copy, Move, Delete, Trash, Extract
    public IReadOnlyList<VfsPath> Sources;
    public VfsPath? Target;
    public ConflictPolicy DefaultPolicy;    // Ask (default), OverwriteAll, SkipAll, RenameAll
}
public interface IFileOpJob
{
    IObservable<FileOpProgress> Progress;   // item path, items done/total, bytes done/total, speed
    Task<FileOpResult> Completion { get; }
    void Cancel();
    void RespondToConflict(ConflictResponse r);   // Overwrite / Skip / Rename / Cancel (+ ApplyToAll)
}
```

**FM-131** Jobs pre-scan (enumerate + size totals) before transfer, with a "Preparing to copy…" phase in the dialog; pre-scan is itself cancellable and capped — if a tree exceeds 250k items the job switches to indeterminate totals rather than stalling.

### 3.2 Progress dialog

**FM-132** Win2000-style modeless progress dialog: recreated flying-paper animation (theme-supplied frame strip, `05-theming.md`), "Copying…" caption, current file name, from → to line, progress bar (blocks style in Win2000 theme), time-remaining estimate (smoothed over a 5 s sliding window; shown only after 3 s and >10 s remaining, like the original's temperament but less comically wrong), Cancel button. One dialog per job; minimizing the dialog parks progress in the taskbar button (`07-shell-ux.md`).

### 3.3 Conflict resolution

**FM-133** Name collisions raise the classic "Confirm File Replace" dialog: both files' icon, size, modified date; buttons Yes / Yes to All / No / No to All / Cancel, plus a *Rename* option (auto "Copy of X" / "X (2)") which Win2000 lacked — deliberate ergonomic addition, present in all themes. Folder-merge conflicts recurse with "Yes to All" scoping to the whole job. Copy-onto-self produces "Copy of X" (Win2000 behavior); move-onto-self is a no-op.

### 3.4 Undo

**FM-134** Multi-level undo stack (depth 10, per shell session, not persisted). Recorded inverses: Move → move back; Copy → delete copies (to trash); Rename → rename back; Trash → restore; New Folder → trash. Permanent deletes and extracts are not undoable and do not push stack entries. `Edit > Undo` shows the verb ("Undo Move of 3 items"). Undo of a stale entry (source since modified/moved) fails soft with an explanatory dialog and pops the entry.

### 3.5 Cancellation & failure semantics

**FM-135** Cancel stops after the in-flight file completes or its partial target is deleted — never leaves a truncated file. Per-item errors (permission denied, path too long, disappeared source, disk full) raise a skippable error dialog (Retry / Skip / Skip All / Cancel); disk-full pre-check runs against pre-scan totals before transfer starts. A cross-volume *move* is copy-then-delete per item (verify length before deleting source); same-volume move uses `rename(2)`/`MoveFileEx` and is near-instant.

### 3.6 Edge cases

**FM-136** Long paths: Windows backend always uses `\\?\`-prefixed paths (no MAX_PATH ceiling); macOS/Linux limited only by `PATH_MAX` handling — enumerate with `openat`-relative operations in the native layer if we hit it (post-v1 hardening). Symlinks copy as links by default, with a job option to follow; cyclic-symlink protection via visited-inode set during pre-scan. macOS specifics: preserve extended attributes and resource forks by copying with `copyfile(3)` (`COPYFILE_ALL`) P/Invoked from C#, not naive stream copy — Finder-compat requires xattrs (tags, quarantine) to survive. Permission-denied on macOS due to TCC (e.g. `~/Documents` without Full Disk Access) is detected (`EPERM` + known TCC path) and routed to the permission-onboarding flow in `08-os-interop.md`, not shown as a generic error.

---

## 4. Change watching

**FM-140** One interface, three backends, uniform coalesced output:

```csharp
public interface IDirectoryWatcher : IDisposable
{
    // Non-recursive (per open folder view); tree pane watches expanded nodes only.
    IObservable<FsChangeBatch> Changes;   // batch: Created[], Deleted[], Modified[], Renamed[](old,new)
}
```

| Platform | Backend | Notes |
|---|---|---|
| macOS | FSEvents (`FSEventStreamCreate`, kFSEventStreamCreateFlagFileEvents) | Per-file events, ~0.3 s latency budget; runs in-process (stable API). `kFSEventStreamEventFlagMustScanSubDirs` triggers a folder rescan. |
| Linux | inotify (IN_CREATE\|DELETE\|MOVED_FROM\|MOVED_TO\|CLOSE_WRITE\|ATTRIB) | One wd per watched dir; on `ENOSPC` (fs.inotify.max_user_watches) degrade to polling and surface a one-time hint to raise the sysctl. |
| Windows | `ReadDirectoryChangesW` (non-recursive per view) | 64 KB buffer; overflow → rescan. |

**FM-141** Batching: raw events buffer 100 ms then flush as one `FsChangeBatch`; a batch touching > 30% of a large (>5k) folder's entries collapses to "rescan folder". Rename detection pairs MOVED_FROM/MOVED_TO by cookie (Linux) or old/new notifications (Windows); FSEvents renames pair by event id. Non-watchable providers (`zip`, unmounted `net`) return `null` and views fall back to refresh-on-focus + F5.

---

## 5. Drag & drop and clipboard

**FM-150** Internal DnD between views/tree/desktop uses Avalonia DnD with our own `FileDropPayload`; default effect follows Explorer rules: same volume → Move, cross volume → Copy, any → Link with Ctrl+Shift (Cmd+Option on macOS); modifier keys update the drag cursor (theme-drawn +copy badge). External DnD interops with the platform: publish `NSFilenamesPboardType`/`public.file-url` (macOS), `CF_HDROP` (Windows), `text/uri-list` (Linux) so drags into other apps work, and accept the same inbound.

**FM-151** Clipboard cut/copy publishes both native file formats (above) and an internal marker distinguishing cut vs copy (`Preferred DropEffect` on Windows; private pasteboard type elsewhere since macOS has no native "cut files" convention). Cut items render at 50% opacity until pasted or clipboard changes.

---

## 6. Icon & thumbnail pipeline

### 6.1 Two-layer resolution

**FM-160** Every `IVfsNode` exposes an `IconKey`: a semantic identifier resolved at render time:

```
IconKey = { SemanticId?, NativeRef?, Size }
SemanticId examples: "folder", "folder.open", "drive.fixed", "drive.cd", "drive.net",
  "computer", "trash.empty", "trash.full", "doc.generic", "doc.text", "doc.image",
  "app.generic", "zip", "link.overlay", "shared.overlay"
```

Resolution order:
1. **Theme layer**: active theme's icon pack (`05-theming.md`) maps SemanticId → recreated raster/vector asset (Win2000 pack: 16/32/48 px pixel-art recreations — never Microsoft's originals, per the IP constraint). File types map extension/UTI/MIME → SemanticId via a shipped table (~60 classes) so a `.txt` gets the recreated notepad-page icon.
2. **Native fallback**: unmapped types (odd apps, custom document icons, .app bundles / .exe with embedded icons) fetch the real platform icon: macOS `NSWorkspace.shared.icon(forFile:)` / `icon(for: UTType)`; Windows `SHGetFileInfo(SHGFI_ICON|SHGFI_SYSICONINDEX)` then `IImageList`; Linux GIO `g_file_info_get_icon()` resolved through the current icon theme (`GtkIconTheme` lookup done in the helper, or direct hicolor-spec lookup to avoid a GTK dependency — take the latter: parse `index.theme` ourselves, no GTK in-process).

**FM-161** Themes choose their fallback posture via a manifest flag: `nativeIconFallback: allow | pixelate | deny`. Win2000 theme uses `pixelate` — native icons are downscaled and quantized (posterize + optional 16-color remap shader) so a modern macOS icon doesn't shatter the period illusion. Win11 theme uses `allow`.

### 6.2 Overlays and states

**FM-162** Overlay badges (link/alias arrow, shared hand, un-synced cloud placeholder) composite at render time bottom-left (link) per Win2000 placement; theme supplies overlay assets. Cut state (50% alpha) and drop-target highlight are view effects, not icon variants.

### 6.3 Thumbnails

**FM-163** Thumbnails view and Properties preview use platform thumbnailers, executed in the **native helper process** (image decoders are crash- and exploit-prone; helper isolation per `01-architecture.md`):
- macOS: `QLThumbnailGenerator.shared.generateBestRepresentation(for:)` (QuickLookThumbnailing).
- Windows: `IShellItemImageFactory::GetImage` (covers IThumbnailProvider handlers).
- Linux: freedesktop thumbnail spec — reuse `~/.cache/thumbnails` when fresh, else invoke registered thumbnailers from `/usr/share/thumbnailers/*.thumbnailer`; built-in decoders (Skia) for common image types.

**FM-164** Shared disk cache at `<appdata>/Bevel/thumbcache/` keyed by SHA-1(canonical path + mtime + size + generator version), PNG payloads, 512 MB LRU cap. In-memory LRU of decoded bitmaps (2048 entries at current view size). Requests are priority-queued by viewport visibility and cancelled on scroll-past.

---

## 7. Search

**FM-170** F3 / Search toolbar button swaps the left pane to the Win2000 **Search bar** ("Search for Files or Folders": name contains, containing text, look-in combo, date/size/type advanced accordions). Results render in the normal item view with an added *In Folder* column; all verbs/DnD work on results.

**FM-171** v1 backend = **streaming walk + platform index assist**:
- Name search: parallel breadth-first VFS walk from the look-in root, glob/substring match, results stream in as found; cancellable; respects hidden-file setting.
- On macOS, if the scope is local-FS, run `NSMetadataQuery` (Spotlight) concurrently and merge (dedupe by path) — Spotlight returns big wins instantly, the walk guarantees completeness where Spotlight is stale or excluded. "Containing text" uses Spotlight `kMDItemTextContent` where available, walk-with-grep (bounded to files < 4 MB, first 200 hits) otherwise.
- Windows later: Windows Search via `SearchAPI`/OLE DB. Linux later: locate-db assist optional.
Rejected: building our own content index in v1 (large scope, duplicates the OS).

---

## 8. Performance

Targets on the reference machine (Apple Silicon M-class, local SSD), Details view:

| Metric | Target |
|---|---|
| Open folder, 1k entries → fully painted | < 80 ms |
| Open folder, 100k entries → first 200 rows painted | < 250 ms |
| Open folder, 100k entries → enumeration complete, sortable | < 2.5 s |
| Re-sort 100k loaded entries on any column | < 150 ms |
| Scroll 100k entries | 60 fps, no placeholder icons visible > 1 frame after settle |
| Memory per 100k-entry view | < 120 MB incl. icon cache share |

**FM-180** Mechanisms mandated: (1) streamed enumeration appended in 512-item chunks via dispatcher batching; (2) UI virtualization for all view modes (custom virtualizing wrap panel for icon modes — Avalonia only provides `VirtualizingStackPanel`, so the wrap variant is ours; flag this cost in 09-engineering-plan.md); (3) stat-on-demand — enumeration fetches name+kind first, size/date resolve lazily for visible rows, with full hydration continuing in the background for sortability; (4) sort/filter on a snapshot array off the UI thread, swap-in on completion; (5) icon/thumbnail resolution priority-queued by visibility (§6.3); (6) natural-order name comparer (numeric-aware, `CompareStringEx SORT_DIGITSASNUMBERS` semantics) implemented once in managed code with culture support, cached collation keys for the 100k re-sort target.

**FM-181** Type-ahead, rubber-band selection, and watcher batch application MUST all operate on the virtualized index space (no realized-container walks).

---

## 9. Out of scope for this chapter

Desktop icon layer reuses `FolderViewModel` + Large Icons view (spec in `07-shell-ux.md`). File-manager exposure over Apple Events / `tell application "Finder"` mapping tables live in `08-os-interop.md`. Theme asset formats in `05-theming.md`.

## Risks

1. **Virtualizing icon-grid panel** — Avalonia lacks a virtualizing wrap panel; ours must handle free placement (desktop mode) too. Medium-high effort, on the critical path for the 100k target. Mitigation: build it in milestone 1 with a synthetic 250k-entry test harness.
2. **Classic.Avalonia coverage gaps** — ToolBar/rebar, StatusBar, TreeView-with-lines, and in-place label edit styling likely need original work; underestimate here blows up the theming schedule (05-theming.md).
3. **macOS Trash put-back asymmetry** — no public API for Finder's put-back metadata; our sidecar approach means items trashed by other apps can't be restored to origin. Users may perceive this as a bug. Mitigation: clear "Original location unknown" UI.
4. **TCC permission friction** — first-run file browsing of `~/Documents`, `~/Desktop`, `~/Downloads` triggers per-folder TCC prompts (or requires Full Disk Access). A file manager that throws permission errors on its home turf looks broken; onboarding flow (08-os-interop.md) must land before public builds.
5. **xattr/resource-fork fidelity** — a copy engine that silently drops Finder tags, quarantine flags, or code-signing xattrs will corrupt user expectations and app bundles. `copyfile(3)` path must be validated early with codesigned .app copy tests.
6. **Watcher scale** — hundreds of expanded tree nodes each holding a watcher can exhaust inotify watches or FSEvents streams; the watch-only-expanded policy plus stream pooling needs load testing.
7. **Search expectation gap** — era-authentic UI with walk-based search may feel slow on huge trees despite Spotlight assist; risk of bad first impressions on network mounts (walks there must be explicitly throttled/confirmed).
8. **IP discipline for icons** — the recreated 16-color icon pack is a substantial art project (~150 assets incl. overlays and animation frames); slippage here blocks the default theme, and any accidental pixel-copying of Microsoft assets is a legal defect, not a polish issue.

## Open questions

1. ~~**Product owner call — extension hiding default (FM-011):**~~ **Resolved 2026-07-04:** default follows the host OS setting (mirror Finder/Explorer's own show-extensions preference), user toggle overrides either way. FM-011 updated.
2. ~~**"My Documents" mapping on macOS:**~~ **Resolved 2026-07-04:** the macOS "My Documents" node maps to `~` (the home folder), not `~/Documents` — friendlier for first run and what the onboarding flow depends on. FM-040 updated.
3. **Search pane vs. results window:** Win2000 used the in-window Search bar; do we also want a Finder-style Cmd+F scoped toolbar search on macOS for muscle-memory compat, or is that theme pollution?
4. **Trash for network/removable volumes on Linux/macOS edge cases:** when per-volume trash is unavailable (no `.Trashes`, FAT32), do we prompt "delete permanently?" (Finder behavior) or copy-to-home-trash (slow, surprising)? Leaning prompt-to-delete; confirm.
5. **Properties > Security tab depth for v1:** read-only ACL display everywhere, or full POSIX mode editing on macOS/Linux as specced (FM-090)? Editing is implemented-cheap but support-expensive.
6. **Undo persistence:** should the undo stack survive shell restarts (persisted journal), given the shell process is long-lived but crashes/updates happen? v1 says no; cheap to revisit.
7. **Free icon placement persistence format** (shared with 07-shell-ux.md): per-folder DB rows vs. sidecar files — DB is specced (FM-050) but the shell-UX chapter owner should co-sign since desktop icon layout uses the same store.
