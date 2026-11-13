"""Диалоги: выбор дорожек, выбор субтитров для хардсаба, заголовок аудио, модели Whisper."""

from __future__ import annotations

import os
import threading
from typing import Any, Callable

from gi.repository import Adw, GLib, Gtk, Pango

from ..core.script import QueueItem, Script
from ..media.probe import TYPE_RU, MediaInfo, Track
from . import util

TYPE_ICON = {"video": "video-x-generic-symbolic", "audio": "audio-x-generic-symbolic",
             "subtitle": "font-x-generic-symbolic", "attachment": "font-x-generic-symbolic"}


class TrackDialog(Adw.Dialog):
    """Выбор дорожек (управление потоками / разборка) или замена дорожек (замена потоков)."""

    def __init__(self, script: Script, item: QueueItem, all_items: list[QueueItem],
                 on_apply: Callable[[], None]) -> None:
        super().__init__(title=item.name, content_width=640, content_height=620)
        self.script = script
        self.item = item
        self.all_items = all_items
        self.on_apply = on_apply
        self.mode = script.track_selection
        info: MediaInfo = item.info  # type: ignore[assignment]
        self.selected: set[int] = set(int(i) for i in item.extra.get("selected", []))
        self.replacements: dict[str, str] = dict(item.extra.get("replacements") or {})
        self._checks: dict[int, Gtk.CheckButton] = {}
        self._rep_labels: dict[int, Gtk.Label] = {}

        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        header.set_show_end_title_buttons(False)
        header.set_show_start_title_buttons(False)
        cancel = Gtk.Button(label="Отмена")
        cancel.connect("clicked", lambda *_: self.close())
        apply = Gtk.Button(label="Применить")
        apply.add_css_class("suggested-action")
        apply.connect("clicked", lambda *_: self._apply(False))
        header.pack_start(cancel)
        header.pack_end(apply)
        toolbar.add_top_bar(header)

        page = Adw.PreferencesPage()
        hint = {"manage": "Отметьте дорожки, которые нужно удалить или сохранить (зависит от режима).",
                "extract": "Отметьте дорожки и шрифты для извлечения.",
                "replace": "Для нужных дорожек выберите файл-замену. Язык, название и флаги сохранятся."}[self.mode or "manage"]
        if self.mode != "replace":
            quick = Adw.PreferencesGroup(title="Быстрый выбор", description=hint)
            box = Gtk.Box(spacing=6, homogeneous=False)
            for label, kind in (("Все", "all"), ("Ничего", "none"), ("Видео", "video"), ("Аудио", "audio"),
                                ("Субтитры", "subtitle"), ("Шрифты", "font")):
                b = Gtk.Button(label=label)
                b.add_css_class("pill")
                b.connect("clicked", lambda _b, k=kind: self._quick(k))
                box.append(b)
            scroller = Gtk.ScrolledWindow(vscrollbar_policy=Gtk.PolicyType.NEVER, child=box)
            quick.add(scroller)
            page.add(quick)
        else:
            page.set_description(hint)

        for kind in ("video", "audio", "subtitle"):
            tracks = info.of_type(kind)
            if not tracks:
                continue
            group = Adw.PreferencesGroup(title=TYPE_RU[kind])
            for t in tracks:
                group.add(self._track_row(t))
            page.add(group)
        if self.mode == "extract" and info.fonts:
            group = Adw.PreferencesGroup(title=f"Шрифты ({len(info.fonts)})")
            exp = Adw.ExpanderRow(title="Вложенные шрифты", subtitle="Сохраняются в подпапку fonts")
            for t in info.fonts:
                exp.add_row(self._track_row(t))
            group.add(exp)
            page.add(group)

        similar = [i for i in all_items if i is not item and i.info is not None and i.info.signature() == info.signature()]
        if similar and self.mode != "replace":
            g = Adw.PreferencesGroup()
            btn = Gtk.Button(label=f"Применить ко всем похожим файлам ({len(similar)})", halign=Gtk.Align.CENTER)
            btn.add_css_class("pill")
            btn.connect("clicked", lambda *_: self._apply(True))
            g.add(btn)
            page.add(g)
        toolbar.set_content(page)
        self.set_child(toolbar)

    def _track_row(self, t: Track) -> Adw.ActionRow:
        title = f"#{t.index} · {TYPE_RU.get(t.type, t.type)}" if t.type != "attachment" else (t.filename or f"#{t.index}")
        row = Adw.ActionRow(title=title, subtitle=t.describe(), use_markup=False)
        row.add_prefix(Gtk.Image(icon_name=TYPE_ICON.get(t.type, "text-x-generic-symbolic")))
        if self.mode == "replace":
            if t.type == "attachment":
                return row
            lbl = Gtk.Label(label=os.path.basename(self.replacements.get(str(t.index), "")) or "—",
                            ellipsize=Pango.EllipsizeMode.MIDDLE, max_width_chars=22)
            lbl.add_css_class("dim-label")
            self._rep_labels[t.index] = lbl
            row.add_suffix(lbl)
            pick = util.icon_button("document-open-symbolic", "Выбрать файл-замену", lambda tt=t: self._pick(tt))
            clear = util.icon_button("edit-clear-symbolic", "Убрать замену", lambda tt=t: self._set_rep(tt, ""))
            row.add_suffix(pick)
            row.add_suffix(clear)
            return row
        check = Gtk.CheckButton(active=t.index in self.selected, valign=Gtk.Align.CENTER)
        check.connect("toggled", self._toggled, t.index)
        row.add_suffix(check)
        row.set_activatable_widget(check)
        self._checks[t.index] = check
        return row

    def _toggled(self, btn: Gtk.CheckButton, idx: int) -> None:
        if btn.get_active():
            self.selected.add(idx)
        else:
            self.selected.discard(idx)

    def _quick(self, kind: str) -> None:
        info: MediaInfo = self.item.info  # type: ignore[assignment]
        for idx, check in self._checks.items():
            t = info.track(idx)
            if t is None:
                continue
            if kind == "all":
                on = True
            elif kind == "none":
                on = False
            elif kind == "font":
                on = t.is_font or check.get_active()
            else:
                on = t.type == kind or (check.get_active() and t.type != kind)
            check.set_active(on)

    def _pick(self, t: Track) -> None:
        start = os.path.dirname(self.item.path)
        util.pick_files(self.get_root(), f"Замена для дорожки #{t.index}", lambda ps: ps and self._set_rep(t, ps[0]),
                        multiple=False, start=start)

    def _set_rep(self, t: Track, path: str) -> None:
        if path:
            self.replacements[str(t.index)] = path
        else:
            self.replacements.pop(str(t.index), None)
        if t.index in self._rep_labels:
            self._rep_labels[t.index].set_label(os.path.basename(path) or "—")

    def _apply(self, to_similar: bool) -> None:
        if self.mode == "replace":
            self.item.extra["replacements"] = dict(self.replacements)
        else:
            self.item.extra["selected"] = sorted(self.selected)
            if to_similar and self.item.info is not None:
                sig = self.item.info.signature()
                media = self.item.info.media_tracks
                pos = {t.index: n for n, t in enumerate(media)}
                sel_pos = {pos[i] for i in self.selected if i in pos}
                font_sel = any(t.index in self.selected for t in self.item.info.fonts)
                for other in self.all_items:
                    if other is self.item or other.info is None or other.info.signature() != sig:
                        continue
                    om = other.info.media_tracks
                    chosen = [om[p].index for p in sel_pos if p < len(om)]
                    if font_sel:
                        chosen += [t.index for t in other.info.fonts]
                    other.extra["selected"] = sorted(chosen)
        self.on_apply()
        self.close()


def choose_subtitle(parent: Gtk.Widget, filename: str, tracks: list[Track], on_done: Callable[[Track | None], None]) -> None:
    """Диалог «надписи не найдены» — выбрать дорожку для хардсаба."""
    dlg = Adw.AlertDialog(heading="Надписи не найдены", body=f"Выберите дорожку субтитров для вшивания в «{filename}»")
    dlg.add_response("skip", "Без хардсаба")
    for t in tracks:
        dlg.add_response(f"t{t.index}", f"#{t.index} {t.title or t.language}"[:40])
    dlg.set_close_response("skip")

    def on_resp(_d, resp: str) -> None:
        if resp.startswith("t"):
            idx = int(resp[1:])
            on_done(next((t for t in tracks if t.index == idx), None))
        else:
            on_done(None)

    dlg.connect("response", on_resp)
    dlg.present(parent)


def ask_audio_title(parent: Gtk.Widget, count: int, presets: list[str], default: str,
                    on_done: Callable[[str | None], None]) -> None:
    """Заголовок для добавленных аудиодорожек (Сборка MKV)."""
    dlg = Adw.AlertDialog(heading="Заголовок аудиодорожек",
                          body=f"Добавлено аудиофайлов: {count}. Название будет записано в дорожки при сборке.")
    box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
    entry = Gtk.Entry(text=default, placeholder_text="Например: Дубляж (Studio)")
    box.append(entry)
    if presets:
        flow = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, max_children_per_line=3, column_spacing=6,
                           row_spacing=6)
        for p in presets:
            b = Gtk.Button(label=p)
            b.add_css_class("pill")
            b.connect("clicked", lambda _b, v=p: entry.set_text(v))
            flow.append(b)
        box.append(flow)
    dlg.set_extra_child(box)
    dlg.add_response("skip", "Без заголовка")
    dlg.add_response("ok", "Применить")
    dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)
    dlg.set_default_response("ok")
    dlg.set_close_response("skip")
    fired = [False]

    def finish(value: str | None) -> None:
        if not fired[0]:
            fired[0] = True
            on_done(value)

    def on_activate(*_a) -> None:
        finish(entry.get_text().strip())
        dlg.close()

    entry.connect("activate", on_activate)
    dlg.connect("response", lambda _d, r: finish(entry.get_text().strip() if r == "ok" else None))
    dlg.present(parent)


def ask_text(parent: Gtk.Widget, heading: str, body: str, value: str, on_done: Callable[[str], None]) -> None:
    dlg = Adw.AlertDialog(heading=heading, body=body)
    entry = Gtk.Entry(text=value)
    dlg.set_extra_child(entry)
    dlg.add_response("cancel", "Отмена")
    dlg.add_response("ok", "OK")
    dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)
    dlg.set_default_response("ok")
    dlg.set_close_response("cancel")
    fired = [False]

    def finish(ok: bool) -> None:
        if not fired[0]:
            fired[0] = True
            if ok:
                on_done(entry.get_text())

    def on_activate(*_a) -> None:
        finish(True)
        dlg.close()

    entry.connect("activate", on_activate)
    dlg.connect("response", lambda _d, r: finish(r == "ok"))
    dlg.present(parent)


class WhisperModelsDialog(Adw.Dialog):
    """Менеджер моделей whisper.cpp: скачивание (с докачкой) и удаление."""

    def __init__(self, on_changed: Callable[[], None]) -> None:
        super().__init__(title="Модели Whisper", content_width=620, content_height=640)
        from ..scripts import speech  # noqa: PLC0415

        self.speech = speech
        self.on_changed = on_changed
        self._cancel: dict[str, threading.Event] = {}
        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        folder = util.icon_button("folder-open-symbolic", "Открыть папку моделей",
                                  lambda: __import__("hktools.core.desktop", fromlist=["x"]).open_path(speech.models_dir()))
        header.pack_start(folder)
        toolbar.add_top_bar(header)
        self.page = Adw.PreferencesPage()
        self.group = Adw.PreferencesGroup(title="Доступные модели", description=f"Папка: {speech.models_dir()}")
        self.page.add(self.group)
        self.rows: dict[str, tuple[Adw.ActionRow, Gtk.Widget]] = {}
        for m in speech.MODELS:
            row = Adw.ActionRow(title=m.key, subtitle=f"~{m.size_mb:.0f} МБ · {m.description}", use_markup=False)
            box = Gtk.Box(spacing=6, valign=Gtk.Align.CENTER)
            row.add_suffix(box)
            self.rows[m.key] = (row, box)
            self.group.add(row)
            self._refresh(m.key)
        toolbar.set_content(self.page)
        self.set_child(toolbar)

    def _clear(self, box: Gtk.Box) -> None:
        child = box.get_first_child()
        while child:
            nxt = child.get_next_sibling()
            box.remove(child)
            child = nxt

    def _refresh(self, key: str, progress: float | None = None, text: str = "") -> None:
        row, box = self.rows[key]
        self._clear(box)  # type: ignore[arg-type]
        path = self.speech.model_path(key)
        if progress is not None:
            bar = Gtk.ProgressBar(fraction=progress / 100, show_text=True, text=text or f"{progress:.0f}%",
                                  valign=Gtk.Align.CENTER)
            bar.set_size_request(160, -1)
            box.append(bar)
            box.append(util.icon_button("process-stop-symbolic", "Отменить",
                                        lambda: self._cancel.get(key, threading.Event()).set()))
        elif os.path.isfile(path):
            ok = Gtk.Image(icon_name="emblem-ok-symbolic")
            ok.add_css_class("success")
            box.append(ok)
            box.append(util.icon_button("user-trash-symbolic", "Удалить модель", lambda: self._delete(key)))
        else:
            btn = Gtk.Button(label="Скачать")
            btn.add_css_class("pill")
            btn.connect("clicked", lambda *_: self._download(key))
            box.append(btn)

    def _delete(self, key: str) -> None:
        def do() -> None:
            try:
                os.remove(self.speech.model_path(key))
            except OSError:
                pass
            self._refresh(key)
            self.on_changed()

        util.confirm(self, "Удалить модель?", key, do, "Удалить", destructive=True)

    def _download(self, key: str) -> None:
        ev = threading.Event()
        self._cancel[key] = ev
        self._refresh(key, 0)

        def prog(pct: float, msg: str) -> None:
            GLib.idle_add(lambda: (self._refresh(key, pct, f"{pct:.0f}% · {msg}"), False)[1])

        def work() -> str:
            return self.speech.download_model(key, prog, ev)

        def done(_res: Any, err: BaseException | None) -> None:
            self._refresh(key)
            if err and not ev.is_set():
                util.confirm(self, "Ошибка загрузки", str(err), lambda: None, "OK")
            self.on_changed()

        util.run_bg(work, done)
