"""Предпросмотр конвертации субтитров: фильтр по актёрам/стилям и ручное включение/исключение реплик."""

from __future__ import annotations

from gi.repository import Adw, GLib, Gtk, Pango

from ..config import settings as app_settings
from ..core.fields import active_words
from ..core.script import QueueItem
from ..media import subtitles as subs
from ..scripts.subtitles import apply_filters

REASON = {"actor": "актёр", "style": "стиль", "effect": "эффект", "regex": "regex", "caps-empty": "пусто после очистки",
          "manual-ex": "исключено вручную", "manual-in": "включено вручную", "empty": "пустая"}


class SubtitlePreview(Adw.Dialog):
    def __init__(self, panel, item: QueueItem) -> None:
        super().__init__(title=f"Предпросмотр — {item.name}", content_width=1000, content_height=700)
        self.panel = panel
        self.item = item
        self.script = panel.script
        try:
            self.doc = subs.load(item.path)
        except OSError as exc:
            self.doc = subs.SubDoc("ass")
            panel.toast(f"Не удалось прочитать: {exc}")
        self.manual_in: set[int] = set(item.extra.get("manual_in", []))
        self.manual_ex: set[int] = set(item.extra.get("manual_ex", []))
        vals = panel.form.values
        self.ex_actors = set(active_words(vals.get("exclude_actors")))
        self.ex_styles = set(active_words(vals.get("exclude_styles")))

        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        self.stats = Gtk.Label()
        self.stats.add_css_class("dim-label")
        header.pack_start(self.stats)
        reset = Gtk.Button(label="Сбросить ручные правки")
        reset.add_css_class("flat")
        reset.connect("clicked", lambda *_: self._reset_manual())
        header.pack_end(reset)
        toolbar.add_top_bar(header)

        paned = Gtk.Paned(orientation=Gtk.Orientation.HORIZONTAL, position=300, shrink_start_child=False)
        side = Adw.PreferencesPage()
        self._filter_group(side, "Актёры", self.doc.actors, self.ex_actors, "exclude_actors")
        self._filter_group(side, "Стили", self.doc.style_names, self.ex_styles, "exclude_styles")
        tip = Adw.PreferencesGroup(description="Снимите флажок, чтобы исключить все реплики актёра или стиля. "
                                               "Щелчок по реплике справа переключает её вручную.")
        side.add(tip)
        paned.set_start_child(side)

        self.search = Gtk.SearchEntry(placeholder_text="Поиск по тексту", margin_start=8, margin_end=8, margin_top=8)
        self.search.connect("search-changed", lambda *_: self.listbox.invalidate_filter())
        self.listbox = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        self.listbox.add_css_class("boxed-list")
        self.listbox.set_filter_func(self._filter_row)
        self.listbox.connect("row-activated", self._on_activate)
        right = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        right.append(self.search)
        scr = Gtk.ScrolledWindow(vexpand=True, child=Adw.Clamp(maximum_size=900, child=self.listbox, margin_top=6,
                                                                 margin_bottom=12, margin_start=8, margin_end=8))
        right.append(scr)
        paned.set_end_child(right)
        toolbar.set_content(paned)
        self.set_child(toolbar)
        self._rows: list[tuple[Gtk.ListBoxRow, int]] = []
        self._render()

    def _filter_group(self, page: Adw.PreferencesPage, title: str, names: list[str], excluded: set[str], key: str) -> None:
        if not names:
            return
        group = Adw.PreferencesGroup(title=f"{title} ({len(names)})")
        counts: dict[str, int] = {}
        for d in self.doc.dialogues:
            k = d.actor if key == "exclude_actors" else d.style
            counts[k] = counts.get(k, 0) + 1
        for name in names:
            row = Adw.SwitchRow(title=name or "(без имени)", active=name not in excluded,
                                subtitle=f"реплик: {counts.get(name, 0)}", use_markup=False)
            row.connect("notify::active", self._on_filter, name, excluded, key)
            group.add(row)
        page.add(group)

    def _on_filter(self, row: Adw.SwitchRow, _p, name: str, excluded: set[str], key: str) -> None:
        if row.get_active():
            excluded.discard(name)
        else:
            excluded.add(name)
        items = [{"word": n, "active": True} for n in sorted(excluded)]
        self.panel.form.set_value(key, items)
        app_settings.set_script_value(self.script.id, key, items)
        self._render()

    def _values(self) -> dict:
        vals = dict(self.panel.form.values)
        vals["exclude_actors"] = [{"word": n, "active": True} for n in self.ex_actors]
        vals["exclude_styles"] = [{"word": n, "active": True} for n in self.ex_styles]
        return vals

    def _render(self) -> None:
        child = self.listbox.get_first_child()
        while child:
            nxt = child.get_next_sibling()
            self.listbox.remove(child)
            child = nxt
        self._rows.clear()
        states = apply_filters(self.doc, self._values(), self.manual_in, self.manual_ex)
        kept = 0
        for st in states:
            if st.reason == "empty":
                continue
            row = Gtk.ListBoxRow(activatable=True)
            box = Gtk.Box(spacing=10, margin_top=6, margin_bottom=6, margin_start=10, margin_end=10)
            t = Gtk.Label(label=subs.fmt_srt_time(st.dialogue.start, ".")[:-1], xalign=0)
            t.add_css_class("numeric")
            t.add_css_class("dim-label")
            box.append(t)
            meta = Gtk.Label(label=" · ".join(x for x in (st.dialogue.style, st.dialogue.actor) if x), xalign=0,
                             width_chars=16, max_width_chars=16, ellipsize=Pango.EllipsizeMode.END)
            meta.add_css_class("caption")
            box.append(meta)
            text = subs.strip_tags(st.text if not st.deleted else st.dialogue.text).replace("\n", " ⏎ ")
            lbl = Gtk.Label(xalign=0, hexpand=True, wrap=True, wrap_mode=Pango.WrapMode.WORD_CHAR)
            esc = GLib.markup_escape_text(text)
            lbl.set_markup(f"<s>{esc}</s>" if st.deleted else esc)
            box.append(lbl)
            if st.reason:
                r = Gtk.Label(label=REASON.get(st.reason, st.reason))
                r.add_css_class("kt-pill")
                r.add_css_class("kt-cat-network" if st.deleted else "kt-cat-audio")
                box.append(r)
            if st.deleted:
                row.add_css_class("kt-sub-deleted")
            else:
                kept += 1
            row.set_child(box)
            row._kt_text = text.lower()  # type: ignore[attr-defined]
            row._kt_index = st.index  # type: ignore[attr-defined]
            row._kt_deleted = st.deleted  # type: ignore[attr-defined]
            self.listbox.append(row)
            self._rows.append((row, st.index))
        total = sum(1 for s in states if s.reason != "empty")
        self.stats.set_label(f"Останется {kept} из {total}")

    def _filter_row(self, row: Gtk.ListBoxRow) -> bool:
        q = self.search.get_text().strip().lower()
        return not q or q in getattr(row, "_kt_text", "")

    def _on_activate(self, _lb, row: Gtk.ListBoxRow) -> None:
        idx = getattr(row, "_kt_index", None)
        if idx is None:
            return
        if getattr(row, "_kt_deleted", False):
            self.manual_ex.discard(idx)
            self.manual_in.add(idx)
        else:
            self.manual_in.discard(idx)
            self.manual_ex.add(idx)
        self._save_manual()
        self._render()

    def _reset_manual(self) -> None:
        self.manual_in.clear()
        self.manual_ex.clear()
        self._save_manual()
        self._render()

    def _save_manual(self) -> None:
        self.item.extra["manual_in"] = sorted(self.manual_in)
        self.item.extra["manual_ex"] = sorted(self.manual_ex)
        self.panel.refresh_rows()
