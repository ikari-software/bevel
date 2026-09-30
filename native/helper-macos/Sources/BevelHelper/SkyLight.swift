import AppKit
import Darwin

// MARK: - SkyLight (private WindowServer client API)

/// The few private SkyLight entry points the window service needs, resolved at RUNTIME with `dlsym`.
///
/// Why dlsym and not `@_silgen_name` + a linker flag: these symbols are private and Apple may rename
/// or remove one in any OS update. A dlsym miss degrades exactly one feature (Space awareness /
/// per-window system focus — the callers have public-API fallbacks) instead of taking the whole
/// helper down at load time, and the package needs no private-framework linker settings.
///
/// Everything here is best-effort and side-effect free unless named `focusWindow`.
enum SkyLight {
    private typealias MainConnection = @convention(c) () -> Int32
    private typealias CopySpacesForWindows = @convention(c) (Int32, Int32, CFArray) -> Unmanaged<CFArray>?
    private typealias CopyManagedDisplaySpaces = @convention(c) (Int32) -> Unmanaged<CFArray>?
    private typealias SetFrontProcess = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, CGWindowID, UInt32) -> CGError
    private typealias PostEventRecord = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, UnsafeMutablePointer<UInt8>) -> CGError
    private typealias ProcessForPID = @convention(c) (pid_t, UnsafeMutablePointer<ProcessSerialNumber>) -> OSStatus

    private struct Symbols: @unchecked Sendable {
        let connection: Int32
        let copySpacesForWindows: CopySpacesForWindows?
        let copyManagedDisplaySpaces: CopyManagedDisplaySpaces?
        let setFrontProcess: SetFrontProcess?
        let postEventRecord: PostEventRecord?
        let processForPID: ProcessForPID?

        static func load() -> Symbols {
            let sky = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW)
            func sym<T>(_ handle: UnsafeMutableRawPointer?, _ name: String, as: T.Type) -> T? {
                guard let handle, let p = dlsym(handle, name) else { return nil }
                return unsafeBitCast(p, to: T.self)
            }
            let main = sym(sky, "CGSMainConnectionID", as: MainConnection.self)
            return Symbols(
                connection: main?() ?? 0,
                copySpacesForWindows: sym(sky, "CGSCopySpacesForWindows", as: CopySpacesForWindows.self),
                copyManagedDisplaySpaces: sym(sky, "CGSCopyManagedDisplaySpaces", as: CopyManagedDisplaySpaces.self),
                setFrontProcess: sym(sky, "_SLPSSetFrontProcessWithOptions", as: SetFrontProcess.self),
                postEventRecord: sym(sky, "SLPSPostEventRecordTo", as: PostEventRecord.self),
                // GetProcessForPID lives in ApplicationServices, not SkyLight. Load it explicitly:
                // dlopen(nil) only sees images the process happens to have loaded already.
                processForPID: sym(
                    dlopen("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", RTLD_NOW),
                    "GetProcessForPID", as: ProcessForPID.self)
            )
        }
    }

    private static let symbols = Symbols.load()

    /// The Spaces each display is CURRENTLY showing (one per display). Empty when unavailable.
    static func currentSpaceIDs() -> Set<Int> {
        guard let copy = symbols.copyManagedDisplaySpaces,
              let displays = copy(symbols.connection)?.takeRetainedValue() as? [[String: Any]]
        else { return [] }
        return Set(displays.compactMap { ($0["Current Space"] as? [String: Any])?["id64"] as? Int })
    }

    /// Every Space `window` belongs to (`[]` = unknown). 0x7 = all space kinds (user, fullscreen, system).
    static func spaceIDs(ofWindow window: CGWindowID) -> [Int] {
        guard let copy = symbols.copySpacesForWindows else { return [] }
        return (copy(symbols.connection, 0x7, [window] as CFArray)?.takeRetainedValue() as? [Int]) ?? []
    }

    /// Make `cgID` THE key window of `pid`, switching Spaces if it lives on another one.
    ///
    /// This is what the Dock does and what accessibility cannot: it addresses a window by its
    /// WindowServer id, so it works for apps that expose no AX tree (Kiro/VS Code-family Electron
    /// builds) and for windows on other Spaces (which AX never lists). Two steps, the same recipe
    /// other window switchers use: bring the owning process front *for that window id*, then post
    /// the pair of synthetic "window became key" records the app's event loop expects.
    ///
    /// Returns false when a symbol is missing or the process serial number can't be resolved —
    /// the caller then falls back to plain app activation.
    @discardableResult
    static func focusWindow(pid: pid_t, cgID: CGWindowID) -> Bool {
        guard let processForPID = symbols.processForPID,
              let setFront = symbols.setFrontProcess,
              let post = symbols.postEventRecord
        else { return false }

        var psn = ProcessSerialNumber()
        guard processForPID(pid, &psn) == noErr else { return false }

        let kCPSUserGenerated: UInt32 = 0x200
        guard setFront(&psn, cgID, kCPSUserGenerated) == .success else { return false }

        var record = [UInt8](repeating: 0, count: 0xf8)
        record[0x04] = 0xf8
        record[0x3a] = 0x10
        var id = cgID
        memcpy(&record[0x3c], &id, MemoryLayout<UInt32>.size)
        memset(&record[0x20], 0xff, 0x10)
        record[0x08] = 0x01   // key-window down
        _ = post(&psn, &record)
        record[0x08] = 0x02   // key-window up
        _ = post(&psn, &record)
        return true
    }
}

// MARK: - Spaces

/// Which Mission Control Spaces are on screen right now, captured once per snapshot pass.
///
/// The question this answers — "is this window on a Space I'm not looking at?" — is what tells a
/// LIVE window apart from the tombstone a just-closed window leaves in CGWindowList. Both are
/// off-screen and AX-less, but only the former sits on a Space no display is showing (a
/// tombstone stays on the Space it died on, which is on screen or was just left).
struct SpaceContext {
    let current: Set<Int>
    private let spacesOf: (CGWindowID) -> [Int]

    init(current: Set<Int>, spacesOf: @escaping (CGWindowID) -> [Int]) {
        self.current = current
        self.spacesOf = spacesOf
    }

    static func capture() -> SpaceContext {
        SpaceContext(current: SkyLight.currentSpaceIDs(), spacesOf: SkyLight.spaceIDs(ofWindow:))
    }

    /// True only when the window's Spaces are KNOWN and none of them is showing. Unknown (missing
    /// SkyLight symbols, empty answer) never claims "other Space": that would keep every ghost.
    func isOnOtherSpace(_ window: CGWindowID) -> Bool {
        guard !current.isEmpty else { return false }
        let spaces = spacesOf(window)
        return !spaces.isEmpty && current.isDisjoint(with: spaces)
    }
}
