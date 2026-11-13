"""Окно интерактивного графика битрейта (Cairo, масштаб колёсиком, подсказка под курсором)."""

from __future__ import annotations

import os

from gi.repository import Adw, Gtk

from ..scripts.bitrate import BitrateData, draw_chart, render_png
from . import util


class BitrateWindow(Adw.Window):
    def __init__(self, parent: Gtk.Window, data: BitrateData) -> None:
        super().__init__(title=f"Битрейт — {os.path.basename(data.path)}", default_width=1180, default_height=620,
                         transient_for=parent)
        self.data = data
        n = max(1, len(data.per_second))
        self.view = [0.0, float(n)]
        self.hover: float | None = None
        self._drag_origin: tuple[float, float] | None = None

        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        header.set_title_widget(Adw.WindowTitle(title=os.path.basename(data.path), subtitle=data.stream_label))
        save = Gtk.Button(icon_name="document-save-symbolic", tooltip_text="Сохранить PNG")
        save.connect("clicked", lambda *_: util.save_file(self, "Сохранить график",
                                                          os.path.splitext(os.path.basename(data.path))[0] + "_bitrate.png",
                                                          lambda p: render_png(self.data, p, dark=self._dark())))
        reset = Gtk.Button(icon_name="zoom-original-symbolic", tooltip_text="Весь файл")
        reset.connect("clicked", lambda *_: self._reset())
        header.pack_end(save)
        header.pack_end(reset)
        toolbar.add_top_bar(header)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        stats = Gtk.Box(spacing=18, margin_start=16, margin_end=16, margin_top=10, margin_bottom=6,
                        halign=Gtk.Align.CENTER)
        for label, value in (("Среднее", f"{data.mean:.2f} Мбит/с"), ("Пик", f"{data.peak:.2f} Мбит/с"),
                             ("Минимум", f"{data.minimum:.2f} Мбит/с"), ("σ", f"{data.stdev:.2f}"),
                             ("FPS", f"{data.fps:g}" if data.fps else "—"),
                             ("Ключевых кадров", str(len(data.keyframes))),
                             ("GOP", f"{data.gop:.2f} с" if data.gop else "—"),
                             ("Размер потока", util.human_size(data.total_bytes))):
            col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
            t = Gtk.Label(label=label)
            t.add_css_class("caption")
            t.add_css_class("dim-label")
            v = Gtk.Label(label=value)
            v.add_css_class("heading")
            col.append(t)
            col.append(v)
            stats.append(col)
        box.append(stats)
        self.area = Gtk.DrawingArea(vexpand=True, hexpand=True)
        self.area.set_draw_func(self._draw)
        motion = Gtk.EventControllerMotion()
        motion.connect("motion", self._on_motion)
        motion.connect("leave", lambda *_: self._set_hover(None))
        self.area.add_controller(motion)
        scroll = Gtk.EventControllerScroll.new(Gtk.EventControllerScrollFlags.VERTICAL)
        scroll.connect("scroll", self._on_scroll)
        self.area.add_controller(scroll)
        drag = Gtk.GestureDrag()
        drag.connect("drag-begin", lambda g, x, y: setattr(self, "_drag_origin", tuple(self.view)))
        drag.connect("drag-update", self._on_drag)
        self.area.add_controller(drag)
        box.append(self.area)
        hint = Gtk.Label(label="Колёсико — масштаб, перетаскивание — прокрутка", margin_bottom=6)
        hint.add_css_class("caption")
        hint.add_css_class("dim-label")
        box.append(hint)
        toolbar.set_content(box)
        self.set_content(toolbar)

    def _dark(self) -> bool:
        return Adw.StyleManager.get_default().get_dark()

    def _draw(self, _area, cr, width: int, height: int) -> None:
        draw_chart(cr, width, height, self.data, dark=self._dark(), view=(self.view[0], self.view[1]),
                   hover_x=self.hover)

    def _set_hover(self, x: float | None) -> None:
        self.hover = x
        self.area.queue_draw()

    def _on_motion(self, _c, x: float, _y: float) -> None:
        self._set_hover(x)

    def _plot_width(self) -> float:
        return max(10.0, self.area.get_width() - 64 - 20)

    def _on_scroll(self, _c, _dx: float, dy: float) -> bool:
        t0, t1 = self.view
        span = t1 - t0
        n = float(len(self.data.per_second) or 1)
        center = t0 + span / 2
        if self.hover is not None:
            center = t0 + (self.hover - 64) / self._plot_width() * span
        factor = 1.25 if dy > 0 else 0.8
        new_span = min(n, max(10.0, span * factor))
        rel = (center - t0) / span if span else 0.5
        nt0 = max(0.0, min(n - new_span, center - rel * new_span))
        self.view = [nt0, nt0 + new_span]
        self.area.queue_draw()
        return True

    def _on_drag(self, _g, dx: float, _dy: float) -> None:
        if not self._drag_origin:
            return
        t0, t1 = self._drag_origin
        span = t1 - t0
        n = float(len(self.data.per_second) or 1)
        shift = -dx / self._plot_width() * span
        nt0 = max(0.0, min(n - span, t0 + shift))
        self.view = [nt0, nt0 + span]
        self.area.queue_draw()

    def _reset(self) -> None:
        self.view = [0.0, float(max(1, len(self.data.per_second)))]
        self.area.queue_draw()
