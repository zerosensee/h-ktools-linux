"""Базовый класс инструмента, контекст выполнения и результат."""

from __future__ import annotations

import datetime as _dt
import itertools
import os
import re
import shutil
import threading
import uuid
from dataclasses import dataclass, field
from typing import Any, Callable

from .. import paths
from ..config import settings as app_settings
from ..log import log
from ..media import ffmpeg as ff
from ..media import probe as probe_mod
from ..media.process import CancelToken, Cancelled, ProcResult
from . import desktop
from .fields import Field, coerce, defaults, walk

VIDEO_CONTAINERS = {".mkv", ".mp4", ".mov", ".webm", ".avi", ".m2ts", ".mts", ".ts", ".m4s", ".m4v", ".wmv", ".flv"}
VIDEO_STREAMS = {".hevc", ".h264", ".h265", ".264", ".265", ".vc1", ".m2v", ".avc", ".ivf"}
AUDIO_CONTAINERS = {".mka", ".m4a"}
AUDIO_STREAMS = {".mp3", ".flac", ".wav", ".ogg", ".oga", ".wma", ".aiff", ".aif", ".alac", ".ape", ".opus", ".ac3",
                 ".eac3", ".ec3", ".dts", ".wv", ".aac", ".thd", ".truehd", ".mlp", ".dtshd", ".pcm", ".mp2", ".m2a",
                 ".w64", ".caf", ".tta"}
SUBTITLE_EXTS = {".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".sup"}
AUDIO_ALL = AUDIO_CONTAINERS | AUDIO_STREAMS
ALL_MEDIA = VIDEO_CONTAINERS | VIDEO_STREAMS | AUDIO_ALL

CATEGORIES = ["Видео", "Аудио", "Контейнеры", "Субтитры", "Сеть", "Инструменты"]
CATEGORY_COLORS = {
    "Видео": "#1B9DE3", "Аудио": "#28CAC6", "Контейнеры": "#EB6E4D",
    "Субтитры": "#A878E8", "Сеть": "#E84D6E", "Инструменты": "#F0A330",
}

_item_ids = itertools.count(1)


@dataclass
class QueueItem:
    path: str
    extra: dict[str, Any] = field(default_factory=dict)
    id: int = field(default_factory=lambda: next(_item_ids))
    status: str = "pending"   # pending | running | done | partial | failed | skipped | cancelled
    progress: float = 0.0
    message: str = ""
    outputs: list[str] = field(default_factory=list)
    info: probe_mod.MediaInfo | None = None
    badge: str = ""

    @property
    def name(self) -> str:
        return os.path.basename(self.path.rstrip("/")) or self.path

    @property
    def ext(self) -> str:
        return os.path.splitext(self.path)[1].lower()

    @property
    def stem(self) -> str:
        return os.path.splitext(os.path.basename(self.path))[0]

    def reset(self) -> None:
        self.status = "pending"
        self.progress = 0.0
        self.message = ""
        self.outputs = []


@dataclass
class Result:
    status: str = "done"
    messages: list[str] = field(default_factory=list)
    outputs: list[str] = field(default_factory=list)
    data: Any = None

    @classmethod
    def done(cls, *outputs: str, msg: str = "", data: Any = None) -> "Result":
        return cls("done", [msg] if msg else [], [o for o in outputs if o], data)

    @classmethod
    def failed(cls, msg: str) -> "Result":
        return cls("failed", [msg])

    @classmethod
    def skipped(cls, msg: str) -> "Result":
        return cls("skipped", [msg])

    @classmethod
    def cancelled(cls) -> "Result":
        return cls("cancelled", ["Отменено пользователем"])


class Batch:
    """Общее состояние одного запуска (резерв путей, счётчик для переименования, вся очередь)."""

    def __init__(self, items: list[QueueItem]) -> None:
        self.items = items
        self._lock = threading.Lock()
        self._reserved: set[str] = set()
        self._counter = 0

    def next_number(self) -> int:
        with self._lock:
            self._counter += 1
            return self._counter

    def reserve(self, path: str) -> str:
        with self._lock:
            base, ext = os.path.splitext(path)
            candidate = path
            n = 1
            while candidate in self._reserved:
                candidate = f"{base}_{n}{ext}"
                n += 1
            self._reserved.add(candidate)
            return candidate


def apply_rename(stem: str, number: int, cfg: dict[str, Any] | None = None) -> str:
    """Пакетное переименование результата (аналог PowerRename из Windows-версии)."""
    cfg = cfg or {}
    if cfg.get("local_rename_override"):
        pattern = cfg.get("local_rename_search", "")
        replacement = cfg.get("local_rename_replace", "")
        use_regex = cfg.get("local_rename_use_regex", True)
        case_sensitive = cfg.get("local_rename_case_sensitive", False)
        enabled = bool(pattern)
    else:
        enabled = bool(app_settings.get("rename_enabled"))
        pattern = app_settings.get("rename_search") or ""
        replacement = app_settings.get("rename_replace") or ""
        use_regex = bool(app_settings.get("rename_use_regex"))
        case_sensitive = bool(app_settings.get("rename_case_sensitive"))
    if not enabled or not pattern:
        return stem
    now = _dt.datetime.now()
    repl = replacement
    repl = re.sub(r"\$\{num:(\d+)\}", lambda m: str(number).zfill(int(m.group(1))), repl)
    for token, value in (("${num}", str(number)), ("${ruuidv4}", str(uuid.uuid4())), ("${YYYY}", f"{now:%Y}"),
                         ("${MM}", f"{now:%m}"), ("${DD}", f"{now:%d}"), ("${hh}", f"{now:%H}"),
                         ("${mm}", f"{now:%M}"), ("${ss}", f"{now:%S}")):
        repl = repl.replace(token, value)
    flags = 0 if case_sensitive else re.IGNORECASE
    try:
        if use_regex:
            # .NET-стиль $1 → \1 для Python
            py_repl = re.sub(r"\$(\d+)", r"\\\1", repl.replace("\\", "\\\\"))
            return re.sub(pattern, py_repl, stem, flags=flags) or stem
        if case_sensitive:
            return stem.replace(pattern, repl) or stem
        return re.sub(re.escape(pattern), lambda _m: repl, stem, flags=flags) or stem
    except re.error as exc:
        log.warning(f"Ошибка в шаблоне переименования: {exc}", "Переименование")
        return stem


class Context:
    """Всё, что нужно инструменту для обработки одного элемента очереди."""

    def __init__(
        self,
        script: "Script",
        item: QueueItem,
        values: dict[str, Any],
        batch: Batch,
        token: CancelToken,
        out_dir: str | None,
        index: int,
        total: int,
        on_progress: Callable[[QueueItem, float, str], None],
        ask: Callable[..., Any] | None = None,
    ) -> None:
        self.script = script
        self.item = item
        self.values = values
        self.batch = batch
        self.token = token
        self.out_dir = out_dir or None
        self.index = index
        self.total = total
        self._on_progress = on_progress
        self.ask = ask  # UI-колбэк для интерактивных вопросов (None в CLI)
        self._temps: list[str] = []
        self.overwrite = bool(app_settings.get("overwrite_existing"))

    # --- утилиты ---
    def get(self, key: str, default: Any = None) -> Any:
        return self.values.get(key, default)

    def progress(self, percent: float, message: str = "") -> None:
        self._on_progress(self.item, max(0.0, min(100.0, percent)), message)

    def log(self, level: str, message: str, details: str = "") -> None:
        log.write(level, message, self.script.name, details)

    def check(self) -> None:
        self.token.check()

    @property
    def cancelled(self) -> bool:
        return self.token.cancelled

    def temp_dir(self, prefix: str = "kt_") -> str:
        d = str(paths.temp_dir(prefix))
        self._temps.append(d)
        return d

    def cleanup(self) -> None:
        for d in self._temps:
            shutil.rmtree(d, ignore_errors=True)
        self._temps.clear()

    def probe(self, path: str | None = None) -> probe_mod.MediaInfo:
        target = path or self.item.path
        if path is None and self.item.info is not None:
            return self.item.info
        info = probe_mod.probe(target)
        if path is None:
            self.item.info = info
        return info

    # --- пути вывода ---
    def target_dir(self, input_path: str | None = None) -> str:
        src_dir = os.path.dirname(os.path.abspath(input_path or self.item.path))
        if self.out_dir:
            d = self.out_dir
        elif app_settings.get("use_auto_subfolder"):
            d = os.path.join(src_dir, app_settings.get("output_subfolder") or "HKTools_Result")
        else:
            d = src_dir
        os.makedirs(d, exist_ok=True)
        return d

    def output_path(self, input_path: str | None = None, ext: str | None = None, suffix: str = "",
                    stem: str | None = None, directory: str | None = None) -> str:
        """Безопасный путь результата (аналог GetSafeOutputPath).

        Подпапка результатов, пакетное переименование, защита исходника и коллизий в пакете.
        """
        input_path = input_path or self.item.path
        in_abs = os.path.abspath(input_path)
        base_stem = stem if stem is not None else os.path.splitext(os.path.basename(input_path))[0]
        ext = ext if ext is not None else os.path.splitext(input_path)[1]
        if ext and not ext.startswith("."):
            ext = "." + ext
        directory = directory or self.target_dir(input_path)
        new_stem = apply_rename(base_stem, self.batch.next_number(), self.values) + suffix
        out = os.path.join(directory, new_stem + ext)
        if os.path.abspath(out) == in_abs:
            out = os.path.join(directory, f"{new_stem}_processed{ext}")
        return self.batch.reserve(out)

    def skip_if_exists(self, path: str) -> Result | None:
        if os.path.exists(path) and not self.overwrite:
            self.progress(100, "Пропущен: файл уже существует")
            return Result.skipped(f"⏭ ПРОПУСК (файл существует): {os.path.basename(path)}")
        return None

    # --- запуск ffmpeg с прогрессом ---
    def ffmpeg(self, args: list[str], duration: float = 0.0, label: str = "Обработка",
               cwd: str | None = None, base: float = 0.0, span: float = 100.0) -> ProcResult:
        def on_prog(p: ff.Progress) -> None:
            self.progress(base + p.percent * span / 100.0, p.label(label))

        res = ff.run_ffmpeg(args, self.token, duration=duration, on_progress=on_prog, cwd=cwd,
                            source=self.script.name)
        if res.cancelled:
            raise Cancelled()
        return res

    # --- исходники ---
    def finalize_source(self, source: str, result: str, results: Result) -> str:
        """Обработать опции «заменить исходник» / «удалить исходник». Возвращает итоговый путь."""
        if self.values.get("overwrite_source") and not self.out_dir:
            return self.replace_source(source, result, results)
        if self.values.get("delete_source") or self.values.get("delete_original"):
            if desktop.trash(source):
                results.messages.append(f"🗑 Исходный файл перемещён в корзину: {os.path.basename(source)}")
            else:
                results.messages.append(f"⚠ Не удалось удалить исходный файл: {os.path.basename(source)}")
        return result

    def replace_source(self, source: str, result: str, results: Result) -> str:
        """Заменить исходник результатом только после проверки результата."""
        try:
            if not os.path.isfile(result) or os.path.getsize(result) == 0:
                raise RuntimeError("результат пустой")
            probe_mod.probe(result, use_cache=False)
            target = os.path.splitext(source)[0] + os.path.splitext(result)[1]
            os.replace(result, target)
            if os.path.abspath(target) != os.path.abspath(source) and os.path.exists(source):
                os.remove(source)
            results.messages.append(f"♻ Исходный файл заменён результатом: {os.path.basename(target)}")
            # подчистить пустую папку результатов
            try:
                os.rmdir(os.path.dirname(result))
            except OSError:
                pass
            return target
        except Exception as exc:  # noqa: BLE001
            results.status = "partial"
            results.messages.append(f"❌ Исходник не заменён ({exc}), результат сохранён: {os.path.basename(result)}")
            return result


def remove_quiet(path: str | None) -> None:
    if path and os.path.exists(path):
        try:
            os.remove(path)
        except OSError:
            pass


class Script:
    """Базовый класс инструмента."""

    id: str = ""
    name: str = ""
    description: str = ""
    category: str = "Инструменты"
    icon: str = "applications-multimedia-symbolic"
    extensions: set[str] | None = None     # None = любые файлы
    input_mode: str = "files"               # files | urls | none
    deps: tuple[str, ...] = ("ffmpeg", "ffprobe")
    track_selection: str | None = None      # None | extract | manage | replace
    keywords: str = ""

    def fields(self, values: dict[str, Any] | None = None) -> list[Field]:
        return []

    def accepts(self, path: str) -> bool:
        if self.extensions is None:
            return True
        return os.path.splitext(path)[1].lower() in self.extensions

    def default_values(self) -> dict[str, Any]:
        return defaults(self.fields())

    def values(self, overrides: dict[str, Any] | None = None) -> dict[str, Any]:
        """Текущие значения: дефолты схемы ← сохранённые ← переопределения (CLI)."""
        vals = self.default_values()
        vals.update(app_settings.script_values(self.id))
        all_fields = {f.key: f for f in walk(self.fields(vals))}
        if overrides:
            for k, v in overrides.items():
                f = all_fields.get(k)
                vals[k] = coerce(f, v) if f else v
        return vals

    def prepare_batch(self, items: list[QueueItem], values: dict[str, Any]) -> list[QueueItem]:
        """Отфильтровать/подготовить элементы перед запуском. По умолчанию — все принимаемые."""
        return [i for i in items if self.input_mode != "files" or self.accepts(i.path)]

    def process(self, item: QueueItem, ctx: Context) -> Result:  # pragma: no cover - абстрактный
        raise NotImplementedError

    def missing_deps(self) -> list[str]:
        from .. import tools  # noqa: PLC0415

        return [d for d in self.deps if not tools.which(d)]

    def skip_reason(self, item: QueueItem) -> str:
        return "Не подходит для этого инструмента"

    # Хуки для интерфейса (в CLI не используются)
    def row_subtitle(self, item: QueueItem) -> str:
        return ""
