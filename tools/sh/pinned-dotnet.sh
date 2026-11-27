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

# Guard: this file only makes sense SOURCED — it exports PATH/DOTNET_ROOT into the caller's shell.
# Executed standalone there is nothing to do but say so.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
	echo "error: source this file, don't execute it:  . \"$0\"" >&2
	exit 1
fi

# The real Homebrew roots. Detection is ANCHORED to these (bevel-ka6c review): a path merely
# containing "homebrew" (/Users/x/homebrew-tools/dotnet) or named after libbrotli is NOT Homebrew.
# Note that on Intel Macs /usr/local/share/dotnet is the OFFICIAL location — Homebrew's Intel root
# is /usr/local/Cellar, not /usr/local as a whole.
__bevel_homebrew_roots() {
	printf '%s\n' /opt/homebrew /usr/local/Cellar /usr/local/opt
	if [ -n "${HOMEBREW_PREFIX:-}" ] && [ "$HOMEBREW_PREFIX" != "/" ]; then
		printf '%s\n%s\n' "$HOMEBREW_PREFIX/Cellar" "$HOMEBREW_PREFIX/opt"
	fi
	return 0
}

__bevel_path_is_homebrew() {
	# $1 = path; resolves symlinks when readlink -f exists, then anchors to a real Homebrew root.
	local p="$1" root
	[ -n "$p" ] || return 1
	# Prefer realpath (macOS), fall back to readlink -f (Linux)
	if command -v realpath >/dev/null 2>&1; then
		p="$(realpath "$p" 2>/dev/null || echo "$p")"
	elif command -v readlink >/dev/null 2>&1; then
		p="$(readlink -f "$p" 2>/dev/null || echo "$p")"
	fi
	while IFS= read -r root; do
		[ -n "$root" ] || continue
		case "$p" in "$root"|"$root"/*) return 0 ;; esac
	done < <(__bevel_homebrew_roots)
	return 1
}

__bevel_is_homebrew_dotnet() {
	# $1 = candidate muxer (dotnet) path. Most authoritative signal first (bevel-ka6c review):
	local cand="$1" base
	# 1) The SDK root the muxer itself reports (`dotnet --info` "Base Path") — sees through wrapper
	#    scripts and symlinks that a path pattern cannot.
	base="$("$cand" --info 2>/dev/null | sed -n 's/^ *Base Path: *//p' | head -n 1 | sed 's/[[:space:]]*$//')"
	[ -n "$base" ] && __bevel_path_is_homebrew "$base" && return 0
	# 2) The candidate path itself, resolved through symlinks, anchored to a real Homebrew root.
	__bevel_path_is_homebrew "$cand" && return 0
	# 3) DOTNET_ROOT itself: when it points into a Homebrew root, the muxer it describes — the first
	#    dotnet on PATH, even a wrapper living outside Homebrew — is a Homebrew install.
	if [ -n "${DOTNET_ROOT:-}" ] && __bevel_path_is_homebrew "$DOTNET_ROOT" \
		&& [ "$cand" = "$(command -v dotnet 2>/dev/null || true)" ]; then
		return 0
	fi
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
	# Homebrew formulae — last resort (bevel-ka6c, see header): versioned AND unversioned, libexec
	# and bin variants. All sit under a Homebrew root, so the classifier above always catches them.
	cands+=(/opt/homebrew/opt/dotnet@10/libexec/dotnet /usr/local/opt/dotnet@10/libexec/dotnet
		/opt/homebrew/opt/dotnet/libexec/dotnet /opt/homebrew/opt/dotnet/bin/dotnet
		/usr/local/opt/dotnet/libexec/dotnet /usr/local/opt/dotnet/bin/dotnet)

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
		# Resolve the fallback through any shim symlink (e.g. /opt/homebrew/bin/dotnet -> Cellar)
		# first, so DOTNET_ROOT is the real install dir, not the shim's bin dir.
		command -v readlink >/dev/null 2>&1 && brew_fallback="$(readlink -f "$brew_fallback" 2>/dev/null || echo "$brew_fallback")"
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
unset -f __bevel_pinned_dotnet __bevel_is_homebrew_dotnet __bevel_path_is_homebrew __bevel_homebrew_roots
