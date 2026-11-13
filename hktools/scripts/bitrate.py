"""Анализ битрейта видео/аудио по пакетам (ffprobe) + экспорт графика в PNG (cairo)."""

from __future__ import annotations

import math
import os
import statistics
from dataclasses import dataclass, field

from .. import tools
from ..core.fields import CHECK, COMBO, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script
from ..media import process
from ..media.process import Cancelled


@dataclass
class BitrateData:
    path: str
    stream_label: str
    codec: str
    duration: float
    fps: float
    per_second: list[float] = field(default_factory=list)   # Мбит/с
    keyframes: list[float] = field(default_factory=list)
    packets: int = 0
    total_bytes: int = 0

    @property
    def mean(self) -> float:
        return statistics.fmean(self.per_second) if self.per_second else 0.0

    @property
    def peak(self) -> float:
        return max(self.per_second) if self.per_second else 0.0

    @property
    def minimum(self) -> float:
        return min(self.per_second) if self.per_second else 0.0

    @property
    def stdev(self) -> float:
        return statistics.stdev(self.per_second) if len(self.per_second) > 1 else 0.0

    @property
    def gop(self) -> float:
        if len(self.keyframes) < 2:
            return 0.0
        return (self.keyframes[-1] - self.keyframes[0]) / (len(self.keyframes) - 1)


def analyze(path: str, selector: str, duration: float, token: process.CancelToken,
            on_progress=None) -> BitrateData:
    ffprobe = tools.require("ffprobe")
    per: dict[int, int] = {}
    keys: list[float] = []
    count = [0, 0]  # пакеты, байты

    def on_line(line: str) -> None:
        if not line.startswith("packet|"):
            return
        size = 0
        pts = dts = -1.0
        key = False
        for seg in line.split("|")[1:]:
            k, _, v = seg.partition("=")
            if k == "size":
                try:
                    size = int(v)
                except ValueError:
                    pass
            elif k == "pts_time":
                try:
                    pts = float(v)
                except ValueError:
                    pass
            elif k == "dts_time":
                try:
                    dts = float(v)
                except ValueError:
                    pass
            elif k == "flags":
                key = "K" in v
        t = pts if pts >= 0 else dts
        if t < 0:
            return
        sec = int(t)
        per[sec] = per.get(sec, 0) + size * 8
        count[0] += 1
        count[1] += size
        if key:
            keys.append(t)
        if on_progress and count[0] % 2000 == 0 and duration > 0:
            on_progress(min(95.0, t / duration * 95), f"Анализ пакетов: {t:.0f} / {duration:.0f} с")

    cmd = [ffprobe, "-hide_banner", "-loglevel", "quiet", "-select_streams", selector,
           "-show_entries", "packet=flags,size,pts_time,dts_time", "-print_format", "compact", path]
    res = process.run(cmd, token, on_stdout=on_line, source="ffprobe")
    if res.cancelled:
        raise Cancelled()
    if not per:
        raise RuntimeError("Не удалось прочитать пакеты потока")
    last = max(per)
    data = BitrateData(path, selector, "", duration or float(last + 1), 0.0,
                       [round(per.get(i, 0) / 1e6, 3) for i in range(last + 1)], keys, count[0], count[1])
    return data


def render_png(data: BitrateData, path: str, width: int = 1920, height: int = 720, dark: bool = True) -> None:
    import cairo  # noqa: PLC0415

    surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, width, height)
    cr = cairo.Context(surface)
    draw_chart(cr, width, height, data, dark=dark, title=True)
    surface.write_to_png(path)


def nice_step(maxv: float, ticks: int = 5) -> float:
    if maxv <= 0:
        return 1.0
    raw = maxv / ticks
    mag = 10 ** math.floor(math.log10(raw))
    for m in (1, 2, 2.5, 5, 10):
        if raw <= m * mag:
            return m * mag
    return 10 * mag


def draw_chart(cr, width: float, height: float, data: BitrateData, *, dark: bool = True, title: bool = False,
               view: tuple[float, float] | None = None, hover_x: float | None = None, accent=(0.11, 0.62, 0.89)) -> None:
    """Отрисовка графика битрейта (используется и в окне, и для PNG)."""
    fg = (0.92, 0.92, 0.94) if dark else (0.12, 0.12, 0.14)
    grid = (1, 1, 1, 0.08) if dark else (0, 0, 0, 0.08)
    bg = (0.12, 0.12, 0.14) if dark else (0.98, 0.98, 0.98)
    cr.set_source_rgb(*bg)
    cr.paint()
    left, right, top, bottom = 64, 20, 48 if title else 20, 36
    pw, ph = max(10, width - left - right), max(10, height - top - bottom)
    series = data.per_second
    n = len(series)
    if not n:
        return
    t0, t1 = view if view else (0.0, float(n))
    t1 = max(t0 + 1, t1)
    i0, i1 = max(0, int(t0)), min(n, int(math.ceil(t1)))
    visible = series[i0:i1] or [0.0]
    peak = max(visible) or 1.0
    step = nice_step(peak * 1.1)
    ymax = step * math.ceil(peak * 1.1 / step)
    cr.select_font_face("Sans", 0, 0)
    cr.set_font_size(12)
    # сетка Y
    v = 0.0
    while v <= ymax + 1e-9:
        y = top + ph - v / ymax * ph
        cr.set_source_rgba(*grid)
        cr.move_to(left, y)
        cr.line_to(left + pw, y)
        cr.stroke()
        cr.set_source_rgb(*fg)
        label = f"{v:g}"
        ext = cr.text_extents(label)
        cr.move_to(left - 8 - ext.width, y + 4)
        cr.show_text(label)
        v += step
    # подпись оси времени
    span = t1 - t0
    tstep = nice_step(span, 8)
    t = math.ceil(t0 / tstep) * tstep
    while t <= t1:
        x = left + (t - t0) / span * pw
        cr.set_source_rgba(*grid)
        cr.move_to(x, top)
        cr.line_to(x, top + ph)
        cr.stroke()
        cr.set_source_rgb(*fg)
        s = int(t)
        label = f"{s // 3600}:{s % 3600 // 60:02d}:{s % 60:02d}" if span > 600 else f"{s // 60}:{s % 60:02d}"
        ext = cr.text_extents(label)
        cr.move_to(x - ext.width / 2, top + ph + 20)
        cr.show_text(label)
        t += tstep
    # ключевые кадры
    if data.keyframes and span < 900:
        cr.set_source_rgba(1, 0.65, 0.2, 0.25)
        for k in data.keyframes:
            if t0 <= k <= t1:
                x = left + (k - t0) / span * pw
                cr.move_to(x, top)
                cr.line_to(x, top + ph)
                cr.stroke()
    # площадь графика
    cr.save()
    cr.rectangle(left, top, pw, ph)
    cr.clip()
    cr.move_to(left + (i0 - t0) / span * pw, top + ph)
    for i in range(i0, i1):
        x = left + (i - t0 + 0.5) / span * pw
        y = top + ph - series[i] / ymax * ph
        cr.line_to(x, y)
    cr.line_to(left + (i1 - t0) / span * pw, top + ph)
    cr.close_path()
    r, g, b = accent
    cr.set_source_rgba(r, g, b, 0.28)
    cr.fill_preserve()
    cr.set_source_rgba(r, g, b, 0.95)
    cr.set_line_width(1.4)
    cr.stroke()
    # среднее
    my = top + ph - data.mean / ymax * ph
    cr.set_dash([6, 4])
    cr.set_source_rgba(0.95, 0.43, 0.3, 0.9)
    cr.move_to(left, my)
    cr.line_to(left + pw, my)
    cr.stroke()
    cr.set_dash([])
    cr.restore()
    cr.set_source_rgb(*fg)
    cr.move_to(8, top - 6 if top > 24 else 14)
    cr.set_font_size(11)
    cr.show_text("Мбит/с")
    if title:
        cr.set_font_size(16)
        cr.move_to(left, 28)
        cr.show_text(f"{os.path.basename(data.path)} — {data.stream_label}  ·  среднее {data.mean:.2f}  "
                     f"·  пик {data.peak:.2f}  ·  мин {data.minimum:.2f} Мбит/с")
    if hover_x is not None and left <= hover_x <= left + pw:
        tt = t0 + (hover_x - left) / pw * span
        idx = int(tt)
        if 0 <= idx < n:
            cr.set_source_rgba(*fg, 0.6)
            cr.move_to(hover_x, top)
            cr.line_to(hover_x, top + ph)
            cr.stroke()
            s = idx
            label = f"{s // 3600}:{s % 3600 // 60:02d}:{s % 60:02d} — {series[idx]:.2f} Мбит/с"
            cr.set_font_size(12)
            ext = cr.text_extents(label)
            bx = min(hover_x + 8, left + pw - ext.width - 12)
            cr.set_source_rgba(*bg, 0.9)
            cr.rectangle(bx - 4, top + 4, ext.width + 8, 20)
            cr.fill()
            cr.set_source_rgb(*fg)
            cr.move_to(bx, top + 18)
            cr.show_text(label)


class BitrateViewer(Script):
    id = "bitrate-viewer"
    name = "Анализ битрейта"
    description = "Посекундный битрейт видео и аудио, ключевые кадры, статистика и интерактивный график"
    category = "Инструменты"
    icon = "utilities-system-monitor-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        return [
            Field("stream", "Поток", COMBO, "Видео", "Параметры анализа", options=["Видео", "Первое аудио"]),
            Field("save_png_export", "Сохранять копию графика в PNG", CHECK, False, "Параметры графиков",
                  comment="PNG 1920×720 в папку результатов"),
            Field("open_chart", "Открывать окно графика после анализа", CHECK, True, "Параметры графиков"),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        info = ctx.probe()
        want_video = ctx.get("stream", "Видео") == "Видео"
        track = (info.video[0] if info.video else None) if want_video else (info.audio[0] if info.audio else None)
        if track is None:
            return Result.failed("❌ В файле нет нужного потока")
        selector = f"{'v' if track.type == 'video' else 'a'}:{track.type_index}"
        data = analyze(item.path, selector, info.duration, ctx.token, lambda p, m: ctx.progress(p, m))
        data.codec = track.codec
        data.stream_label = f"{'Видео' if track.type == 'video' else 'Аудио'} #{track.index} ({track.codec})"
        if track.type == "video" and data.duration > 0:
            fps = data.packets / data.duration
            data.fps = round(fps, 3) if 1 <= fps <= 360 else track.fps
        outputs = []
        if ctx.get("save_png_export"):
            png = ctx.output_path(item.path, ext=".png", suffix="_bitrate")
            try:
                render_png(data, png)
                outputs.append(png)
            except Exception as exc:  # noqa: BLE001
                ctx.log("WARNING", f"PNG не сохранён: {exc}")
        ctx.progress(100, "Готово")
        return Result.done(*outputs, data=data,
                           msg=f"📈 {item.name}: среднее {data.mean:.2f}, пик {data.peak:.2f}, мин {data.minimum:.2f} "
                               f"Мбит/с, σ {data.stdev:.2f}, ключевых кадров {len(data.keyframes)}")
