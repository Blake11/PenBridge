#!/usr/bin/env bash
# Create (once) a stable self-signed code-signing identity "PenBridge Dev" in the login keychain.
#
# Usage: scripts/mac-dev-cert.sh [--check | --delete | --help]
#
# Why: macOS keys the Accessibility (TCC) grant to the app's code-signing designated requirement.
# Ad-hoc/unsigned rebuilds get a new cdhash each build, so the grant silently stops working.
# Signing every dev build with the same certificate keeps the requirement stable
# (identifier "com.blake11.penbridge" and certificate leaf = <this cert>), so one grant survives rebuilds.
#
# Trade-offs:
#   - Local-only: the cert is self-signed, so Gatekeeper still treats the app as unidentified;
#     it is NOT for distribution (releases are ad-hoc signed, see ADR-0007).
#   - codesign works with the cert even while it is untrusted (find-identity shows
#     CSSMERR_TP_NOT_TRUSTED), and the designated requirement pins the leaf hash, so trust is not
#     strictly needed. The script still adds a user-domain "code signing" trust setting for this one
#     cert (macOS asks for your login password; cancelling is fine — it only warns). The private
#     key never leaves your login keychain. Delete with --delete.
#   - The first codesign may show "codesign wants to use key …" — click "Always Allow".
#   - If you delete/recreate the cert, the TCC grant breaks once: run scripts/mac-run.sh --reset-tcc.
#   - Valid for 10 years; idempotent — does nothing if the identity already exists.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
OPENSSL=/usr/bin/openssl

has_identity() { security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null | grep -q "\"$DEV_CERT_NAME\""; }
is_valid()     { security find-identity -v -p codesigning "$KEYCHAIN" 2>/dev/null | grep -q "\"$DEV_CERT_NAME\""; }

case "${1:-}" in
  -h|--help) usage_from_header "$0"; exit 0 ;;
  --check)
    if is_valid; then ok "\"$DEV_CERT_NAME\" present and trusted for code signing"; exit 0
    elif has_identity; then warn "\"$DEV_CERT_NAME\" present but not trusted — re-run without --check"; exit 1
    else fail "\"$DEV_CERT_NAME\" not found"; exit 1; fi ;;
  --delete)
    step "Deleting \"$DEV_CERT_NAME\""
    while security find-certificate -c "$DEV_CERT_NAME" "$KEYCHAIN" >/dev/null 2>&1; do
      security delete-identity -c "$DEV_CERT_NAME" "$KEYCHAIN" >/dev/null 2>&1 \
        || security delete-certificate -c "$DEV_CERT_NAME" "$KEYCHAIN" >/dev/null
    done
    ok "deleted (run scripts/mac-run.sh --reset-tcc before the next run)"; exit 0 ;;
  "") ;;
  *) die "unknown argument: $1" ;;
esac

step "Code-signing identity \"$DEV_CERT_NAME\""
if is_valid; then ok "already exists and is trusted — nothing to do"; exit 0; fi

if ! has_identity; then
  tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
  cat >"$tmp/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $DEV_CERT_NAME
O = PenBridge local development
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
subjectKeyIdentifier = hash
CNF
  info "generating RSA-2048 key + self-signed certificate…"
  "$OPENSSL" req -new -x509 -newkey rsa:2048 -nodes -days 3650 -sha256 \
    -config "$tmp/cert.cnf" -keyout "$tmp/key.pem" -out "$tmp/cert.pem" >/dev/null 2>&1
  pass="penbridge-$$-$RANDOM"
  # -legacy is needed by OpenSSL 3 so `security` can read the p12; LibreSSL (/usr/bin/openssl) ignores it.
  if "$OPENSSL" pkcs12 -help 2>&1 | grep -q -- '-legacy'; then legacy=(-legacy); else legacy=(); fi
  "$OPENSSL" pkcs12 -export ${legacy[@]+"${legacy[@]}"} -inkey "$tmp/key.pem" -in "$tmp/cert.pem" \
    -name "$DEV_CERT_NAME" -passout "pass:$pass" -out "$tmp/id.p12" >/dev/null 2>&1
  security import "$tmp/id.p12" -k "$KEYCHAIN" -P "$pass" -T /usr/bin/codesign -T /usr/bin/security >/dev/null
  ok "imported into login keychain"
  CERT_PEM="$tmp/cert.pem"
else
  info "identity exists but is not trusted yet"
  CERT_PEM="$(mktemp)"; security find-certificate -c "$DEV_CERT_NAME" -p "$KEYCHAIN" >"$CERT_PEM"
fi

info "trusting it for code signing (macOS will ask for your login password)…"
if security add-trusted-cert -r trustRoot -p codeSign -k "$KEYCHAIN" "$CERT_PEM" && is_valid; then
  ok "\"$DEV_CERT_NAME\" ready and trusted — scripts/mac-run.sh signs with it automatically"
elif has_identity; then
  warn "trust not added (cancelled?) — signing still works; re-run this script to retry"
  ok "\"$DEV_CERT_NAME\" ready — scripts/mac-run.sh signs with it automatically"
else
  die "identity not found after import — check Keychain Access → login → \"$DEV_CERT_NAME\""
fi
