"""Хранилище настроек (~/.config/hktools/settings.json)."""

from __future__ import annotations

import copy
import json
import os
import threading
from typing import Any

from . import paths
from .log import log

GENERAL_DEFAULTS: dict[str, Any] = {
    # Вывод
    "overwrite_existing": False,
    "use_auto_subfolder": True,
    "output_subfolder": "HKTools_Result",
    "clear_list_on_add": False,
    # Параллельная обработка
    "enable_parallel": False,
    "max_parallel": 2,
    # Пакетное переименование результатов (аналог PowerRename)
    "rename_enabled": False,
    "rename_search": "",
    "rename_replace": "",
    "rename_use_regex": True,
    "rename_case_sensitive": False,
    # Интерфейс
    "theme": "system",  # system | light | dark
    "show_settings_sidebar": True,
    "last_dir": "",
    # Интеграция с рабочим столом
    "notify_on_finish": True,
    "notify_only_unfocused": True,
    "status_file": True,  # $XDG_RUNTIME_DIR/hktools/status.json для Waybar/Quickshell
    # Внешние утилиты: пустая строка = искать в PATH
    "tool_paths": {},
    "qaac_command": "",  # например: "wine /opt/qaac/qaac64.exe"
    "vaapi_device": "",  # пусто = автоопределение /dev/dri/renderD*
    "whisper_models_dir": "",
    "timing_history": [],
}


class Settings:
    def __init__(self) -> None:
        self._path = paths.config_dir() / "settings.json"
        self._lock = threading.RLock()
        self._data: dict[str, Any] = {"general": {}, "scripts": {}}
        self._save_timer: threading.Timer | None = None
        self.load()

    # --- файл ---------------------------------------------------------------
    def load(self) -> None:
        with self._lock:
            try:
                with open(self._path, encoding="utf-8") as fh:
                    raw = json.load(fh)
                if isinstance(raw, dict):
                    self._data["general"] = dict(raw.get("general") or {})
                    self._data["scripts"] = dict(raw.get("scripts") or {})
            except FileNotFoundError:
                pass
            except (OSError, ValueError) as exc:
                log.warning(f"Файл настроек повреждён, используются значения по умолчанию: {exc}", "Настройки")
                try:
                    os.replace(self._path, self._path.with_suffix(".json.bak"))
                except OSError:
                    pass

    def save_now(self) -> None:
        with self._lock:
            if self._save_timer:
                self._save_timer.cancel()
                self._save_timer = None
            tmp = self._path.with_suffix(".json.tmp")
            try:
                with open(tmp, "w", encoding="utf-8") as fh:
                    json.dump(self._data, fh, ensure_ascii=False, indent=2)
                os.replace(tmp, self._path)
            except OSError as exc:
                log.error(f"Не удалось сохранить настройки: {exc}", "Настройки")

    def _schedule_save(self) -> None:
        with self._lock:
            if self._save_timer:
                self._save_timer.cancel()
            self._save_timer = threading.Timer(0.6, self.save_now)
            self._save_timer.daemon = True
            self._save_timer.start()

    # --- общие настройки ----------------------------------------------------
    def get(self, key: str, default: Any = None) -> Any:
        with self._lock:
            if key in self._data["general"]:
                return copy.deepcopy(self._data["general"][key])
            if key in GENERAL_DEFAULTS:
                return copy.deepcopy(GENERAL_DEFAULTS[key])
            return default

    def set(self, key: str, value: Any) -> None:
        with self._lock:
            self._data["general"][key] = copy.deepcopy(value)
        self._schedule_save()

    def tool_path(self, name: str) -> str:
        return str((self.get("tool_paths") or {}).get(name, "") or "")

    def set_tool_path(self, name: str, value: str) -> None:
        tp = dict(self.get("tool_paths") or {})
        if value:
            tp[name] = value
        else:
            tp.pop(name, None)
        self.set("tool_paths", tp)

    # --- настройки инструментов --------------------------------------------
    def script_values(self, script_id: str) -> dict[str, Any]:
        with self._lock:
            return copy.deepcopy(self._data["scripts"].get(script_id, {}))

    def set_script_value(self, script_id: str, key: str, value: Any) -> None:
        with self._lock:
            self._data["scripts"].setdefault(script_id, {})[key] = copy.deepcopy(value)
        self._schedule_save()

    def reset_script(self, script_id: str) -> None:
        with self._lock:
            self._data["scripts"].pop(script_id, None)
        self._schedule_save()


settings = Settings()
