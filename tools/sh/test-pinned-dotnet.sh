#!/usr/bin/env bash
#
# Test harness for tools/sh/pinned-dotnet.sh (bevel-ka6c review — the HIGH "repeatable verification"
# finding). Hermetic and self-contained: builds stub dotnet shims in a temp dir and SOURCES the real
# pinned-dotnet.sh under a faked PATH / HOME / DOTNET_ROOT / HOMEBREW_PREFIX in subshells, then
# asserts the resulting env (PATH / DOTNET_ROOT / BEVEL_DOTNET_FROM_HOMEBREW) and exit code.
# No real dotnet is needed, so this runs anywhere bash does — including a .NET-less CI container.
#
# Scenarios:
#   1. brew-first PATH  -> a non-Homebrew install wins, BEVEL_DOTNET_FROM_HOMEBREW stays unset
#   2. brew-only        -> BEVEL_DOTNET_FROM_HOMEBREW=1, DOTNET_ROOT resolved through the shim symlink
#   3. no usable SDK    -> sourcing exits 1
#   4. PATH stub at a non-Homebrew path CONTAINING "/homebrew/" is NOT misclassified
#   5. negative control naming libbrotli (path has both "libbrotli" and "/homebrew/") -> not misclassified
#   6. unit tests of the classifier: anchored roots (/opt/homebrew, /usr/local/Cellar, /usr/local/opt,
#      $HOMEBREW_PREFIX/{Cellar,opt}), `dotnet --info` Base Path, and DOTNET_ROOT itself
#   7. executed instead of sourced -> the guard says "source, don't execute" and exits 1
#
# Hermeticity note: the fixed candidate paths baked into pinned-dotnet.sh (/usr/local/share/dotnet,
# /usr/share/dotnet, /usr/lib/dotnet, /opt/homebrew/opt/..., /usr/local/opt/...) cannot be faked, so
# the scenarios that need them ABSENT (2, 3, 6b) are SKIPped with a notice on hosts that have any of
# them (e.g. a dev Mac). The CI "shell-scripts" job runs this in a plain ubuntu container with no .NET
# installed, where every scenario executes.
#
# Usage: bash tools/sh/test-pinned-dotnet.sh     (exit 0 = every executed scenario passed)

set -u

PINNED="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/pinned-dotnet.sh"
REPO="$(cd "$(dirname "$PINNED")/../.." && pwd)"
GLOBAL_JSON_VERSION="$(sed -n 's/.*"version"[^"]*"\([^"]*\)".*/\1/p' "$REPO/global.json" | head -n 1)"
[ -n "$GLOBAL_JSON_VERSION" ] || GLOBAL_JSON_VERSION="10.0.101"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/bevel-pinned-test.XXXXXX")"
TMP="$(cd "$TMP" && pwd -P)"   # physical path: readlink -f / pwd -P resolve /var -> /private/var on macOS
trap 'rm -rf "$TMP"' EXIT

pass=0 fail=0 skip=0
ok()   { pass=$((pass+1)); printf 'ok   %s\n' "$1"; }
bad()  { fail=$((fail+1)); printf 'FAIL %s\n' "$1"; }
skip() { skip=$((skip+1)); printf 'skip %s\n' "$1"; }
assert_eq() { # <desc> <expected> <actual>
	if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected [$2] got [$3])"; fi
}

# --- stub dotnet shims ---------------------------------------------------------------
# $1 = shim path, $2 = install root reported as `dotnet --info` "Base Path",
# $3 = ok|nosdk (whether `dotnet --version` finds an SDK global.json accepts)
make_stub() {
	mkdir -p "$(dirname "$1")"
	cat > "$1" <<EOF
#!/usr/bin/env bash
# stub dotnet built by test-pinned-dotnet.sh — emulates the muxer, not a real SDK
case "\${1:-}" in
--version)
	if [ "$3" = ok ]; then printf '%s\n' "$GLOBAL_JSON_VERSION"; exit 0
	else echo 'A compatible .NET SDK was not found' >&2; exit 1; fi ;;
--info)
	printf ' Base Path:   %s/sdk/%s/\n' "$2" "$GLOBAL_JSON_VERSION"
	exit 0 ;;
*) exit 0 ;;
esac
EOF
	chmod +x "$1"
}

# fake Homebrew prefix: a Cellar keg plus the shim symlink Homebrew puts on PATH
PREFIX="$TMP/prefix"
KEG="$PREFIX/Cellar/dotnet/9.9.9"
make_stub "$KEG/libexec/dotnet" "$KEG/libexec" ok
BREWBIN="$TMP/brewbin"; mkdir -p "$BREWBIN"
ln -s "$KEG/libexec/dotnet" "$BREWBIN/dotnet"
# a brew formula reachable only through DOTNET_ROOT ($HOMEBREW_PREFIX/opt)
make_stub "$PREFIX/opt/dotnet@10/libexec/dotnet" "$PREFIX/opt/dotnet@10/libexec" ok
# official-style install at the dotnet-install default $HOME/.dotnet
HOME_OK="$TMP/home-ok";       make_stub "$HOME_OK/.dotnet/dotnet" "$HOME_OK/.dotnet" ok
HOME_EMPTY="$TMP/home-empty"; mkdir -p "$HOME_EMPTY"
# a dotnet whose SDK global.json does NOT accept
NOSDK="$TMP/nosdkbin"; make_stub "$NOSDK/dotnet" "$NOSDK" nosdk
# non-Homebrew paths that must NOT be misclassified (a bare-substring match would flag both)
NEG1="$TMP/neg/homebrew/bin";            make_stub "$NEG1/dotnet" "$TMP/neg/homebrew" ok
NEG2="$TMP/neg2/libbrotli/homebrew/bin"; make_stub "$NEG2/dotnet" "$TMP/neg2/libbrotli/homebrew" ok

SAFE_PATH="/usr/bin:/bin"   # no dotnet lives here on macOS or in a plain Linux container

# Source the real pinned-dotnet.sh in a subshell with a faked environment; sets OUT and RC.
# $1=PATH  $2=HOME  $3=DOTNET_ROOT ("-" = unset)  $4=HOMEBREW_PREFIX ("-" = unset)
source_pinned() {
	OUT="$(bash -c '
		export PATH="$1" HOME="$2"
		if [ "$3" = - ]; then unset DOTNET_ROOT; else export DOTNET_ROOT="$3"; fi
		if [ "$4" = - ]; then unset HOMEBREW_PREFIX; else export HOMEBREW_PREFIX="$4"; fi
		unset BEVEL_DOTNET_FROM_HOMEBREW
		if . "$5"; then
			printf "FLAG=%s\nDOTNET=%s\nDOTNET_ROOT=%s\n" \
				"${BEVEL_DOTNET_FROM_HOMEBREW:-}" "$(command -v dotnet 2>/dev/null || true)" "${DOTNET_ROOT:-}"
		else
			exit 1
		fi
	' _ "$1" "$2" "$3" "$4" "$PINNED" 2>&1)"
	RC=$?
}
field() { printf '%s\n' "$OUT" | sed -n "s/^$1=//p"; }

# Fixed candidate paths baked into pinned-dotnet.sh that cannot be faked: if the HOST has any of
# them, scenarios requiring their absence are not hermetic here (the CI container has none).
host_has_fixed_dotnet() {
	local p
	for p in /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet \
		/opt/homebrew/opt/dotnet@10/libexec/dotnet /opt/homebrew/opt/dotnet/libexec/dotnet \
		/opt/homebrew/opt/dotnet/bin/dotnet /usr/local/opt/dotnet@10/libexec/dotnet \
		/usr/local/opt/dotnet/libexec/dotnet /usr/local/opt/dotnet/bin/dotnet; do
		[ -x "$p" ] && return 0
	done
	return 1
}

# The classifier under test, extracted from the REAL file (not re-implemented here): every
# __bevel_* function definition, without running the file's tail.
eval "$(sed -n '/^__bevel_[a-z_]*() {/,/^}/p' "$PINNED")"

u() { # <desc> <expected 0|1> <function> [args...] — runs under the harness's current env
	local desc="$1" want="$2"; shift 2
	local got=1
	"$@" >/dev/null 2>&1 && got=0
	assert_eq "$desc" "$want" "$got"
}

echo "== pinned-dotnet.sh harness (bevel-ka6c review) =="

# --- 6. unit tests of the classifier (anchored roots, Base Path, DOTNET_ROOT) -----------
unset HOMEBREW_PREFIX DOTNET_ROOT
export PATH="$SAFE_PATH"
u "anchored root /opt/homebrew"                     0 __bevel_path_is_homebrew /opt/homebrew/opt/dotnet@10/libexec/dotnet
u "anchored root /opt/homebrew/Cellar"             0 __bevel_path_is_homebrew /opt/homebrew/Cellar/dotnet/11.0.100/bin/dotnet
u "anchored root /usr/local/Cellar"                0 __bevel_path_is_homebrew /usr/local/Cellar/dotnet/9.0.0/bin/dotnet
u "anchored root /usr/local/opt"                   0 __bevel_path_is_homebrew /usr/local/opt/dotnet@10/libexec/dotnet
u "official /usr/local/share/dotnet is NOT homebrew (Intel official)" 1 __bevel_path_is_homebrew /usr/local/share/dotnet/dotnet
u "official \$HOME/.dotnet is NOT homebrew"        1 __bevel_path_is_homebrew "$HOME/.dotnet/dotnet"
u "non-root path containing /homebrew/ NOT homebrew" 1 __bevel_path_is_homebrew "$NEG1/dotnet"
u "libbrotli-named path NOT homebrew"               1 __bevel_path_is_homebrew "$NEG2/dotnet"
export HOMEBREW_PREFIX="$PREFIX"
u "anchored root \$HOMEBREW_PREFIX/Cellar" 0 __bevel_path_is_homebrew "$KEG/bin/dotnet"
u "anchored root \$HOMEBREW_PREFIX/opt"    0 __bevel_path_is_homebrew "$PREFIX/opt/dotnet@10/libexec/dotnet"
# classification by the resolved SDK root (dotnet --info Base Path) over the path pattern
make_stub "$TMP/wrap/dotnet" "$PREFIX/opt/dotnet@10/libexec" ok
make_stub "$TMP/wrap2/dotnet" "$TMP/innocuous" ok
u "Base Path under \$HOMEBREW_PREFIX/opt (innocuous wrapper path) IS homebrew" 0 __bevel_is_homebrew_dotnet "$TMP/wrap/dotnet"
u "innocuous Base Path is NOT homebrew" 1 __bevel_is_homebrew_dotnet "$TMP/wrap2/dotnet"
# DOTNET_ROOT itself: a brew-rooted DOTNET_ROOT marks the PATH muxer as Homebrew
export DOTNET_ROOT="$KEG/libexec" PATH="$TMP/wrap2:$SAFE_PATH"
u "brew-rooted DOTNET_ROOT marks the PATH muxer homebrew" 0 __bevel_is_homebrew_dotnet "$TMP/wrap2/dotnet"
u "brew-rooted DOTNET_ROOT does not mark a candidate outside it" 1 __bevel_is_homebrew_dotnet /usr/local/share/dotnet/dotnet
export DOTNET_ROOT=/usr/local/share/dotnet
u "clean DOTNET_ROOT does not mark the PATH muxer" 1 __bevel_is_homebrew_dotnet "$TMP/wrap2/dotnet"
unset DOTNET_ROOT HOMEBREW_PREFIX
export PATH="$SAFE_PATH"

# --- 1. brew-first PATH -> official chosen, flag unset ---------------------------------
source_pinned "$BREWBIN:$SAFE_PATH" "$HOME_OK" - "$PREFIX"
assert_eq "brew-first PATH: sourcing succeeds" 0 "$RC"
assert_eq "brew-first PATH: BEVEL_DOTNET_FROM_HOMEBREW unset" "" "$(field FLAG)"
if [ "$(field DOTNET)" = "$BREWBIN/dotnet" ]; then
	bad "brew-first PATH: brew shim is still first on PATH"
else
	ok "brew-first PATH: a non-Homebrew dotnet is first on PATH ($(field DOTNET))"
fi
dr="$(field DOTNET_ROOT)"
if [ -n "$dr" ]; then
		if ( export HOMEBREW_PREFIX="$PREFIX"; __bevel_path_is_homebrew "$dr" ); then
			bad "brew-first PATH: chosen DOTNET_ROOT $dr is Homebrew"
		else
			ok "brew-first PATH: chosen DOTNET_ROOT is not Homebrew ($dr)"
		fi
fi

# --- 4/5. non-Homebrew lookalike paths on PATH are not misclassified --------------------
source_pinned "$NEG1:$SAFE_PATH" "$HOME_EMPTY" - -
assert_eq "homebrew-substring path: sourcing succeeds" 0 "$RC"
assert_eq "homebrew-substring path: flag unset" "" "$(field FLAG)"
assert_eq "homebrew-substring path: dotnet unchanged" "$NEG1/dotnet" "$(field DOTNET)"
assert_eq "homebrew-substring path: DOTNET_ROOT untouched" "" "$(field DOTNET_ROOT)"

source_pinned "$NEG2:$SAFE_PATH" "$HOME_EMPTY" - -
assert_eq "libbrotli-named path: sourcing succeeds" 0 "$RC"
assert_eq "libbrotli-named path: flag unset" "" "$(field FLAG)"
assert_eq "libbrotli-named path: dotnet unchanged" "$NEG2/dotnet" "$(field DOTNET)"

# --- 2/3/6b. need the fixed candidate paths ABSENT (hermetic only without a host dotnet) ---
if host_has_fixed_dotnet; then
	skip "brew-only / no-SDK / DOTNET_ROOT-brew scenarios: this host has a fixed-path dotnet candidate (CI's .NET-less container runs them)"
else
	# 2. brew-only -> flag set, DOTNET_ROOT resolved through the shim symlink (readlink -f)
	source_pinned "$BREWBIN:$SAFE_PATH" "$HOME_EMPTY" - "$PREFIX"
	assert_eq "brew-only: sourcing succeeds" 0 "$RC"
	assert_eq "brew-only: BEVEL_DOTNET_FROM_HOMEBREW=1" 1 "$(field FLAG)"
	assert_eq "brew-only: DOTNET_ROOT is the resolved keg libexec" "$KEG/libexec" "$(field DOTNET_ROOT)"
	assert_eq "brew-only: resolved dotnet first on PATH" "$KEG/libexec/dotnet" "$(field DOTNET)"

	# 3. no usable SDK -> sourcing exits 1
	source_pinned "$NOSDK:$SAFE_PATH" "$HOME_EMPTY" - "$PREFIX"
	assert_eq "no SDK: sourcing exits 1" 1 "$RC"
	case "$OUT" in
		*"no installed dotnet"*) ok "no SDK: error names the missing SDK" ;;
		*) bad "no SDK: unexpected output: $OUT" ;;
	esac

	# 6b. brew reachable only through DOTNET_ROOT -> still last-resort flag
	source_pinned "$SAFE_PATH" "$HOME_EMPTY" "$PREFIX/opt/dotnet@10/libexec" "$PREFIX"
	assert_eq "DOTNET_ROOT-only brew: sourcing succeeds" 0 "$RC"
	assert_eq "DOTNET_ROOT-only brew: flag set" 1 "$(field FLAG)"
	assert_eq "DOTNET_ROOT-only brew: DOTNET_ROOT is the brew formula dir" "$PREFIX/opt/dotnet@10/libexec" "$(field DOTNET_ROOT)"
fi

# --- 7. executed (not sourced) -> guard ------------------------------------------------
guard_out="$(bash "$PINNED" 2>&1)"; guard_rc=$?
assert_eq "executed-not-sourced: exit 1" 1 "$guard_rc"
case "$guard_out" in
	*"source"*) ok "executed-not-sourced: message says source, don't execute" ;;
	*) bad "executed-not-sourced: unexpected output: $guard_out" ;;
esac

echo
echo "== $pass passed, $fail failed, $skip skipped =="
[ "$fail" -eq 0 ]
