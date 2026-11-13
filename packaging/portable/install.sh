#!/usr/bin/env bash
# Установка готовой сборки hktools (из архива релиза). Нужен рантайм .NET ≥ 8, GTK 4, libadwaita, ffmpeg.
#   ./install.sh                 — в ~/.local
#   sudo ./install.sh /usr/local — для всех пользователей
#   ./install.sh --uninstall [PREFIX]
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
UNINSTALL=0
if [[ ${1:-} == --uninstall ]]; then UNINSTALL=1; shift; fi
PREFIX=${1:-$HOME/.local}
APP_ID=io.github.zerosensee.HKTools
LIBDIR=$PREFIX/lib/hktools
DATADIR=$PREFIX/share/hktools

if [[ $UNINSTALL == 1 ]]; then
  rm -rf "$LIBDIR" "$DATADIR" "$PREFIX/bin/hktools" "$PREFIX/share/applications/$APP_ID.desktop" \
         "$PREFIX/share/metainfo/$APP_ID.metainfo.xml" "$PREFIX/share/kio/servicemenus/hktools.desktop"
  for s in 16 32 48 64 128 256 512; do rm -f "$PREFIX/share/icons/hicolor/${s}x${s}/apps/$APP_ID.png"; done
  echo "hktools удалён из $PREFIX"; exit 0
fi

command -v dotnet >/dev/null || echo "⚠ Не найден dotnet: установите рантайм .NET ≥ 8 (Arch: sudo pacman -S dotnet-runtime)"
install -d "$LIBDIR" "$PREFIX/bin"
cp -r lib/. "$LIBDIR/"
printf '#!/bin/sh\nexport HKTOOLS_DATA_DIR="%s"\nexport DOTNET_CLI_TELEMETRY_OPTOUT=1\nexec dotnet "%s/hktools.dll" "$@"\n' "$DATADIR" "$LIBDIR" > "$PREFIX/bin/hktools"
chmod 755 "$PREFIX/bin/hktools"
install -Dm644 "data/$APP_ID.desktop" "$PREFIX/share/applications/$APP_ID.desktop"
install -Dm644 "data/$APP_ID.metainfo.xml" "$PREFIX/share/metainfo/$APP_ID.metainfo.xml"
for s in 16 32 48 64 128 256 512; do
  install -Dm644 "data/icons/hktools-$s.png" "$PREFIX/share/icons/hicolor/${s}x${s}/apps/$APP_ID.png"
done
install -Dm644 data/icons/hktools-256.png "$DATADIR/icons/hktools-256.png"
for f in hyprland/hktools.conf hyprland/hktools.lua waybar/hktools.jsonc waybar/hktools.css filemanagers/thunar-uca.xml; do
  install -Dm644 "data/$f" "$DATADIR/$f"
done
install -Dm644 data/filemanagers/hktools-dolphin.desktop "$PREFIX/share/kio/servicemenus/hktools.desktop"
gtk-update-icon-cache -q -t "$PREFIX/share/icons/hicolor" 2>/dev/null || true
update-desktop-database -q "$PREFIX/share/applications" 2>/dev/null || true
echo "Готово: $PREFIX/bin/hktools   (проверка утилит: hktools deps)"
case ":$PATH:" in *":$PREFIX/bin:"*) ;; *) echo "Добавьте $PREFIX/bin в PATH" ;; esac
