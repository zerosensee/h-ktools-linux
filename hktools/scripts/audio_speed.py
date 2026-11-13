from __future__ import annotations

import os
from fractions import Fraction

from .. import hw
from ..core.fields import CHECK, COMBO, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script
from .common import delete_original_field, finish_ffmpeg

NTSC = Fraction(24000, 1001)
MODES: dict[str, tuple[Fraction, Fraction]] = {
    "Slowdown (25.000 → 23.976)": (Fraction(25), NTSC),
    "Speedup (23.976 → 25.000)": (NTSC, Fraction(25)),
    "Custom (24.000 → 23.976)": (Fraction(24), NTSC),
    "Custom (23.976 → 24.000)": (NTSC, Fraction(24)),
    "Custom (25.000 → 24.000)": (Fraction(25), Fraction(24)),
    "Custom (29.970 → 23.976)": (Fraction(30000, 1001), NTSC),
}


class AudioSpeed(Script):
    id = "audio-speed"
    name = "Изменение скорости аудио"
    description = "PAL ↔ NTSC: изменение скорости как в eac3to (с тоном) или с сохранением тона (rubberband)"
    category = "Аудио"
    icon = "media-seek-forward-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        G = "Настройки скорости"
        return [
            Field("SpeedMode", "Режим преобразования", COMBO, list(MODES)[0], G, options=list(MODES)),
            Field("KeepPitch", "Сохранять тон (time-stretch)", CHECK, False, G,
                  comment="Выключено — как eac3to: скорость и тон меняются вместе (ресэмплинг)"),
            Field("OutputFormat", "Формат вывода", COMBO, "FLAC", G, options=["FLAC", "WAV"]),
            delete_original_field("Общие", "DeleteOriginal"),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        if not info.audio:
            return Result.failed("❌ В файле нет аудиодорожки")
        track = info.audio[0]
        src_fps, dst_fps = MODES.get(str(ctx.get("SpeedMode")), list(MODES.values())[0])
        factor = dst_fps / src_fps  # >1 — ускорение
        fmt = str(ctx.get("OutputFormat", "FLAC"))
        ext = ".flac" if fmt == "FLAC" else ".wav"
        out = ctx.output_path(item.path, ext=ext)
        if skip := ctx.skip_if_exists(out):
            return skip
        sr = track.sample_rate or 48000
        if ctx.get("KeepPitch"):
            flt = f"rubberband=tempo={float(factor):.10f}" if hw.has_filter("rubberband") else \
                  f"atempo={float(factor):.10f}"
        else:
            flt = f"asetrate={float(sr * factor):.6f},aresample={sr}:resampler=soxr" if hw.has_filter("aresample") \
                else f"asetrate={float(sr * factor):.6f},aresample={sr}"
        codec = "flac" if fmt == "FLAC" else ("pcm_s24le" if (track.bit_depth or 24) > 16 else "pcm_s16le")
        args = ["-i", item.path, "-map", f"0:{track.index}", "-vn", "-sn", "-af", flt, "-c:a", codec,
                "-map_metadata", "-1", out]
        new_duration = info.duration / float(factor) if info.duration else 0.0
        res = ctx.ffmpeg(args, new_duration, f"Скорость ×{float(factor):.5f}")
        ctx.values.setdefault("delete_original", ctx.get("DeleteOriginal", False))
        return finish_ffmpeg(ctx, res, item.path, out,
                             f"✅ {float(src_fps):.3f} → {float(dst_fps):.3f} fps: {os.path.basename(out)}")
