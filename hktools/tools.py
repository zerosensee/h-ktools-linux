"""Внешние утилиты: поиск, версии, пакеты Arch и команды установки."""

from __future__ import annotations

import os
import shlex
import shutil
import subprocess
from dataclasses import dataclass, field

from .config import settings


@dataclass(frozen=True)
class Tool:
    key: str
    title: str
    candidates: tuple[str, ...]
    package: str  # пакет из официальных репозиториев Arch (pacman)
    aur: bool = False
    aur_variants: tuple[str, ...] = ()
    required: bool = False
    version_args: tuple[str, ...] = ("--version",)
    purpose: str = ""


TOOLS: dict[str, Tool] = {
    t.key: t
    for t in (
        Tool("ffmpeg", "FFmpeg", ("ffmpeg",), "ffmpeg", required=True, version_args=("-version",),
             purpose="Кодирование, ремукс, фильтры — основа почти всех инструментов"),
        Tool("ffprobe", "FFprobe", ("ffprobe",), "ffmpeg", required=True, version_args=("-version",),
             purpose="Анализ структуры медиафайлов"),
        Tool("mkvmerge", "MKVToolNix (mkvmerge)", ("mkvmerge",), "mkvtoolnix-cli",
             purpose="Сборка MKV, сдвиг аудио без перекодирования, пересадка дорожек"),
        Tool("mkvextract", "MKVToolNix (mkvextract)", ("mkvextract",), "mkvtoolnix-cli",
             purpose="Извлечение VobSub-субтитров из MKV"),
        Tool("yt-dlp", "yt-dlp", ("yt-dlp",), "yt-dlp", purpose="Загрузка медиа по ссылке"),
        Tool("deno", "Deno (JS-рантайм для yt-dlp)", ("deno",), "deno",
             purpose="Нужен yt-dlp для части сайтов (YouTube)"),
        Tool("whisper", "whisper.cpp", ("whisper-cli", "whisper.cpp", "whisper-cpp"), "whisper.cpp", aur=True,
             aur_variants=("whisper.cpp", "whisper.cpp-cuda", "whisper.cpp-vulkan", "whisper.cpp-hip"),
             version_args=("--help",), purpose="Распознавание речи (CPU / CUDA / Vulkan / ROCm)"),
        Tool("fdkaac", "fdkaac (Fraunhofer FDK AAC)", ("fdkaac",), "fdkaac",
             purpose="Качественный AAC/HE-AAC — замена QAAC в Linux"),
        Tool("wine", "Wine", ("wine",), "wine", version_args=("--version",),
             purpose="Необязательно: запуск qaac64.exe, если нужен именно Apple AAC"),
        Tool("notify-send", "libnotify (notify-send)", ("notify-send",), "libnotify",
             purpose="Уведомления о завершении (mako, swaync, dunst, Quickshell)"),
    )
}


@dataclass
class ToolStatus:
    tool: Tool
    path: str | None
    version: str = ""
    extra: dict = field(default_factory=dict)

    @property
    def found(self) -> bool:
        return bool(self.path)


def which(key: str) -> str | None:
    """Путь к утилите: сначала пользовательский путь из настроек, затем PATH."""
    override = settings.tool_path(key)
    if override:
        p = os.path.expanduser(override)
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p
        found = shutil.which(p)
        if found:
            return found
    tool = TOOLS.get(key)
    names = tool.candidates if tool else (key,)
    for name in names:
        found = shutil.which(name)
        if found:
            return found
    return None


def require(key: str) -> str:
    path = which(key)
    if not path:
        tool = TOOLS.get(key)
        pkg = tool.package if tool else key
        hint = f"paru -S {pkg}" if tool and tool.aur else f"sudo pacman -S {pkg}"
        raise ToolMissing(f"Не найдена утилита «{tool.title if tool else key}». Установите: {hint}")
    return path


class ToolMissing(RuntimeError):
    pass


def probe_version(key: str) -> ToolStatus:
    tool = TOOLS[key]
    path = which(key)
    status = ToolStatus(tool, path)
    if not path:
        return status
    try:
        out = subprocess.run([path, *tool.version_args], capture_output=True, text=True, timeout=8,
                             errors="replace")
        text = (out.stdout or out.stderr or "").strip().splitlines()
        first = text[0] if text else ""
        if key in ("ffmpeg", "ffprobe"):
            parts = first.split()
            status.version = parts[2] if len(parts) > 2 else first
        elif key == "whisper":
            status.version = "установлен"
        else:
            status.version = first[:80]
    except (OSError, subprocess.SubprocessError):
        status.version = "?"
    return status


def qaac_command() -> list[str] | None:
    """Команда запуска qaac (через Wine или нативную обёртку), если настроена."""
    raw = (settings.get("qaac_command") or "").strip()
    if raw:
        try:
            return [os.path.expanduser(p) for p in shlex.split(raw)]
        except ValueError:
            return None
    found = shutil.which("qaac") or shutil.which("qaac64")
    return [found] if found else None


def aur_helper() -> str | None:
    for helper in ("paru", "yay", "pikaur", "trizen", "aura"):
        if shutil.which(helper):
            return helper
    return None


def install_command(keys: list[str]) -> str:
    """Собрать shell-команду установки пакетов для pacman/AUR."""
    repo_pkgs: list[str] = []
    aur_pkgs: list[str] = []
    for key in keys:
        tool = TOOLS[key]
        target = aur_pkgs if tool.aur else repo_pkgs
        if tool.package not in target:
            target.append(tool.package)
    parts: list[str] = []
    if repo_pkgs:
        parts.append("sudo pacman -S --needed " + " ".join(repo_pkgs))
    if aur_pkgs:
        helper = aur_helper()
        if helper:
            parts.append(f"{helper} -S --needed " + " ".join(aur_pkgs))
        else:
            parts.append("echo 'Нужен AUR-помощник (paru/yay) для: " + " ".join(aur_pkgs) + "'")
    return " && ".join(parts) if parts else "true"


TERMINALS = (
    ("xdg-terminal-exec", []),
    ("kitty", ["--"]),
    ("foot", []),
    ("ghostty", ["-e"]),
    ("alacritty", ["-e"]),
    ("wezterm", ["start", "--"]),
    ("konsole", ["-e"]),
    ("gnome-terminal", ["--"]),
    ("xfce4-terminal", ["-x"]),
    ("xterm", ["-e"]),
)


def run_in_terminal(shell_command: str) -> bool:
    """Открыть команду в терминале пользователя ($TERMINAL или первый найденный)."""
    wrapped = f"{shell_command}; echo; read -rp 'Готово. Нажмите Enter, чтобы закрыть…' _"
    term_env = os.environ.get("TERMINAL", "").strip()
    candidates: list[tuple[str, list[str]]] = []
    if term_env:
        candidates.append((term_env, ["-e"]))
    candidates.extend(TERMINALS)
    for name, prefix in candidates:
        exe = shutil.which(name)
        if not exe:
            continue
        try:
            subprocess.Popen([exe, *prefix, "bash", "-lc", wrapped], start_new_session=True)
            return True
        except OSError:
            continue
    return False
