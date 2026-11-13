#!/usr/bin/env bash
# Build (and, when credentials are present, sign + notarize + staple) the macOS installer package.
#
# Usage: build-pkg.sh <version> <rid: osx-arm64|osx-x64> <publishDir> <outDir>
#
# The pkg installs /usr/local/vpass-print-service/vpass-print, symlinks /usr/local/bin/vpass-print and
# registers the launchd daemon. Pair afterwards with: sudo vpass-print pair <code> --region <au|uk|us|ca>
#
# Signing is driven by environment variables (all optional; missing = unsigned build with a warning):
#   MACOS_SIGN_APP_IDENTITY        "Developer ID Application: <Company> (<TEAMID>)"  -> codesign the binary
#   MACOS_SIGN_INSTALLER_IDENTITY  "Developer ID Installer: <Company> (<TEAMID>)"    -> sign the pkg
#   MACOS_SIGN_KEYCHAIN            keychain holding both identities (CI imports into a temporary one)
# Notarization (needs a signed pkg), App Store Connect API key:
#   APPLE_API_KEY_PATH  path to AuthKey_<KEYID>.p8
#   APPLE_API_KEY_ID    key id
#   APPLE_API_ISSUER_ID issuer id (UUID)
set -euo pipefail
VERSION="$1"; RID="$2"; PUBLISH_DIR="$3"; OUT_DIR="$4"
HERE="$(cd "$(dirname "$0")" && pwd)"
PKG="$OUT_DIR/vpass-print-service-$VERSION-$RID.pkg"
KEYCHAIN_ARGS=()
if [[ -n "${MACOS_SIGN_KEYCHAIN:-}" ]]; then KEYCHAIN_ARGS=(--keychain "$MACOS_SIGN_KEYCHAIN"); fi

ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT
mkdir -p "$ROOT/usr/local/vpass-print-service" "$OUT_DIR"
cp "$PUBLISH_DIR/vpass-print" "$ROOT/usr/local/vpass-print-service/vpass-print"
chmod 755 "$ROOT/usr/local/vpass-print-service/vpass-print"
BIN="$ROOT/usr/local/vpass-print-service/vpass-print"

# 1. Sign the binary with the hardened runtime (required for notarization).
if [[ -n "${MACOS_SIGN_APP_IDENTITY:-}" ]]; then
  echo "Signing binary as: $MACOS_SIGN_APP_IDENTITY"
  codesign --force --timestamp --options runtime \
    --entitlements "$HERE/entitlements.plist" \
    --identifier io.vpass.printservice \
    --sign "$MACOS_SIGN_APP_IDENTITY" ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} "$BIN"
  codesign --verify --strict --verbose=2 "$BIN"
else
  echo "WARNING: MACOS_SIGN_APP_IDENTITY not set - binary is NOT signed (Gatekeeper will block the pkg)."
fi

# 2. Build the pkg, signed with the Developer ID Installer identity when available.
SIGN_PKG_ARGS=()
if [[ -n "${MACOS_SIGN_INSTALLER_IDENTITY:-}" ]]; then
  SIGN_PKG_ARGS=(--sign "$MACOS_SIGN_INSTALLER_IDENTITY" --timestamp ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"})
  echo "Signing pkg as: $MACOS_SIGN_INSTALLER_IDENTITY"
fi
pkgbuild \
  --root "$ROOT" \
  --identifier io.vpass.printservice \
  --version "$VERSION" \
  --scripts "$HERE/scripts" \
  --install-location / \
  ${SIGN_PKG_ARGS[@]+"${SIGN_PKG_ARGS[@]}"} \
  "$PKG"

# 3. Notarize + staple (only meaningful for a signed pkg).
if [[ -n "${MACOS_SIGN_INSTALLER_IDENTITY:-}" && -n "${APPLE_API_KEY_PATH:-}" && -n "${APPLE_API_KEY_ID:-}" && -n "${APPLE_API_ISSUER_ID:-}" ]]; then
  echo "Submitting for notarization ..."
  OUT_JSON="$(mktemp)"
  if ! xcrun notarytool submit "$PKG" \
      --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" \
      --wait --timeout 30m --output-format json > "$OUT_JSON"; then
    cat "$OUT_JSON"; echo "notarytool submit failed"; exit 1
  fi
  cat "$OUT_JSON"
  STATUS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("status",""))' "$OUT_JSON")"
  SUB_ID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("id",""))' "$OUT_JSON")"
  if [[ "$STATUS" != "Accepted" ]]; then
    echo "Notarization $STATUS - log:"
    xcrun notarytool log "$SUB_ID" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" || true
    exit 1
  fi
  xcrun stapler staple "$PKG"
  xcrun stapler validate "$PKG"
  spctl --assess --type install --verbose=2 "$PKG"
elif [[ -n "${MACOS_SIGN_INSTALLER_IDENTITY:-}" ]]; then
  echo "WARNING: signed but NOT notarized (APPLE_API_KEY_* not set) - Gatekeeper will still block it."
fi

pkgutil --check-signature "$PKG" || true
echo "built $PKG"
