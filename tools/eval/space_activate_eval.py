#!/usr/bin/env python3
"""Measure Bevel other-Space activate paint + latency (ce-optimize harness).

Outputs a single JSON object on stdout with all gate + diagnostic keys from
spec bevel-runtime-space-activate. Safe to run without Jump: paint metrics
become 0 and activate_rpc_ok_rate stays 1.0 (no samples) only when
SPACE_ACTIVATE_REQUIRE_JUMP=1 is unset — then we still require helper tests.

Env:
  SPACE_ACTIVATE_SKIP_SWIFT_TEST=1  — skip swift test (faster dry run)
  SPACE_ACTIVATE_REQUIRE_JUMP=1     — fail gates if no Jump other-Space pair
  BEVEL_RESTART_LOG                 — override restart log path
"""

from __future__ import annotations

import json
import os
import re
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "native" / "helper-macos"
LEFT_HINT = "Jump Desktop"


def run(cmd: list[str], cwd: Path | None = None, timeout: int = 120) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        cmd,
        cwd=str(cwd) if cwd else None,
        text=True,
        capture_output=True,
        timeout=timeout,
    )


def helper_tests_passed() -> int:
    if os.environ.get("SPACE_ACTIVATE_SKIP_SWIFT_TEST") == "1":
        return 1
    r = run(["swift", "test"], cwd=HELPER, timeout=180)
    return 1 if r.returncode == 0 else 0


def windowserver_rss_mb() -> float:
    r = run(["ps", "-axo", "rss,comm"])
    total = 0
    for line in r.stdout.splitlines():
        parts = line.split(None, 1)
        if len(parts) != 2:
            continue
        if "WindowServer" in parts[1] and "WindowServer" == Path(parts[1]).name:
            try:
                total += int(parts[0])
            except ValueError:
                pass
    return round(total / 1024.0, 1)


def restart_log_path() -> Path:
    if p := os.environ.get("BEVEL_RESTART_LOG"):
        return Path(p)
    tmp = os.environ.get("TMPDIR", "/tmp")
    return Path(tmp) / "bevel-restart.log"


ACTIVATE_RE = re.compile(
    r"\[(\d{2}:\d{2}:\d{2}\.\d+)\].*activate cg=(\d+) pid=(\d+) otherSpace=(true|false)"
)
SWITCH_RE = re.compile(
    r"\[(\d{2}:\d{2}:\d{2}\.\d+)\].*switchSpace → (?:ok=)?(true|false)"
)
FOCUS_WS_RE = re.compile(
    r"\[(\d{2}:\d{2}:\d{2}\.\d+)\].*focusViaWindowServer → (true|false)"
)
ACTIVATE_FOCUS_RE = re.compile(
    r"\[(\d{2}:\d{2}:\d{2}\.\d+)\].*activate-focus pid=(\d+) cg=(\d+).*Jump Desktop"
)


def parse_ts(ts: str) -> float:
    h, m, rest = ts.split(":")
    s, ms = rest.split(".")
    return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000.0


def analyze_restart_log(path: Path, lookback: int = 40) -> dict:
    """Parse recent otherSpace activates from bevel-restart.log.

    The restart log can be multi-GB of drop/keep spam; never scan the whole file.
    Extract only activate/switch/focus lines via ripgrep.
    """
    empty = {
        "log_samples": 0,
        "activate_rpc_ok_rate": 1.0,
        "sibling_steal_rate": 0.0,
        "activate_p50_ms": 0.0,
        "activate_p95_ms": 0.0,
        "switch_space_ms_p50": 0.0,
        "focus_settle_ms_p50": 0.0,
        "currents_match_rate": 0.0,
    }
    if not path.exists():
        return empty

    pat = r"activate cg=|switchSpace →|focusViaWindowServer →|activate-focus pid="
    r = run(["rg", "-N", pat, str(path)], timeout=60)
    lines = r.stdout.splitlines()
    if not lines:
        return empty

    events: list[dict] = []
    i = 0
    while i < len(lines):
        m = ACTIVATE_RE.search(lines[i])
        if not m or m.group(4) != "true":
            i += 1
            continue
        t0 = parse_ts(m.group(1))
        cg = int(m.group(2))
        pid = int(m.group(3))
        block = {"t0": t0, "cg": cg, "pid": pid, "ok": False, "steal": False,
                 "switch_ms": None, "focus_ms": None, "to_space": None}
        for j in range(i + 1, min(i + 40, len(lines))):
            if ACTIVATE_RE.search(lines[j]):
                break
            sm = SWITCH_RE.search(lines[j])
            if sm and block["switch_ms"] is None:
                block["ok"] = sm.group(2) == "true"
                block["switch_ms"] = (parse_ts(sm.group(1)) - t0) * 1000
            # Prefer newer format "N->M" if present on same line
            arrow = re.search(r"(\d+)->(\d+)", lines[j])
            if arrow and block["to_space"] is None:
                block["to_space"] = int(arrow.group(2))
            af = ACTIVATE_FOCUS_RE.search(lines[j])
            if af and int(af.group(2)) == pid and block["focus_ms"] is None:
                # Only count steals that fire DURING the activate ladder (before focusViaWindowServer).
                if int(af.group(3)) != cg:
                    block["steal"] = True
            fm = FOCUS_WS_RE.search(lines[j])
            if fm and block["focus_ms"] is None:
                block["focus_ms"] = (parse_ts(fm.group(1)) - t0) * 1000
                if fm.group(2) != "true":
                    block["ok"] = False
                break  # end of ladder companion scan
        events.append(block)
        i += 1

    events = events[-lookback:]
    if not events:
        return empty

    oks = [1.0 if e["ok"] else 0.0 for e in events]
    steals = [1.0 if e["steal"] else 0.0 for e in events]
    act_ms = [e["focus_ms"] for e in events if e["focus_ms"] is not None]
    sw_ms = [e["switch_ms"] for e in events if e["switch_ms"] is not None]

    def pct(xs: list[float], p: float) -> float:
        if not xs:
            return 0.0
        xs = sorted(xs)
        k = min(len(xs) - 1, max(0, int(round((p / 100.0) * (len(xs) - 1)))))
        return round(xs[k], 1)

    return {
        "log_samples": len(events),
        "activate_rpc_ok_rate": round(sum(oks) / len(oks), 3),
        "sibling_steal_rate": round(sum(steals) / len(steals), 3),
        "activate_p50_ms": pct(act_ms, 50),
        "activate_p95_ms": pct(act_ms, 95),
        "switch_space_ms_p50": pct(sw_ms, 50),
        "focus_settle_ms_p50": pct(act_ms, 50),
        "currents_match_rate": round(sum(oks) / len(oks), 3),
    }


LIVE_BENCH = r'''
import AppKit
import CoreGraphics
import Darwin
import Foundation

typealias MainConnection = @convention(c) () -> Int32
typealias CopyManaged = @convention(c) (Int32) -> Unmanaged<CFArray>?
typealias CopySpaces = @convention(c) (Int32, Int32, CFArray) -> Unmanaged<CFArray>?
typealias ShowSpaces = @convention(c) (Int32, CFArray) -> Void
typealias HideSpaces = @convention(c) (Int32, CFArray) -> Void
typealias SetCurrent = @convention(c) (Int32, CFString, UInt64) -> Void

let sky = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW)!
func sym<T>(_ n: String, _ t: T.Type) -> T { unsafeBitCast(dlsym(sky, n), to: t) }
let conn = sym("CGSMainConnectionID", MainConnection.self)()
let copyManaged = sym("CGSCopyManagedDisplaySpaces", CopyManaged.self)
let copySpaces = sym("CGSCopySpacesForWindows", CopySpaces.self)
let show = sym("CGSShowSpaces", ShowSpaces.self)
let hide = sym("CGSHideSpaces", HideSpaces.self)
let setCurrent = sym("CGSManagedDisplaySetCurrentSpace", SetCurrent.self)

func quartz(_ cocoa: CGPoint) -> CGPoint {
    let maxY = NSScreen.screens.map(\.frame.maxY).max() ?? cocoa.y
    return CGPoint(x: cocoa.x, y: maxY - cocoa.y)
}
func park(_ cocoa: CGPoint) {
    CGAssociateMouseAndMouseCursorPosition(0)
    CGWarpMouseCursorPosition(quartz(cocoa))
    CGAssociateMouseAndMouseCursorPosition(1)
}

struct Jump { let id: CGWindowID; let title: String; let spaces: [Int]; let on: Bool; let bounds: CGRect }

func jumps() -> [Jump] {
    // Titles need Screen Recording; without it every name is "". Pick large layer-0
    // Jump frames that have a Space id (fullscreen RDP sessions are ~display-sized).
    let info = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] ?? []
    return info.compactMap { e -> Jump? in
        guard (e[kCGWindowOwnerName as String] as? String) == "Jump Desktop" else { return nil }
        guard (e[kCGWindowLayer as String] as? Int) == 0 else { return nil }
        let title = e[kCGWindowName as String] as? String ?? ""
        let id = e[kCGWindowNumber as String] as? CGWindowID ?? 0
        let on = e[kCGWindowIsOnscreen as String] as? Bool ?? false
        let sp = (copySpaces(conn, 0x7, [id] as CFArray)?.takeRetainedValue() as? [Int]) ?? []
        guard !sp.isEmpty else { return nil }
        let b = e[kCGWindowBounds as String] as? [String: Any] ?? [:]
        let r = CGRect(x: b["X"] as? CGFloat ?? 0, y: b["Y"] as? CGFloat ?? 0,
                       width: b["Width"] as? CGFloat ?? 0, height: b["Height"] as? CGFloat ?? 0)
        // Ignore tiny chrome / status scaffolding.
        guard r.width >= 800, r.height >= 500 else { return nil }
        return Jump(id: id, title: title, spaces: sp, on: on, bounds: r)
    }
}

func currents() -> [String: Int] {
    var m: [String: Int] = [:]
    let displays = copyManaged(conn)?.takeRetainedValue() as? [[String: Any]] ?? []
    for d in displays {
        guard let u = d["Display Identifier"] as? String else { continue }
        m[u] = (d["Current Space"] as? [String: Any])?["id64"] as? Int ?? -1
    }
    return m
}

func displayUUID(forSpace space: Int) -> String? {
    let displays = copyManaged(conn)?.takeRetainedValue() as? [[String: Any]] ?? []
    for d in displays {
        let listed = d["Spaces"] as? [[String: Any]] ?? []
        let ids = Set(listed.compactMap { $0["id64"] as? Int })
        if ids.contains(space), let u = d["Display Identifier"] as? String { return u }
    }
    return nil
}

func screenMid(uuid: String) -> CGPoint? {
    for s in NSScreen.screens {
        guard let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
              let cf = CGDisplayCreateUUIDFromDisplayID(n.uint32Value)?.takeRetainedValue(),
              let u = CFUUIDCreateString(nil, cf) as String?,
              u.caseInsensitiveCompare(uuid) == .orderedSame else { continue }
        return CGPoint(x: s.frame.midX, y: s.frame.midY)
    }
    return nil
}

let trials = max(1, Int(CommandLine.arguments.dropFirst().first ?? "4") ?? 4)
var paintOk = 0
var currentOk = 0
var onscreenOk = 0
var attempted = 0
let saved = NSEvent.mouseLocation

for _ in 0..<trials {
    let cur = currents()
    let list = jumps()
    guard let target = list.first(where: { j in
        guard let sp = j.spaces.first, let uuid = displayUUID(forSpace: sp) else { return false }
        return cur[uuid] != sp
    }) else { break }
    guard let dest = target.spaces.first, let uuid = displayUUID(forSpace: dest) else { break }
    let from = cur[uuid] ?? -1
    attempted += 1
    if let mid = screenMid(uuid: uuid) { park(mid) }
    else if target.bounds.width > 0 {
        let primaryMaxY = NSScreen.screens.map(\.frame.maxY).max() ?? 0
        let qx = target.bounds.midX
        let qy = target.bounds.midY
        park(CGPoint(x: qx, y: primaryMaxY - qy))
    }
    if from > 0 { hide(conn, [from] as CFArray) }
    show(conn, [dest] as CFArray)
    setCurrent(conn, uuid as CFString, UInt64(dest))
    usleep(400_000)
    let afterCur = currents()[uuid]
    let afterOn = jumps().first(where: { $0.id == target.id })?.on ?? false
    if afterCur == dest { currentOk += 1 }
    if afterOn { onscreenOk += 1 }
    if afterCur == dest && afterOn { paintOk += 1 }
}

park(saved)
let rate = attempted == 0 ? 0.0 : Double(paintOk) / Double(attempted)
let out: [String: Any] = [
    "attempted": attempted,
    "first_click_paint_rate": rate,
    "currents_match_rate": attempted == 0 ? 0.0 : Double(currentOk) / Double(attempted),
    "onscreen_match_rate": attempted == 0 ? 0.0 : Double(onscreenOk) / Double(attempted),
]
if let data = try? JSONSerialization.data(withJSONObject: out),
   let s = String(data: data, encoding: .utf8) {
    print(s)
}
'''


def live_paint_bench(trials: int = 4) -> dict:
    with tempfile.TemporaryDirectory() as td:
        src = Path(td) / "bench.swift"
        bin_path = Path(td) / "bench"
        src.write_text(LIVE_BENCH)
        c = run(["swiftc", "-O", "-o", str(bin_path), str(src)], timeout=60)
        if c.returncode != 0:
            return {
                "attempted": 0,
                "first_click_paint_rate": 0.0,
                "currents_match_rate": 0.0,
                "onscreen_match_rate": 0.0,
                "bench_error": (c.stderr or c.stdout)[-500:],
            }
        r = run([str(bin_path), str(trials)], timeout=60)
        if r.returncode != 0 or not r.stdout.strip():
            return {
                "attempted": 0,
                "first_click_paint_rate": 0.0,
                "currents_match_rate": 0.0,
                "onscreen_match_rate": 0.0,
                "bench_error": (r.stderr or r.stdout)[-500:],
            }
        try:
            return json.loads(r.stdout.strip().splitlines()[-1])
        except json.JSONDecodeError:
            return {
                "attempted": 0,
                "first_click_paint_rate": 0.0,
                "currents_match_rate": 0.0,
                "onscreen_match_rate": 0.0,
                "bench_error": r.stdout[-500:],
            }


def main() -> int:
    t0 = time.time()
    tests = helper_tests_passed()
    logm = analyze_restart_log(restart_log_path())
    live = live_paint_bench(4)
    rss = windowserver_rss_mb()

    attempted = int(live.get("attempted") or 0)
    require_jump = os.environ.get("SPACE_ACTIVATE_REQUIRE_JUMP") == "1"
    if require_jump and attempted == 0:
        # Force gate failure when Jump required but missing
        paint = 0.0
    else:
        paint = float(live.get("first_click_paint_rate") or 0.0)

    # Prefer live currents/onscreen; fall back to log currents_match
    currents = float(live.get("currents_match_rate") or logm.get("currents_match_rate") or 0.0)
    onscreen = float(live.get("onscreen_match_rate") or 0.0)

    out = {
        "helper_tests_passed": tests,
        "activate_rpc_ok_rate": logm["activate_rpc_ok_rate"] if logm["log_samples"] else 1.0,
        "sibling_steal_rate": logm["sibling_steal_rate"],
        "activate_p95_ms": logm["activate_p95_ms"],
        "activate_p50_ms": logm["activate_p50_ms"],
        "switch_space_ms_p50": logm["switch_space_ms_p50"],
        "focus_settle_ms_p50": logm["focus_settle_ms_p50"],
        "first_click_paint_rate": paint,
        "currents_match_rate": currents,
        "onscreen_match_rate": onscreen,
        "windowserver_rss_mb": rss,
        "log_samples": logm["log_samples"],
        "live_attempts": attempted,
        "elapsed_s": round(time.time() - t0, 2),
    }
    if "bench_error" in live:
        out["bench_error"] = live["bench_error"]

    json.dump(out, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0 if tests == 1 else 1


if __name__ == "__main__":
    sys.exit(main())
