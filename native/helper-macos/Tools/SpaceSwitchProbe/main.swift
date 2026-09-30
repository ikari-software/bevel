import AppKit
import CoreGraphics
import Darwin
import Foundation

// Minimal SkyLight + CG probe: measure current Spaces, find Jump Desktop windows,
// call focusWindow, measure again. No AX. Run from any TCC identity.

private enum SL {
    typealias MainConnection = @convention(c) () -> Int32
    typealias CopySpacesForWindows = @convention(c) (Int32, Int32, CFArray) -> Unmanaged<CFArray>?
    typealias CopyManagedDisplaySpaces = @convention(c) (Int32) -> Unmanaged<CFArray>?
    typealias SetFrontProcess = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, CGWindowID, UInt32) -> CGError
    typealias PostEventRecord = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, UnsafeMutablePointer<UInt8>) -> CGError
    typealias ProcessForPID = @convention(c) (pid_t, UnsafeMutablePointer<ProcessSerialNumber>) -> OSStatus

    static let sky = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW)
    static let asSvc = dlopen("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", RTLD_NOW)
    static func sym<T>(_ h: UnsafeMutableRawPointer?, _ name: String, as: T.Type) -> T? {
        guard let h, let p = dlsym(h, name) else { return nil }
        return unsafeBitCast(p, to: T.self)
    }

    static let connection = sym(sky, "CGSMainConnectionID", as: MainConnection.self)?() ?? 0
    static let copySpaces = sym(sky, "CGSCopySpacesForWindows", as: CopySpacesForWindows.self)
    static let copyDisplays = sym(sky, "CGSCopyManagedDisplaySpaces", as: CopyManagedDisplaySpaces.self)
    static let setFront = sym(sky, "_SLPSSetFrontProcessWithOptions", as: SetFrontProcess.self)
    static let postEvent = sym(sky, "SLPSPostEventRecordTo", as: PostEventRecord.self)
    static let processForPID = sym(asSvc, "GetProcessForPID", as: ProcessForPID.self)
}

func currentSpaceIDs() -> Set<Int> {
    guard let copyDisplays = SL.copyDisplays,
          let displays = copyDisplays(SL.connection)?.takeRetainedValue() as? [[String: Any]]
    else { return [] }
    return Set(displays.compactMap { ($0["Current Space"] as? [String: Any])?["id64"] as? Int })
}

func spaceIDs(of window: CGWindowID) -> [Int] {
    guard let copySpaces = SL.copySpaces else { return [] }
    return (copySpaces(SL.connection, 0x7, [window] as CFArray)?.takeRetainedValue() as? [Int]) ?? []
}

@discardableResult
func focusWindow(pid: pid_t, cgID: CGWindowID) -> Bool {
    guard let processForPID = SL.processForPID, let setFront = SL.setFront, let postEvent = SL.postEvent else {
        fputs("SkyLight symbols missing\n", stderr)
        return false
    }
    var psn = ProcessSerialNumber()
    guard processForPID(pid, &psn) == noErr else {
        fputs("GetProcessForPID failed for \(pid)\n", stderr)
        return false
    }
    let kCPSUserGenerated: UInt32 = 0x200
    let rc = setFront(&psn, cgID, kCPSUserGenerated)
    guard rc == .success else {
        fputs("_SLPSSetFrontProcessWithOptions rc=\(rc.rawValue)\n", stderr)
        return false
    }
    var record = [UInt8](repeating: 0, count: 0xf8)
    record[0x04] = 0xf8
    record[0x3a] = 0x10
    var id = cgID
    memcpy(&record[0x3c], &id, MemoryLayout<UInt32>.size)
    memset(&record[0x20], 0xff, 0x10)
    record[0x08] = 0x01
    _ = postEvent(&psn, &record)
    record[0x08] = 0x02
    _ = postEvent(&psn, &record)
    return true
}

struct Win {
    let id: CGWindowID
    let pid: pid_t
    let owner: String
    let title: String
    let onScreen: Bool
    let layer: Int
}

func listWindows(ownerContains: String) -> [Win] {
    let info = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] ?? []
    return info.compactMap { e in
        let owner = e[kCGWindowOwnerName as String] as? String ?? ""
        guard owner.localizedCaseInsensitiveContains(ownerContains) else { return nil }
        let layer = e[kCGWindowLayer as String] as? Int ?? -1
        guard layer == 0 else { return nil }
        return Win(
            id: e[kCGWindowNumber as String] as? CGWindowID ?? 0,
            pid: e[kCGWindowOwnerPID as String] as? pid_t ?? 0,
            owner: owner,
            title: e[kCGWindowName as String] as? String ?? "",
            onScreen: e[kCGWindowIsOnscreen as String] as? Bool ?? false,
            layer: layer
        )
    }
}

var positional: [String] = []
var doFocus = false
var cgOverride: CGWindowID?
var ai = 1
while ai < CommandLine.arguments.count {
    let a = CommandLine.arguments[ai]
    if a == "--focus" {
        doFocus = true
        ai += 1
    } else if a == "--cg" {
        ai += 1
        guard ai < CommandLine.arguments.count, let v = UInt32(CommandLine.arguments[ai]) else {
            fputs("--cg needs a window id\n", stderr)
            exit(2)
        }
        cgOverride = CGWindowID(v)
        ai += 1
    } else {
        positional.append(a)
        ai += 1
    }
}
let filter = positional.first ?? "Jump Desktop"
let targetTitle = positional.count > 1 ? positional[1] : nil

print("symbols: conn=\(SL.connection) copySpaces=\(SL.copySpaces != nil) copyDisplays=\(SL.copyDisplays != nil) setFront=\(SL.setFront != nil) post=\(SL.postEvent != nil) psn=\(SL.processForPID != nil)")

let before = currentSpaceIDs()
print("currentSpaces BEFORE: \(before.sorted())")

var wins = listWindows(ownerContains: filter)
if let targetTitle {
    wins = wins.filter { $0.title == targetTitle }
}
if let cgOverride {
    wins = wins.filter { $0.id == cgOverride }
    if wins.isEmpty {
        // Titles are often blank without Screen Recording; resolve by id alone.
        let info = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] ?? []
        if let e = info.first(where: { ($0[kCGWindowNumber as String] as? CGWindowID) == cgOverride }),
           let pid = e[kCGWindowOwnerPID as String] as? pid_t {
            wins = [Win(
                id: cgOverride,
                pid: pid,
                owner: e[kCGWindowOwnerName as String] as? String ?? "",
                title: e[kCGWindowName as String] as? String ?? "",
                onScreen: e[kCGWindowIsOnscreen as String] as? Bool ?? false,
                layer: e[kCGWindowLayer as String] as? Int ?? 0
            )]
        }
    }
}

for w in wins {
    let spaces = spaceIDs(of: w.id)
    let other = !before.isEmpty && !spaces.isEmpty && before.isDisjoint(with: Set(spaces))
    print("win cg=\(w.id) pid=\(w.pid) onScreen=\(w.onScreen) title='\(w.title)' spaces=\(spaces) otherSpace=\(other)")
}

guard doFocus else {
    print("(pass --focus [and optional title / --cg N] to activate)")
    exit(0)
}

guard let target = wins.first else {
    fputs("no matching window\n", stderr)
    exit(2)
}

print("focusing cg=\(target.id) pid=\(target.pid) …")
let ok = focusWindow(pid: target.pid, cgID: target.id)
print("focusWindow returned \(ok)")

// Give Mission Control a beat to settle.
Thread.sleep(forTimeInterval: 0.6)

let after = currentSpaceIDs()
print("currentSpaces AFTER:  \(after.sorted())")
let winSpaces = Set(spaceIDs(of: target.id))
let switched = !winSpaces.isEmpty && !after.isDisjoint(with: winSpaces)
print("window spaces now: \(winSpaces.sorted())")
print("RESULT: \(switched ? "SPACE SWITCHED (window's Space is now current on a display)" : "NO SWITCH (window's Space still not current)")")
exit(switched ? 0 : 1)
