from __future__ import annotations

import os

from ..core.fields import COMBO, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script
from .common import delete_original_field, finish_ffmpeg

FORMATS = {"MP4": ".mp4", "MKV": ".mkv", "MOV": ".mov", "WEBM": ".webm", "AVI": ".avi", "TS": ".ts"}
TEXT_SUBS = {"subrip", "ass", "ssa", "webvtt", "mov_text", "text"}
WEBM_VIDEO = {"vp8", "vp9", "av1"}
WEBM_AUDIO = {"opus", "vorbis"}


class ContainerConvert(Script):
    id = "container-convert"
    name = "Конвертация контейнера"
    description = "Перемещение видео- и аудиодорожек в другой контейнер без перекодирования (-c copy)"
    category = "Видео"
    icon = "emblem-synchronizing-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        return [
            Field("target_format", "Целевой формат", COMBO, "MP4", "Общие", options=list(FORMATS)),
            delete_original_field(),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        fmt = str(ctx.get("target_format", "MP4"))
        out = ctx.output_path(item.path, ext=FORMATS.get(fmt, ".mp4"))
        if skip := ctx.skip_if_exists(out):
            return skip
        info = ctx.probe()
        maps: list[str] = []
        codec_args: list[str] = []
        notes: list[str] = []
        experimental = False
        sub_out = 0
        for t in info.media_tracks:
            convert_sub: str | None = None
            if fmt in ("MP4", "MOV"):
                if t.type == "subtitle":
                    if t.codec not in TEXT_SUBS:
                        notes.append(f"#{t.index} {t.codec} (нужен текстовый формат)")
                        continue
                    convert_sub = "mov_text"
                if t.type == "audio" and t.codec in ("truehd", "mlp"):
                    experimental = True
            elif fmt == "WEBM":
                if t.type == "video" and t.codec not in WEBM_VIDEO or t.type == "audio" and t.codec not in WEBM_AUDIO:
                    notes.append(f"#{t.index} {t.codec}")
                    continue
                if t.type == "subtitle":
                    if t.codec not in TEXT_SUBS:
                        notes.append(f"#{t.index} {t.codec}")
                        continue
                    convert_sub = None if t.codec == "webvtt" else "webvtt"
            elif fmt == "AVI" and t.type == "subtitle":
                notes.append(f"#{t.index} субтитры")
                continue
            elif fmt == "TS" and t.type == "subtitle" and t.codec in TEXT_SUBS:
                notes.append(f"#{t.index} текстовые субтитры")
                continue
            maps += ["-map", f"0:{t.index}"]
            if t.type == "subtitle":
                if convert_sub:
                    codec_args += [f"-c:s:{sub_out}", convert_sub]
                sub_out += 1
        if not maps:
            return Result.failed("❌ Нет дорожек, совместимых с выбранным контейнером")
        args = ["-i", item.path, *maps]
        if fmt == "MKV" and info.attachments:
            args += ["-map", "0:t?"]
        args += ["-c", "copy", *codec_args, "-map_metadata", "0", "-map_chapters", "0"]
        if fmt in ("MP4", "MOV"):
            args += ["-movflags", "+faststart"]
            if info.video and info.video[0].codec == "hevc":
                args += ["-tag:v", "hvc1"]
        if experimental:
            args += ["-strict", "-2"]
        args.append(out)
        for n in notes:
            ctx.log("WARNING", f"{item.name}: дорожка {n} не поддерживается {fmt} — пропущена")
        res = ctx.ffmpeg(args, info.duration, f"Ремукс в {fmt}")
        result = finish_ffmpeg(ctx, res, item.path, out, f"✅ Сконвертирован в {fmt}: {os.path.basename(out)}")
        if notes and result.status == "done":
            result.status = "partial"
            result.messages.insert(0, "⚠ Пропущены несовместимые дорожки: " + "; ".join(notes))
        return result
