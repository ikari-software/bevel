import AppKit
import ApplicationServices
import Foundation
import GRPCCore
import GRPCProtobuf

/// Menu-bar status-item mirroring — the "Ice technique" (docs/spec/02-macos-platform.md §5).
///
/// M3-A (this file) implements **discovery + limited mode (§5.5)**: it enumerates the menu-bar
/// status-item windows via `CGWindowListCopyWindowInfo` (status-item layer, menu-bar Y-band; Req
/// 5.1), keys each by `ownerPID:windowNumber` (Req 5.2), correlates it to its `NSRunningApplication`
/// for name + icon, and returns them in left-to-right menu-bar order. Icons are the owning app's
/// icon (non-live) — no Screen Recording is required. Live ScreenCaptureKit capture (§5.3) and
/// click-forwarding (§5.5) arrive in M3-B / M3-C.
///
/// macOS 26 (Tahoe) note: `CGWindowListCopyWindowInfo` reports many status items as owned by
/// Control Center rather than their real app (FB18327911), so owner attribution can be coarse here;
/// the self-test + attribution fallback live in M3-E.
final class TrayServiceImpl: RegistrableRPCService, @unchecked Sendable {
    let expectedKey: String
    /// The shell process that launched us; its own menu-bar presence (if any) is excluded.
    let parentPID: pid_t

    /// The status-item window level. Menu-bar extras sit at `kCGStatusWindowLevel` (25); the main
    /// menu bar itself is one below (24). We filter to this layer within the menu-bar Y-band.
    private let statusWindowLayer = 25

    /// App-icon PNG cache keyed by bundle id — the icon render is off the hot path (limited mode).
    private let iconCacheLock = NSLock()
    private var iconCache: [String: Data] = [:]

    private let debug = ProcessInfo.processInfo.environment["BEVEL_DEBUG_TRAY"] == "1"
    private func dbg(_ msg: @autoclosure () -> String) {
        guard debug else { return }
        FileHandle.standardError.write(Data(("[BEVEL-TRAY] " + msg() + "\n").utf8))
    }

    init(expectedKey: String, parentPID: pid_t = 0) {
        self.expectedKey = expectedKey
        self.parentPID = parentPID
    }

    // MARK: - RPC registration

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        let serviceName = "bevel.helper.v1.TrayService"

        // ── ListTrayItems ────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "ListTrayItems"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_ListTrayItemsRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_ListTrayItemsReply>(),
            handler: { [weak self] request, context in
                guard let self else { throw RPCError(code: .internalError, message: "TrayService deallocated") }
                try AuthInterceptor.authenticate(
                    request.metadata, expectedKey: self.expectedKey, expectedCapability: "tray")
                _ = try await ServerRequest(stream: request)
                var reply = Bevel_Helper_V1_ListTrayItemsReply()
                reply.items = self.enumerateTrayItems()
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )

        // ── Changes (server-streaming) ───────────────────────────────────
        // Emits a SNAPSHOT burst, then re-enumerates on a throttled poll and streams
        // ADDED/REMOVED/UPDATED deltas until the client disconnects. Each subscriber diffs against
        // its own last-sent set — there's normally one subscriber (the taskbar), so no shared state.
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Changes"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_TrayChangesRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_TrayChange>(),
            handler: { [weak self] request, context in
                guard let self else { throw RPCError(code: .internalError, message: "TrayService deallocated") }
                try AuthInterceptor.authenticate(
                    request.metadata, expectedKey: self.expectedKey, expectedCapability: "tray")
                _ = try await ServerRequest(stream: request)

                return StreamingServerResponse(of: Bevel_Helper_V1_TrayChange.self) { [weak self] writer in
                    guard let self else { return [:] }

                    // 1. Full snapshot.
                    var lastByID: [String: Bevel_Helper_V1_TrayItem] = [:]
                    for item in self.enumerateTrayItems() {
                        var change = Bevel_Helper_V1_TrayChange()
                        change.kind = .snapshot
                        change.item = item
                        try await writer.write(change)
                        lastByID[item.itemID] = item
                    }

                    // 2. Poll + diff until cancelled (client disconnect cancels the producer Task).
                    while !Task.isCancelled {
                        try await Task.sleep(nanoseconds: 2_000_000_000)
                        let current = self.enumerateTrayItems()
                        var currentByID: [String: Bevel_Helper_V1_TrayItem] = [:]
                        for item in current { currentByID[item.itemID] = item }

                        // Added / updated.
                        for item in current {
                            if let prev = lastByID[item.itemID] {
                                if prev != item {
                                    try await writer.write(self.change(.updated, item))
                                }
                            } else {
                                try await writer.write(self.change(.added, item))
                            }
                        }
                        // Removed.
                        for (id, item) in lastByID where currentByID[id] == nil {
                            try await writer.write(self.change(.removed, item))
                        }
                        lastByID = currentByID
                    }
                    return [:]
                }
            }
        )
    }

    private func change(_ kind: Bevel_Helper_V1_TrayChange.Kind, _ item: Bevel_Helper_V1_TrayItem)
        -> Bevel_Helper_V1_TrayChange {
        var c = Bevel_Helper_V1_TrayChange()
        c.kind = kind
        c.item = item
        return c
    }

    // MARK: - Enumeration

    /// Discovers the current menu-bar status items (Req 5.1/5.2). Left-to-right menu-bar order.
    func enumerateTrayItems() -> [Bevel_Helper_V1_TrayItem] {
        let ownPID = getpid()
        guard let windows = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else {
            return []
        }

        var found: [(x: CGFloat, item: Bevel_Helper_V1_TrayItem)] = []
        for w in windows {
            guard let layer = w[kCGWindowLayer as String] as? Int, layer == statusWindowLayer else { continue }
            guard let pid = w[kCGWindowOwnerPID as String] as? pid_t, pid != ownPID, pid != parentPID else { continue }
            guard let boundsDict = w[kCGWindowBounds as String] as? [String: Any],
                  let rect = CGRect(dictionaryRepresentation: boundsDict as CFDictionary) else { continue }

            // Menu-bar band: pinned near the top with a menu-bar-ish height; status items are narrow.
            guard rect.origin.y <= 40, rect.height >= 8, rect.height <= 40,
                  rect.width >= 8, rect.width <= 400 else { continue }

            let windowNumber = (w[kCGWindowNumber as String] as? Int) ?? 0
            let ownerName = (w[kCGWindowOwnerName as String] as? String) ?? ""
            let windowName = (w[kCGWindowName as String] as? String) ?? ""

            // On macOS 26, kCGWindowOwnerName is "Control Center" for every status item
            // (FB18327911); the real identity lives in kCGWindowName (a bundle id, an app path,
            // or a system-item name). Resolve display name + limited-mode icon from that.
            let identity = resolveItem(windowName: windowName, ownerPID: pid, ownerName: ownerName)

            var item = Bevel_Helper_V1_TrayItem()
            item.itemID = "\(pid):\(windowNumber)"
            item.ownerPid = pid
            item.ownerBundleID = identity.bundleID
            item.ownerName = identity.name
            item.tooltip = identity.name
            var pr = Bevel_Helper_V1_PixelRect()
            pr.x = Int32(rect.origin.x); pr.y = Int32(rect.origin.y)
            pr.width = Int32(rect.width); pr.height = Int32(rect.height)
            item.bounds = pr
            item.iconPng = identity.icon
            item.isLive = false   // limited mode (§5.5) until SCK capture (M3-B)
            found.append((rect.origin.x, item))
        }

        let sorted = found.sorted { $0.x < $1.x }.map { $0.item }
        dbg("enumerated \(sorted.count) status items: " + sorted.map { $0.ownerName }.joined(separator: ", "))
        return sorted
    }

    // MARK: - Identity resolution (macOS 26-aware, §5.2)

    /// Resolves a status item's display name + limited-mode icon from its `kCGWindowName` (the real
    /// identity on macOS 26, where the owner is always Control Center). Handles an app path, a
    /// resolvable bundle id, a sub-identifier whose base bundle resolves (e.g.
    /// `com.bjango.istatmenus.cpu` → `com.bjango.istatmenus`), and falls back to the owning process's
    /// icon + a friendly name for Apple/system modules (`WiFi`, `Sound`, `com.apple.menuextra.*`, …).
    private func resolveItem(windowName: String, ownerPID: pid_t, ownerName: String)
        -> (bundleID: String, name: String, icon: Data) {
        let ws = NSWorkspace.shared

        // 1. An app path, e.g. "/Applications/Parallels Toolbox.app".
        if windowName.hasPrefix("/"), windowName.hasSuffix(".app") {
            let name = ((windowName as NSString).lastPathComponent as NSString).deletingPathExtension
            return (windowName, name, pngFromIcon(ws.icon(forFile: windowName)))
        }

        // 2. A bundle id that resolves directly to an installed app.
        if windowName.contains("."), let url = ws.urlForApplication(withBundleIdentifier: windowName) {
            let name = (url.deletingPathExtension().lastPathComponent)
            return (windowName, name, cachedAppIconPNG(bundleID: windowName))
        }

        // 3. A sub-identifier whose base (first 3 dot-segments) bundle resolves — iStat Menus et al.
        if windowName.contains(".") {
            let segs = windowName.split(separator: ".")
            if segs.count >= 3 {
                let base = segs.prefix(3).joined(separator: ".")
                if let url = ws.urlForApplication(withBundleIdentifier: base) {
                    let app = url.deletingPathExtension().lastPathComponent
                    let leaf = segs.last.map(String.init) ?? windowName
                    return (base, "\(app) — \(leaf)", cachedAppIconPNG(bundleID: base))
                }
            }
        }

        // 4. System / generic module (WiFi, Sound, Clock, Item-0, com.apple.menuextra.*): keep the
        //    owning process's icon (Control Center on macOS 26) with a friendly name.
        let app = NSRunningApplication(processIdentifier: ownerPID)
        let icon = app?.bundleIdentifier.map { cachedAppIconPNG(bundleID: $0) } ?? Data()
        return (app?.bundleIdentifier ?? "", friendlyName(windowName.isEmpty ? ownerName : windowName), icon)
    }

    /// Humanizes a raw window/system name: strips the `com.apple.menuextra.` prefix, drops a trailing
    /// `-0` suffix, and leaves already-friendly names (WiFi, Sound, Clock) as-is.
    private func friendlyName(_ raw: String) -> String {
        var s = raw
        if let r = s.range(of: "com.apple.menuextra.") { s.removeSubrange(r) }
        if s.hasSuffix("-0") { s = String(s.dropLast(2)) }
        if s.isEmpty { return "Menu item" }
        return s.prefix(1).uppercased() + s.dropFirst()
    }

    /// Test seam for `friendlyName`.
    func friendlyNameForTest(_ raw: String) -> String { friendlyName(raw) }

    // MARK: - Icons (limited mode)

    private func cachedAppIconPNG(bundleID: String) -> Data {
        if bundleID.isEmpty { return Data() }
        iconCacheLock.lock()
        if let cached = iconCache[bundleID] { iconCacheLock.unlock(); return cached }
        iconCacheLock.unlock()

        let png = appIconPNG(bundleID: bundleID)
        iconCacheLock.lock(); iconCache[bundleID] = png; iconCacheLock.unlock()
        return png
    }

    /// The app with this bundle id's icon as a 16×16 PNG, or empty if it can't be resolved.
    private func appIconPNG(bundleID: String) -> Data {
        guard let appPath = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)?.path else {
            return Data()
        }
        return pngFromIcon(NSWorkspace.shared.icon(forFile: appPath))
    }

    /// Renders an NSImage into a fixed 16×16 PNG (mirrors WindowServiceImpl's sizing so the reply
    /// stays small and well under gRPC's message limit — tray icons never need more).
    private func pngFromIcon(_ icon: NSImage) -> Data {
        let target = NSSize(width: 16, height: 16)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 16, pixelsHigh: 16,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else {
            return Data()
        }
        rep.size = target
        guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return Data() }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        icon.draw(in: NSRect(origin: .zero, size: target), from: .zero, operation: .copy, fraction: 1.0)
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:]) ?? Data()
    }
}
