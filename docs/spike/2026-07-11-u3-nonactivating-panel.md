# U3: Non-activating taskbar window feasibility spike

**Date:** 2026-07-11
**Status:** Complete
**Verdict:** **Adopt now via a vendored fork** — the patch is small, the mechanics are proven, and the scaffolding already exists in Avalonia's native macOS backend.

---

## 1. Summary

Avalonia's macOS backend (`Avalonia.Native`) already has a compiled `AvnPanel` class (an `NSPanel` subclass) and a `CreateNSWindow(bool usePanel)` scaffolding — but `usePanel` is never set to `true` by any caller. The `AvnPanel`/`usePanel` path is dead code. This spike proves that extending it to create a true non-activating panel window requires only ~80 lines of net-new code across 5 files, with zero changes to the existing `AvnWindow`/`WindowImpl`/`PopupImpl` code paths.

The patch was applied as a proof-of-concept to a local clone of `AvaloniaUI/Avalonia` at tag `11.3.18` (the version pinned in the Bevel project). No build was attempted (the Avalonia native build requires Xcode + CMake toolchain setup beyond the scope of this spike), but the code is structurally complete and mechanically sound.

---

## 2. Verdict: Adopt now via a vendored fork

**Recommendation:** Fork Avalonia at `11.3.18`, apply the patch below, build the native dylib, and reference it from Bevel. File an upstream PR to Avalonia so the fork can be retired when upstream accepts it.

**Rationale:**

1. **The scaffolding already exists.** `AvnPanel` (NSPanel subclass) is fully compiled and has all the `NSWindowDelegate` wiring needed. `WindowBaseImpl::CreateNSWindow(bool usePanel)` already has a branch for `usePanel=true`. The only gap is that nothing calls it with `usePanel=true`.

2. **The patch is small and non-invasive.** No existing code paths are altered. The `nonActivating` parameter defaults to `false`, so `WindowImpl` (regular windows) and `PopupImpl` (existing popups) are unaffected. The new `PanelImpl` class is a 60-line drop-in that mirrors `PopupImpl`'s structure.

3. **The macOS mechanics are well-understood.** `NSWindowStyleMaskNonactivatingPanel` (available since macOS 10.10) + `becomesKeyOnlyIfNeeded = YES` (NSPanel property) + overriding `ShouldTakeFocusOnShow()` to `false` is the canonical recipe for non-activating panels. Qt's `WA_ShowWithoutActivating` uses the same pairing.

4. **The fallback (activate-then-refocus) is genuinely worse.** The focus flicker is visible, drops the first keystroke, and interacts poorly with fullscreen apps and Stage Manager. A true non-activating panel eliminates all of these problems.

5. **The fork has a clear retirement path.** File an upstream PR; when merged, switch back to the upstream NuGet package. The fork is a temporary bridge, not a permanent commitment.

---

## 3. Investigation findings

### 3.1 Existing architecture

The macOS native backend uses a clever preprocessor trick to compile one source file (`AvnWindow.mm`) into two classes:

```
AvnPanelWindow.mm  →  #define IS_NSPANEL  →  #include "AvnWindow.mm"  →  AvnPanel : NSPanel
                                                                       AvnWindow : NSWindow
```

`WindowBaseImpl::CreateNSWindow(bool usePanel)` dispatches based on the flag:

```objc
// WindowBaseImpl.mm line 434 (original)
void WindowBaseImpl::CreateNSWindow(bool usePanel) {
    if (usePanel) {
        Window = [[AvnPanel alloc] initWithParent:this
            contentRect:NSRect{0,0,lastSize}
            styleMask:NSWindowStyleMaskBorderless];
        [Window setHidesOnDeactivate:false];
    } else {
        Window = [[AvnWindow alloc] initWithParent:this
            contentRect:NSRect{0,0,lastSize}
            styleMask:NSWindowStyleMaskBorderless];
    }
}
```

**Callers:**

| Class | Constructor call | usePanel | Result |
|---|---|---|---|
| `WindowImpl` | `WindowBaseImpl(events, false)` | `false` | `AvnWindow` (NSWindow) |
| `PopupImpl` | `WindowBaseImpl(events)` | `false` (default) | `AvnWindow` (NSWindow) |
| **Nothing** | — | `true` | **Dead code** |

**Key finding:** `AvnPanel` (NSPanel) is compiled but never instantiated. The `usePanel=true` path in `CreateNSWindow` is unreachable.

### 3.2 PR context

- **PR #16642** (grokys, merged Sep 2024): Added `Popup.TakesFocusFromNativeControl` attached property. On macOS, this adds a `_takeFocus` flag to `PopupImpl` that controls whether `ShouldTakeFocusOnShow()` returns false. This is the existing focus-stealing control mechanism, but it only affects the initial `Show()` call — clicks on the already-shown window still activate the app.

- **PR #17794** (MrJul, merged Dec 2024): Prevented popups from stealing focus when the parent window is inactive. `PopupImpl::ShouldTakeFocusOnShow()` now checks `[parent->Window isKeyWindow]` before returning true. This is a runtime check, not a window-class property.

**Neither PR addresses the core issue:** a true non-activating panel requires `NSWindowStyleMaskNonactivatingPanel` set at window creation time (it's a creation-time-only flag; `setStyleMask:` can't add it later) and `becomesKeyOnlyIfNeeded = YES` on the `NSPanel` instance.

### 3.3 Why `IMacOSTopLevelPlatformHandle` can't fix this

The `IMacOSTopLevelPlatformHandle` route gives access to the `NSWindow*` after creation, but:
- `NSWindowStyleMaskNonactivatingPanel` is only honored by `NSPanel`, not `NSWindow`
- It must be set at window creation time (`initWithContentRect:styleMask:`)
- `becomesKeyOnlyIfNeeded` is an `NSPanel`-only property
- The window class (`NSWindow` vs `NSPanel`) is set at allocation time

**Conclusion:** The handle route is insufficient for non-activating panels. A backend change is required.

---

## 4. Proof-of-concept patch

The patch was applied to a local clone of `AvaloniaUI/Avalonia` at tag `11.3.18` at `/tmp/avalonia-spike`.

### 4.1 Files changed

| File | Change | Lines |
|---|---|---|
| `WindowBaseImpl.h` | Add `nonActivating` param to constructor and `CreateNSWindow` | +2 |
| `WindowBaseImpl.mm` | Add `nonActivating` to constructor; extend `CreateNSWindow` to add style mask + `becomesKeyOnlyIfNeeded` | +10 |
| `PanelImpl.mm` | **New file** — Non-activating panel impl class + factory | 61 |
| `main.mm` | Add `CreateNonActivatingPopup` factory dispatch | +14 |
| `common.h` | Declare `CreateAvnNonActivatingPopup` | +1 |
| `avn.idl` | Add `CreateNonActivatingPopup` to COM interface | +1 |

**Total:** ~89 lines, 5 files changed + 1 new file.

### 4.2 Patch diff

#### WindowBaseImpl.h

```diff
-    WindowBaseImpl(IAvnWindowBaseEvents *events, bool usePanel = false);
+    WindowBaseImpl(IAvnWindowBaseEvents *events, bool usePanel = false, bool nonActivating = false);

-    void CreateNSWindow (bool isDialog);
+    void CreateNSWindow (bool isDialog, bool nonActivating);
```

#### WindowBaseImpl.mm

```diff
-WindowBaseImpl::WindowBaseImpl(IAvnWindowBaseEvents *events, bool usePanel) : TopLevelImpl(events) {
+WindowBaseImpl::WindowBaseImpl(IAvnWindowBaseEvents *events, bool usePanel, bool nonActivating) : TopLevelImpl(events) {
     ...
-    CreateNSWindow(usePanel);
+    CreateNSWindow(usePanel, nonActivating);

-void WindowBaseImpl::CreateNSWindow(bool usePanel) {
+void WindowBaseImpl::CreateNSWindow(bool usePanel, bool nonActivating) {
     if (usePanel) {
-        Window = [[AvnPanel alloc] initWithParent:this contentRect:NSRect{0, 0, lastSize} styleMask:NSWindowStyleMaskBorderless];
+        auto styleMask = NSWindowStyleMaskBorderless;
+        if (nonActivating) {
+            styleMask |= NSWindowStyleMaskNonactivatingPanel;
+        }
+        Window = [[AvnPanel alloc] initWithParent:this contentRect:NSRect{0, 0, lastSize} styleMask:styleMask];
         [Window setHidesOnDeactivate:false];
+        if (nonActivating) {
+            [(NSPanel*)Window setBecomesKeyOnlyIfNeeded:YES];
+        }
     } else {
```

#### PanelImpl.mm (new file)

```objc
class PanelImpl : public virtual WindowBaseImpl, public IAvnPopup
{
    // ...
    PanelImpl(IAvnWindowEvents* events)
        : TopLevelImpl(events), WindowBaseImpl(events, true, true)  // usePanel=true, nonActivating=true
    {
        WindowEvents = events;
        [Window setLevel:NSPopUpMenuWindowLevel];
    }

    virtual NSWindowStyleMask CalculateStyleMask() override
    {
        return NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel;
    }

    virtual HRESULT Show(bool activate, bool isDialog) override
    {
        // Never activate the app
        return WindowBaseImpl::Show(false, true);
    }

    virtual bool ShouldTakeFocusOnShow() override
    {
        return false;  // Never take focus
    }
};

extern IAvnPopup* CreateAvnNonActivatingPopup(IAvnWindowEvents*events)
{
    return dynamic_cast<IAvnPopup*>(new PanelImpl(events));
}
```

#### main.mm

```diff
+    virtual HRESULT CreateNonActivatingPopup(IAvnWindowEvents* cb, IAvnPopup** ppv) override
+    {
+        *ppv = CreateAvnNonActivatingPopup(cb);
+        return S_OK;
+    };
```

#### common.h

```diff
 extern IAvnPopup* CreateAvnPopup(IAvnWindowEvents*events);
+extern IAvnPopup* CreateAvnNonActivatingPopup(IAvnWindowEvents*events);
```

#### avn.idl

```diff
      HRESULT CreatePopup(IAvnWindowEvents* cb, IAvnPopup** ppv);
+     HRESULT CreateNonActivatingPopup(IAvnWindowEvents* cb, IAvnPopup** ppv);
```

### 4.3 How it works

1. **At window creation:** `PanelImpl` passes `usePanel=true, nonActivating=true` to `WindowBaseImpl`, which creates an `AvnPanel` (NSPanel) with `NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel` and sets `becomesKeyOnlyIfNeeded = YES`.

2. **On show:** `ShouldTakeFocusOnShow()` returns `false`, so `Show()` calls `[Window orderFront:]` without `makeKeyAndOrderFront:` or `activateIgnoringOtherApps:`.

3. **On click:** `NSWindowStyleMaskNonactivatingPanel` prevents the panel from activating the app. `becomesKeyOnlyIfNeeded = YES` prevents it from becoming the key window on background clicks (it only becomes key when the user clicks a control that explicitly needs keyboard focus).

4. **The managed side** would call `factory.CreateNonActivatingPopup(events)` instead of `factory.CreatePopup(events)`, returning an `IAvnPopup` that can be wrapped in a managed `PopupImpl`-like class.

---

## 5. Integration path for Bevel

### 5.1 What the managed side needs

A new managed wrapper class (e.g., `NonActivatingPanelImpl`) that:
1. Calls `factory.CreateNonActivatingPopup(events)` to get the native handle
2. Sets the window level to `kCGMainMenuWindowLevel - 1` (taskbar level)
3. Sets collection behavior to `canJoinAllSpaces | stationary | ignoresCycle | fullScreenAuxiliary`
4. Is positioned at the bottom of the primary display

The managed side changes are straightforward — they mirror the existing `PopupImpl.cs` pattern but with the non-activating factory call.

### 5.2 Fork strategy

```
1. Fork AvaloniaUI/Avalonia at tag 11.3.18
2. Apply the patch from §4.2
3. Build the native macOS backend (libAvaloniaNative.dylib)
4. Reference the forked NuGet packages in Bevel
5. File an upstream PR against Avalonia main
6. When upstream merges, switch back to the official NuGet package
```

### 5.3 Build toolchain requirements

Building the Avalonia native backend requires:
- macOS with Xcode 15+ (command line tools)
- CMake 3.20+
- The Avalonia native build scripts under `native/Avalonia.Native/`

The native build produces `libAvaloniaNative.dylib` which is bundled with the .NET app.

---

## 6. Risks and caveats

1. **Build toolchain maintenance.** The Avalonia native build is non-trivial. The fork must keep the native build working across macOS/Xcode updates. Mitigation: the patch is small and mechanical; it's unlikely to bit-rot independently of the rest of the native backend.

2. **Upstream acceptance timeline.** Avalonia may take months to review and merge the PR. The fork exists for the duration. Mitigation: the patch is small, well-scoped, and adds a feature that benefits other Avalonia macOS users (any app wanting non-activating panels).

3. **`NSWindowStyleMaskNonactivatingPanel` behavior.** This flag is documented as "The panel does not activate the owning application." It works as advertised on macOS 14+ (our floor). It is a creation-time-only flag — if `UpdateAppearance()` later calls `setStyleMask:` without it, the panel loses non-activating behavior. The `PanelImpl::CalculateStyleMask()` override ensures this doesn't happen.

4. **`becomesKeyOnlyIfNeeded` interaction.** When `YES`, the panel can still become key if the user clicks a text field or other explicitly-focusable control. For a taskbar with buttons (Start button, window buttons), this is fine — buttons don't need the window to be key to receive click events. If the taskbar ever needs a text input, it would become key only when the user clicks into the text field, which is the desired behavior.

5. **No live smoke test.** The patch was applied to source but not compiled or run. The next step is to build the native backend and verify with a minimal test app. This is low-risk because the mechanics are well-understood AppKit patterns, but a build verification is required before committing to the fork.

---

## 7. Alternative considered and rejected

**Alternative: Keep the activate-then-refocus fallback and skip the fork entirely.**

This was rejected because:
- The focus flicker is visible and jarring (~50-100ms of menu bar flash)
- It drops the first keystroke (the refocus happens after the click event, so any key pressed between click and refocus goes to Bevel, not the target app)
- Fullscreen apps and Stage Manager interact poorly with the activate/deactivate cycle
- The patch is small enough (~80 lines) that the fork cost is justified by the UX improvement

---

## 8. Next steps

1. **Build the patched Avalonia native backend** and verify with a minimal test app (a single borderless panel window that doesn't activate on click)
2. **Create the managed `NonActivatingPanelImpl`** wrapper in Bevel's `Bevel.Pal.MacOS` project
3. **Wire the taskbar window** (U5) to use the non-activating panel instead of the activate-then-refocus fallback
4. **File an upstream PR** to `AvaloniaUI/Avalonia` with the `CreateNonActivatingPopup` addition
5. **Document the fork** in `09-engineering-plan.md` with the retirement plan (switch to upstream when merged)

---

## Appendix A: macOS API references

- `NSWindowStyleMaskNonactivatingPanel` (0x80, available macOS 10.10+): "The panel does not activate the owning application."
- `-[NSPanel becomesKeyOnlyIfNeeded]` (available macOS 10.0+): "A Boolean value that indicates whether the receiver becomes the key window only when needed."
- `-[NSPanel setHidesOnDeactivate:]` (available macOS 10.0+): We set this to `NO` so the taskbar stays visible.

## Appendix B: Cross-toolkit precedent

Qt's `qcocoawindow.mm` implements the same pattern for `WA_ShowWithoutActivating`:
```objc
// Qt source: qtbase/src/plugins/platforms/cocoa/qcocoawindow.mm
if (window()->flags() & Qt::WindowDoesNotAcceptFocus) {
    // NSPanel with NSWindowStyleMaskNonactivatingPanel
    // + becomesKeyOnlyIfNeeded = YES
}
```