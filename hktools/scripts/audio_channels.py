from __future__ import annotations

import os

from ..core.fields import CHECK, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script, remove_quiet
from ..media import ffmpeg as ff
from .common import delete_original_field

# имена каналов ffmpeg → суффиксы как у eac3to
CHANNEL_SUFFIX = {"FL": "L", "FR": "R", "FC": "C", "LFE": "LFE", "SL": "SL", "SR": "SR", "BL": "BL", "BR": "BR",
                  "BC": "BC", "FLC": "FLC", "FRC": "FRC", "TC": "TC", "LFE2": "LFE2", "WL": "WL", "WR": "WR"}
PAIRS = (("L", "R", "LR"), ("SL", "SR", "SLSR"), ("BL", "BR", "BLBR"))


def layout_channels(layout: str, channels: int) -> list[str]:
    """Список каналов раскладки ffmpeg (5.1(side) → FL FR FC LFE SL SR)."""
    known = {
        "mono": ["FC"], "stereo": ["FL", "FR"], "2.1": ["FL", "FR", "LFE"], "3.0": ["FL", "FR", "FC"],
        "quad": ["FL", "FR", "BL", "BR"], "quad(side)": ["FL", "FR", "SL", "SR"],
        "4.0": ["FL", "FR", "FC", "BC"], "5.0": ["FL", "FR", "FC", "BL", "BR"],
        "5.0(side)": ["FL", "FR", "FC", "SL", "SR"], "5.1": ["FL", "FR", "FC", "LFE", "BL", "BR"],
        "5.1(side)": ["FL", "FR", "FC", "LFE", "SL", "SR"], "6.1": ["FL", "FR", "FC", "LFE", "BC", "SL", "SR"],
        "7.1": ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"],
        "7.1(wide)": ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC"],
    }
    if layout in known:
        return known[layout]
    if "+" in layout:
        return layout.split("+")
    defaults = {1: "mono", 2: "stereo", 3: "2.1", 4: "quad", 5: "5.0", 6: "5.1", 7: "6.1", 8: "7.1"}
    return known.get(defaults.get(channels, ""), [f"c{i}" for i in range(channels)])


class AudioChannels(Script):
    id = "audio-channels"
    name = "Разделение каналов"
    description = "Разделение многоканального аудио на моно-WAV (L, R, C, LFE, SL, SR…) со склейкой стереопар"
    category = "Аудио"
    icon = "audio-card-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        return [
            Field("MergeStereo", "Склеивать каналы в стереопары (LR, SLSR, BLBR)", CHECK, True, "Параметры разделения"),
            delete_original_field("Общие", "DeleteOriginal"),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        if not info.audio:
            return Result.failed("❌ В файле нет аудиодорожки")
        track = info.audio[0]
        if track.channels < 2:
            return Result.skipped("⏭ Дорожка моно — разделять нечего")
        chans = layout_channels(track.channel_layout, track.channels)
        layout = track.channel_layout if track.channel_layout and "+" not in track.channel_layout else None
        first = ctx.output_path(item.path, ext=".wav", suffix=".L")
        base = first[: -len(".L.wav")]
        outputs = {CHANNEL_SUFFIX.get(c, c): f"{base}.{CHANNEL_SUFFIX.get(c, c)}.wav" for c in chans}
        if all(os.path.exists(p) for p in outputs.values()) and not ctx.overwrite:
            return Result.skipped("⏭ ПРОПУСК: моно-каналы уже существуют")
        codec = "pcm_s24le" if (track.bit_depth or 24) > 16 else "pcm_s16le"
        split = f"[0:{track.index}]channelsplit=" + (f"channel_layout={layout}" if layout else "")
        labels = "".join(f"[{CHANNEL_SUFFIX.get(c, c)}]" for c in chans)
        args = ["-i", item.path, "-filter_complex", split.rstrip("=") + labels]
        for c in chans:
            suf = CHANNEL_SUFFIX.get(c, c)
            args += ["-map", f"[{suf}]", "-c:a", codec, outputs[suf]]
        res = ctx.ffmpeg(args, info.duration, "Разделение каналов", span=70 if ctx.get("MergeStereo") else 100)
        if not res.ok:
            for p in outputs.values():
                remove_quiet(p)
            return Result.failed(f"❌ Ошибка разделения каналов\n{res.error_text()}")
        result = Result.done(*outputs.values(), msg=f"✅ Разделено на {len(outputs)} канал(а/ов): {os.path.basename(base)}")
        if ctx.get("MergeStereo"):
            for left, right, pair in PAIRS:
                if left in outputs and right in outputs:
                    target = f"{base}.{pair}.wav"
                    ctx.progress(75, f"Склейка {pair}…")
                    r = ff.run_ffmpeg(["-i", outputs[left], "-i", outputs[right], "-filter_complex",
                                       "[0:a][1:a]join=inputs=2:channel_layout=stereo:map=0.0-FL|1.0-FR[a]",
                                       "-map", "[a]", "-c:a", codec, target], ctx.token, source=self.name)
                    ctx.check()
                    if r.ok:
                        result.outputs.append(target)
                    else:
                        result.status = "partial"
                        result.messages.append(f"⚠ Не удалось склеить {pair}")
        ctx.values.setdefault("delete_original", ctx.get("DeleteOriginal", False))
        ctx.finalize_source(item.path, result.outputs[0], result)
        ctx.progress(100, "Готово")
        return result
