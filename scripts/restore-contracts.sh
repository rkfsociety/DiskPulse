#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-1.0.0}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
PACKAGES_DIR="$REPO_ROOT/.packages"
mkdir -p "$PACKAGES_DIR"

PACKAGE_FILE="PCPulse.Contracts.$VERSION.nupkg"
DESTINATION="$PACKAGES_DIR/$PACKAGE_FILE"
if [[ -s "$DESTINATION" ]]; then
  echo "PCPulse.Contracts $VERSION already present at $DESTINATION"
  exit 0
fi

TAG="contracts-v$VERSION"
URL="https://github.com/rkfsociety/PCPulse/releases/download/$TAG/$PACKAGE_FILE"
echo "Downloading $URL ..."
curl -fsSL "$URL" -o "$DESTINATION"

if [[ ! -s "$DESTINATION" ]]; then
  echo "Failed to download $PACKAGE_FILE from $URL" >&2
  exit 1
fi

echo "Saved: $DESTINATION"
