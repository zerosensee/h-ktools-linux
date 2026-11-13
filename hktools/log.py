"""Журнал приложения: файл с ротацией + кольцевой буфер для окна журнала."""

from __future__ import annotations

import logging
import logging.handlers
import threading
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Callable

from . import paths

LEVELS = ("DEBUG", "INFO", "SUCCESS", "WARNING", "ERROR")
SUCCESS = 25
logging.addLevelName(SUCCESS, "SUCCESS")


@dataclass
class LogEntry:
    ts: float
    level: str
    source: str
    message: str
    details: str = ""
    seq: int = field(default=0)

    @property
    def time_str(self) -> str:
        return time.strftime("%H:%M:%S", time.localtime(self.ts))


class LogHub:
    """Потокобезопасный журнал с подписчиками (UI подписывается через GLib.idle_add)."""

    def __init__(self, capacity: int = 5000) -> None:
        self._lock = threading.Lock()
        self._entries: deque[LogEntry] = deque(maxlen=capacity)
        self._listeners: list[Callable[[LogEntry], None]] = []
        self._seq = 0
        self._file_logger = logging.getLogger("hktools")
        self._file_logger.setLevel(logging.DEBUG)
        self._file_logger.propagate = False
        if not self._file_logger.handlers:
            try:
                handler = logging.handlers.RotatingFileHandler(
                    paths.state_dir() / "hktools.log", maxBytes=2_000_000, backupCount=5, encoding="utf-8"
                )
                handler.setFormatter(logging.Formatter("%(asctime)s [%(levelname)s] %(message)s"))
                self._file_logger.addHandler(handler)
            except OSError:
                pass

    def add_listener(self, cb: Callable[[LogEntry], None]) -> None:
        with self._lock:
            self._listeners.append(cb)

    def remove_listener(self, cb: Callable[[LogEntry], None]) -> None:
        with self._lock:
            if cb in self._listeners:
                self._listeners.remove(cb)

    def entries(self) -> list[LogEntry]:
        with self._lock:
            return list(self._entries)

    def clear(self) -> None:
        with self._lock:
            self._entries.clear()

    def write(self, level: str, message: str, source: str = "hktools", details: str = "") -> None:
        level = level.upper()
        with self._lock:
            self._seq += 1
            entry = LogEntry(time.time(), level, source, message, details, self._seq)
            self._entries.append(entry)
            listeners = list(self._listeners)
        py_level = {"DEBUG": logging.DEBUG, "INFO": logging.INFO, "SUCCESS": SUCCESS,
                    "WARNING": logging.WARNING, "ERROR": logging.ERROR}.get(level, logging.INFO)
        text = f"[{source}] {message}" + (f"\n{details}" if details else "")
        self._file_logger.log(py_level, text)
        for cb in listeners:
            try:
                cb(entry)
            except Exception:  # noqa: BLE001 — слушатель не должен ронять журнал
                pass

    def debug(self, msg: str, source: str = "hktools", details: str = "") -> None:
        self.write("DEBUG", msg, source, details)

    def info(self, msg: str, source: str = "hktools", details: str = "") -> None:
        self.write("INFO", msg, source, details)

    def success(self, msg: str, source: str = "hktools", details: str = "") -> None:
        self.write("SUCCESS", msg, source, details)

    def warning(self, msg: str, source: str = "hktools", details: str = "") -> None:
        self.write("WARNING", msg, source, details)

    def error(self, msg: str, source: str = "hktools", details: str = "") -> None:
        self.write("ERROR", msg, source, details)


log = LogHub()
