#!/usr/bin/env python3
"""Measure taskbar hover-preview TTFP + click/capture RPC latency (ce-optimize).

preview_ttfp_ms = hover_delay_ms + (0 if cache modeled warm else capture_rpc_p50_ms)
Env:
  TASKBAR_PREVIEW_SKIP_TESTS=1  — skip dotnet test
  TASKBAR_PREVIEW_CACHE_HIT=1   — model warm-cache TTFP (delay only)
  BEVEL_HELPER_*                — forwarded to HelperActivateJump
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HELPER_JUMP = ROOT / "tools" / "eval" / "HelperActivateJump"
TASKBAR_CS = ROOT / "src" / "Bevel.Taskbar" / "TaskbarView.axaml.cs"
TASKBAR_TESTS = ROOT / "tests" / "Bevel.Taskbar.Tests" / "Bevel.Taskbar.Tests.csproj"


def run(cmd: list[str], cwd: Path | None = None, timeout: int = 180) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        cmd, cwd=str(cwd) if cwd else None, text=True, capture_output=True, timeout=timeout
    )


def hover_delay_ms() -> float:
    text = TASKBAR_CS.read_text()
    m = re.search(
        r"TooltipShowDelay\s*=\s*TimeSpan\.FromMilliseconds\((\d+)\)",
        text,
    )
    return float(m.group(1)) if m else 400.0


def cache_present() -> bool:
    return (ROOT / "src" / "Bevel.Taskbar" / "Model" / "WindowPreviewCache.cs").exists()


def taskbar_tests_passed() -> int:
    if os.environ.get("TASKBAR_PREVIEW_SKIP_TESTS") == "1":
        return 1
    r = run(
        ["dotnet", "test", str(TASKBAR_TESTS), "-clp:ErrorsOnly", "--nologo"],
        timeout=300,
    )
    return 1 if r.returncode == 0 else 0


def windowserver_rss_mb() -> float:
    r = run(["ps", "-axo", "rss,comm"])
    total = 0
    for line in r.stdout.splitlines():
        parts = line.split(None, 1)
        if len(parts) != 2:
            continue
        if Path(parts[1]).name == "WindowServer":
            try:
                total += int(parts[0])
            except ValueError:
                pass
    return round(total / 1024.0, 1)


def helper_probe(mode: str, count: int = 6) -> dict:
    args = ["dotnet", "run", "--project", str(HELPER_JUMP), "-c", "Release", "--no-build", "--"]
    if mode == "capture":
        args += ["capture", str(count)]
    else:
        args += [str(count)]
    # Ensure built
    build = run(["dotnet", "build", str(HELPER_JUMP), "-c", "Release", "-clp:ErrorsOnly"], timeout=120)
    if build.returncode != 0:
        return {"error": (build.stderr or build.stdout)[-400:]}
    r = run(args, timeout=120)
    if r.returncode != 0:
        return {"error": (r.stderr or r.stdout)[-400:], "activate_ok_rate": 0.0, "capture_ok_rate": 0.0}
    for line in reversed(r.stdout.strip().splitlines()):
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue
    return {"error": "no-json", "stdout": r.stdout[-200:]}


def main() -> int:
    t0 = time.time()
    delay = hover_delay_ms()
    tests = taskbar_tests_passed()
    cap = helper_probe("capture", 6)
    act = helper_probe("activate", 4)

    capture_p50 = float(cap.get("capture_rpc_p50_ms") or 0)
    capture_p95 = float(cap.get("capture_rpc_p95_ms") or 0)
    capture_ok = float(cap.get("capture_ok_rate") or 0)
    if "error" in cap and "capture_ok_rate" not in cap:
        capture_ok = 0.0

    activate_ok = float(act.get("activate_ok_rate") or 0)
    click_p50 = float(act.get("click_rpc_p50_ms") or 0)
    if "error" in act and "activate_ok_rate" not in act:
        activate_ok = 0.0

    warm = os.environ.get("TASKBAR_PREVIEW_CACHE_HIT") == "1" or cache_present()
    # Warm-cache model: popup can paint stale bytes immediately after the hover delay.
    # Cold / no-cache: must wait for CaptureWindow too.
    if warm and os.environ.get("TASKBAR_PREVIEW_FORCE_COLD") != "1":
        ttfp = delay
        cache_hit_ttfp = delay
    else:
        ttfp = delay + capture_p50
        cache_hit_ttfp = delay  # potential if cache were added

    out = {
        "taskbar_tests_passed": tests,
        "preview_ttfp_ms": round(ttfp, 1),
        "cache_hit_ttfp_ms": round(cache_hit_ttfp, 1),
        "hover_delay_ms": delay,
        "capture_rpc_p50_ms": capture_p50,
        "capture_rpc_p95_ms": capture_p95,
        "capture_ok_rate": capture_ok,
        "activate_ok_rate": activate_ok,
        "click_rpc_p50_ms": click_p50,
        "windowserver_rss_mb": windowserver_rss_mb(),
        "cache_present": cache_present(),
        "elapsed_s": round(time.time() - t0, 2),
    }
    if "error" in cap:
        out["capture_error"] = cap["error"]
    if "error" in act:
        out["activate_error"] = act["error"]
    print(json.dumps(out, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
