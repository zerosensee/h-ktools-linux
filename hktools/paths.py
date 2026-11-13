"""XDG-пути приложения."""

from __future__ import annotations

import os
import tempfile
from pathlib import Path

_APP = "hktools"


def _xdg(var: str, fallback: str) -> Path:
    value = os.environ.get(var)
    if value and os.path.isabs(value):
        return Path(value)
    return Path.home() / fallback


def config_dir() -> Path:
    p = _xdg("XDG_CONFIG_HOME", ".config") / _APP
    p.mkdir(parents=True, exist_ok=True)
    return p


def data_dir() -> Path:
    p = _xdg("XDG_DATA_HOME", ".local/share") / _APP
    p.mkdir(parents=True, exist_ok=True)
    return p


def cache_dir() -> Path:
    p = _xdg("XDG_CACHE_HOME", ".cache") / _APP
    p.mkdir(parents=True, exist_ok=True)
    return p


def state_dir() -> Path:
    p = _xdg("XDG_STATE_HOME", ".local/state") / _APP
    p.mkdir(parents=True, exist_ok=True)
    return p


def runtime_dir() -> Path:
    base = os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir()
    p = Path(base) / _APP
    p.mkdir(parents=True, exist_ok=True)
    return p


def temp_dir(prefix: str = "kt_") -> Path:
    """Создать временную папку (вызывающий сам удаляет её)."""
    return Path(tempfile.mkdtemp(prefix=prefix))


def status_file() -> Path:
    return runtime_dir() / "status.json"


def whisper_models_dir(override: str | None = None) -> Path:
    p = Path(os.path.expanduser(override)) if override else data_dir() / "whisper-models"
    p.mkdir(parents=True, exist_ok=True)
    return p
