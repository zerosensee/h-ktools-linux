"""Окно синхронизации «Пересадки аудио» по осциллограммам (замена Win2D-окна AudioSync)."""

from __future__ import annotations

import os
import threading

from gi.repository import Adw, Gdk, Gtk, Pango

from ..core.script import AUDIO_ALL, VIDEO_CONTAINERS
from ..media import waveform
from ..media.probe import probe
from ..media.process import CancelToken
from . import util

LIMITS = [("Первые 5 мин", 300), ("Первые 10 мин", 600), ("Первые 30 мин", 1800), ("Весь файл", None)]


class WaveformSync(Adw.Window):
    def __init__(self, panel) -> None:
        super().__init__(title="Синхронизация по осциллограмме", default_width=1200, default_height=640,
                         transient_for=panel.window)
        self.panel = panel
        vals = panel.form.values
        self.ref_env: waveform.Envelope | None = None
        self.don_env: waveform.Envelope | None = None
        self.shift = int(vals.get("transplant_shift_ms", 0))
        self.t0 = 0.0            # левый край, мс
        self.span = 20_000.0     # ширина окна, мс
        self.hover: float | None = None
        self._drag: tuple[str, float, float] | None = None
        self.token = CancelToken()
        self.ref_tracks: list = []
        self.don_tracks: list = []

        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        self.apply_btn = Gtk.Button(label="Применить сдвиг")
        self.apply_btn.add_css_class("suggested-action")
        self.apply_btn.connect("clicked", lambda *_: self._apply())
        header.pack_end(self.apply_btn)
        toolbar.add_top_bar(header)

        top = Gtk.Grid(column_spacing=8, row_spacing=6, margin_start=12, margin_end=12, margin_top=8, margin_bottom=4)
        # эталон — файл из очереди
        files = [i for i in panel.items if i.ext in VIDEO_CONTAINERS]
        self.ref_items = files
        top.attach(Gtk.Label(label="Эталон (видео):", xalign=0), 0, 0, 1, 1)
        self.ref_dd = Gtk.DropDown(model=Gtk.StringList.new([i.name for i in files] or ["(очередь пуста)"]),
                                   hexpand=True)
        self.ref_track_dd = Gtk.DropDown(model=Gtk.StringList.new(["—"]))
        top.attach(self.ref_dd, 1, 0, 1, 1)
        top.attach(self.ref_track_dd, 2, 0, 1, 1)
        top.attach(Gtk.Label(label="Донор аудио:", xalign=0), 0, 1, 1, 1)
        self.don_path = str(vals.get("transplant_source", "") or "")
        self.don_btn = Gtk.Button(hexpand=True)
        self.don_label = Gtk.Label(label=os.path.basename(self.don_path) or "Выбрать файл…",
                                   ellipsize=Pango.EllipsizeMode.MIDDLE)
        self.don_btn.set_child(self.don_label)
        self.don_btn.connect("clicked", lambda *_: self._pick_donor())
        self.don_track_dd = Gtk.DropDown(model=Gtk.StringList.new(["—"]))
        top.attach(self.don_btn, 1, 1, 1, 1)
        top.attach(self.don_track_dd, 2, 1, 1, 1)
        self.limit_dd = Gtk.DropDown(model=Gtk.StringList.new([x[0] for x in LIMITS]), selected=1)
        load = Gtk.Button(label="Построить осциллограммы")
        load.add_css_class("pill")
        load.connect("clicked", lambda *_: self._load())
        top.attach(self.limit_dd, 3, 0, 1, 1)
        top.attach(load, 3, 1, 1, 1)
        self.ref_dd.connect("notify::selected", lambda *_: self._probe_ref())

        self.area = Gtk.DrawingArea(vexpand=True, hexpand=True, focusable=True)
        self.area.set_draw_func(self._draw)
        motion = Gtk.EventControllerMotion()
        motion.connect("motion", lambda _c, x, y: self._set_hover(x))
        motion.connect("leave", lambda *_: self._set_hover(None))
        self.area.add_controller(motion)
        scroll = Gtk.EventControllerScroll.new(Gtk.EventControllerScrollFlags.BOTH_AXES)
        scroll.connect("scroll", self._on_scroll)
        self.area.add_controller(scroll)
        drag = Gtk.GestureDrag()
        drag.connect("drag-begin", self._drag_begin)
        drag.connect("drag-update", self._drag_update)
        drag.connect("drag-end", lambda *_: setattr(self, "_drag", None))
        self.area.add_controller(drag)
        keys = Gtk.EventControllerKey()
        keys.connect("key-pressed", self._on_key)
        self.area.add_controller(keys)

        bottom = Gtk.Box(spacing=6, margin_start=12, margin_end=12, margin_top=6, margin_bottom=10)
        self.progress = Gtk.ProgressBar(hexpand=True, visible=False, valign=Gtk.Align.CENTER, show_text=True)
        self.hint = Gtk.Label(label="Тяните нижнюю дорожку мышью · колёсико — масштаб · Shift+колёсико — прокрутка · "
                                    "←/→ ±1 мс, Shift ±10 мс", hexpand=True, xalign=0,
                              ellipsize=Pango.EllipsizeMode.END)
        self.hint.add_css_class("dim-label")
        self.hint.add_css_class("caption")
        bottom.append(self.hint)
        bottom.append(self.progress)
        for step in (-100, -10, -1):
            b = Gtk.Button(label=f"{step:+d}")
            b.add_css_class("flat")
            b.connect("clicked", lambda _b, s=step: self._set_shift(self.shift + s))
            bottom.append(b)
        self.spin = Gtk.SpinButton.new_with_range(-3_600_000, 3_600_000, 1)
        self.spin.set_value(self.shift)
        self.spin.set_width_chars(9)
        self.spin.connect("value-changed", lambda s: self._set_shift(int(s.get_value()), from_spin=True))
        bottom.append(self.spin)
        bottom.append(Gtk.Label(label="мс"))
        for step in (1, 10, 100):
            b = Gtk.Button(label=f"{step:+d}")
            b.add_css_class("flat")
            b.connect("clicked", lambda _b, s=step: self._set_shift(self.shift + s))
            bottom.append(b)
        auto = Gtk.Button(label="Авто")
        auto.set_tooltip_text("Подобрать сдвиг кросс-корреляцией огибающих")
        auto.connect("clicked", lambda *_: self._auto())
        bottom.append(auto)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        box.append(top)
        box.append(self.area)
        box.append(bottom)
        toolbar.set_content(box)
        self.set_content(toolbar)
        self.connect("close-request", lambda *_: (self.token.cancel(), False)[1])
        self._probe_ref()
        if self.don_path:
            self._probe_donor()

    # --- выбор файлов ---
    def _probe_ref(self) -> None:
        i = self.ref_dd.get_selected()
        if not (0 <= i < len(self.ref_items)):
            return
        path = self.ref_items[i].path

        def done(info, err) -> None:
            if err or info is None:
                return
            self.ref_tracks = info.audio
            self.ref_track_dd.set_model(Gtk.StringList.new([f"#{t.index} {t.describe()}"[:60] for t in info.audio]
                                                           or ["нет аудио"]))

        util.run_bg(lambda: probe(path), done)

    def _pick_donor(self) -> None:
        start = os.path.dirname(self.don_path) if self.don_path else None
        util.pick_files(self, "Файл-донор аудио", lambda ps: ps and self._set_donor(ps[0]), multiple=False, start=start,
                        patterns=[f"*{e}" for e in sorted(AUDIO_ALL | VIDEO_CONTAINERS)])

    def _set_donor(self, path: str) -> None:
        self.don_path = path
        self.don_label.set_label(os.path.basename(path))
        self.don_env = None
        self._probe_donor()
        self.area.queue_draw()

    def _probe_donor(self) -> None:
        path = self.don_path

        def done(info, err) -> None:
            if err or info is None:
                self.hint.set_label(f"Не удалось прочитать донора: {err}")
                return
            self.don_tracks = info.audio
            self.don_track_dd.set_model(Gtk.StringList.new([f"#{t.index} {t.describe()}"[:60] for t in info.audio]
                                                           or ["нет аудио"]))
            sel = int(self.panel.form.values.get("transplant_source_track", 0))
            if 0 <= sel < len(info.audio):
                self.don_track_dd.set_selected(sel)

        util.run_bg(lambda: probe(path), done)

    # --- загрузка огибающих ---
    def _load(self) -> None:
        ri, di = self.ref_track_dd.get_selected(), self.don_track_dd.get_selected()
        fi = self.ref_dd.get_selected()
        if not (0 <= fi < len(self.ref_items)) or not (0 <= ri < len(self.ref_tracks)) or \
                not (0 <= di < len(self.don_tracks)):
            self.hint.set_label("Выберите эталонное видео и файл-донор с аудиодорожками")
            return
        ref_path, ref_idx = self.ref_items[fi].path, self.ref_tracks[ri].index
        don_path, don_idx = self.don_path, self.don_tracks[di].index
        limit = LIMITS[self.limit_dd.get_selected()][1]
        self.token.cancel()
        self.token = CancelToken()
        token = self.token
        self.progress.set_visible(True)
        self.progress.set_fraction(0)
        self.progress.set_text("Декодирование эталона…")

        def prog(part: int, pct: float) -> None:
            util.idle(lambda: (self.progress.set_fraction((part * 100 + pct) / 200),
                               self.progress.set_text(("Эталон" if part == 0 else "Донор") + f" · {pct:.0f}%")))

        def work():
            r = waveform.compute(ref_path, ref_idx, limit, token, lambda p: prog(0, p))
            d = waveform.compute(don_path, don_idx, limit, token, lambda p: prog(1, p))
            return r, d

        def done(res, err) -> None:
            self.progress.set_visible(False)
            if err:
                if token is self.token:
                    self.hint.set_label(f"Ошибка: {err}")
                return
            self.ref_env, self.don_env = res
            self.t0 = 0.0
            self.span = min(30_000.0, float(max(self.ref_env.length_ms, 1000)))
            self.area.queue_draw()
            self.area.grab_focus()

        util.run_bg(work, done)

    def _auto(self) -> None:
        if not (self.ref_env and self.don_env):
            self.hint.set_label("Сначала постройте осциллограммы")
            return
        ev = threading.Event()
        self.progress.set_visible(True)
        self.progress.set_text("Поиск сдвига…")
        self.progress.pulse()
        ref, don = self.ref_env, self.don_env

        def done(lag, err) -> None:
            self.progress.set_visible(False)
            if err:
                self.hint.set_label(f"Авто-синхронизация не удалась: {err}")
                return
            self._set_shift(int(lag))
            self.panel.toast(f"Найден сдвиг: {int(lag):+d} мс")

        util.run_bg(lambda: waveform.auto_offset(ref, don, cancel=ev,
                                                 on_progress=lambda p: util.idle(self.progress.set_fraction, p / 100)),
                    done)

    # --- сдвиг ---
    def _set_shift(self, value: int, from_spin: bool = False) -> None:
        self.shift = int(value)
        if not from_spin and int(self.spin.get_value()) != self.shift:
            self.spin.set_value(self.shift)
        self.area.queue_draw()

    def _apply(self) -> None:
        form = self.panel.form
        form.set_value("transplant_shift_ms", self.shift)
        if self.don_path:
            form.set_value("transplant_source", self.don_path)
        di = self.don_track_dd.get_selected()
        if 0 <= di < len(self.don_tracks):
            form.set_value("transplant_source_track", di)
        self.panel.toast(f"Сдвиг {self.shift:+d} мс применён")
        self.close()

    # --- отрисовка ---
    def _geometry(self) -> tuple[float, float, float, float]:
        w, h = self.area.get_width(), self.area.get_height()
        ruler = 26.0
        lane = (h - ruler) / 2
        return float(w), float(h), ruler, lane

    def _draw(self, _a, cr, width: int, height: int) -> None:
        dark = Adw.StyleManager.get_default().get_dark()
        bg = (0.11, 0.11, 0.13) if dark else (0.97, 0.97, 0.98)
        fg = (0.9, 0.9, 0.92) if dark else (0.15, 0.15, 0.18)
        cr.set_source_rgb(*bg)
        cr.paint()
        w, h, ruler, lane = float(width), float(height), 26.0, (height - 26.0) / 2
        ms_per_px = self.span / max(1.0, w)
        # линейка
        cr.set_source_rgb(*fg)
        cr.select_font_face("Sans", 0, 0)
        cr.set_font_size(11)
        from ..scripts.bitrate import nice_step  # noqa: PLC0415

        step = nice_step(self.span / 1000, 10) * 1000
        t = (self.t0 // step) * step
        while t <= self.t0 + self.span:
            x = (t - self.t0) / ms_per_px
            cr.set_source_rgba(*fg, 0.25)
            cr.move_to(x, ruler)
            cr.line_to(x, h)
            cr.stroke()
            cr.set_source_rgba(*fg, 0.8)
            s = max(0, t) / 1000
            label = f"{int(s // 60)}:{s % 60:06.3f}" if step < 1000 else f"{int(s // 60)}:{int(s % 60):02d}"
            cr.move_to(x + 3, 16)
            cr.show_text(label)
            t += step
        if not (self.ref_env and self.don_env):
            cr.set_source_rgba(*fg, 0.6)
            cr.set_font_size(15)
            msg = "Выберите файлы и нажмите «Построить осциллограммы»"
            ext = cr.text_extents(msg)
            cr.move_to((w - ext.width) / 2, h / 2)
            cr.show_text(msg)
            return
        for env, top, color, offset, title in (
            (self.ref_env, ruler, (0.11, 0.62, 0.89), 0, "Эталон"),
            (self.don_env, ruler + lane, (0.16, 0.79, 0.78), self.shift, f"Донор ({self.shift:+d} мс)"),
        ):
            mid = top + lane / 2
            scale = (lane / 2 - 6) / max(1, max(self.ref_env.peak, self.don_env.peak))
            cr.set_source_rgba(*color, 0.85)
            for px in range(int(w)):
                a = self.t0 + px * ms_per_px - offset
                v = env.window_max(a, a + ms_per_px) * scale
                if v > 0.5:
                    cr.move_to(px + 0.5, mid - v)
                    cr.line_to(px + 0.5, mid + v)
            cr.set_line_width(1.0)
            cr.stroke()
            cr.set_source_rgba(*fg, 0.15)
            cr.move_to(0, mid)
            cr.line_to(w, mid)
            cr.stroke()
            cr.set_source_rgba(*fg, 0.85)
            cr.set_font_size(12)
            cr.move_to(8, top + 16)
            cr.show_text(title)
        cr.set_source_rgba(*fg, 0.3)
        cr.move_to(0, ruler + lane)
        cr.line_to(w, ruler + lane)
        cr.stroke()
        if self.hover is not None:
            cr.set_source_rgba(1, 0.6, 0.2, 0.8)
            cr.move_to(self.hover, ruler)
            cr.line_to(self.hover, h)
            cr.stroke()
            tms = self.t0 + self.hover * ms_per_px
            cr.move_to(self.hover + 4, ruler + 14)
            cr.show_text(f"{tms / 1000:.3f} с")

    def _set_hover(self, x: float | None) -> None:
        self.hover = x
        self.area.queue_draw()

    def _on_scroll(self, ctrl, dx: float, dy: float) -> bool:
        state = ctrl.get_current_event_state()
        w = max(1, self.area.get_width())
        if state & Gdk.ModifierType.SHIFT_MASK or abs(dx) > abs(dy):
            delta = (dx if abs(dx) > abs(dy) else dy) * self.span * 0.1
            self.t0 = max(-self.span / 2, self.t0 + delta)
        else:
            anchor = self.t0 + (self.hover if self.hover is not None else w / 2) / w * self.span
            factor = 1.25 if dy > 0 else 0.8
            total = float(max(self.ref_env.length_ms if self.ref_env else 60_000, 1000))
            new_span = max(50.0, min(total * 1.2, self.span * factor))
            rel = (anchor - self.t0) / self.span
            self.t0 = max(-new_span / 2, anchor - rel * new_span)
            self.span = new_span
        self.area.queue_draw()
        return True

    def _drag_begin(self, _g, x: float, y: float) -> None:
        _w, _h, ruler, lane = self._geometry()
        mode = "shift" if y > ruler + lane else "pan"
        self._drag = (mode, self.t0, float(self.shift))

    def _drag_update(self, _g, dx: float, _dy: float) -> None:
        if not self._drag:
            return
        mode, t0, shift = self._drag
        ms = dx * self.span / max(1, self.area.get_width())
        if mode == "pan":
            self.t0 = max(-self.span / 2, t0 - ms)
            self.area.queue_draw()
        else:
            self._set_shift(int(round(shift + ms)))

    def _on_key(self, _c, keyval: int, _code: int, state: Gdk.ModifierType) -> bool:
        step = 10 if state & Gdk.ModifierType.SHIFT_MASK else 1
        if keyval in (Gdk.KEY_Left, Gdk.KEY_KP_Left):
            self._set_shift(self.shift - step)
            return True
        if keyval in (Gdk.KEY_Right, Gdk.KEY_KP_Right):
            self._set_shift(self.shift + step)
            return True
        return False
