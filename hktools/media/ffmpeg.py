"""Обёртки над ffmpeg: прогресс, поиск черных полос, извлечение вложений."""

from __future__ import annotations

import os
import re
import time
from dataclasses import dataclass
from typing import Callable

from .. import tools
from . import process

# Стерео-даунмикс с коэффициентами HandBrake (центр/тылы 0.7071, LFE отбрасывается).
# Фильтр pan обходит нормализацию матрицы libswresample и сохраняет громкость фронта.
STEREO_DOWNMIX_PAN = "pan=stereo|FL=FL+0.7071*FC+0.7071*BL+0.7071*SL|FR=FR+0.7071*FC+0.7071*BR+0.7071*SR"


@dataclass
class Progress:
    percent: float = 0.0
    time_s: float = 0.0
    speed: float | None = None
    fps: float | None = None
    bitrate: str = ""
    eta: str = ""

    def label(self, prefix: str = "") -> str:
        parts = [prefix] if prefix else []
        parts.append(f"{self.percent:.1f}%" if self.percent > 0 else _fmt_time(self.time_s))
        if self.fps:
            parts.append(f"{self.fps:.0f} fps")
        if self.speed:
            parts.append(f"{self.speed:.2f}x")
        if self.eta:
            parts.append(f"ETA {self.eta}")
        return " · ".join(parts)


def _fmt_time(sec: float) -> str:
    sec = max(0, int(sec))
    h, rem = divmod(sec, 3600)
    m, s = divmod(rem, 60)
    return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def ffmpeg_bin() -> str:
    return tools.require("ffmpeg")


def run_ffmpeg(
    args: list[str],
    token: process.CancelToken | None = None,
    *,
    duration: float = 0.0,
    on_progress: Callable[[Progress], None] | None = None,
    overwrite: bool = True,
    cwd: str | None = None,
    loglevel: str = "error",
    source: str = "FFmpeg",
) -> process.ProcResult:
    """Запустить ffmpeg с машиночитаемым прогрессом (-progress pipe:1).

    `args` — всё, что идёт после базовых флагов (входы, фильтры, выход).
    """
    cmd = [ffmpeg_bin(), "-hide_banner", "-nostdin", "-loglevel", loglevel,
           "-progress", "pipe:1", "-nostats", "-y" if overwrite else "-n", *args]
    state: dict[str, str] = {}
    started = time.monotonic()
    last_emit = [0.0]

    def on_line(line: str) -> None:
        if "=" not in line:
            return
        key, _, value = line.partition("=")
        key = key.strip()
        state[key] = value.strip()
        if key != "progress" or on_progress is None:
            return
        now = time.monotonic()
        if value.strip() != "end" and now - last_emit[0] < 0.25:
            return
        last_emit[0] = now
        p = Progress()
        out_us = state.get("out_time_us") or state.get("out_time_ms")
        try:
            p.time_s = max(0.0, int(out_us) / 1_000_000) if out_us and out_us != "N/A" else 0.0
        except ValueError:
            p.time_s = 0.0
        if duration > 0:
            p.percent = min(100.0, p.time_s / duration * 100.0)
        try:
            spd = state.get("speed", "").rstrip("x")
            p.speed = float(spd) if spd and spd != "N/A" else None
        except ValueError:
            p.speed = None
        try:
            fps = float(state.get("fps", "0"))
            p.fps = fps if fps > 0 else None
        except ValueError:
            p.fps = None
        br = state.get("bitrate", "")
        p.bitrate = "" if br in ("", "N/A") else br
        if duration > 0 and p.speed and p.speed > 0:
            remaining = (duration - p.time_s) / p.speed
            p.eta = _fmt_time(remaining)
        elif duration > 0 and p.percent > 1:
            elapsed = now - started
            p.eta = _fmt_time(elapsed / p.percent * (100 - p.percent))
        if value.strip() == "end":
            p.percent = 100.0 if duration > 0 else p.percent
        on_progress(p)

    return process.run(cmd, token, on_stdout=on_line, cwd=cwd, source=source)


def filter_escape(path: str) -> str:
    """Экранирование пути для опций фильтров ffmpeg (subtitles=filename='…')."""
    return (path.replace("\\", "\\\\").replace(":", "\\:").replace("'", "\\'")
            .replace(",", "\\,").replace("[", "\\[").replace("]", "\\]").replace(";", "\\;"))


_CROP_RE = re.compile(r"crop=(\d+):(\d+):(\d+):(\d+)")


def detect_crop(
    path: str,
    seek: float,
    frames: int,
    limit: float,
    round_to: int,
    skip: int,
    reset: int,
    mode: str,
    token: process.CancelToken | None = None,
) -> tuple[int, int, int, int] | None:
    params = [f"limit={limit:.4f}", f"round={max(1, round_to)}", f"reset={reset}"]
    if skip:
        params.append(f"skip={skip}")
    if mode and mode != "black":
        params.append(f"mode={mode}")
    cmd = [ffmpeg_bin(), "-hide_banner", "-nostdin", "-loglevel", "info"]
    if seek > 0:
        cmd += ["-ss", f"{seek:.3f}"]
    cmd += ["-i", path, "-map", "0:v:0", "-frames:v", str(frames), "-vf", "cropdetect=" + ":".join(params),
            "-an", "-sn", "-f", "null", "-"]
    found: list[tuple[int, int, int, int]] = []

    def on_err(line: str) -> None:
        m = _CROP_RE.search(line)
        if m:
            found.append(tuple(int(x) for x in m.groups()))  # type: ignore[arg-type]

    res = process.run(cmd, token, on_stderr=on_err, source="cropdetect")
    if res.cancelled or not found:
        return None
    return found[-1]


def dump_attachments(path: str, out_dir: str, token: process.CancelToken | None = None) -> list[str]:
    """Извлечь все вложения (шрифты) в out_dir. Возвращает список файлов."""
    os.makedirs(out_dir, exist_ok=True)
    before = set(os.listdir(out_dir))
    cmd = [ffmpeg_bin(), "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
           "-dump_attachment:t", "", "-i", path, "-t", "0", "-f", "null", "-"]
    process.run(cmd, token, cwd=out_dir, source="FFmpeg")
    after = set(os.listdir(out_dir))
    return [os.path.join(out_dir, f) for f in sorted(after - before)]


def dump_attachment(path: str, stream_index: int, out_file: str, token: process.CancelToken | None = None) -> bool:
    cmd = [ffmpeg_bin(), "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
           f"-dump_attachment:{stream_index}", out_file, "-i", path, "-t", "0", "-f", "null", "-"]
    process.run(cmd, token, source="FFmpeg")
    return os.path.isfile(out_file) and os.path.getsize(out_file) > 0
