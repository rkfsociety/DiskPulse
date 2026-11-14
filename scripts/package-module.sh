#!/usr/bin/env bash
set -euo pipefail

RUNTIME="${1:-linux-x64}"
if [[ "$RUNTIME" != "win-x64" && "$RUNTIME" != "linux-x64" && "$RUNTIME" != "linux-arm64" ]]; then
  echo "Unsupported runtime: $RUNTIME (expected win-x64, linux-x64, or linux-arm64)" >&2
  exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
MODULE_PROJECT="$REPO_ROOT/src/DiskPulse.PulseModule/DiskPulse.PulseModule.csproj"
HELPER_PROJECT="$REPO_ROOT/src/DiskPulse.Helper/DiskPulse.Helper.csproj"
MODULES_OUT="$REPO_ROOT/publish/modules"

"$SCRIPT_DIR/restore-contracts.sh"

VERSION="$(dotnet msbuild "$MODULE_PROJECT" -nologo -getProperty:Version | tr -d '[:space:]')"
if [[ ! "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Invalid module version: $VERSION" >&2
  exit 1
fi

STAGING_DIR="$(mktemp -d)"
HELPER_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGING_DIR" "$HELPER_DIR"' EXIT

mkdir -p "$MODULES_OUT"

dotnet restore "$MODULE_PROJECT" --locked-mode
dotnet publish "$MODULE_PROJECT" \
  --configuration Release \
  --no-self-contained \
  --output "$STAGING_DIR" \
  --no-restore

dotnet restore "$HELPER_PROJECT" --locked-mode
dotnet publish "$HELPER_PROJECT" \
  --configuration Release \
  --runtime "$RUNTIME" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --output "$HELPER_DIR" \
  --no-restore

if [[ "$RUNTIME" == win-* ]]; then
  HELPER_NAME="DiskPulse.Helper.exe"
else
  HELPER_NAME="DiskPulse.Helper"
fi

cp -f "$HELPER_DIR/$HELPER_NAME" "$STAGING_DIR/$HELPER_NAME"
if [[ "$RUNTIME" != win-* ]]; then
  chmod +x "$STAGING_DIR/$HELPER_NAME"
fi

rm -rf "$STAGING_DIR/runtimes"
rm -f \
  "$STAGING_DIR"/PCPulse.Contracts* \
  "$STAGING_DIR"/Avalonia* \
  "$STAGING_DIR"/MicroCom.* \
  "$STAGING_DIR"/SkiaSharp* \
  "$STAGING_DIR"/HarfBuzzSharp* \
  "$STAGING_DIR"/Tmds.DBus* \
  "$STAGING_DIR"/*.pdb

if [[ ! -f "$STAGING_DIR/module.json" ]]; then
  echo "Missing module.json in $STAGING_DIR" >&2
  exit 1
fi

if [[ ! -f "$STAGING_DIR/DiskPulse.PulseModule.dll" ]]; then
  echo "Missing DiskPulse.PulseModule.dll in $STAGING_DIR" >&2
  exit 1
fi

MANIFEST_OUT="$MODULES_OUT/diskpulse-module-$VERSION.manifest.json"
ZIP_OUT="$MODULES_OUT/diskpulse-module-$VERSION-$RUNTIME.zip"
cp -f "$STAGING_DIR/module.json" "$MANIFEST_OUT"
rm -f "$ZIP_OUT" "$ZIP_OUT.sha256"

(
  cd "$STAGING_DIR"
  zip -r -y "$ZIP_OUT" .
)

SHA_HEX="$(sha256sum "$ZIP_OUT" | awk '{print tolower($1)}')"
printf '%s' "$SHA_HEX" > "$ZIP_OUT.sha256"

echo "Packaged PCPulse module:"
echo "  Manifest: $MANIFEST_OUT"
echo "  Archive : $ZIP_OUT"
echo "  SHA-256 : $SHA_HEX"
