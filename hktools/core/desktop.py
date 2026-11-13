"""Интеграция с рабочим столом: уведомления, статус для Waybar/Quickshell, корзина, открытие папок."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import threading
import time

from .. import APP_ID, APP_NAME, paths
from ..config import settings


def notify(title: str, body: str = "", urgency: str = "normal", icon: str = APP_ID) -> None:
    """Уведомление через notify-send (работает с mako, swaync, dunst, Quickshell)."""
    exe = shutil.which("notify-send")
    if not exe:
        return
    try:
        subprocess.Popen([exe, f"--app-name={APP_NAME}", f"--icon={icon}", f"--urgency={urgency}", title, body],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    except OSError:
        pass


def trash(path: str) -> bool:
    """Переместить файл в корзину (freedesktop), при неудаче — удалить."""
    try:
        import gi  # noqa: PLC0415

        gi.require_version("Gio", "2.0")
        from gi.repository import Gio  # noqa: PLC0415

        if Gio.File.new_for_path(path).trash(None):
            return True
    except Exception:  # noqa: BLE001
        pass
    gio = shutil.which("gio")
    if gio:
        if subprocess.run([gio, "trash", path], capture_output=True).returncode == 0:
            return True
    try:
        os.remove(path)
        return True
    except OSError:
        return False


def open_path(path: str) -> None:
    """Открыть файл/папку во внешнем приложении (xdg-open)."""
    for opener in ("xdg-open", "gio"):
        exe = shutil.which(opener)
        if exe:
            args = [exe, "open", path] if opener == "gio" else [exe, path]
            try:
                subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
                return
            except OSError:
                continue


def show_in_folder(path: str) -> None:
    """Показать файл в файловом менеджере (D-Bus FileManager1, иначе открыть папку)."""
    exe = shutil.which("dbus-send")
    if exe and os.path.exists(path):
        uri = "file://" + os.path.abspath(path)
        res = subprocess.run(
            [exe, "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
             "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
             f"array:string:{uri}", "string:"],
            capture_output=True, timeout=3,
        )
        if res.returncode == 0:
            return
    open_path(os.path.dirname(path) if os.path.isfile(path) else path)


class StatusWriter:
    """Пишет $XDG_RUNTIME_DIR/hktools/status.json для модулей Waybar / Quickshell / eww."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._last = 0.0
        self._clear_timer: threading.Timer | None = None

    def _write(self, data: dict) -> None:
        if not settings.get("status_file"):
            return
        target = paths.status_file()
        tmp = target.with_suffix(".tmp")
        try:
            tmp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, target)
        except OSError:
            pass

    def update(self, script: str, done: int, total: int, percent: float, current: str, force: bool = False) -> None:
        now = time.monotonic()
        with self._lock:
            if not force and now - self._last < 0.8:
                return
            self._last = now
            if self._clear_timer:
                self._clear_timer.cancel()
                self._clear_timer = None
        self._write({
            "text": f"{percent:.0f}%",
            "alt": "running",
            "class": "running",
            "percentage": int(percent),
            "tooltip": f"{script}\n{done}/{total} · {current}",
            "script": script, "done": done, "total": total, "current": current, "ts": time.time(),
        })

    def finish(self, script: str, ok: int, failed: int, cancelled: bool) -> None:
        state = "cancelled" if cancelled else ("failed" if failed else "done")
        icon = {"done": "✓", "failed": "!", "cancelled": "×"}[state]
        self._write({
            "text": icon, "alt": state, "class": state, "percentage": 100,
            "tooltip": f"{script}: готово {ok}, ошибок {failed}" + (" (отменено)" if cancelled else ""),
            "script": script, "ts": time.time(),
        })
        with self._lock:
            if self._clear_timer:
                self._clear_timer.cancel()
            self._clear_timer = threading.Timer(15.0, self.clear)
            self._clear_timer.daemon = True
            self._clear_timer.start()

    def clear(self) -> None:
        self._write({"text": "", "alt": "idle", "class": "idle", "tooltip": "hktools: нет задач", "ts": time.time()})


status = StatusWriter()
