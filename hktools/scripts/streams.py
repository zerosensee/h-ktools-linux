"""Управление потоками, замена потоков и разборка контейнера (выбор дорожек в UI)."""

from __future__ import annotations

import os
import re

from .. import tools
from ..core.fields import CHECK, COMBO, TEXT, Field
from ..core.script import VIDEO_CONTAINERS, Context, QueueItem, Result, Script, remove_quiet
from ..media import ffmpeg as ff
from ..media.probe import SUB_CONVERT, MediaInfo, Track, raw_extension
from .common import finish_ffmpeg, output_fields

TYPE_CHAR = {"video": "v", "audio": "a", "subtitle": "s"}


def selected_ids(item: QueueItem) -> list[int]:
    return sorted(int(i) for i in item.extra.get("selected", []))


def track_summary(item: QueueItem) -> str:
    info = item.info
    if info is None:
        return ""
    sel = selected_ids(item)
    total = len(info.media_tracks) + len(info.fonts)
    return f"выбрано дорожек: {len(sel)} из {total}"


def _disposition(t: Track) -> str:
    flags = [name for name, on in (("default", t.default), ("forced", t.forced), ("hearing_impaired", t.hearing_impaired),
                                   ("comment", t.commentary), ("original", t.original)) if on]
    return "+".join(flags) if flags else "0"


class StreamManagement(Script):
    id = "stream-management"
    name = "Управление потоками"
    description = "Удаление или сохранение выбранных дорожек (видео, аудио, субтитры) в MKV и MP4 без перекодирования"
    category = "Контейнеры"
    icon = "view-list-symbolic"
    extensions = VIDEO_CONTAINERS | {".mka", ".m4a"}
    track_selection = "manage"

    def fields(self, values=None):
        return [
            Field("mode", "Режим работы", COMBO, "Удалить выбранные", "Режим",
                  options=["Удалить выбранные", "Сохранить только выбранные"]),
            Field("use_m4a", "Одиночную аудиодорожку сохранять в M4A", CHECK, False, "Аудио",
                  comment="Если в результате остаётся ровно одна аудиодорожка"),
            *output_fields(),
        ]

    def row_subtitle(self, item: QueueItem) -> str:
        return track_summary(item)

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        sel = set(selected_ids(item))
        keep_mode = ctx.get("mode") == "Сохранить только выбранные"
        media = info.media_tracks
        kept = [t for t in media if (t.index in sel) == keep_mode]
        if not sel:
            return Result.skipped("⏭ Дорожки не выбраны — откройте «Дорожки» у файла")
        if not kept:
            return Result.failed("❌ После операции не осталось ни одной дорожки")
        if len(kept) == len(media):
            return Result.skipped("⏭ Нечего менять: набор дорожек совпадает с исходным")
        single_audio = len(kept) == 1 and kept[0].type == "audio"
        ext = item.ext
        codec_args = ["-c", "copy"]
        if single_audio and ctx.get("use_m4a"):
            ext = ".m4a"
            if kept[0].codec not in ("aac", "alac", "ac3", "eac3", "mp3", "opus", "flac"):
                codec_args = ["-c:a", "aac", "-b:a", "256k"]
        out = ctx.output_path(item.path, ext=ext)
        if skip := ctx.skip_if_exists(out):
            return skip
        args = ["-i", item.path]
        for t in kept:
            args += ["-map", f"0:{t.index}"]
        if ext == ".mkv" and info.attachments:
            args += ["-map", "0:t?"]
        args += [*codec_args, "-map_metadata", "0", "-map_chapters", "0"]
        if ext in (".mp4", ".m4a", ".mov"):
            args += ["-movflags", "+faststart"]
        args.append(out)
        res = ctx.ffmpeg(args, info.duration, "Перепаковка")
        removed = len(media) - len(kept)
        return finish_ffmpeg(ctx, res, item.path, out,
                             f"✅ Удалено дорожек: {removed}, осталось: {len(kept)} — {os.path.basename(out)}")


class StreamReplacement(Script):
    id = "stream-replacement"
    name = "Замена потоков"
    description = "Замена дорожек в MKV/MP4 на внешние файлы с сохранением языка, названий и флагов"
    category = "Контейнеры"
    icon = "view-refresh-symbolic"
    extensions = VIDEO_CONTAINERS
    track_selection = "replace"

    def fields(self, values=None):
        return output_fields()

    def row_subtitle(self, item: QueueItem) -> str:
        reps = item.extra.get("replacements") or {}
        if not reps:
            return "замены не заданы"
        return "замен: " + ", ".join(f"#{k} ← {os.path.basename(v)}" for k, v in sorted(reps.items(), key=lambda x: int(x[0])))

    def process(self, item: QueueItem, ctx: Context) -> Result:
        reps = {int(k): v for k, v in (item.extra.get("replacements") or {}).items() if v and os.path.isfile(v)}
        if not reps:
            return Result.skipped("⏭ Замены не заданы — откройте «Дорожки» у файла")
        info = ctx.probe()
        out = ctx.output_path(item.path)
        if skip := ctx.skip_if_exists(out):
            return skip
        is_mp4 = item.ext in (".mp4", ".mov", ".m4v")
        args = ["-i", item.path]
        input_of: dict[int, int] = {}
        for n, (idx, path) in enumerate(sorted(reps.items()), start=1):
            args += ["-i", path]
            input_of[idx] = n
        meta: list[str] = []
        out_idx = 0
        for t in info.media_tracks:
            if t.index in input_of:
                ch = TYPE_CHAR.get(t.type, "a")
                args += ["-map", f"{input_of[t.index]}:{ch}:0"]
                meta += [f"-metadata:s:{out_idx}", f"language={t.language or 'und'}",
                         f"-metadata:s:{out_idx}", f"title={t.title}", f"-disposition:{out_idx}", _disposition(t)]
                if is_mp4 and t.type == "subtitle":
                    meta += [f"-c:s:{sum(1 for x in info.subtitles if x.index < t.index)}", "mov_text"]
            else:
                args += ["-map", f"0:{t.index}"]
            out_idx += 1
        if item.ext == ".mkv" and info.attachments:
            args += ["-map", "0:t?"]
        args += ["-c", "copy", *meta, "-map_metadata", "0", "-map_chapters", "0"]
        if is_mp4:
            args += ["-movflags", "+faststart"]
        args.append(out)
        res = ctx.ffmpeg(args, info.duration, "Замена потоков")
        return finish_ffmpeg(ctx, res, item.path, out, f"✅ Заменено дорожек: {len(reps)} — {os.path.basename(out)}")


_SANITIZE = re.compile(r'[\\/:*?"<>|\x00-\x1f]')


def _clean_name(name: str) -> str:
    name = _SANITIZE.sub("", name.strip().strip("'\""))
    name = re.sub(r"_{2,}", "_", name)
    name = re.sub(r"\.{2,}", ".", name)
    return name.strip(" ._-") or "track"


class TrackExtractor(Script):
    id = "track-extractor"
    name = "Разборка контейнера"
    description = "Массовое извлечение видео, аудио, субтитров и шрифтов из MKV/MP4 с авто-именованием"
    category = "Контейнеры"
    icon = "document-save-symbolic"
    extensions = VIDEO_CONTAINERS | {".mka", ".mks"}
    track_selection = "extract"

    def fields(self, values=None):
        return [
            Field("name_format", "Шаблон имени файла", TEXT, "{original}", "Именование",
                  comment="Теги: {original} {id} {type} {lang} {title} {codec}. Совпадающие имена получат _trackNN"),
            Field("create_subfolders", "Отдельная подпапка для каждого файла", CHECK, False, "Папки"),
            Field("fonts_subfolder", "Шрифты — в подпапку «fonts»", CHECK, True, "Папки"),
        ]

    def row_subtitle(self, item: QueueItem) -> str:
        return track_summary(item)

    @staticmethod
    def format_name(template: str, stem: str, t: Track, suffix: str) -> str:
        lang = t.language if t.language and t.language != "und" else ""
        if suffix:
            lang = f"{lang}_{suffix}" if lang else suffix
        name = template
        for tag, val in (("{original}", stem), ("{id}", str(t.index)), ("{lang}", lang), ("{title}", t.title),
                         ("{codec}", t.codec), ("{type}", {"video": "video", "audio": "audio",
                                                           "subtitle": "subs"}.get(t.type, t.type))):
            name = name.replace(tag, val)
        name = re.sub(r"[_\-. ]{2,}", lambda m: m.group(0)[0], name)
        return _clean_name(name)

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        sel = set(selected_ids(item))
        tracks = [t for t in info.media_tracks if t.index in sel]
        fonts = [t for t in info.attachments if t.index in sel]
        if not tracks and not fonts:
            return Result.skipped("⏭ Дорожки не выбраны")
        base_dir = ctx.target_dir()
        if ctx.get("create_subfolders"):
            base_dir = os.path.join(base_dir, item.stem)
        os.makedirs(base_dir, exist_ok=True)
        template = str(ctx.get("name_format") or "{original}")
        langs = [t.language for t in tracks if t.language and t.language != "und"]
        dup_langs = {lang for lang in langs if langs.count(lang) > 1}
        reserved: set[str] = set()
        args = ["-i", item.path]
        planned: list[str] = []
        skipped: list[str] = []
        vobsub: list[tuple[Track, str]] = []
        for t in tracks:
            ext = raw_extension(t)
            suffix = _clean_name(t.title).replace(" ", "_") if t.language in dup_langs and t.title else ""
            name = self.format_name(template, item.stem, t, suffix) + ext
            path = os.path.join(base_dir, name)
            if path in reserved:
                stem, e = os.path.splitext(name)
                path = os.path.join(base_dir, f"{stem}_track{t.index:02d}{e}")
                ctx.log("WARNING", f"Одинаковое имя: дорожка #{t.index} сохранена как {os.path.basename(path)} "
                                   "(добавьте {id} в шаблон)")
            reserved.add(path)
            if os.path.exists(path) and os.path.getsize(path) > 0 and not ctx.overwrite:
                skipped.append(os.path.basename(path))
                continue
            if t.codec == "dvd_subtitle" and item.ext == ".mkv" and tools.which("mkvextract"):
                vobsub.append((t, os.path.splitext(path)[0] + ".idx"))
                continue
            ch = TYPE_CHAR.get(t.type, "a")
            if t.type == "audio" and t.codec.startswith("pcm_"):
                codec = "pcm_s24le" if t.codec in ("pcm_bluray", "pcm_dvd") or (t.bit_depth or 0) > 16 else "copy"
            elif t.type == "subtitle":
                codec = SUB_CONVERT.get(t.codec, "copy")
            else:
                codec = "copy"
            args += ["-map", f"0:{t.index}", f"-c:{ch}", codec]
            if ext == ".h265" or ext == ".h264":
                args += ["-f", "hevc" if ext == ".h265" else "h264"]
            args.append(path)
            planned.append(path)
        outputs: list[str] = []
        result = Result.done()
        if planned:
            res = ctx.ffmpeg(args, info.duration, "Извлечение дорожек", span=90 if fonts else 100)
            for p in planned:
                if os.path.isfile(p) and os.path.getsize(p) > 0:
                    outputs.append(p)
                else:
                    remove_quiet(p)
                    result.messages.append(f"❌ Не извлечено: {os.path.basename(p)}")
            if not res.ok:
                result.messages.append(res.error_text(6))
        if vobsub:
            mkvextract = tools.require("mkvextract")
            from ..media import process  # noqa: PLC0415

            spec = [f"{t.index}:{p}" for t, p in vobsub]
            r = process.run([mkvextract, item.path, "tracks", *spec], ctx.token, source=self.name)
            for _, p in vobsub:
                if os.path.isfile(p):
                    outputs.append(p)
            if not r.ok:
                result.messages.append("⚠ mkvextract завершился с ошибкой (VobSub)")
        if fonts:
            font_dir = os.path.join(base_dir, "fonts") if ctx.get("fonts_subfolder", True) else base_dir
            os.makedirs(font_dir, exist_ok=True)
            for n, f in enumerate(fonts):
                ctx.check()
                ctx.progress(90 + 10 * n / len(fonts), f"Шрифт {f.filename}")
                name = _clean_name(f.filename or f"font_{f.index}.ttf")
                path = os.path.join(font_dir, name)
                if path in reserved:
                    stem, e = os.path.splitext(name)
                    path = os.path.join(font_dir, f"{stem}_{f.index}{e}")
                reserved.add(path)
                if os.path.exists(path) and os.path.getsize(path) > 0 and not ctx.overwrite:
                    skipped.append(name)
                    continue
                if ff.dump_attachment(item.path, f.index, path, ctx.token):
                    outputs.append(path)
                else:
                    result.messages.append(f"❌ Не извлечён шрифт: {name}")
        if skipped:
            result.messages.insert(0, f"⏭ Уже существуют, пропущены: {len(skipped)}")
        result.outputs = outputs
        failed = sum(1 for m in result.messages if m.startswith("❌"))
        if not outputs:
            result.status = "skipped" if skipped and not failed else "failed"
        elif failed:
            result.status = "partial"
        result.messages.append(f"✅ Извлечено файлов: {len(outputs)} → {base_dir}")
        ctx.progress(100, "Готово")
        return result


def default_selection(script: Script, info: MediaInfo) -> list[int]:
    if script.track_selection == "extract":
        return [t.index for t in info.media_tracks] + [t.index for t in info.fonts]
    return []
