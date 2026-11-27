# shellcheck shell=bash
#
# Source this (don't execute it) from any script that runs `dotnet`:
#
#   . "<path to repo>/tools/sh/pinned-dotnet.sh"
#
# It finds the repo root from its own location, so callers need not set anything first.
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
# has an SDK global.json accepts. Among non-Homebrew candidates the current PATH comes first, so a
# working setup is never overridden.
#
# Homebrew installs are additionally LAST-RESORT (bevel-ka6c): the apphost stamped into a single-file
# publish comes from the SDK's host pack, and a Homebrew SDK's singlefilehost links
# /opt/homebrew/opt/brotli (and zstd), so a build running on a Homebrew SDK ships a binary that refuses to
# load on any Mac without Homebrew at that exact path. Any non-Homebrew install that satisfies global.json
# wins; a Homebrew install is used only when nothing else does, and BEVEL_DOTNET_FROM_HOMEBREW=1 is then
# exported so packaging scripts can refuse to ship such a build (packaging/macos/build-app.sh).

__bevel_is_homebrew_dotnet() {
	# A Homebrew dotnet is a shim/symlink into the Cellar (…/Cellar/dotnet…/bin/dotnet,
	# …/homebrew/opt/dotnet@N/libexec/dotnet). Official installs (/usr/local/share/dotnet, ~/.dotnet,
	# distro packages) never live under a Cellar/homebrew path — note that on Intel Macs
	# /usr/local/share/dotnet is the OFFICIAL location, not Homebrew's /usr/local/Cellar.
	local resolved="$1"
	command -v readlink >/dev/null 2>&1 && resolved="$(readlink -f "$1" 2>/dev/null || echo "$1")"
	case "$resolved" in */Cellar/*|*/homebrew/*) return 0 ;; esac
	case "$1" in */Cellar/*|*/homebrew/*) return 0 ;; esac
	return 1
}

__bevel_pinned_dotnet() {
	local root cand dir ver path_dotnet brew_fallback=""
	local -a cands=()
	root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
	unset BEVEL_DOTNET_FROM_HOMEBREW

	path_dotnet="$(command -v dotnet 2>/dev/null || true)"
	[ -n "$path_dotnet" ] && cands+=("$path_dotnet")
	# dotnet.exe: Git Bash on Windows, where DOTNET_ROOT is e.g. C:\Program Files\dotnet.
	[ -n "${DOTNET_ROOT:-}" ] && cands+=("$DOTNET_ROOT/dotnet" "$DOTNET_ROOT/dotnet.exe")
	# Official installer (macOS), dotnet-install.sh default, Linux packages.
	cands+=(/usr/local/share/dotnet/dotnet "$HOME/.dotnet/dotnet" /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet)
	# Homebrew's versioned formulae — last resort (bevel-ka6c, see header).
	cands+=(/opt/homebrew/opt/dotnet@10/libexec/dotnet /usr/local/opt/dotnet@10/libexec/dotnet)

	for cand in "${cands[@]}"; do
		[ -x "$cand" ] || continue
		ver="$(cd "$root" && "$cand" --version 2>/dev/null)" || continue
		[ -n "$ver" ] || continue
		if __bevel_is_homebrew_dotnet "$cand"; then
			[ -n "$brew_fallback" ] || brew_fallback="$cand"
			continue
		fi
		# The dotnet already on PATH works: change nothing. (It may be a wrapper whose own idea of
		# DOTNET_ROOT we must not override.)
		[ "$cand" = "$path_dotnet" ] && return 0
		# A fallback install. These are real install dirs, the host binary sits at their top level.
		dir="$(cd "$(dirname "$cand")" && pwd -P)"
		export DOTNET_ROOT="$dir"
		export PATH="$dir:$PATH"
		echo "==> Using .NET SDK $ver from $dir (${path_dotnet:-no dotnet on PATH}${path_dotnet:+ is Homebrew or has no SDK that global.json accepts})" >&2
		return 0
	done

	if [ -n "$brew_fallback" ]; then
		dir="$(cd "$(dirname "$brew_fallback")" && pwd -P)"
		export DOTNET_ROOT="$dir"
		export PATH="$dir:$PATH"
		export BEVEL_DOTNET_FROM_HOMEBREW=1
		echo "==> Using Homebrew .NET SDK from $dir — last resort only: its apphost links /opt/homebrew brotli (bevel-ka6c); packaging builds refuse this" >&2
		return 0
	fi

	echo "error: no installed dotnet has the SDK $root/global.json requires:" >&2
	sed -n 's/.*"version"[^"]*"\([^"]*\)".*/  version \1/p' "$root/global.json" >&2
	echo "  Install it from https://dotnet.microsoft.com/download (or set DOTNET_ROOT to an install that has it)." >&2
	return 1
}

__bevel_pinned_dotnet || exit 1
unset -f __bevel_pinned_dotnet __bevel_is_homebrew_dotnet
