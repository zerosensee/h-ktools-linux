from __future__ import annotations

import os
import re

from .. import tools
from ..core.fields import CHECK, COMBO, EXPANDER, Cond, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script, remove_quiet
from ..media import ffmpeg as ff
from ..media import process
from ..media.process import Cancelled
from .common import delete_original_field

# формат → (расширение, кодек ffmpeg)
FORMATS: dict[str, tuple[str, str]] = {
    "AAC (FDK-AAC)": (".m4a", "fdkaac"),
    "QAAC (Wine)": (".m4a", "qaac"),
    "AAC (FFmpeg)": (".m4a", "aac"),
    "FLAC": (".flac", "flac"),
    "WAV": (".wav", "pcm_s24le"),
    "AC3": (".ac3", "ac3"),
    "EAC3": (".eac3", "eac3"),
    "MP3": (".mp3", "libmp3lame"),
    "OPUS": (".opus", "libopus"),
    "OGG": (".ogg", "libvorbis"),
    "DTS": (".dts", "dca"),
    "WavPack": (".wv", "wavpack"),
    "ALAC": (".m4a", "alac"),
    "WMA": (".wma", "wmav2"),
    "AIFF": (".aiff", "pcm_s16be"),
    "ADPCM": (".wav", "adpcm_ima_wav"),
}
WAV_DEPTHS = {"16-bit": "pcm_s16le", "24-bit": "pcm_s24le", "32-bit": "pcm_s32le", "32-bit Float": "pcm_f32le"}
BITRATE_FORMATS = ("AAC (FFmpeg)", "MP3", "OGG", "AC3", "EAC3", "DTS", "WMA", "OPUS", "ADPCM")
QAAC_MODES = ["True VBR (-V)", "Constrained VBR (-v)", "ABR (-a)", "CBR (-c)", "HE AAC (--he)"]
FDK_MODES = ["VBR (-m)", "CBR (-b)", "HE-AAC (-p 5)", "HE-AAC v2 (-p 29)"]


class AudioEncode(Script):
    id = "audio-encode"
    name = "Кодирование аудио"
    description = "Перекодирование в FDK-AAC, FLAC, WAV, AC3/E-AC3, Opus, MP3 и др. (QAAC — через Wine)"
    category = "Аудио"
    icon = "audio-x-generic-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        E, P = "Экспорт", "Экспорт:Параметры кодирования"
        is_fdk, is_qaac = Cond("target_format", "AAC (FDK-AAC)"), Cond("target_format", "QAAC (Wine)")
        return [
            Field("target_format", "Целевой формат", COMBO, "AAC (FDK-AAC)", E, options=list(FORMATS)),
            Field("use_m4a_container", "Упаковать AAC в контейнер M4A", CHECK, True, E,
                  visible=[Cond("target_format", ("AAC (FDK-AAC)", "QAAC (Wine)", "AAC (FFmpeg)"))],
                  comment="Без контейнера (ADTS) плееры и Telegram могут показывать неверную длительность",
                  warning=("Внимание!", "Без контейнера M4A плееры, файловые менеджеры и Telegram могут показывать "
                           "неправильную (огромную) длительность. Сам звук при этом в порядке.")),
            # FDK
            Field("fdk_mode", "Режим FDK-AAC", COMBO, "VBR (-m)", P, options=FDK_MODES, visible=[is_fdk]),
            Field("fdk_vbr", "Качество VBR (1–5)", COMBO, "5", P, options=["1", "2", "3", "4", "5"],
                  comment="5 ≈ 224+ кбит/с стерео, 4 ≈ 128–160", visible=[is_fdk, Cond("fdk_mode", "VBR (-m)")]),
            Field("fdk_bitrate", "Битрейт (кбит/с)", COMBO, "192", P,
                  options=["32", "48", "64", "80", "96", "128", "160", "192", "224", "256", "320"],
                  visible=[is_fdk, Cond("fdk_mode", "VBR (-m)", negate=True)]),
            Field("fdk_afterburner", "Afterburner (лучше качество, медленнее)", CHECK, True, P, visible=[is_fdk]),
            # QAAC
            Field("qaac_mode", "Режим кодирования QAAC", COMBO, "True VBR (-V)", P, options=QAAC_MODES,
                  visible=[is_qaac], comment="Нужен qaac64.exe + Wine: путь задаётся в Настройках"),
            Field("qaac_quality", "Качество True VBR [0–127]", COMBO, "127", P,
                  options=["0", "16", "32", "48", "64", "80", "90", "96", "112", "127"],
                  visible=[is_qaac, Cond("qaac_mode", "True VBR (-V)")]),
            Field("qaac_bitrate", "Битрейт AAC (кбит/с)", COMBO, "192", P,
                  options=["0", "64", "96", "128", "160", "192", "224", "256", "320"],
                  comment="0 — максимальный доступный",
                  visible=[is_qaac, Cond("qaac_mode", "True VBR (-V)", negate=True)]),
            Field("qaac_advanced", "Дополнительные параметры QAAC", EXPANDER, None, P, toggle=False,
                  visible=[is_qaac], children=[
                      Field("qaac_no_delay", "Компенсировать задержку энкодера (--no-delay)", CHECK, False, P),
                      Field("qaac_limiter", "Смарт-лимитер (--limiter)", CHECK, False, P),
                  ]),
            # FFmpeg
            Field("bitrate", "Битрейт (кбит/с)", COMBO, "320k", P,
                  options=["64k", "96k", "128k", "160k", "192k", "224k", "256k", "320k", "448k", "640k"],
                  visible=[Cond("target_format", BITRATE_FORMATS)],
                  comment="Для OGG битрейт ограничивается по числу каналов"),
            Field("compression", "Уровень сжатия (FLAC 0–12, WavPack 0–8)", COMBO, "5", P,
                  options=[str(i) for i in range(13)], visible=[Cond("target_format", ("FLAC", "WavPack"))]),
            Field("wav_bit_depth", "Битность WAV", COMBO, "24-bit", P, options=list(WAV_DEPTHS),
                  visible=[Cond("target_format", "WAV")]),
            Field("audio_track", "Аудиодорожка", COMBO, "Первая", "Общие", options=["Первая", "Все (отдельные файлы)"]),
            delete_original_field(),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        tracks = info.audio
        if not tracks:
            return Result.failed("❌ В файле нет аудиодорожек")
        if ctx.get("audio_track") != "Все (отдельные файлы)":
            tracks = tracks[:1]
        fmt = str(ctx.get("target_format", "AAC (FDK-AAC)"))
        ext, codec = FORMATS.get(fmt, (".m4a", "aac"))
        if codec in ("fdkaac", "qaac", "aac") and not ctx.get("use_m4a_container", True):
            ext = ".aac"
        result = Result.done()
        for n, track in enumerate(tracks):
            suffix = f"_a{track.type_index}" if len(tracks) > 1 else ""
            out = ctx.output_path(item.path, ext=ext, suffix=suffix)
            if skip := ctx.skip_if_exists(out):
                result.messages += skip.messages
                continue
            base, span = n * 100 / len(tracks), 100 / len(tracks)
            if codec == "fdkaac":
                ok, err = self._pipe(ctx, item.path, track.index, out, self._fdk_cmd(ctx, out), info.duration, base, span)
            elif codec == "qaac":
                qaac = tools.qaac_command()
                if not qaac:
                    return Result.failed("❌ QAAC не настроен: укажите команду (например «wine ~/qaac/qaac64.exe») "
                                         "в Настройки → Внешние утилиты, либо выберите «AAC (FDK-AAC)»")
                ok, err = self._pipe(ctx, item.path, track.index, out, self._qaac_cmd(ctx, qaac, out), info.duration,
                                     base, span, cwd=os.path.dirname(out))
            else:
                args = ["-i", item.path, "-map", f"0:{track.index}", "-vn", "-sn", "-map_metadata", "-1"]
                args += self._ffmpeg_codec_args(ctx, fmt, codec, track.channels, track.channel_layout)
                args.append(out)
                res = ctx.ffmpeg(args, info.duration, f"Кодирование {fmt}", base=base, span=span)
                ok, err = res.ok, res.error_text()
            if ok and os.path.isfile(out) and os.path.getsize(out) > 0:
                result.outputs.append(out)
                result.messages.append(f"✅ {fmt}: {os.path.basename(out)}")
            else:
                remove_quiet(out)
                result.status = "failed" if not result.outputs else "partial"
                result.messages.append(f"❌ Ошибка кодирования дорожки #{track.index}\n{err}")
        if not result.outputs and result.status == "done":
            result.status = "skipped"
        if result.outputs and result.status == "done":
            ctx.finalize_source(item.path, result.outputs[0], result)
        return result

    # --- ffmpeg ---
    @staticmethod
    def _ffmpeg_codec_args(ctx: Context, fmt: str, codec: str, channels: int, layout: str = "") -> list[str]:
        pre: list[str] = []
        if fmt == "OPUS" and channels > 2:
            # libopus принимает только «тыловые» раскладки Vorbis: 5.1(side) → 5.1 без смешивания каналов
            if layout == "5.1(side)":
                pre += ["-af", "channelmap=channel_layout=5.1"]
            pre += ["-mapping_family", "1"]
        if fmt == "WMA" and channels > 2:
            pre += ["-af", ff.STEREO_DOWNMIX_PAN]
        return pre + AudioEncode._codec_only(ctx, fmt, codec, channels)

    @staticmethod
    def _codec_only(ctx: Context, fmt: str, codec: str, channels: int) -> list[str]:
        if fmt == "WAV":
            return ["-c:a", WAV_DEPTHS.get(str(ctx.get("wav_bit_depth", "24-bit")), "pcm_s24le")]
        args = ["-c:a", codec]
        br = str(ctx.get("bitrate", "320k"))
        if fmt in ("FLAC", "WavPack"):
            level = int(ctx.get("compression", "5"))
            args += ["-compression_level", str(min(level, 8) if fmt == "WavPack" else level)]
        elif fmt in BITRATE_FORMATS:
            if fmt == "OGG":
                kbps = int(br.rstrip("k"))
                limit = 224 if channels == 1 else 500 if channels == 2 else 1000
                br = f"{min(kbps, limit)}k"
            if fmt == "DTS" and channels > 2 and int(br.rstrip("k")) < 768:
                br = "768k"  # кодер dca не принимает меньший битрейт для многоканала
            args += ["-b:a", br]
        if fmt == "DTS":
            args += ["-strict", "-2"]
        if fmt == "AAC (FFmpeg)" and ctx.get("use_m4a_container", True):
            args += ["-movflags", "+faststart"]
        return args

    # --- конвейеры AAC ---
    @staticmethod
    def _fdk_cmd(ctx: Context, out: str) -> list[str]:
        cmd = [tools.require("fdkaac"), "--ignorelength", "-S"]
        mode = str(ctx.get("fdk_mode", "VBR (-m)"))
        if mode.startswith("VBR"):
            cmd += ["-m", str(ctx.get("fdk_vbr", "5"))]
        else:
            cmd += ["-b", str(ctx.get("fdk_bitrate", "192"))]
            if "29" in mode:
                cmd += ["-p", "29"]
            elif "-p 5" in mode:
                cmd += ["-p", "5"]
        cmd += ["-a", "1" if ctx.get("fdk_afterburner", True) else "0"]
        if out.endswith(".aac"):
            cmd += ["-f", "2"]  # ADTS
        cmd += ["-o", out, "-"]
        return cmd

    @staticmethod
    def _qaac_cmd(ctx: Context, qaac: list[str], out: str) -> list[str]:
        mode = str(ctx.get("qaac_mode", "True VBR (-V)"))
        cmd = [*qaac, "--ignorelength", "--threading"]
        br = str(ctx.get("qaac_bitrate", "192"))
        if mode.startswith("True VBR"):
            cmd += ["-V", str(ctx.get("qaac_quality", "127"))]
        elif mode.startswith("Constrained"):
            cmd += ["-v", br]
        elif mode.startswith("ABR"):
            cmd += ["-a", br]
        elif mode.startswith("CBR"):
            cmd += ["-c", br]
        else:
            cmd += ["--he", "-v", br]
        if ctx.get("qaac_no_delay"):
            cmd.append("--no-delay")
        if ctx.get("qaac_limiter"):
            cmd.append("--limiter")
        if out.endswith(".aac"):
            cmd.append("--adts")
        cmd += ["-o", os.path.basename(out), "-"]
        return cmd

    def _pipe(self, ctx: Context, src: str, stream: int, out: str, consumer: list[str], duration: float,
              base: float, span: float, cwd: str | None = None) -> tuple[bool, str]:
        producer = [ff.ffmpeg_bin(), "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:2",
                    "-nostats", "-i", src, "-map", f"0:{stream}", "-vn", "-sn", "-c:a", "pcm_s24le", "-f", "wav", "-"]
        if os.path.exists(out) and ctx.overwrite:
            os.remove(out)

        def on_err(line: str) -> None:
            if line.startswith("out_time_us=") and duration > 0:
                try:
                    pct = min(100.0, int(line.split("=", 1)[1]) / 1e6 / duration * 100)
                except ValueError:
                    return
                ctx.progress(base + pct * span / 100, f"Кодирование AAC · {pct:.1f}%")

        res = process.run_pipeline(producer, consumer, ctx.token, on_producer_stderr=on_err, cwd=cwd,
                                   source=self.name)
        if res.cancelled:
            raise Cancelled()
        errors = [l for l in res.stderr_tail if not re.match(r"^\s*\w+=", l)]
        return res.ok, "\n".join(errors[-10:])
