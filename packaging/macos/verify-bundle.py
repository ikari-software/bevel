#!/usr/bin/env python3
"""Packaging gate: prove a single-file Bevel.App bundle carries its Avalonia XAML before it ships.

Why this exists (bevel-9pgv): a dist/Bevel.app once crash-looped the taskbar on
`XamlLoadException: No precompiled XAML found for Bevel.Taskbar.TaskbarView` and shipped a 0-byte
Bevel.Themes.Luna.dll. Nothing in the publish pipeline notices either: Avalonia's XAML compiler rewrites
the intermediate assembly IN PLACE after CoreCompile (no inputs/outputs of its own), so an interrupted or
concurrent Release build can leave obj/ holding plain IL without compiled XAML, and PublishReadyToRun +
PublishSingleFile happily crossgen and bundle whatever they find. The launcher's health monitor catches
the crash at run time; this gate catches it at build time, before dist/ exists.

Usage: verify-bundle.py <published single-file executable>
Exit 0 when every Avalonia UI assembly in the bundle is non-empty and carries both the compiled-XAML
types (CompiledAvaloniaXaml.*) and the `!AvaloniaResources` manifest resource that avares:// resolves
against; exit 1 (with the offending entries) otherwise.

Bundle layout per dotnet/runtime Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs — the payload is
appended to the apphost; a marker (int64 header offset + SHA-256 of ".net core bundle\n") locates the
manifest. Component assemblies rewritten by composite ReadyToRun keep their IL, metadata and managed
resources, so the byte-level checks hold for R2R builds too. Deflate-compressed entries are inflated.
"""
import hashlib
import struct
import sys
import zlib

MARKER = hashlib.sha256(b".net core bundle\n").digest()
ASSEMBLY = 1  # FileType.Assembly

# Every assembly whose XAML the shell loads at run time (App.axaml, the taskbar/desktop/explorer views,
# the theme Styles sets and the vendored Classic templates they build on). Missing compiled XAML in ANY
# of these is a boot failure for some role — exactly the class of bug this gate exists for.
XAML_ASSEMBLIES = (
    "Bevel.App.dll",
    "Bevel.Taskbar.dll",
    "Bevel.Desktop.dll",
    "Bevel.FileManager.dll",
    "Bevel.Themes.Win2000.dll",
    "Bevel.Themes.Luna.dll",
    "Classic.Avalonia.Theme.dll",
    "Classic.CommonControls.Avalonia.dll",
)


def read_7bit(buf, pos):
    n = shift = 0
    while True:
        b = buf[pos]
        pos += 1
        n |= (b & 0x7F) << shift
        if not b & 0x80:
            return n, pos
        shift += 7


def read_string(buf, pos):
    n, pos = read_7bit(buf, pos)
    return buf[pos:pos + n].decode("utf-8"), pos + n


def read_manifest(data):
    at = data.find(MARKER)
    if at < 8:
        raise SystemExit("verify-bundle: not a .NET single-file bundle (bundle marker not found)")
    (header,) = struct.unpack_from("<q", data, at - 8)
    if header <= 0 or header >= len(data):
        raise SystemExit(f"verify-bundle: bundle header offset {header} is outside the file")
    pos = header
    major, _minor, count = struct.unpack_from("<IIi", data, pos)
    pos += 12
    _bundle_id, pos = read_string(data, pos)
    if major >= 2:
        pos += 5 * 8  # deps.json + runtimeconfig.json (offset, size) pairs, flags
    entries = {}
    for _ in range(count):
        offset, size = struct.unpack_from("<qq", data, pos)
        pos += 16
        compressed = 0
        if major >= 6:
            (compressed,) = struct.unpack_from("<q", data, pos)
            pos += 8
        kind = data[pos]
        pos += 1
        rel, pos = read_string(data, pos)
        entries[rel] = (kind, offset, size, compressed)
    return entries


def payload(data, entry):
    _kind, offset, size, compressed = entry
    if compressed:
        return zlib.decompress(data[offset:offset + compressed], -15)
    return data[offset:offset + size]


def main(argv):
    if len(argv) != 2:
        raise SystemExit(__doc__)
    path = argv[1]
    with open(path, "rb") as f:
        data = f.read()
    entries = read_manifest(data)
    failures = []

    # 1. No managed Bevel/Classic assembly may be empty (the 0-byte Luna DLL glitch).
    for rel, entry in sorted(entries.items()):
        if entry[0] == ASSEMBLY and (rel.startswith("Bevel.") or rel.startswith("Classic.")) and entry[2] == 0:
            failures.append(f"{rel}: 0-byte assembly in the bundle")

    # 2. Every XAML-bearing UI assembly must be present with compiled XAML + the avares:// resource index.
    for rel in XAML_ASSEMBLIES:
        entry = entries.get(rel)
        if entry is None:
            failures.append(f"{rel}: missing from the bundle")
            continue
        il = payload(data, entry)
        problems = []
        if b"CompiledAvaloniaXaml" not in il:
            problems.append("no compiled XAML (CompiledAvaloniaXaml types absent)")
        if b"!AvaloniaResources" not in il:
            problems.append("no !AvaloniaResources manifest resource")
        if problems:
            failures.append(f"{rel} ({len(il):,} bytes): " + "; ".join(problems))
        else:
            print(f"  ok  {rel:38} {len(il):>10,} bytes  compiled XAML + AvaloniaResources")

    if failures:
        sys.stdout.flush()  # keep the ok lines ahead of the verdict when both streams hit one terminal
        print("verify-bundle: FAILED — the published bundle would throw XamlLoadException at run time:",
              file=sys.stderr)
        for line in failures:
            print(f"  BAD {line}", file=sys.stderr)
        print("  A stale or interrupted incremental build usually explains this: clean src/*/obj and "
              "src/*/bin (Release) and republish.", file=sys.stderr)
        return 1
    print(f"verify-bundle: {len(XAML_ASSEMBLIES)} Avalonia assemblies verified in {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
