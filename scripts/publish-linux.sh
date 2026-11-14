#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
if [[ "$RID" != "linux-x64" && "$RID" != "linux-arm64" ]]; then
  echo "Unsupported Linux RID: $RID (expected linux-x64 or linux-arm64)" >&2
  exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
APP_PROJECT="$REPO_ROOT/src/DiskPulse.App/DiskPulse.App.csproj"
HELPER_PROJECT="$REPO_ROOT/src/DiskPulse.Helper/DiskPulse.Helper.csproj"
OUTPUT_DIR="$REPO_ROOT/publish/$RID"

"$SCRIPT_DIR/restore-contracts.sh"
mkdir -p "$OUTPUT_DIR"

for project in "$APP_PROJECT" "$HELPER_PROJECT"; do
  dotnet restore "$project" --locked-mode
  dotnet publish "$project" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --output "$OUTPUT_DIR" \
    --no-restore
done

rm -f "$OUTPUT_DIR"/*.pdb
chmod +x "$OUTPUT_DIR/DiskPulse" "$OUTPUT_DIR/DiskPulse.Helper"
cp -f "$REPO_ROOT/assets/cleaner-icon.png" "$OUTPUT_DIR/cleaner-icon.png"
cp -f "$SCRIPT_DIR/install-linux.sh" "$OUTPUT_DIR/install.sh"
chmod +x "$OUTPUT_DIR/install.sh"

VERSION="$(dotnet msbuild "$APP_PROJECT" -nologo -getProperty:Version | tr -d '[:space:]')"
ARCHIVE_PATH="$REPO_ROOT/publish/DiskPulse-$VERSION-$RID.tar.gz"
tar -C "$OUTPUT_DIR" -czf "$ARCHIVE_PATH" .

echo "Published standalone Linux bundle: $ARCHIVE_PATH"
