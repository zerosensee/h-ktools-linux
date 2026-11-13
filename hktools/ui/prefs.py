"""Глобальные настройки приложения (Adw.PreferencesDialog)."""

from __future__ import annotations

from typing import Callable

from gi.repository import Adw, GObject, Gtk

from .. import paths, tools
from ..config import settings as s
from . import util

THEMES = [("system", "Как в системе"), ("light", "Светлая"), ("dark", "Тёмная")]


def apply_theme() -> None:
    scheme = {"light": Adw.ColorScheme.FORCE_LIGHT, "dark": Adw.ColorScheme.FORCE_DARK}.get(
        s.get("theme"), Adw.ColorScheme.DEFAULT)
    Adw.StyleManager.get_default().set_color_scheme(scheme)


def _switch(group: Adw.PreferencesGroup, key: str, title: str, subtitle: str = "",
            on_change: Callable[[], None] | None = None) -> Adw.SwitchRow:
    row = Adw.SwitchRow(title=title, active=bool(s.get(key)))
    if subtitle:
        row.set_subtitle(subtitle)
    row.connect("notify::active", lambda r, _p: (s.set(key, r.get_active()), on_change and on_change()))
    group.add(row)
    return row


def _entry(group: Adw.PreferencesGroup, key: str, title: str, tooltip: str = "",
           on_change: Callable[[], None] | None = None) -> Adw.EntryRow:
    row = Adw.EntryRow(title=title, text=str(s.get(key) or ""))
    if tooltip:
        row.set_tooltip_text(tooltip)
    row.connect("changed", lambda r: (s.set(key, r.get_text()), on_change and on_change()))
    group.add(row)
    return row


class PreferencesDialog(Adw.PreferencesDialog):
    def __init__(self, on_changed: Callable[[], None]) -> None:
        super().__init__(title="Настройки")
        self.on_changed = on_changed
        self.set_search_enabled(True)

        # --- Общие ---
        page = Adw.PreferencesPage(title="Общие", icon_name="emblem-system-symbolic")
        out = Adw.PreferencesGroup(title="Вывод")
        _switch(out, "overwrite_existing", "Перезаписывать существующие результаты",
                "Иначе файл пропускается, если результат уже есть")
        sub_row = _switch(out, "use_auto_subfolder", "Сохранять в подпапку рядом с исходником",
                          "Если папка результатов не выбрана вручную", on_change=self._changed)
        name = _entry(out, "output_subfolder", "Имя подпапки результатов", on_change=self._changed)
        sub_row.bind_property("active", name, "sensitive", GObject.BindingFlags.SYNC_CREATE)
        _switch(out, "clear_list_on_add", "Очищать список при добавлении новых файлов")
        page.add(out)
        par = Adw.PreferencesGroup(title="Параллельная обработка",
                                   description="Несколько файлов одновременно. Для NVENC обычно разумно 2–3 потока.")
        en = _switch(par, "enable_parallel", "Включить параллельную обработку")
        spin = Adw.SpinRow.new_with_range(1, 16, 1)
        spin.set_title("Максимум одновременных задач")
        spin.set_value(int(s.get("max_parallel") or 2))
        spin.connect("notify::value", lambda r, _p: s.set("max_parallel", int(r.get_value())))
        en.bind_property("active", spin, "sensitive", GObject.BindingFlags.SYNC_CREATE)
        par.add(spin)
        page.add(par)
        self.add(page)

        # --- Переименование ---
        page = Adw.PreferencesPage(title="Переименование", icon_name="document-edit-symbolic")
        g = Adw.PreferencesGroup(title="Пакетное переименование результатов",
                                 description="Поиск и замена в имени выходного файла (аналог PowerRename). "
                                             "Переменные: $1 $2 — группы, ${num}, ${num:2}, ${YYYY} ${MM} ${DD}, "
                                             "${hh} ${mm} ${ss}, ${ruuidv4}")
        _switch(g, "rename_enabled", "Включить переименование", on_change=self._changed)
        _entry(g, "rename_search", "Найти", "Например:  - (\\d+)", on_change=self._changed)
        _entry(g, "rename_replace", "Заменить на", "Например:  - [$1]", on_change=self._changed)
        _switch(g, "rename_use_regex", "Регулярные выражения", on_change=self._changed)
        _switch(g, "rename_case_sensitive", "Учитывать регистр", on_change=self._changed)
        page.add(g)
        ex = Adw.PreferencesGroup(title="Пример")
        self.preview = Adw.ActionRow(title="Серия 01 [1080p].mkv", subtitle="", use_markup=False)
        ex.add(self.preview)
        page.add(ex)
        self.add(page)

        # --- Интерфейс и рабочий стол ---
        page = Adw.PreferencesPage(title="Интеграция", icon_name="preferences-desktop-display-symbolic")
        g = Adw.PreferencesGroup(title="Оформление", description="Цвета берутся из темы GTK — в том числе сгенерированной "
                                                                  "matugen (Ryoku) через ~/.config/gtk-4.0/gtk.css")
        theme = Adw.ComboRow(title="Тема", model=Gtk.StringList.new([t[1] for t in THEMES]))
        cur = next((i for i, t in enumerate(THEMES) if t[0] == s.get("theme")), 0)
        theme.set_selected(cur)
        theme.connect("notify::selected", lambda r, _p: (s.set("theme", THEMES[r.get_selected()][0]), apply_theme()))
        g.add(theme)
        page.add(g)
        g = Adw.PreferencesGroup(title="Уведомления и статус")
        _switch(g, "notify_on_finish", "Уведомление по завершении пакета", "Через notify-send (mako, swaync, dunst…)")
        _switch(g, "notify_only_unfocused", "Только если окно не в фокусе")
        _switch(g, "status_file", "Файл статуса для Waybar / Quickshell", str(paths.status_file()))
        page.add(g)
        self.add(page)

        # --- Утилиты ---
        page = Adw.PreferencesPage(title="Утилиты", icon_name="applications-utilities-symbolic")
        g = Adw.PreferencesGroup(title="Пути к программам", description="Пусто — искать в PATH. Можно указать, например, "
                                                                         "ffmpeg из ffmpeg-full (AUR) или свою сборку.")
        for key in ("ffmpeg", "ffprobe", "mkvmerge", "mkvextract", "yt-dlp", "whisper", "fdkaac"):
            t = tools.TOOLS[key]
            row = Adw.EntryRow(title=t.title, text=s.tool_path(key))
            row.set_tooltip_text(f"Найдено: {tools.which(key) or 'нет'}")
            row.connect("changed", lambda r, k=key: s.set_tool_path(k, r.get_text().strip()))
            btn = util.icon_button("document-open-symbolic", "Выбрать файл")
            btn.connect("clicked", lambda _b, r=row: util.pick_files(self.get_root(), "Исполняемый файл",
                                                                     lambda ps: ps and r.set_text(ps[0]), multiple=False))
            row.add_suffix(btn)
            g.add(row)
        page.add(g)
        g = Adw.PreferencesGroup(title="Дополнительно")
        _entry(g, "qaac_command", "Команда QAAC (Wine)",
               "Например: wine /home/user/qaac/qaac64.exe  — нужны qaac64.exe и библиотеки Apple (QTfiles64)")
        _entry(g, "vaapi_device", "Устройство VAAPI", "Пусто — авто (/dev/dri/renderD128…)")
        _entry(g, "whisper_models_dir", "Папка моделей Whisper", str(paths.whisper_models_dir()))
        page.add(g)
        self.add(page)
        self._changed()

    def _changed(self) -> None:
        from ..core.script import apply_rename  # noqa: PLC0415

        try:
            self.preview.set_subtitle("→ " + apply_rename("Серия 01 [1080p]", 1) + ".mkv")
        except Exception:  # noqa: BLE001
            pass
        self.on_changed()
