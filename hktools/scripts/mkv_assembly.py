from __future__ import annotations

import os
import re

from .. import tools
from ..core.fields import CHECK, COMBO, EXPANDER, KEYWORDS, TEXT, Cond, Field
from ..core.script import (AUDIO_ALL, SUBTITLE_EXTS, VIDEO_CONTAINERS, Context, QueueItem, Result, Script,
                           remove_quiet)
from ..media.probe import mkvmerge_identify, probe
from .audio_shift import mkvmerge_progress_runner

DELIMS = ".-_ ([{"
TOKEN_SPLIT = re.compile(r"[._\-\s()\[\]{}]+")
SIGNS = {"sign", "signs", "sings", "songs", "song", "надписи", "надпись", "титры", "forced"}
FULL = {"full", "полные", "полный", "complete", "main", "dialogue", "dialogs"}
LANGS = {"en", "eng", "ru", "rus", "de", "ger", "deu", "fr", "fre", "fra", "es", "spa", "it", "ita", "ja", "jpn",
         "uk", "ukr", "pl", "pol", "pt", "por", "zh", "chi", "zho", "ko", "kor", "tr", "tur", "ar", "ara", "hi",
         "hin", "cs", "cze", "ces", "hu", "hun", "bg", "bul", "sr", "srp", "he", "heb", "nl", "dut", "nld", "sv", "swe"}
ROLE_LABEL = {"signs": "Надписи", "full": "Полные", "other": "Субтитры"}
ROLE_ORDER = {"signs": 0, "full": 1, "other": 2}


def is_companion(video_stem: str, comp_stem: str) -> bool:
    v, c = video_stem.lower(), comp_stem.lower()
    if c == v:
        return True
    return len(c) > len(v) and c.startswith(v) and c[len(v)] in DELIMS


def belongs(video_stem: str, item: QueueItem) -> bool:
    pinned = item.extra.get("mux_pinned")
    if pinned:
        return str(pinned).lower() == video_stem.lower()
    return is_companion(video_stem, item.stem)


def remainder_tokens(video_stem: str, comp_stem: str) -> list[str]:
    if not is_companion(video_stem, comp_stem) or len(comp_stem) == len(video_stem):
        return []
    return [t for t in TOKEN_SPLIT.split(comp_stem[len(video_stem):]) if t]


def subs_role(video_stem: str, item: QueueItem) -> str:
    override = item.extra.get("mux_role")
    if override in ROLE_LABEL:
        return override
    tokens = [t.lower() for t in remainder_tokens(video_stem, item.stem)]
    if not tokens:
        return "full"
    if any(t in SIGNS for t in tokens):
        return "signs"
    if any(t in FULL for t in tokens):
        return "full"
    return "other"


def infer_title(video_stem: str, comp_stem: str) -> str:
    tokens = remainder_tokens(video_stem, comp_stem)
    if not tokens or all(t.lower() in LANGS for t in tokens):
        return ""
    return " ".join(tokens)


def companions(video: QueueItem, items: list[QueueItem]) -> tuple[list[QueueItem], list[tuple[QueueItem, str]]]:
    audio, subs = [], []
    for it in items:
        if it is video or it.ext in VIDEO_CONTAINERS:
            continue
        if not belongs(video.stem, it):
            continue
        if it.ext in AUDIO_ALL:
            audio.append(it)
        elif it.ext in SUBTITLE_EXTS:
            subs.append((it, subs_role(video.stem, it)))
    audio.sort(key=lambda i: (0 if i.extra.get("mux_main") else 1, i.path.lower()))
    subs.sort(key=lambda p: (ROLE_ORDER[p[1]], p[0].path.lower()))
    return audio, subs


def owner_video(item: QueueItem, items: list[QueueItem]) -> QueueItem | None:
    best = None
    for it in items:
        if it.ext in VIDEO_CONTAINERS and belongs(it.stem, item):
            if best is None or len(it.stem) > len(best.stem):
                best = it
    return best


def sanitize_title(t: str | None) -> str:
    return re.sub(r"[\r\n\t]+", " ", t or "").strip()


class MkvAssembly(Script):
    id = "mkv-assembly"
    name = "Сборка MKV"
    description = "Сборка MKV/MP4 из видео, внешних аудио и субтитров с сопоставлением по имени файла"
    category = "Контейнеры"
    icon = "package-x-generic-symbolic"
    extensions = VIDEO_CONTAINERS | AUDIO_ALL | SUBTITLE_EXTS
    deps = ("ffmpeg", "ffprobe", "mkvmerge")

    def fields(self, values=None):
        return [
            Field("output_container", "Формат контейнера", COMBO, "MKV", "Сборка", options=["MKV", "MP4"]),
            Field("clean_tracks", "Удалить из видео дорожки, заменяемые внешними", CHECK, True, "Сборка",
                  comment="Встроенные аудио/субтитры убираются, если найдены внешние того же типа"),
            Field("position_before_builtin", "Новые дорожки перед встроенными", CHECK, False, "Сборка",
                  visible=[Cond("clean_tracks", False)]),
            Field("external_lang", "Язык внешних дорожек", TEXT, "rus", "Сборка"),
            Field("subs_full_title", "Заголовок полных субтитров", TEXT, "Субтитры", "Субтитры"),
            Field("subs_signs_title", "Заголовок надписей", TEXT, "Надписи", "Субтитры"),
            Field("audio_title_prompt", "Спрашивать заголовок аудио при добавлении", EXPANDER, False, "Аудио",
                  comment="Заготовки показываются в диалоге для быстрого выбора", children=[
                      Field("audio_title_default", "Заголовок по умолчанию", TEXT, "", "Аудио",
                            comment="Применяется без вопроса, если диалог выключен"),
                      Field("audio_title_presets", "Заготовленные заголовки", KEYWORDS, [], "Аудио"),
                  ]),
        ]

    def prepare_batch(self, items, values):
        return [i for i in items if i.ext in VIDEO_CONTAINERS]

    def skip_reason(self, item: QueueItem) -> str:
        return "Сопутствующий файл — собирается вместе с видео"

    def describe_group(self, item: QueueItem, items: list[QueueItem]) -> str:
        if item.ext in VIDEO_CONTAINERS:
            audio, subs = companions(item, items)
            if not audio and not subs:
                return "внешних дорожек не найдено"
            parts = []
            if audio:
                parts.append(f"аудио: {len(audio)}")
            if subs:
                parts.append("субтитры: " + ", ".join(ROLE_LABEL[r] for _, r in subs))
            return " · ".join(parts)
        owner = owner_video(item, items)
        if not owner:
            return "⚠ не найдено видео с таким именем"
        label = ""
        if item.ext in SUBTITLE_EXTS:
            label = f" ({ROLE_LABEL[subs_role(owner.stem, item)]})"
        elif item.extra.get("mux_main"):
            label = " (основная)"
        if item.extra.get("mux_title"):
            label += f" «{item.extra['mux_title']}»"
        return f"→ {owner.name}{label}"

    @staticmethod
    def _embedded_title(path: str) -> str:
        try:
            info = probe(path)
        except RuntimeError:
            return ""
        if info.audio and info.audio[0].title:
            return info.audio[0].title
        return info.title

    def process(self, item: QueueItem, ctx: Context) -> Result:
        audio_items, subs = companions(item, ctx.batch.items)
        is_mp4 = str(ctx.get("output_container", "MKV")).upper() == "MP4"
        out = ctx.output_path(item.path, ext=".mp4" if is_mp4 else ".mkv")
        if skip := ctx.skip_if_exists(out):
            return skip
        clean = bool(ctx.get("clean_tracks", True))
        lang = str(ctx.get("external_lang", "rus") or "und")
        full_title = str(ctx.get("subs_full_title", "Субтитры"))
        signs_title = str(ctx.get("subs_signs_title", "Надписи"))
        default_title = sanitize_title(ctx.get("audio_title_default", ""))
        if not audio_items and not subs:
            ctx.log("WARNING", f"{item.name}: внешних дорожек не найдено, будет выполнен только ремукс")
        if is_mp4:
            return self._mp4(item, ctx, out, audio_items, subs, clean, lang)

        mkvmerge = tools.require("mkvmerge")
        cmd = [mkvmerge, "-o", out, "--title", item.stem]
        video_opts: list[str] = []
        if clean:
            if audio_items:
                video_opts.append("--no-audio")
            if subs:
                video_opts.append("--no-subtitles")
            video_opts += ["--no-global-tags", "--no-track-tags"]
        cmd += [*video_opts, item.path]
        for n, a in enumerate(audio_items):
            main = n == 0
            title = (sanitize_title(a.extra.get("mux_title")) or default_title or self._embedded_title(a.path)
                     or infer_title(item.stem, a.stem))
            cmd += ["--no-video", "--no-subtitles", "--language", f"-1:{lang}",
                    "--default-track-flag", f"-1:{'yes' if main else 'no'}",
                    "--forced-display-flag", f"-1:{'yes' if main else 'no'}"]
            if title:
                cmd += ["--track-name", f"-1:{title}"]
            cmd.append(a.path)
        signs_default = False
        for s, role in subs:
            is_def = role == "signs" and not signs_default
            signs_default = signs_default or role == "signs"
            title = signs_title if role == "signs" else full_title
            cmd += ["--language", f"-1:{lang}", "--default-track-flag", f"-1:{'yes' if is_def else 'no'}",
                    "--forced-display-flag", f"-1:{'yes' if is_def else 'no'}"]
            if s.ext in (".srt", ".ass", ".ssa", ".vtt"):
                cmd += ["--sub-charset", "-1:UTF-8"]
            if title:
                cmd += ["--track-name", f"-1:{title}"]
            cmd.append(s.path)
        if not clean and (audio_items or subs):
            try:
                data = mkvmerge_identify(item.path)
                tracks = data.get("tracks", [])
                video = next((t for t in tracks if t.get("type") == "video"), None)
                order = [f"0:{video['id']}"] if video else []
                new = [f"{i}:0" for i in range(1, 1 + len(audio_items) + len(subs))]
                builtin = [f"0:{t['id']}" for t in tracks if not video or t["id"] != video["id"]]
                order += (new + builtin) if ctx.get("position_before_builtin") else (builtin + new)
                cmd += ["--track-order", ",".join(order)]
            except RuntimeError as exc:
                ctx.log("WARNING", f"Порядок дорожек по умолчанию: {exc}")
        res = mkvmerge_progress_runner(ctx, cmd, "Сборка MKV")
        if res.ok and os.path.isfile(out):
            ctx.progress(100, "Готово")
            return Result.done(out, msg=f"✅ Собран MKV ({len(audio_items)} аудио, {len(subs)} субтитров): "
                                        f"{os.path.basename(out)}")
        remove_quiet(out)
        return Result.failed(f"❌ Ошибка сборки MKV\n{res.error_text()}")

    def _mp4(self, item, ctx, out, audio_items, subs, clean, lang) -> Result:
        skipped = []
        audio_ok = []
        for a in audio_items:
            if a.ext in (".flac", ".thd", ".truehd", ".dts", ".dtshd", ".mlp"):
                skipped.append(f"{a.name} (формат не для MP4)")
            else:
                audio_ok.append(a)
        subs_ok = []
        for s, role in subs:
            if s.ext in (".srt", ".vtt"):
                subs_ok.append((s, role))
            else:
                skipped.append(f"{s.name} (ASS/SSA/PGS не поддерживаются MP4)")
        args = ["-i", item.path]
        for a in audio_ok:
            args += ["-i", a.path]
        for s, _ in subs_ok:
            args += ["-i", s.path]
        args += ["-map", "0:v"]
        if audio_ok:
            for i in range(len(audio_ok)):
                args += ["-map", f"{1 + i}:a"]
        elif not clean:
            args += ["-map", "0:a?"]
        start = 1 + len(audio_ok)
        for i in range(len(subs_ok)):
            args += ["-map", f"{start + i}:s"]
        args += ["-c", "copy"]
        if subs_ok:
            args += ["-c:s", "mov_text"]
        for i in range(len(audio_ok)):
            args += [f"-metadata:s:a:{i}", f"language={lang}"]
            args += [f"-disposition:a:{i}", "default" if i == 0 else "0"]
        for i, (_, role) in enumerate(subs_ok):
            args += [f"-metadata:s:s:{i}", f"language={lang}",
                     f"-metadata:s:s:{i}", f"title={ROLE_LABEL[role]}"]
        args += ["-movflags", "+faststart", out]
        try:
            duration = probe(item.path).duration
        except RuntimeError:
            duration = 0.0
        res = ctx.ffmpeg(args, duration, "Сборка MP4")
        if res.ok and os.path.isfile(out):
            r = Result.done(out, msg=f"✅ Собран MP4: {os.path.basename(out)}")
            if skipped:
                r.status = "partial"
                r.messages.insert(0, "⚠ Пропущено: " + "; ".join(skipped))
            return r
        remove_quiet(out)
        return Result.failed(f"❌ Ошибка сборки MP4\n{res.error_text()}")
