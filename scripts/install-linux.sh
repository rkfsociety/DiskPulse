#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${HOME}/.local/share/diskpulse"
BIN_DIR="${HOME}/.local/bin"
APPS_DIR="${HOME}/.local/share/applications"

mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$APPS_DIR"

cp -f "$SCRIPT_DIR/DiskPulse" "$INSTALL_DIR/DiskPulse"
cp -f "$SCRIPT_DIR/DiskPulse.Helper" "$INSTALL_DIR/DiskPulse.Helper"
if [[ -f "$SCRIPT_DIR/cleaner-icon.png" ]]; then
  cp -f "$SCRIPT_DIR/cleaner-icon.png" "$INSTALL_DIR/cleaner-icon.png"
fi

chmod +x "$INSTALL_DIR/DiskPulse" "$INSTALL_DIR/DiskPulse.Helper"
ln -sf "$INSTALL_DIR/DiskPulse" "$BIN_DIR/diskpulse"

cat > "$APPS_DIR/diskpulse.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=DiskPulse
Comment=Анализ дисков, безопасная очистка временных файлов и управление программами
Exec=$INSTALL_DIR/DiskPulse
Icon=$INSTALL_DIR/cleaner-icon.png
Terminal=false
Categories=System;Utility;
EOF

echo "DiskPulse установлен в $INSTALL_DIR"
echo "Запуск из терминала: $BIN_DIR/diskpulse"
