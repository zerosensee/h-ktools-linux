from __future__ import annotations

import os
import shutil
from typing import Any

from .. import encoders as enc_mod
from .. import hw
from ..core.fields import CHECK, COMBO, EXPANDER, FLOAT, INT, KEYWORDS, Cond, Field, active_words
from ..core.script import VIDEO_CONTAINERS, Context, QueueItem, Result, Script, remove_quiet
from ..media import ffmpeg as ff
from ..media.probe import MediaInfo, Track, normalize_lang
from .common import REPLACE_WARNING

STANDARD_HEIGHTS = (240, 360, 480, 720, 1080, 1440, 2160, 4320)


def make_even(v: int) -> int:
    return 2 if v < 2 else (v // 2) * 2


def snap_height(raw: int) -> int:
    best = STANDARD_HEIGHTS[0]
    for c in STANDARD_HEIGHTS:
        if abs(raw - c) < abs(raw - best) or (abs(raw - c) == abs(raw - best) and c < best):
            best = c
    return best


def compute_scale(base_w: int, base_h: int, factor: str) -> tuple[int, int] | str:
    """Целевое разрешение: высота приводится к стандартному ряду, ширина — по пропорциям базы."""
    if base_w <= 0 or base_h <= 0:
        return "разрешение исходного видео неизвестно"
    try:
        coef = float(str(factor).replace(",", "."))
    except ValueError:
        return "некорректный коэффициент"
    if coef <= 0:
        return "некорректный коэффициент"
    th = make_even(snap_height(round(base_h * coef)))
    tw = make_even(round(base_w * th / base_h))
    if tw == base_w and th == base_h:
        return "целевое разрешение совпадает с текущим"
    return tw, th


class VideoEncode(Script):
    id = "video-encode"
    name = "Кодирование видео"
    description = "Транскодирование с NVENC / VAAPI / x265 / x264 / SVT-AV1, вшиванием надписей, автокропом и масштабом"
    category = "Видео"
    icon = "video-x-generic-symbolic"
    extensions = VIDEO_CONTAINERS

    def fields(self, values=None):
        encs = enc_mod.available_encoders()
        titles = [e.title for e in encs]
        G = "Видео:Кодирование"
        out: list[Field] = [
            Field("encoder", "Энкодер", COMBO, titles[0], G, options=titles,
                  comment="NVENC — NVIDIA, VAAPI — AMD/Intel (через Mesa / intel-media-driver)"),
            Field("output_container", "Контейнер файла", COMBO, ".mkv", G, options=[".mkv", ".mp4"]),
            Field("force_10bit", "Принудительно 10 бит (Main10)", CHECK, False, G,
                  disabled=[Cond("cpu_codec", "AVC / H.264"), Cond("nvenc_codec", "AVC / H.264"),
                            Cond("vaapi_codec", "AVC / H.264")]),
            Field("lossless", "Режим Lossless", CHECK, False, "Видео:Битрейт"),
        ]
        for e in encs:
            for f in e.fields():
                f.visible = [Cond("encoder", e.title), *f.visible]
                out.append(f)
        F = "Видео:Фильтры"
        out += [
            Field("autocrop_enabled", "Автоматическая обрезка чёрных полос", EXPANDER, False, F,
                  comment="Letterbox / Pillarbox по нескольким точкам видео", children=[
                      Field("autocrop_limit", "Порог чёрного (limit)", FLOAT, 0.094, F, minimum=0.0, maximum=1.0,
                            step=0.005, digits=3, comment="0.094 ≈ 24/255"),
                      Field("autocrop_round", "Кратность сторон (round)", INT, 16, F, minimum=2, maximum=64),
                      Field("autocrop_mode", "Режим детекции", COMBO, "black", F, options=["black", "mvedges"]),
                      Field("autocrop_probe_frames", "Кадров для анализа", INT, 25, F, minimum=5, maximum=300),
                      Field("autocrop_skip_frames", "Пропуск кадров детектора (skip)", INT, 2, F, minimum=0,
                            maximum=1000),
                      Field("autocrop_reset_frames", "Сброс детектора (reset)", INT, 0, F, minimum=0, maximum=1000),
                      Field("autocrop_probe_points", "Количество точек анализа", INT, 3, F, minimum=1, maximum=10),
                      Field("autocrop_tolerance", "Порог микрообрезки (px)", INT, 16, F, minimum=0, maximum=128,
                            comment="Срез меньше порога игнорируется"),
                  ]),
            Field("resize_enabled", "Изменение разрешения", EXPANDER, False, F,
                  comment="Расчёт от кадра после обрезки, итог — к стандартному ряду (720p, 1080p…)", children=[
                      Field("resize_factor", "Коэффициент масштаба", COMBO, "0.5", F,
                            options=["0.25", "0.5", "0.75", "1.5", "2.0"]),
                      Field("resize_algorithm", "Алгоритм", COMBO, "lanczos", F,
                            options=["lanczos", "bicubic", "bilinear", "spline"]),
                      Field("resize_allow_upscale", "Разрешить увеличение кадра", CHECK, False, F),
                  ]),
            Field("audio_codec", "Кодек аудио", COMBO, "copy", "Аудио", options=["copy", "aac", "ac3", "eac3", "opus", "flac"]),
            Field("audio_bitrate", "Битрейт аудио", COMBO, "320k", "Аудио",
                  options=["128k", "192k", "256k", "320k", "448k", "640k"],
                  visible=[Cond("audio_codec", ("aac", "ac3", "eac3", "opus"))]),
            Field("audio_channels", "Каналы", COMBO, "Original", "Аудио", options=["Original", "1", "2", "6"],
                  visible=[Cond("audio_codec", "copy", negate=True)]),
            Field("audio_lang_priority", "Приоритет языка аудио", KEYWORDS,
                  [{"word": "rus", "active": True}, {"word": "jpn", "active": False}, {"word": "eng", "active": False}],
                  "Аудио", comment="Первая найденная по порядку; иначе — дорожка по умолчанию"),
            Field("burn_in_subtitles", "Вшивать найденные надписи в видео (hardsub)", CHECK, True, "Субтитры"),
            Field("sub_keywords", "Поиск надписей по названию дорожки", KEYWORDS,
                  [{"word": "Надписи", "active": True}, {"word": "Signs", "active": True}], "Субтитры",
                  visible=[Cond("burn_in_subtitles", True)]),
            Field("sub_not_found_action", "Если надписи не найдены", COMBO, "Пропускать хардсаб", "Субтитры",
                  options=["Пропускать хардсаб", "Спрашивать"], visible=[Cond("burn_in_subtitles", True)]),
            Field("strip_keywords", "Удалять строки субтитров, содержащие", KEYWORDS,
                  [{"word": r"{\fad(500,500)\b1\an3\fnTahoma\fs50\shad3\bord1.3\4c&H000000&\4a&H00&}", "active": False}],
                  "Субтитры", visible=[Cond("burn_in_subtitles", True)]),
            Field("overwrite_source", "Заменить исходный файл результатом", CHECK, False, "Общие",
                  warning=REPLACE_WARNING),
        ]
        return out

    # --- выбор дорожек ---
    @staticmethod
    def pick_audio(info: MediaInfo, langs: list[str]) -> Track | None:
        audio = info.audio
        for lang in langs:
            nl = normalize_lang(lang.strip())
            for t in audio:
                nt = normalize_lang(t.language)
                if nt == nl or (nt != "und" and (nl in nt or nt in nl)):
                    return t
        for t in audio:
            if t.default or t.forced:
                return t
        return audio[0] if audio else None

    def _pick_subs(self, item: QueueItem, ctx: Context, info: MediaInfo) -> Track | None:
        words = active_words(ctx.get("sub_keywords"), lower=True)
        subs = info.subtitles
        for w in words:
            for t in subs:
                if w in t.title.lower():
                    return t
        if ctx.get("sub_not_found_action") == "Спрашивать" and subs and ctx.ask:
            ctx.progress(0, "Ожидание выбора субтитров…")
            return ctx.ask("choose_subtitle", item.name, subs)
        return None

    def _detect_crop(self, ctx: Context, path: str, w: int, h: int, duration: float) -> tuple[int, int, str] | None:
        points = int(ctx.get("autocrop_probe_points", 3))
        if duration < 5 or points <= 1:
            offsets = [duration / 2 if duration > 0 else 0.0]
        else:
            offsets = [duration * i / (points + 1) for i in range(1, points + 1)]
        rnd = int(ctx.get("autocrop_round", 16))
        tol = int(ctx.get("autocrop_tolerance", 16))
        max_w = max_h = 0
        for n, off in enumerate(offsets, 1):
            ctx.check()
            ctx.progress(0, f"Анализ чёрных полос (точка {n}/{len(offsets)})…")
            found = ff.detect_crop(path, off, int(ctx.get("autocrop_probe_frames", 25)),
                                   float(ctx.get("autocrop_limit", 0.094)), rnd,
                                   int(ctx.get("autocrop_skip_frames", 2)), int(ctx.get("autocrop_reset_frames", 0)),
                                   str(ctx.get("autocrop_mode", "black")), ctx.token)
            if not found:
                continue
            cw, ch = found[0], found[1]
            if w and h and cw >= w and ch >= h:
                return None  # в одной из точек кадр полноэкранный — обрезать нечего
            max_w, max_h = max(max_w, cw), max(max_h, ch)
        if not max_w or not max_h:
            return None
        if rnd > 1:
            max_w = (max_w + rnd - 1) // rnd * rnd
            max_h = (max_h + rnd - 1) // rnd * rnd
        max_w += max_w % 2
        max_h += max_h % 2
        if w:
            max_w = min(max_w, w)
            if 0 < w - max_w <= tol:
                max_w = w
        if h:
            max_h = min(max_h, h)
            if 0 < h - max_h <= tol:
                max_h = h
        if w and h and (max_w, max_h) == (w, h):
            return None
        x = ((w - max_w) // 2 // 2) * 2 if w > max_w else 0
        y = ((h - max_h) // 2 // 2) * 2 if h > max_h else 0
        return max_w, max_h, f"crop={max_w}:{max_h}:{x}:{y}"

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        if not info.video:
            return Result.failed("❌ В файле нет видеодорожки")
        container = str(ctx.get("output_container", ".mkv"))
        if not container.startswith("."):
            container = "." + container
        out = ctx.output_path(item.path, ext=container)
        if skip := ctx.skip_if_exists(out):
            return skip

        encoder = enc_mod.by_id(str(ctx.get("encoder", "CPU"))) or enc_mod.ALL_ENCODERS[0]
        if not encoder.available():
            return Result.failed(f"❌ Энкодер «{encoder.title}» недоступен на этой системе (см. «Зависимости»)")
        v: dict[str, Any] = ctx.values
        ectx = enc_mod.EncContext(bool(v.get("lossless")), bool(v.get("force_10bit")), container,
                                  hw.pick_vaapi_device())
        hw_frames = encoder.id == "vaapi" and bool(v.get("vaapi_hwdecode"))
        notes: list[str] = []
        tmp = ctx.temp_dir("kt_venc_")
        fonts_dir = os.path.join(tmp, "fonts")
        os.makedirs(fonts_dir, exist_ok=True)

        # 1. Надписи для вшивания
        sub_track: Track | None = None
        sub_file: str | None = None
        if v.get("burn_in_subtitles") and not hw_frames:
            sub_track = self._pick_subs(item, ctx, info)
            if sub_track and not sub_track.is_bitmap_sub:
                if info.fonts:
                    ctx.progress(0, "Извлечение шрифтов…")
                    ff.dump_attachments(item.path, fonts_dir, ctx.token)
                sub_file = os.path.join(tmp, "subs.ass")
                ctx.progress(0, "Извлечение субтитров…")
                res = ff.run_ffmpeg(["-i", item.path, "-map", f"0:{sub_track.index}", "-c:s", "ass", sub_file],
                                    ctx.token, source=self.name)
                ctx.check()
                if not res.ok or not os.path.isfile(sub_file):
                    notes.append("не удалось извлечь субтитры — хардсаб пропущен")
                    sub_file, sub_track = None, None
                else:
                    strip = active_words(ctx.get("strip_keywords"))
                    if strip:
                        with open(sub_file, encoding="utf-8", errors="replace") as fh:
                            lines = fh.read().splitlines()
                        kept = [ln for ln in lines if not any(w in ln for w in strip)]
                        if len(kept) != len(lines):
                            with open(sub_file, "w", encoding="utf-8") as fh:
                                fh.write("\n".join(kept) + "\n")
                            ctx.log("INFO", f"Удалено строк субтитров по фильтру: {len(lines) - len(kept)}")
            if sub_track:
                ctx.log("INFO", f"Вшиваются субтитры #{sub_track.index}: {sub_track.describe()}")
        elif v.get("burn_in_subtitles") and hw_frames:
            notes.append("при аппаратном декодировании VAAPI хардсаб недоступен")

        # 2. Аудио
        audio = self.pick_audio(info, active_words(ctx.get("audio_lang_priority")))

        # 3. Фильтры: хардсаб → кроп → масштаб
        vtrack = info.video[0]
        w, h = vtrack.width, vtrack.height
        chain: list[str] = []
        if sub_file:
            chain.append(f"subtitles=filename='{ff.filter_escape(sub_file)}':fontsdir='{ff.filter_escape(fonts_dir)}'")
        cur_w, cur_h = w, h
        badge = []
        if v.get("autocrop_enabled") and not hw_frames:
            crop = self._detect_crop(ctx, item.path, w, h, info.duration)
            if crop:
                cur_w, cur_h, flt = crop
                chain.append(flt)
                badge.append(f"{w}x{h} ➔ {cur_w}x{cur_h}")
                ctx.log("INFO", f"Обрезка: {w}x{h} → {cur_w}x{cur_h}")
        if v.get("resize_enabled"):
            target = compute_scale(cur_w, cur_h, str(v.get("resize_factor", "0.5")))
            if isinstance(target, tuple):
                tw, th = target
                if (tw > cur_w or th > cur_h) and not v.get("resize_allow_upscale"):
                    notes.append(f"масштаб пропущен: увеличение до {tw}x{th} запрещено")
                else:
                    algo = str(v.get("resize_algorithm", "lanczos"))
                    if hw_frames:
                        chain.append(f"scale_vaapi=w={tw}:h={th}")
                    else:
                        chain.append(f"scale={tw}:{th}:flags={algo}")
                    badge.append(f"➔ {tw}x{th}" if badge else f"{w}x{h} ➔ {tw}x{th}")
            else:
                notes.append(f"масштаб пропущен: {target}")
        if badge:
            item.badge = " ".join(badge)
        chain += encoder.filter_suffix(v, ectx)

        # 4. Команда
        args: list[str] = [*encoder.input_args(v, ectx), "-i", item.path]
        if sub_track is not None and sub_track.is_bitmap_sub:
            graph = f"[0:{vtrack.index}][0:{sub_track.index}]overlay"
            graph += ("," + ",".join(chain) if chain else "") + "[vout]"
            args += ["-filter_complex", graph, "-map", "[vout]"]
        else:
            if chain:
                args += ["-vf", ",".join(chain)]
            args += ["-map", f"0:{vtrack.index}"]
        if audio is not None:
            args += ["-map", f"0:{audio.index}"]
        args += encoder.args(v, ectx)
        acodec = str(v.get("audio_codec", "copy"))
        args += ["-c:a", acodec]
        if acodec != "copy":
            if acodec in ("aac", "ac3", "eac3", "opus"):
                args += ["-b:a", str(v.get("audio_bitrate", "320k"))]
            ch = str(v.get("audio_channels", "Original"))
            if ch == "2" and audio and audio.channels > 2:
                args += ["-af", ff.STEREO_DOWNMIX_PAN]
            elif ch not in ("Original", "2"):
                args += ["-ac", ch]
            if acodec == "opus":
                args[args.index("-c:a") + 1] = "libopus"
                if audio and audio.channels > 2 and ch == "Original":
                    if audio.channel_layout == "5.1(side)":
                        args += ["-af", "channelmap=channel_layout=5.1"]
                    args += ["-mapping_family", "1"]
        tag = encoder.tag(v, ectx)
        if tag:
            args += ["-tag:v", tag]
        if container == ".mp4":
            args += ["-movflags", "+faststart"]
        args += ["-map_metadata", "-1", "-map_chapters", "0", out]

        for n in notes:
            ctx.log("WARNING", f"{item.name}: {n}")
        ctx.progress(0, "Кодирование видео…")
        res = ctx.ffmpeg(args, info.duration, "Кодирование")
        if not (res.ok and os.path.isfile(out) and os.path.getsize(out) > 0):
            remove_quiet(out)
            return Result.failed(f"❌ ОШИБКА кодирования: {item.name}\n{res.error_text()}")
        result = Result.done(msg=f"✅ ОБРАБОТАНО: {os.path.basename(out)}")
        result.outputs = [ctx.finalize_source(item.path, out, result)]
        shutil.rmtree(tmp, ignore_errors=True)
        return result
