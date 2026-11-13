from __future__ import annotations

import os

from .. import tools
from ..core.fields import COMBO, INT, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script, remove_quiet
from ..media import process
from ..media.probe import mkvmerge_identify
from ..media.process import Cancelled

MODES = ["MKA без перекодирования (mkvmerge)", "FLAC (FFmpeg Lossless)", "WAV (FFmpeg PCM)"]


def shift_filter(ms: int) -> str:
    if ms >= 0:
        return f"adelay={ms}:all=1"
    return f"atrim=start={abs(ms) / 1000:.3f},asetpts=PTS-STARTPTS"


def mkvmerge_audio_tid(path: str) -> int | None:
    data = mkvmerge_identify(path)
    for t in data.get("tracks", []):
        if t.get("type") == "audio":
            return int(t.get("id", 0))
    return None


def mkvmerge_progress_runner(ctx: Context, cmd: list[str], label: str, base: float = 0, span: float = 100) -> process.ProcResult:
    def on_line(line: str) -> None:
        if "%" in line and ":" in line:
            try:
                pct = float(line.rsplit(":", 1)[1].strip().rstrip("%"))
                ctx.progress(base + pct * span / 100, f"{label} · {pct:.0f}%")
            except ValueError:
                pass

    res = process.run(cmd, ctx.token, on_stdout=on_line, split_cr=True, capture_stdout=True,
                      source=ctx.script.name)
    if res.cancelled:
        raise Cancelled()
    # mkvmerge пишет ошибки и предупреждения в stdout
    msgs = [ln for ln in res.stdout.splitlines() if "%" not in ln and ln.strip()]
    res.stderr_tail = msgs[-15:] + res.stderr_tail
    for ln in msgs:
        if ln.lower().startswith(("warning", "предупреждение")):
            ctx.log("WARNING", ln)
    # mkvmerge: 0 — ок, 1 — предупреждения (файл создан), 2 — ошибка
    if res.returncode == 1:
        res.returncode = 0
    return res


class AudioShift(Script):
    id = "audio-shift"
    name = "Сдвиг аудио"
    description = "Сдвиг (задержка) аудиодорожки: без перекодирования через mkvmerge или в FLAC/WAV"
    category = "Аудио"
    icon = "media-skip-forward-symbolic"
    extensions = ALL_MEDIA
    deps = ("ffmpeg", "ffprobe", "mkvmerge")

    def fields(self, values=None):
        return [
            Field("ShiftMs", "Величина сдвига (мс)", INT, 1000, "Настройки сдвига", minimum=0, maximum=36_000_000,
                  step=10),
            Field("ShiftDirection", "Направление сдвига", COMBO, "Вперед", "Настройки сдвига",
                  options=["Вперед", "Назад"], comment="Вперёд — звук позже (задержка), назад — раньше"),
            Field("OutputFormat", "Формат и режим вывода", COMBO, MODES[0], "Настройки экспорта", options=MODES,
                  comment="mkvmerge сохраняет исходный кодек бит-в-бит (замена eac3to)"),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        ms = int(ctx.get("ShiftMs", 0))
        if ctx.get("ShiftDirection") == "Назад":
            ms = -ms
        mode = str(ctx.get("OutputFormat", MODES[0]))
        info = ctx.probe()
        if not info.audio:
            return Result.failed("❌ В файле нет аудиодорожки")
        sign = f"{ms:+d}"
        if mode.startswith("MKA"):
            mkvmerge = tools.require("mkvmerge")
            tid = mkvmerge_audio_tid(item.path)
            if tid is None:
                return Result.failed("❌ mkvmerge не нашёл аудиодорожку")
            out = ctx.output_path(item.path, ext=".mka", suffix=f"_shift{sign}ms")
            if skip := ctx.skip_if_exists(out):
                return skip
            cmd = [mkvmerge, "-o", out, "--no-video", "--no-subtitles", "--no-chapters",
                   "--no-attachments", "--audio-tracks", str(tid), "--sync", f"{tid}:{ms}", item.path]
            res = mkvmerge_progress_runner(ctx, cmd, "Сдвиг (mkvmerge)")
        else:
            flac = mode.startswith("FLAC")
            out = ctx.output_path(item.path, ext=".flac" if flac else ".wav", suffix=f"_shift{sign}ms")
            if skip := ctx.skip_if_exists(out):
                return skip
            track = info.audio[0]
            codec = "flac" if flac else ("pcm_s24le" if (track.bit_depth or 24) > 16 else "pcm_s16le")
            res = ctx.ffmpeg(["-i", item.path, "-map", f"0:{track.index}", "-vn", "-sn", "-af", shift_filter(ms),
                              "-c:a", codec, out], info.duration + max(0, ms) / 1000, "Сдвиг аудио")
        if res.ok and os.path.isfile(out):
            ctx.progress(100, "Готово")
            return Result.done(out, msg=f"✅ Сдвиг {sign} мс: {os.path.basename(out)}")
        remove_quiet(out)
        return Result.failed(f"❌ Ошибка сдвига\n{res.error_text()}")
