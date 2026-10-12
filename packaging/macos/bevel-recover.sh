#!/usr/bin/env bash
# Recover from a half-dead Bevel: orphaned role processes, and a Dock left hidden.
#
# WHY THIS EXISTS
#   Bevel is a multi-process shell supervised by a launcher. When the LAUNCHER dies without a clean
#   shutdown — a crash, a SIGKILL — its children are re-parented to launchd and keep running. They
#   still hold the shell-core socket and settings.db, so the NEXT launch collides with them and can
#   itself die, which is how one crash becomes a stuck shell. Worse, Bevel hides the macOS Dock while
#   it runs, so a launcher that aborts can leave the user with no Dock and nothing alive to restore it.
#
#   This was cleaned up by hand twice before it became a script. Hence a script.
#
# WHAT IT DOES NOT DO
#   It does not SIGKILL by default, ever. MacOSDockController restores the Dock from
#   AppDomain.ProcessExit, which SIGTERM reaches and SIGKILL does not — so killing hard is precisely
#   what strands the Dock. --force exists for a wedged process, and then the Dock is healed explicitly
#   because we know ProcessExit was skipped.
#
# Usage:
#   bevel-recover.sh              diagnose, then recover gently (SIGTERM)
#   bevel-recover.sh --dry-run    diagnose only, change nothing
#   bevel-recover.sh --force      escalate to SIGKILL after the grace period, then heal the Dock
set -uo pipefail

GRACE=8                 # seconds to wait for SIGTERM to land before reporting/escalating
MARKER="$HOME/.bevel/dock-claimed.json"
DRY=0; FORCE=0
for a in "$@"; do
	case "$a" in
		--dry-run) DRY=1 ;;
		--force)   FORCE=1 ;;
		-h|--help) sed -n '2,22p' "$0"; exit 0 ;;
		*) echo "unknown option: $a (try --help)" >&2; exit 2 ;;
	esac
done

say() { printf '%s\n' "$*"; }
run() { if [ "$DRY" = 1 ]; then say "  would run: $*"; else "$@" >/dev/null 2>&1; fi; }

# ── Enumerate ───────────────────────────────────────────────────────────────────────────────────
# Match the real executable path so this never matches an editor, a log tail, or this script itself.
# The LAUNCHER is the Bevel process with no --role (an argument-less launch boots --role=launcher).
pids_all()      { pgrep -f '/Bevel\.app/Contents/MacOS/Bevel$|/Bevel\.app/Contents/MacOS/Bevel ' 2>/dev/null; }
pids_helper()   { pgrep -f '/Bevel\.app/Contents/MacOS/BevelHelper' 2>/dev/null; }
role_of()       { ps -o command= -p "$1" 2>/dev/null | grep -oE '\-\-role=[a-z]+' | head -1 | cut -d= -f2; }
ppid_of()       { ps -o ppid= -p "$1" 2>/dev/null | tr -d ' '; }

launcher_pid=""; children=""; signalled=0
for p in $(pids_all); do
	r="$(role_of "$p")"
	if [ -z "$r" ] || [ "$r" = "launcher" ]; then launcher_pid="$p"; else children="$children $p"; fi
done
helpers="$(pids_helper | tr '\n' ' ')"

say "Bevel processes:"
found=0
for p in $(pids_all) $helpers; do
	found=1
	printf '  pid %-7s ppid %-6s %s\n' "$p" "$(ppid_of "$p")" "$(role_of "$p" || echo helper)"
done
[ "$found" = 0 ] && say "  (none running)"

dock_hidden=0
[ "$(defaults read com.apple.dock autohide 2>/dev/null)" = "1" ] && dock_hidden=1
say "Dock autohide: $dock_hidden$( [ -f "$MARKER" ] && echo '   (marker present — Bevel claims it hid the Dock)' )"

# ── Nothing wrong? ──────────────────────────────────────────────────────────────────────────────
if [ "$found" = 0 ] && [ ! -f "$MARKER" ]; then
	if [ "$dock_hidden" = 1 ]; then
		say ""
		say "No Bevel running and no marker, but the Dock is hidden. That is YOUR setting, not Bevel's"
		say "leftovers — leaving it alone. Un-hide it yourself with:"
		say "  defaults write com.apple.dock autohide -bool false && killall Dock"
	else
		say ""
		say "Nothing to recover."
	fi
	exit 0
fi

# A live launcher means a (probably) healthy shell. Signal the LAUNCHER and let it drive the shutdown
# of core, taskbar and helper — signalling a child instead just gets it respawned by the supervisor.
if [ -n "$launcher_pid" ]; then
	say ""
	say "A launcher is alive (pid $launcher_pid) — this looks like a RUNNING shell, not an orphan set."
	say "Asking it to quit kindly; it shuts its children down in order."
	say "(If the UI is responsive, Start > Turn Off is better than this script.)"
	run kill -TERM "$launcher_pid"; signalled=1
else
	# Orphans: no launcher to drive it, so signal them directly. UI roles first, core LAST so it is
	# still up while peers detach and gets the final chance to flush settings.db. The helper is a
	# child of core and exits with it.
	say ""
	if [ -z "$(echo "$children" | tr -d ' ')" ]; then
		say "No Bevel processes at all — only leftover state to tidy (see Dock, below)."
	else
		say "No launcher alive — these are orphans. SIGTERM directly: UI roles first, core last so it flushes."
	fi
	for want in taskbar filer desktop; do
		for p in $children; do
			[ "$(role_of "$p")" = "$want" ] && { say "  TERM -> $p ($want)"; run kill -TERM "$p"; signalled=1; }
		done
	done
	for p in $children; do
		[ "$(role_of "$p")" = "core" ] && { say "  TERM -> $p (core, last)"; run kill -TERM "$p"; signalled=1; }
	done
fi

# ── Wait ────────────────────────────────────────────────────────────────────────────────────────
# Only wait if something was actually signalled. An orphaned Dock marker with no processes left is a
# legitimate state (hard crash, children already gone) and must not cost the grace period.
waited=0
if [ "$signalled" = 1 ] && [ "$DRY" != 1 ]; then
	while [ "$waited" -lt "$GRACE" ]; do
		sleep 1; waited=$((waited + 1))
		[ -z "$(pids_all)$(pids_helper)" ] && break
	done
fi
remaining="$(pids_all) $(pids_helper)"; remaining="$(echo "$remaining" | tr -s ' ' | sed 's/^ *//;s/ *$//')"

killed_hard=0
if [ -n "$remaining" ] && [ "$DRY" = 1 ]; then
	say ""
	say "--dry-run: nothing was signalled, so these are of course still alive: $remaining"
elif [ -n "$remaining" ]; then
	say ""
	say "Still alive after ${waited}s: $remaining"
	if [ "$FORCE" = 1 ]; then
		say "--force: escalating to SIGKILL. This SKIPS AppDomain.ProcessExit, so the Dock will not"
		say "self-restore — healing it explicitly below."
		for p in $remaining; do run kill -9 "$p"; done
		killed_hard=1
		sleep 1
	else
		say "Not escalating. Re-run with --force if they are genuinely wedged."
		say "Note: --force skips the Dock restore handler, which is why it is not the default."
	fi
fi

# ── Dock ────────────────────────────────────────────────────────────────────────────────────────
# Only touch the Dock when Bevel can no longer restore it itself. On a clean SIGTERM the controller
# has already put the user's ORIGINAL value back and deleted the marker, so doing anything here would
# overwrite a correct restore with a guess.
say ""
still_running="$(pids_all)$(pids_helper)"
if [ -n "$still_running" ]; then
	say "Bevel is still running — not touching the Dock."
elif [ -f "$MARKER" ]; then
	# The marker survived, so the controller never got to restore. It records the value Bevel captured
	# BEFORE hiding the Dock — honour that rather than assuming the user wants it visible.
	original="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["autohide"])' "$MARKER" 2>/dev/null)"
	case "$original" in
		True|true)  want=true ;;
		False|false) want=false ;;
		*) want=false; say "  (marker unreadable — falling back to visible)" ;;
	esac
	say "Marker left behind: restoring YOUR captured autohide=$want"
	run defaults write com.apple.dock autohide -bool "$want"
	run killall Dock
	run rm -f "$MARKER"
elif [ "$killed_hard" = 1 ] && [ "$dock_hidden" = 1 ]; then
	say "SIGKILLed with the Dock hidden and no marker to consult — making the Dock visible."
	run defaults write com.apple.dock autohide -bool false
	run killall Dock
else
	say "Dock needs nothing: SIGTERM let Bevel restore it, or it was never hidden by Bevel."
fi

say ""
say "Final state:"
n="$(pids_all | wc -l | tr -d ' ')"; h="$(pids_helper | wc -l | tr -d ' ')"
say "  Bevel processes: $n   helper: $h   Dock autohide: $(defaults read com.apple.dock autohide 2>/dev/null || echo '?')"
