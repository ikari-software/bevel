# shellcheck shell=bash
#
# Source this (don't execute it) from any script that runs `dotnet`, AFTER setting ROOT to the repo root:
#
#   ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
#   . "$ROOT/tools/sh/pinned-dotnet.sh"
#
# It makes `dotnet` resolve to an install that has the SDK global.json pins, by putting that install first
# on PATH and exporting DOTNET_ROOT.
#
# Why: the first `dotnet` on PATH is whatever the machine happened to install last. A Homebrew `dotnet` 11
# in /opt/homebrew/bin shadows the official /usr/local/share/dotnet that carries 10.0.x, and the build
# then dies with "A compatible .NET SDK was not found". Changing the user's shell profile would fix one
# machine. Resolving here fixes every machine and CI.
#
# The SDK choice itself is left to the dotnet host. We don't reimplement global.json's rollForward rules:
# each candidate is asked `dotnet --version` from inside the repo, which succeeds only when that install
# has an SDK global.json accepts. The first candidate that answers wins. The current PATH comes first, so
# a working setup is never overridden.

__bevel_pinned_dotnet() {
	local root="${ROOT:?pinned-dotnet.sh: set ROOT to the repo root before sourcing}"
	local cand dir ver
	local -a cands=()

	cand="$(command -v dotnet 2>/dev/null || true)"
	[ -n "$cand" ] && cands+=("$cand")
	[ -n "${DOTNET_ROOT:-}" ] && cands+=("$DOTNET_ROOT/dotnet")
	# Official installer (macOS), dotnet-install.sh default, Linux packages, Homebrew's versioned formula.
	cands+=(/usr/local/share/dotnet/dotnet "$HOME/.dotnet/dotnet" /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet
		/opt/homebrew/opt/dotnet@10/libexec/dotnet /usr/local/opt/dotnet@10/libexec/dotnet)

	for cand in "${cands[@]}"; do
		[ -x "$cand" ] || continue
		ver="$(cd "$root" && "$cand" --version 2>/dev/null)" || continue
		[ -n "$ver" ] || continue
		# The dotnet already on PATH works: change nothing. (It may be a wrapper, e.g. Homebrew's, whose
		# own idea of DOTNET_ROOT we must not override.)
		[ "$cand" = "$(command -v dotnet 2>/dev/null)" ] && return 0
		# A fallback install. These are real install dirs, the host binary sits at their top level.
		dir="$(cd "$(dirname "$cand")" && pwd -P)"
		export DOTNET_ROOT="$dir"
		export PATH="$dir:$PATH"
		echo "==> Using .NET SDK $ver from $dir (${cands[0]%/dotnet} has no SDK that global.json accepts)" >&2
		return 0
	done

	echo "error: no installed dotnet has the SDK $root/global.json requires:" >&2
	sed -n 's/.*"version"[^"]*"\([^"]*\)".*/  version \1/p' "$root/global.json" >&2
	echo "  Install it from https://dotnet.microsoft.com/download (or set DOTNET_ROOT to an install that has it)." >&2
	return 1
}

__bevel_pinned_dotnet || exit 1
unset -f __bevel_pinned_dotnet
