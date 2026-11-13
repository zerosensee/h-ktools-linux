#!/usr/bin/env bash
# hktools для Arch / CachyOS / Ryoku: установка зависимостей и приложения.
#   ./install.sh            — для текущего пользователя (~/.local), sudo нужен только для pacman
#   ./install.sh --system   — в /usr/local
#   ./install.sh --pkg      — собрать и установить пакет через makepkg (рекомендуется)
#   ./install.sh --no-deps  — не трогать пакеты системы
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

MODE=user
DEPS=1
for a in "$@"; do
  case "$a" in
    --system) MODE=system ;;
    --pkg) MODE=pkg ;;
    --no-deps) DEPS=0 ;;
    -h|--help) sed -n '2,7p' "$0"; exit 0 ;;
  esac
done

c() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
w() { printf '\033[1;33m==>\033[0m %s\n' "$*"; }

if ! command -v pacman >/dev/null 2>&1; then
  w "pacman не найден: скрипт рассчитан на Arch-подобные системы. Установите зависимости вручную:"
  w ".NET SDK 8 (для сборки) и рантайм .NET ≥ 8, GTK 4, libadwaita, ffmpeg, mkvtoolnix, yt-dlp"
  DEPS=0
fi

if [[ $DEPS == 1 ]]; then
  REQ=(dotnet-sdk-8.0 gtk4 libadwaita ffmpeg)
  OPT=(mkvtoolnix-cli yt-dlp deno fdkaac libnotify xdg-desktop-portal-gtk)
  c "Обязательные пакеты: ${REQ[*]}"
  c "Рекомендуемые: ${OPT[*]}"
  sudo pacman -S --needed "${REQ[@]}" "${OPT[@]}"
  HELPER=""
  for h in paru yay; do command -v "$h" >/dev/null && HELPER=$h && break; done
  if ! command -v whisper-cli >/dev/null && ! command -v whisper.cpp >/dev/null; then
    if [[ -n $HELPER ]]; then
      VARIANT=whisper.cpp
      if lspci 2>/dev/null | grep -qi 'nvidia'; then VARIANT=whisper.cpp-cuda
      elif lspci 2>/dev/null | grep -Eqi 'vga.*(amd|ati|intel)'; then VARIANT=whisper.cpp-vulkan; fi
      read -rp "Установить $VARIANT из AUR для распознавания речи? [y/N] " ans
      [[ ${ans,,} == y* ]] && "$HELPER" -S --needed "$VARIANT"
    else
      w "Для распознавания речи установите whisper.cpp из AUR (нужен paru или yay)"
    fi
  fi
fi

case "$MODE" in
  pkg)
    c "Сборка пакета (makepkg)"
    (cd packaging/arch && makepkg -si --noconfirm --nocheck)
    ;;
  system)
    c "Сборка (dotnet publish)"
    make build
    c "Установка в /usr/local"
    sudo make install PREFIX=/usr/local
    ;;
  user)
    c "Сборка (dotnet publish)"
    make build
    c "Установка в ~/.local"
    make install PREFIX="$HOME/.local"
    case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) w "Добавьте ~/.local/bin в PATH" ;; esac
    ;;
esac

c "Проверка утилит:"
hktools deps || "$HOME/.local/bin/hktools" deps || true
if [[ -n ${HYPRLAND_INSTANCE_SIGNATURE:-} ]]; then
  c "Hyprland обнаружен. Правила окон и бинд: $( [[ $MODE == user ]] && echo "$HOME/.local" || echo /usr)/share/hktools/hyprland/"
fi
c "Готово! Запуск: hktools"
