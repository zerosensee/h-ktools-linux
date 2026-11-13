from __future__ import annotations

import os

from .. import tools
from ..core.fields import CHECK, INT, TEXT, Field
from ..core.script import VIDEO_CONTAINERS, Context, QueueItem, Result, Script, remove_quiet
from ..media.probe import mkvmerge_identify, probe
from .audio_shift import mkvmerge_progress_runner

AAC_PRIMING_MS = 1024 / 48000 * 1000  # 21.333 мс


class AudioTransplant(Script):
    """Пересадка аудиодорожки из файла-донора в видео с синхронизацией по осциллограмме."""

    id = "audio-transplant"
    name = "Пересадка аудио"
    description = "Пересадка дорожки из другого файла в видео со сдвигом по осциллограмме (без перекодирования)"
    category = "Аудио"
    icon = "insert-object-symbolic"
    extensions = VIDEO_CONTAINERS
    deps = ("ffmpeg", "ffprobe", "mkvmerge")

    def fields(self, values=None):
        G = "Пересадка"
        return [
            Field("transplant_source", "Файл-донор аудио", TEXT, "", G, placeholder="/путь/к/озвучке.mka",
                  browse="file"),
            Field("transplant_source_track", "Аудиодорожка донора (№ с 0)", INT, 0, G, minimum=0, maximum=99),
            Field("transplant_shift_ms", "Сдвиг (мс)", INT, 0, G, minimum=-3_600_000, maximum=3_600_000, step=1,
                  comment="Положительный — звук позже"),
            Field("transplant_aac_comp", "Компенсировать задержку AAC (−21,33 мс)", CHECK, False, G,
                  comment="Только если донор — AAC с неучтённым priming-сдвигом"),
            Field("transplant_title", "Название дорожки", TEXT, "", G),
            Field("transplant_lang", "Язык дорожки (ISO 639-2)", TEXT, "rus", G),
            Field("transplant_subs", "Внешние субтитры (надписи), необязательно", TEXT, "", G, browse="file"),
            Field("transplant_subs_title", "Название субтитров", TEXT, "Надписи", G),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        src = os.path.expanduser(str(ctx.get("transplant_source", "")).strip())
        if not src or not os.path.isfile(src):
            return Result.failed("❌ Не выбран файл-донор аудио")
        mkvmerge = tools.require("mkvmerge")
        src_info = mkvmerge_identify(src)
        audio_ids = [int(t["id"]) for t in src_info.get("tracks", []) if t.get("type") == "audio"]
        idx = int(ctx.get("transplant_source_track", 0))
        if idx >= len(audio_ids):
            return Result.failed(f"❌ В доноре нет аудиодорожки №{idx}")
        tid = audio_ids[idx]
        shift = float(ctx.get("transplant_shift_ms", 0))
        if ctx.get("transplant_aac_comp"):
            try:
                codec = probe(src).audio[idx].codec
            except (RuntimeError, IndexError):
                codec = ""
            if codec == "aac":
                shift -= AAC_PRIMING_MS
        shift_ms = int(round(shift))

        out = ctx.output_path(item.path, ext=".mkv")
        if skip := ctx.skip_if_exists(out):
            return skip
        dest_info = mkvmerge_identify(item.path)
        lang = str(ctx.get("transplant_lang", "rus") or "und")
        title = str(ctx.get("transplant_title", "")).strip()
        cmd = [mkvmerge, "-o", out]
        # 0: донор
        cmd += ["--audio-tracks", str(tid), "--no-video", "--no-subtitles", "--no-chapters", "--no-attachments",
                "--no-global-tags", "--sync", f"{tid}:{shift_ms}", "--language", f"{tid}:{lang}",
                "--default-track-flag", f"{tid}:yes", "--forced-display-flag", f"{tid}:yes"]
        if title:
            cmd += ["--track-name", f"{tid}:{title}"]
        cmd.append(src)
        next_input = 1
        subs = os.path.expanduser(str(ctx.get("transplant_subs", "")).strip())
        subs_input = None
        if subs and os.path.isfile(subs):
            subs_input = next_input
            next_input += 1
            cmd += ["--language", f"0:{lang}", "--track-name", f"0:{ctx.get('transplant_subs_title', 'Надписи')}",
                    "--default-track-flag", "0:yes", "--forced-display-flag", "0:yes", "--sub-charset", "0:UTF-8", subs]
        dest_input = next_input
        video_tid = None
        for t in dest_info.get("tracks", []):
            if t.get("type") in ("audio", "subtitles"):
                cmd += ["--default-track-flag", f"{t['id']}:no", "--forced-display-flag", f"{t['id']}:no"]
            elif t.get("type") == "video" and video_tid is None:
                video_tid = t["id"]
        cmd.append(item.path)
        order = []
        if video_tid is not None:
            order.append(f"{dest_input}:{video_tid}")
        order.append(f"0:{tid}")
        if subs_input is not None:
            order.append(f"{subs_input}:0")
        for t in dest_info.get("tracks", []):
            if t["id"] != video_tid:
                order.append(f"{dest_input}:{t['id']}")
        cmd += ["--track-order", ",".join(order)]
        ctx.log("INFO", f"Сдвиг пересаживаемой дорожки: {shift_ms:+d} мс")
        res = mkvmerge_progress_runner(ctx, cmd, "Пересадка (mkvmerge)")
        if res.ok and os.path.isfile(out):
            ctx.progress(100, "Готово")
            return Result.done(out, msg=f"✅ Аудио пересажено ({shift_ms:+d} мс): {os.path.basename(out)}")
        remove_quiet(out)
        return Result.failed(f"❌ Ошибка mkvmerge\n{res.error_text()}")
