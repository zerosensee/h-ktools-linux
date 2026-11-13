"""Автоматическая форма настроек по декларативной схеме (Field) — аналог ScriptSettingsControl."""

from __future__ import annotations

import os
from collections import OrderedDict
from typing import Any, Callable

from gi.repository import Adw, Gtk

from ..config import settings as app_settings
from ..core.fields import CHECK, COMBO, EXPANDER, FLOAT, INT, KEYWORDS, TEXT, Field, coerce
from ..core.script import Script
from . import util


class SettingsForm(Gtk.Box):
    def __init__(self, script: Script, on_change: Callable[[str, Any], None] | None = None) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.script = script
        self.on_change = on_change
        self.values: dict[str, Any] = {}
        self._rows: dict[str, tuple[Field, Gtk.Widget]] = {}
        self._groups: list[tuple[Adw.PreferencesGroup, list[str]]] = []
        self._guard = False
        self.rebuild()

    # --- построение ---
    def rebuild(self) -> None:
        child = self.get_first_child()
        while child is not None:
            nxt = child.get_next_sibling()
            self.remove(child)
            child = nxt
        self._rows.clear()
        self._groups.clear()
        self.values = self.script.values()
        fields = self.script.fields(self.values)
        if not fields:
            status = Adw.StatusPage(icon_name="emblem-ok-symbolic", title="Нет настроек",
                                    description="Этот инструмент работает без дополнительных параметров")
            status.add_css_class("compact")
            status.set_vexpand(True)
            self.append(status)
            return
        tabs: "OrderedDict[str, OrderedDict[str, list[Field]]]" = OrderedDict()
        for f in fields:
            tabs.setdefault(f.tab, OrderedDict()).setdefault(f.section, []).append(f)
        use_tabs = len(tabs) > 1
        stack = Adw.ViewStack(vexpand=True) if use_tabs else None
        for tab, sections in tabs.items():
            page = Adw.PreferencesPage()
            for section, flist in sections.items():
                group = Adw.PreferencesGroup(title=section)
                keys: list[str] = []
                for f in flist:
                    row = self._make_row(f)
                    if row is not None:
                        group.add(row)
                        keys.append(f.key)
                        keys += [c.key for c in f.children]
                page.add(group)
                self._groups.append((group, keys))
            if stack is not None:
                sp = stack.add_titled(page, tab or "Общие", tab or "Общие")
                sp.set_icon_name(_TAB_ICONS.get(tab, "emblem-system-symbolic"))
            else:
                page.set_vexpand(True)
                self.append(page)
        if stack is not None:
            switcher = Adw.ViewSwitcher(stack=stack, policy=Adw.ViewSwitcherPolicy.WIDE)
            switcher.set_margin_top(6)
            switcher.set_margin_start(6)
            switcher.set_margin_end(6)
            self.append(switcher)
            self.append(stack)
        reset = Gtk.Button(label="Сбросить настройки инструмента", halign=Gtk.Align.CENTER)
        reset.add_css_class("flat")
        reset.set_margin_bottom(8)
        reset.connect("clicked", lambda *_: util.confirm(self, "Сбросить настройки?",
                                                         f"Все параметры «{self.script.name}» вернутся к значениям по умолчанию.",
                                                         self._reset, "Сбросить", destructive=True))
        self.append(reset)
        self._update_states()

    def _reset(self) -> None:
        app_settings.reset_script(self.script.id)
        self.rebuild()
        if self.on_change:
            self.on_change("*", None)

    def _set(self, f: Field, value: Any) -> None:
        if self._guard:
            return
        value = coerce(f, value)
        self.values[f.key] = value
        app_settings.set_script_value(self.script.id, f.key, value)
        self._update_states()
        if self.on_change:
            self.on_change(f.key, value)

    def set_value(self, key: str, value: Any) -> None:
        """Программно изменить значение (например, сдвиг из окна синхронизации)."""
        entry = self._rows.get(key)
        app_settings.set_script_value(self.script.id, key, value)
        self.values[key] = value
        if not entry:
            return
        f, row = entry
        self._guard = True
        try:
            if isinstance(row, Adw.SwitchRow):
                row.set_active(bool(value))
            elif isinstance(row, Adw.SpinRow):
                row.set_value(float(value))
            elif isinstance(row, Adw.EntryRow):
                row.set_text(str(value))
            elif isinstance(row, Adw.ComboRow) and f.options and str(value) in f.options:
                row.set_selected(f.options.index(str(value)))
        finally:
            self._guard = False
        self._update_states()

    def _update_states(self) -> None:
        for key, (f, row) in self._rows.items():
            row.set_visible(f.is_visible(self.values))
            row.set_sensitive(not f.is_disabled(self.values))
        for group, keys in self._groups:
            group.set_visible(any(self._rows[k][1].get_visible() for k in keys if k in self._rows))

    # --- строки ---
    def _make_row(self, f: Field) -> Gtk.Widget | None:
        val = self.values.get(f.key, f.default)
        row: Gtk.Widget
        if f.type == CHECK:
            row = Adw.SwitchRow(title=f.label, active=bool(val), use_markup=False)
            if f.comment:
                row.set_subtitle(f.comment)
            row.connect("notify::active", self._on_switch, f)
        elif f.type == COMBO:
            opts = list(f.options or [])
            if str(val) not in opts:
                val = f.default if f.default in opts else (opts[0] if opts else "")
                self.values[f.key] = val
                app_settings.set_script_value(self.script.id, f.key, val)
            row = Adw.ComboRow(title=f.label, model=Gtk.StringList.new(opts), use_markup=False)
            if opts:
                row.set_selected(opts.index(str(val)))
            if f.comment:
                row.set_subtitle(f.comment)
            row.connect("notify::selected", lambda r, _p, ff=f, o=opts: self._set(ff, o[r.get_selected()])
                        if 0 <= r.get_selected() < len(o) else None)
        elif f.type == TEXT:
            row = Adw.EntryRow(title=f.label, text=str(val or ""), use_markup=False)
            if f.comment:
                row.add_suffix(_info_button(f.comment))
            row.connect("changed", lambda r, ff=f: self._set(ff, r.get_text()))
            if f.browse:
                btn = util.icon_button("document-open-symbolic" if f.browse == "file" else "folder-open-symbolic",
                                       "Выбрать…")
                btn.connect("clicked", lambda *_a, r=row, ff=f: self._browse(r, ff))
                row.add_suffix(btn)
                clear = util.icon_button("edit-clear-symbolic", "Очистить", lambda r=row: r.set_text(""))
                row.add_suffix(clear)
        elif f.type in (INT, FLOAT):
            lo = f.minimum if f.minimum is not None else (-1e9 if f.type == FLOAT else -2_000_000_000)
            hi = f.maximum if f.maximum is not None else (1e9 if f.type == FLOAT else 2_000_000_000)
            step = f.step or (1 if f.type == INT else 0.01)
            row = Adw.SpinRow.new_with_range(lo, hi, step)
            row.set_use_markup(False)
            row.set_title(f.label)
            row.set_digits(0 if f.type == INT else f.digits)
            try:
                row.set_value(float(val))
            except (TypeError, ValueError):
                row.set_value(float(f.default or 0))
            if f.comment:
                row.set_subtitle(f.comment)
            row.connect("notify::value", lambda r, _p, ff=f: self._set(ff, int(r.get_value()) if ff.type == INT
                                                                       else round(r.get_value(), ff.digits)))
        elif f.type == KEYWORDS:
            row = KeywordsRow(f, val, lambda items, ff=f: self._set(ff, items))
        elif f.type == EXPANDER:
            row = Adw.ExpanderRow(title=f.label, use_markup=False)
            if f.comment:
                row.set_subtitle(f.comment)
            if f.toggle:
                row.set_show_enable_switch(True)
                row.set_enable_expansion(bool(val))
                row.set_expanded(bool(val))
                row.connect("notify::enable-expansion", self._on_expander, f)
            for child in f.children:
                crow = self._make_row(child)
                if crow is not None:
                    row.add_row(crow)
        else:
            return None
        self._rows[f.key] = (f, row)
        return row

    def _on_switch(self, row: Adw.SwitchRow, _p, f: Field) -> None:
        if self._guard:
            return
        active = row.get_active()
        if active and f.warning:
            def revert() -> None:
                self._guard = True
                row.set_active(False)
                self._guard = False

            util.confirm(self, f.warning[0], f.warning[1], lambda: self._set(f, True), "Включить", on_no=revert)
            return
        if not active and f.warning and f.key == "use_m4a_container":
            pass
        self._set(f, active)

    def _on_expander(self, row: Adw.ExpanderRow, _p, f: Field) -> None:
        enabled = row.get_enable_expansion()
        if enabled:
            row.set_expanded(True)
        self._set(f, enabled)

    def _browse(self, row: Adw.EntryRow, f: Field) -> None:
        win = self.get_root()
        start = os.path.dirname(row.get_text()) if row.get_text() else app_settings.get("last_dir")
        if f.browse == "folder":
            util.pick_folder(win, f.label, lambda p: row.set_text(p), start)
        else:
            util.pick_files(win, f.label, lambda ps: ps and row.set_text(ps[0]), multiple=False, start=start)


def _info_button(text: str) -> Gtk.Widget:
    """Кнопка «i» с пояснением (у EntryRow нет подзаголовка)."""
    btn = Gtk.MenuButton(icon_name="help-about-symbolic", valign=Gtk.Align.CENTER, tooltip_text=text)
    btn.add_css_class("flat")
    label = Gtk.Label(label=text, wrap=True, max_width_chars=40, margin_top=6, margin_bottom=6, margin_start=6,
                      margin_end=6, xalign=0)
    btn.set_popover(Gtk.Popover(child=label))
    return btn


_TAB_ICONS = {
    "Видео": "video-x-generic-symbolic", "Аудио": "audio-x-generic-symbolic", "Субтитры": "font-x-generic-symbolic",
    "Общие": "emblem-system-symbolic", "Основные": "emblem-system-symbolic", "Движок": "applications-engineering-symbolic",
    "Экспорт": "document-save-symbolic",
}


class KeywordsRow(Adw.ExpanderRow):
    """Список ключевых слов с флажками активности (KeywordList из Windows-версии)."""

    def __init__(self, f: Field, value: Any, on_change: Callable[[list[dict]], None]) -> None:
        super().__init__(title=f.label, use_markup=False)
        self.field = f
        self.items: list[dict] = [dict(i) for i in (value or []) if isinstance(i, dict)]
        self.on_change = on_change
        self._children: list[Gtk.Widget] = []
        self._rebuild()

    def _summary(self) -> str:
        active = [i["word"] for i in self.items if i.get("active")]
        if not self.items:
            return self.field.comment or "Пусто"
        txt = ", ".join(active[:4]) + ("…" if len(active) > 4 else "")
        return f"Активно {len(active)} из {len(self.items)}" + (f": {txt}" if txt else "")

    def _emit(self) -> None:
        self.set_subtitle(self._summary())
        self.on_change([dict(i) for i in self.items])

    def _rebuild(self) -> None:
        for w in self._children:
            self.remove(w)
        self._children.clear()
        self.set_subtitle(self._summary())
        for idx, item in enumerate(self.items):
            row = Adw.EntryRow(title="", text=str(item.get("word", "")))
            check = Gtk.CheckButton(active=bool(item.get("active")), valign=Gtk.Align.CENTER,
                                    tooltip_text="Активно")
            check.connect("toggled", self._on_toggle, idx)
            row.add_prefix(check)
            if self.field.extra_flag:
                key, label = self.field.extra_flag
                tb = Gtk.ToggleButton(label=label, active=bool(item.get(key)), valign=Gtk.Align.CENTER)
                tb.add_css_class("flat")
                tb.connect("toggled", self._on_flag, idx, key)
                row.add_suffix(tb)
            delete = util.icon_button("user-trash-symbolic", "Удалить", lambda i=idx: self._delete(i))
            row.add_suffix(delete)
            row.connect("changed", self._on_edit, idx)
            self.add_row(row)
            self._children.append(row)
        add = Adw.EntryRow(title="Добавить…", show_apply_button=True)
        add.connect("apply", self._on_add)
        add.connect("entry-activated", self._on_add)
        self.add_row(add)
        self._children.append(add)

    def _on_toggle(self, btn: Gtk.CheckButton, idx: int) -> None:
        self.items[idx]["active"] = btn.get_active()
        self._emit()

    def _on_flag(self, btn: Gtk.ToggleButton, idx: int, key: str) -> None:
        self.items[idx][key] = btn.get_active()
        self._emit()

    def _on_edit(self, row: Adw.EntryRow, idx: int) -> None:
        if idx < len(self.items):
            self.items[idx]["word"] = row.get_text()
            self._emit()

    def _delete(self, idx: int) -> None:
        if idx < len(self.items):
            self.items.pop(idx)
            self._emit()
            self._rebuild()
            self.set_expanded(True)

    def _on_add(self, row: Adw.EntryRow) -> None:
        text = row.get_text().strip()
        if not text:
            return
        self.items.append({"word": text, "active": True})
        self._emit()
        self._rebuild()
        self.set_expanded(True)
