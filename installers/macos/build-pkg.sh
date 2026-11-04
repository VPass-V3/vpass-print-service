#!/usr/bin/env bash
# Build a macOS installer package. Usage: build-pkg.sh <version> <rid: osx-arm64|osx-x64> <publishDir> <outDir>
# The pkg installs /usr/local/vpass-print-service/vpass-print, symlinks /usr/local/bin/vpass-print
# and registers the launchd daemon. Pair afterwards with: sudo vpass-print pair <code> --region <au|uk|us|ca>
# Signing/notarisation (Developer ID) must be added before public distribution (see README).
set -euo pipefail
VERSION="$1"; RID="$2"; PUBLISH_DIR="$3"; OUT_DIR="$4"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(mktemp -d)"
mkdir -p "$ROOT/usr/local/vpass-print-service" "$OUT_DIR"
cp "$PUBLISH_DIR/vpass-print" "$ROOT/usr/local/vpass-print-service/vpass-print"
chmod 755 "$ROOT/usr/local/vpass-print-service/vpass-print"
pkgbuild \
  --root "$ROOT" \
  --identifier io.vpass.printservice \
  --version "$VERSION" \
  --scripts "$HERE/scripts" \
  --install-location / \
  "$OUT_DIR/vpass-print-service-$VERSION-$RID.pkg"
echo "built $OUT_DIR/vpass-print-service-$VERSION-$RID.pkg"
