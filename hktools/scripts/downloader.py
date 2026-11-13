"""Загрузка медиа по ссылке через yt-dlp."""

from __future__ import annotations

import json
import os
import re
import shlex

from .. import tools
from ..core.fields import CHECK, COMBO, TEXT, Cond, Field
from ..core.script import Context, QueueItem, Result, Script
from ..media import process
from ..media.process import Cancelled

PROGRESS_RE = re.compile(r"\[download\]\s+(\d+(?:\.\d+)?)%(?:\s+of\s+~?\s*([\d.]+\w+))?(?:.*?at\s+([\d.]+\w+/s))?"
                         r"(?:.*?ETA\s+([\d:]+))?")
DEST_RE = re.compile(r"(?:Destination:|Merging formats into|has already been downloaded|"
                     r"\[ExtractAudio\] Destination:|\[Merger\] Merging formats into)\s*\"?(.+?)\"?$")

DEFAULT_FORMATS = [
    ("Лучшее видео + аудио", "bv*+ba/b", False),
    ("Только аудио (лучшее)", "ba/b", True),
]


def fetch_info(url: str, token: process.CancelToken | None = None) -> dict:
    """Метаданные ссылки: название, доступные высоты, субтитры."""
    ytdlp = tools.require("yt-dlp")
    cmd = [ytdlp, "-J", "--no-playlist", "--no-warnings", url]
    rc, out, err = process.capture(cmd, timeout=90, token=token)
    if rc != 0 or not out.strip():
        raise RuntimeError(err.strip().splitlines()[-1] if err.strip() else "yt-dlp не вернул данные")
    data = json.loads(out)
    heights = sorted({int(f["height"]) for f in data.get("formats", []) if f.get("height") and f.get("vcodec") != "none"},
                     reverse=True)
    formats = list(DEFAULT_FORMATS)
    for h in heights:
        formats.append((f"До {h}p", f"bv*[height<={h}]+ba/b[height<={h}]/b", False))
    subs = sorted(set((data.get("subtitles") or {}).keys()))
    auto = sorted(set((data.get("automatic_captions") or {}).keys()) - set(subs))
    return {
        "title": data.get("title") or url,
        "uploader": data.get("uploader") or data.get("channel") or "",
        "duration": data.get("duration") or 0,
        "formats": formats,
        "subtitles": subs,
        "auto_subtitles": auto[:40],
    }


class MediaDownloader(Script):
    id = "media-downloader"
    name = "Загрузка медиа"
    description = "Скачивание видео и аудио по ссылке через yt-dlp с выбором качества и субтитров"
    category = "Сеть"
    icon = "folder-download-symbolic"
    input_mode = "urls"
    extensions = None
    deps = ("yt-dlp", "ffmpeg")

    def fields(self, values=None):
        F, S = "Формат сохранения", "Субтитры"
        return [
            Field("VideoContainer", "Контейнер видео", COMBO, "Авто", F, options=["Авто", "MP4", "MKV"]),
            Field("AudioFormat", "Формат аудио (режим «только аудио»)", COMBO, "Исходный", F,
                  options=["Исходный", "MP3 (320k)", "M4A (AAC)", "FLAC", "Opus"]),
            Field("ForceOverwrite", "Перезаписывать существующие файлы", CHECK, False, F),
            Field("DownloadSubtitles", "Скачивать субтитры", CHECK, False, S),
            Field("EmbedSubtitles", "Встраивать субтитры в видео", CHECK, True, S,
                  visible=[Cond("DownloadSubtitles", True)]),
            Field("SubLangs", "Языки субтитров по умолчанию", TEXT, "ru,en", S,
                  comment="Используется, если у ссылки не выбран язык; «all» — все",
                  visible=[Cond("DownloadSubtitles", True)]),
            Field("UseBrowserCookies", "Cookies из браузера", COMBO, "Нет", "Дополнительно",
                  options=["Нет", "firefox", "chromium", "chrome", "brave", "vivaldi", "zen"],
                  comment="Для закрытых/возрастных видео (--cookies-from-browser)"),
            Field("AdditionalArgs", "Дополнительные аргументы yt-dlp", TEXT, "", "Дополнительно"),
        ]

    def row_subtitle(self, item: QueueItem) -> str:
        meta = item.extra.get("meta") or {}
        bits = []
        if meta.get("uploader"):
            bits.append(meta["uploader"])
        if item.extra.get("format_label"):
            bits.append(item.extra["format_label"])
        if item.extra.get("sub_lang") and item.extra["sub_lang"] != "none":
            bits.append(f"субтитры: {item.extra['sub_lang']}")
        return " · ".join(bits)

    def process(self, item: QueueItem, ctx: Context) -> Result:
        ytdlp = tools.require("yt-dlp")
        url = item.path
        target = ctx.out_dir or os.path.expanduser(os.environ.get("XDG_DOWNLOAD_DIR", "~/Загрузки"))
        if not ctx.out_dir:
            for cand in (target, os.path.expanduser("~/Downloads")):
                if os.path.isdir(cand):
                    target = cand
                    break
        os.makedirs(target, exist_ok=True)
        fmt = item.extra.get("format_arg") or "bv*+ba/b"
        audio_only = bool(item.extra.get("audio_only")) or fmt in ("ba", "ba/b")
        cmd = [ytdlp, "--newline", "--no-playlist", "-f", fmt, "-P", target, "-o", "%(title)s.%(ext)s",
               "--http-chunk-size", "10M", "--concurrent-fragments", "4", "--retries", "25",
               "--fragment-retries", "25", "--no-mtime"]
        cmd.append("--force-overwrites" if ctx.get("ForceOverwrite") else "--no-force-overwrites")
        if audio_only:
            af = str(ctx.get("AudioFormat", "Исходный"))
            mapping = {"MP3 (320k)": ["--extract-audio", "--audio-format", "mp3", "--audio-quality", "0"],
                       "M4A (AAC)": ["--extract-audio", "--audio-format", "m4a"],
                       "FLAC": ["--extract-audio", "--audio-format", "flac"],
                       "Opus": ["--extract-audio", "--audio-format", "opus"]}
            cmd += mapping.get(af, [])
        else:
            vc = str(ctx.get("VideoContainer", "Авто"))
            if vc == "MP4":
                cmd += ["--merge-output-format", "mp4", "--remux-video", "mp4"]
            elif vc == "MKV":
                cmd += ["--merge-output-format", "mkv"]
        sub_lang = item.extra.get("sub_lang")
        if ctx.get("DownloadSubtitles") or (sub_lang and sub_lang != "none"):
            langs = sub_lang if sub_lang and sub_lang != "none" else str(ctx.get("SubLangs", "ru,en"))
            cmd += ["--write-subs", "--write-auto-subs"]
            cmd += ["--all-subs"] if langs == "all" else ["--sub-langs", langs]
            if ctx.get("EmbedSubtitles", True) and not audio_only:
                cmd.append("--embed-subs")
                if str(ctx.get("VideoContainer", "Авто")) != "MP4" and "--merge-output-format" not in cmd:
                    cmd += ["--merge-output-format", "mkv"]
        ffmpeg = tools.which("ffmpeg")
        if ffmpeg:
            cmd += ["--ffmpeg-location", ffmpeg]
        cookies = str(ctx.get("UseBrowserCookies", "Нет"))
        if cookies != "Нет":
            cmd += ["--cookies-from-browser", cookies]
        extra = str(ctx.get("AdditionalArgs", "")).strip()
        if extra:
            try:
                cmd += shlex.split(extra)
            except ValueError as exc:
                return Result.failed(f"❌ Ошибка в дополнительных аргументах: {exc}")
        cmd += ["--", url]
        outputs: list[str] = []
        phase = [0]

        def on_line(line: str) -> None:
            m = PROGRESS_RE.search(line)
            if m:
                pct = float(m.group(1))
                if pct >= 100:
                    phase[0] += 1
                bits = [f"{pct:.1f}%"]
                if m.group(2):
                    bits.append(m.group(2))
                if m.group(3):
                    bits.append(m.group(3))
                if m.group(4):
                    bits.append(f"ETA {m.group(4)}")
                # Видео и аудио качаются отдельно: делим шкалу
                share = 50 if not audio_only and "+" in fmt else 100
                base = min(phase[0], 1) * share if share == 50 else 0
                ctx.progress(min(99, base + pct * share / 100), "Загрузка · " + " · ".join(bits))
                return
            d = DEST_RE.search(line)
            if d:
                path = d.group(1).strip()
                if path and path not in outputs:
                    outputs.append(path)
            if "[Merger]" in line or "[ExtractAudio]" in line or "[EmbedSubtitle]" in line:
                ctx.progress(99, "Постобработка…")

        res = process.run(cmd, ctx.token, on_stdout=on_line, on_stderr=lambda l: None, source=self.name)
        if res.cancelled:
            raise Cancelled()
        final = [p if os.path.isabs(p) else os.path.join(target, p) for p in outputs]
        final = [p for p in final if os.path.exists(p)]
        # после склейки оставляем только итоговый файл
        merged = [p for p in final if not re.search(r"\.f\d+\.\w+$", p)]
        final = merged or final
        if res.ok:
            ctx.progress(100, "Готово")
            name = os.path.basename(final[-1]) if final else (item.extra.get("meta") or {}).get("title", url)
            return Result.done(*final[-1:], msg=f"✅ Скачано: {name}")
        return Result.failed(f"❌ Ошибка yt-dlp\n{res.error_text()}")
