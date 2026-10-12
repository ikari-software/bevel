#!/usr/bin/env bash
# One-time setup of the six GitHub secrets that release-macos.yml needs (bevel-9po).
#
# WHY YOU RUN THIS AND NOT AN AGENT
#   Two of the three groups cannot be automated away:
#     * The signing cert's PRIVATE KEY has to leave your keychain. That is a deliberate human act —
#       macOS will prompt you to allow it, and an assistant's sandbox will (rightly) refuse to do it.
#     * An App Store Connect API key can only be minted in the WEB UI. There is no CLI, because you
#       would need an API key to call the API that creates API keys. The .p8 downloads exactly once.
#   Everything after that is scriptable, so this script does the rest and checks the result.
#
# SECRET HYGIENE
#   Nothing secret is ever echoed, passed as an argv (visible in `ps`), or left behind: values go to
#   `gh secret set` on STDIN from files in a 0700 temp dir that is shredded on exit, including on error.
#
# USAGE
#   # Preferred — export JUST the Developer ID cert from Keychain Access first (see below), then:
#   ./packaging/macos/setup-release-secrets.sh --p12 ~/Desktop/BevelSigning.p12 \
#       --p8 ~/Downloads/AuthKey_ABC123XYZ9.p8 --issuer 69a6de70-xxxx-xxxx-xxxx-xxxxxxxxxxxx
#
#   # Or let the script export from your login keychain (NOTE: exports EVERY identity in it — both
#   # your Developer ID and your Apple Development key end up in the .p12. Same team, both yours, but
#   # a wider blast radius than necessary if the secret ever leaked. Prefer --p12.)
#   ./packaging/macos/setup-release-secrets.sh --export-all --p8 ... --issuer ...
#
#   --check   verify which of the six secrets exist, change nothing
#
# HOW TO GET THE TWO INPUTS
#   .p12 (precise, recommended):
#     Keychain Access > login > My Certificates > "Developer ID Application: … (4TP7TPH2K6)"
#     right-click > Export… > Personal Information Exchange (.p12) > set a password you discard after.
#   .p8 + key id + issuer id:
#     appstoreconnect.apple.com > Users and Access > Integrations > Keys > "+"
#     Role: Developer. Download AuthKey_<KEYID>.p8 (ONCE — it is unrecoverable).
#     The KEY ID is in the filename. The ISSUER ID is shown above the key list.
set -uo pipefail

REPO_SECRETS=(SIGNING_CERT_P12 SIGNING_CERT_PASSWORD KEYCHAIN_PASSWORD NOTARY_KEY_P8 NOTARY_KEY_ID NOTARY_ISSUER)
P12=""; P8=""; ISSUER=""; EXPORT_ALL=0; CHECK=0

while [ $# -gt 0 ]; do
	case "$1" in
		--p12)        P12="${2:?--p12 needs a path}"; shift 2 ;;
		--p8)         P8="${2:?--p8 needs a path}"; shift 2 ;;
		--issuer)     ISSUER="${2:?--issuer needs the issuer UUID}"; shift 2 ;;
		--export-all) EXPORT_ALL=1; shift ;;
		--check)      CHECK=1; shift ;;
		-h|--help)    sed -n '2,38p' "$0"; exit 0 ;;
		*) echo "unknown option: $1 (try --help)" >&2; exit 2 ;;
	esac
done

have() { command -v "$1" >/dev/null 2>&1; }
have gh || { echo "need the GitHub CLI (brew install gh)" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "run: gh auth login" >&2; exit 1; }

# ── --check ─────────────────────────────────────────────────────────────────────────────────────
present() { gh secret list 2>/dev/null | awk '{print $1}' | grep -qx "$1"; }
if [ "$CHECK" = 1 ]; then
	echo "release-macos.yml secrets:"
	missing=0
	for s in "${REPO_SECRETS[@]}"; do
		if present "$s"; then printf '  %-24s set\n' "$s"; else printf '  %-24s MISSING\n' "$s"; missing=1; fi
	done
	[ "$missing" = 1 ] && echo "" && echo "Incomplete — release-macos.yml will fail at the cert import step."
	exit 0
fi

[ -z "$P12" ] && [ "$EXPORT_ALL" = 0 ] && { echo "need --p12 <path> (or --export-all). See --help." >&2; exit 2; }
[ -z "$P8" ] && { echo "need --p8 <path to AuthKey_*.p8>. See --help." >&2; exit 2; }
[ -z "$ISSUER" ] && { echo "need --issuer <uuid>. See --help." >&2; exit 2; }
[ -f "$P8" ] || { echo "no such file: $P8" >&2; exit 1; }

# Key id comes from the filename Apple gives you: AuthKey_<KEYID>.p8
KEYID="$(basename "$P8" | sed -n 's/^AuthKey_\(.*\)\.p8$/\1/p')"
[ -n "$KEYID" ] || { echo "cannot read the key id from '$(basename "$P8")' — expected AuthKey_<KEYID>.p8" >&2; exit 1; }

TMP="$(mktemp -d)"; chmod 700 "$TMP"
cleanup() {
	# Overwrite before unlinking: a signing key and an API key are both long-lived credentials.
	for f in "$TMP"/*; do [ -f "$f" ] && dd if=/dev/urandom of="$f" bs=1k count=8 conv=notrunc 2>/dev/null; done
	rm -rf "$TMP"
}
trap cleanup EXIT INT TERM

# ── Certificate ─────────────────────────────────────────────────────────────────────────────────
if [ "$EXPORT_ALL" = 1 ]; then
	echo "==> Exporting identities from your login keychain (macOS will ask you to Allow)"
	echo "    This includes EVERY identity in that keychain, not just the Developer ID."
	LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32 > "$TMP/p12pw"
	security export -k "$HOME/Library/Keychains/login.keychain-db" -t identities -f pkcs12 \
		-P "$(cat "$TMP/p12pw")" -o "$TMP/signing.p12" || { echo "export failed (declined the prompt?)" >&2; exit 1; }
	P12="$TMP/signing.p12"
else
	[ -f "$P12" ] || { echo "no such file: $P12" >&2; exit 1; }
	printf 'Password for %s (not echoed, not stored): ' "$(basename "$P12")" >&2
	stty -echo; read -r PW; stty echo; echo "" >&2
	printf '%s' "$PW" > "$TMP/p12pw"; unset PW
fi

# Prove the .p12 actually contains the Developer ID before shipping it to CI — a wrong or
# key-less export fails confusingly inside the runner, minutes later.
if ! openssl pkcs12 -in "$P12" -passin "file:$TMP/p12pw" -nokeys -legacy -out "$TMP/certs.pem" 2>/dev/null; then
	echo "could not open the .p12 with that password" >&2; exit 1
fi
if ! grep -q 'BEGIN CERTIFICATE' "$TMP/certs.pem"; then echo "that .p12 has no certificate in it" >&2; exit 1; fi
subj="$(openssl x509 -in "$TMP/certs.pem" -noout -subject 2>/dev/null)"
case "$subj" in
	*"Developer ID Application"*) echo "==> .p12 contains: Developer ID Application  (good)" ;;
	*) echo "WARNING: first cert is not a Developer ID Application cert:" ; echo "         $subj"
	   echo "         Notarization needs Developer ID. Continuing, but expect CI to reject it." ;;
esac
if ! openssl pkcs12 -in "$P12" -passin "file:$TMP/p12pw" -nocerts -legacy -noout 2>/dev/null; then
	echo "that .p12 has NO PRIVATE KEY — re-export including the key" >&2; exit 1
fi
echo "==> .p12 contains a private key  (good)"

# ── Upload ──────────────────────────────────────────────────────────────────────────────────────
# base64 so the value survives as a single-line secret; STDIN so nothing lands in argv.
base64 -i "$P12" > "$TMP/p12.b64"
base64 -i "$P8"  > "$TMP/p8.b64"
[ -s "$TMP/p12.b64" ] && [ -s "$TMP/p8.b64" ] || { echo "base64 produced nothing" >&2; exit 1; }

echo "==> Setting secrets"
gh secret set SIGNING_CERT_P12      < "$TMP/p12.b64" && echo "    SIGNING_CERT_P12"
gh secret set SIGNING_CERT_PASSWORD < "$TMP/p12pw"   && echo "    SIGNING_CERT_PASSWORD"
gh secret set NOTARY_KEY_P8         < "$TMP/p8.b64"  && echo "    NOTARY_KEY_P8"
printf '%s' "$KEYID"  | gh secret set NOTARY_KEY_ID  && echo "    NOTARY_KEY_ID      ($KEYID)"
printf '%s' "$ISSUER" | gh secret set NOTARY_ISSUER  && echo "    NOTARY_ISSUER      ($ISSUER)"

if ! present KEYCHAIN_PASSWORD; then
	LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 40 > "$TMP/kcpw"
	gh secret set KEYCHAIN_PASSWORD < "$TMP/kcpw" && echo "    KEYCHAIN_PASSWORD  (generated)"
else
	echo "    KEYCHAIN_PASSWORD  (already set — left alone)"
fi

echo ""
"$0" --check
echo ""
echo "Next: push a tag to run the pipeline, e.g."
echo "  git tag v0.1.1 && git push origin v0.1.1"
echo "Watch it with:  gh run watch \$(gh run list --workflow=release-macos.yml -L1 --json databaseId -q '.[0].databaseId')"
echo ""
echo "If you used --export-all, consider re-doing this with a single-cert --p12 export so your"
echo "Apple Development key is not also sitting in a CI secret."
