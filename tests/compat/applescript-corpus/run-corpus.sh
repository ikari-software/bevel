#!/usr/bin/env bash
#
# AppleScript compat corpus runner (M4-E / bevel-4qr). Mechanically retargets each Finder snippet to
# the Bevel target and runs it via osascript, classifying pass / fail / out-of-scope. Measures the
# 09-engineering-plan M4 exit metric (>=30/40 in-scope scripts behave after retargeting).
#
# Target: defaults to the ae-probe2 harness (pl.ikari.bevel.aetest); set BEVEL_TARGET=Bevel to run
# against the real app. The target app must already be running + LaunchServices-registered.
#
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -n "${BEVEL_TARGET:-}" ]; then REPL="application \"$BEVEL_TARGET\""
else REPL='application id "pl.ikari.bevel.aetest"'; fi

pass=0; fail=0; oos=0; inscope=0
for f in "$DIR"/*.applescript; do
	base=$(basename "$f" .applescript)
	# Out-of-scope (System Events UI scripting, entire contents, window chrome): expected failures.
	case "$base" in
		9*|*oos*) oos=$((oos + 1)); printf "  ~  %-34s out of scope\n" "$base"; continue ;;
	esac
	inscope=$((inscope + 1))
	script=$(sed "s|application \"Finder\"|$REPL|g" "$f")   # the mechanical retarget
	# `timeout` is GNU coreutils (absent on stock macOS); a perl SIGALRM survives exec and caps osascript portably.
	if perl -e 'alarm shift; exec @ARGV' 8 osascript -e "$script" >/dev/null 2>&1; then
		pass=$((pass + 1)); printf "  ok %-34s\n" "$base"
	else
		fail=$((fail + 1)); printf "  XX %-34s\n" "$base"
	fi
done

echo "──────────────────────────────────────────────"
echo "in-scope: $inscope   pass: $pass   fail: $fail   out-of-scope: $oos"
if [ "$pass" -ge 30 ]; then echo "VERDICT: meets the M4 exit gate (>=30 pass)"
else echo "VERDICT: $pass/$inscope in-scope pass (gate is >=30/40; grow corpus / implement remaining verbs)"; fi
