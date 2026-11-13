from __future__ import annotations

import os

from ..core.fields import COMBO, TEXT, Cond, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script
from ..media import ffmpeg as ff
from .common import delete_original_field, finish_ffmpeg

MODES = [
    "FFmpeg (коэффициенты HandBrake)",
    "FFmpeg + нормализация EBU R128",
    "Dolby Pro Logic II (матричное)",
]
OUT = {"E-AC3": (".eac3", "eac3"), "AC3": (".ac3", "ac3"), "AAC": (".m4a", "aac"), "FLAC": (".flac", "flac"),
       "Opus": (".opus", "libopus")}


class AudioDownmix(Script):
    id = "audio-downmix"
    name = "Даунмикс в Stereo"
    description = "Сведение 5.1/7.1 в стерео 2.0 (DDP/DD/AAC/FLAC) — HandBrake-матрица, EBU R128 или Pro Logic II"
    category = "Аудио"
    icon = "audio-speakers-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        G = "Параметры сведения"
        return [
            Field("DownmixMode", "Режим сведения", COMBO, MODES[0], G, options=MODES,
                  comment="В Linux вместо Dolby Encoding Engine — точная матрица ffmpeg"),
            Field("OutputFormat", "Формат вывода", COMBO, "E-AC3", G, options=list(OUT)),
            Field("Bitrate", "Битрейт (кбит/с)", COMBO, "256", G,
                  options=["128", "192", "224", "256", "320", "384", "448", "640"],
                  visible=[Cond("OutputFormat", ("E-AC3", "AC3", "AAC", "Opus"))]),
            Field("Suffix", "Суффикс файла", TEXT, "_stereo", "Общие"),
            delete_original_field("Общие", "DeleteOriginal"),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        if not info.audio:
            return Result.failed("❌ В файле нет аудиодорожки")
        track = info.audio[0]
        fmt = str(ctx.get("OutputFormat", "E-AC3"))
        ext, codec = OUT.get(fmt, (".eac3", "eac3"))
        out = ctx.output_path(item.path, ext=ext, suffix=str(ctx.get("Suffix", "_stereo")))
        if skip := ctx.skip_if_exists(out):
            return skip
        mode = str(ctx.get("DownmixMode", MODES[0]))
        if track.channels <= 2:
            ctx.log("WARNING", f"{item.name}: дорожка уже {track.channels}-канальная — будет просто перекодирована")
        af: list[str] = []
        extra: list[str] = []
        if mode.startswith("Dolby"):
            af.append("aresample=matrix_encoding=dplii")
            extra += ["-ac", "2"]
        elif track.channels > 2:
            af.append(ff.STEREO_DOWNMIX_PAN)
        if "R128" in mode:
            af.append("loudnorm=I=-23:TP=-1:LRA=11")
            extra += ["-ar", str(track.sample_rate or 48000)]
        args = ["-i", item.path, "-map", f"0:{track.index}", "-vn", "-sn", "-map_metadata", "-1"]
        if af:
            args += ["-af", ",".join(af)]
        args += [*extra, "-c:a", codec]
        if codec != "flac":
            args += ["-b:a", f"{ctx.get('Bitrate', '256')}k"]
        if ext == ".m4a":
            args += ["-movflags", "+faststart"]
        args.append(out)
        res = ctx.ffmpeg(args, info.duration, "Сведение в стерео")
        ctx.values.setdefault("delete_original", ctx.get("DeleteOriginal", False))
        return finish_ffmpeg(ctx, res, item.path, out, f"✅ Стерео: {os.path.basename(out)}")
