"""Главное окно: боковая навигация по инструментам + страницы."""

from __future__ import annotations

import os

from gi.repository import Adw, Gio, Gtk, Pango

from .. import APP_NAME
from ..core.script import CATEGORIES
from ..scripts import all_scripts
from . import util
from .pages import CAT_CSS, DepsPage, HomePage, LogPage, TimingPage, about
from .prefs import PreferencesDialog
from .work_panel import WorkPanel


class MainWindow(Adw.ApplicationWindow):
    def __init__(self, app: Adw.Application, icon_path: str | None) -> None:
        super().__init__(application=app, title=APP_NAME, default_width=1280, default_height=820)
        self.set_size_request(360, 420)
        self.icon_path = icon_path
        self.scripts = all_scripts()
        self.pages: dict[str, Gtk.Widget] = {}
        self.page_rows: dict[str, Gtk.ListBoxRow] = {}
        self.busy: set[str] = set()

        self.split = Adw.NavigationSplitView(min_sidebar_width=230, max_sidebar_width=300)
        # --- боковая панель ---
        side_tv = Adw.ToolbarView()
        side_header = Adw.HeaderBar()
        side_header.set_title_widget(Adw.WindowTitle(title=APP_NAME, subtitle=""))
        menu = Gio.Menu()
        menu.append("Настройки", "app.preferences")
        menu.append("Зависимости", "win.page::deps")
        menu.append("Журнал", "win.page::log")
        menu.append("Горячие клавиши", "win.show-help-overlay")
        menu.append("О программе", "app.about")
        side_header.pack_end(Gtk.MenuButton(icon_name="open-menu-symbolic", menu_model=menu, primary=True,
                                            tooltip_text="Меню"))
        side_tv.add_top_bar(side_header)
        self.nav = Gtk.ListBox(selection_mode=Gtk.SelectionMode.SINGLE)
        self.nav.add_css_class("navigation-sidebar")
        self.nav.connect("row-activated", self._on_nav)
        self.nav.set_header_func(self._nav_header)
        self._add_nav("home", "Главная", "go-home-symbolic", "")
        for cat in CATEGORIES:
            for s in self.scripts:
                if s.category == cat:
                    self._add_nav(s.id, s.name, s.icon, cat)
        self._add_nav("log", "Журнал", "utilities-terminal-symbolic", "Система")
        self._add_nav("deps", "Зависимости", "package-x-generic-symbolic", "Система")
        side_tv.set_content(Gtk.ScrolledWindow(child=self.nav, vexpand=True,
                                               hscrollbar_policy=Gtk.PolicyType.NEVER))
        self.split.set_sidebar(Adw.NavigationPage(title=APP_NAME, child=side_tv, tag="sidebar"))

        # --- содержимое ---
        self.stack = Gtk.Stack(transition_type=Gtk.StackTransitionType.CROSSFADE, transition_duration=120)
        self.content_page = Adw.NavigationPage(title="Главная", child=self.stack, tag="content")
        self.split.set_content(self.content_page)

        bp = Adw.Breakpoint.new(Adw.BreakpointCondition.parse("max-width: 640sp"))
        bp.add_setter(self.split, "collapsed", True)
        self.add_breakpoint(bp)
        self.set_content(self.split)

        act = Gio.SimpleAction.new("page", __import__("gi").repository.GLib.VariantType.new("s"))
        act.connect("activate", lambda _a, v: self.show_page(v.get_string()))
        self.add_action(act)
        for name, cb in (("add-files", self._add_files), ("start", self._start), ("open-log", lambda: self.show_page("log"))):
            a = Gio.SimpleAction.new(name, None)
            a.connect("activate", lambda *_x, f=cb: f())
            self.add_action(a)
        self._setup_help()
        self.show_page("home")

    # --- навигация ---
    def _add_nav(self, page_id: str, title: str, icon: str, category: str) -> None:
        row = Gtk.ListBoxRow()
        box = Gtk.Box(spacing=10, margin_top=2, margin_bottom=2)
        img = util.image(icon)
        if category in CAT_CSS:
            img.add_css_class(CAT_CSS[category])
            img.add_css_class("kt-nav-icon")
        box.append(img)
        lbl = Gtk.Label(label=title, xalign=0, hexpand=True, ellipsize=Pango.EllipsizeMode.END)
        box.append(lbl)
        spinner = Gtk.Spinner(visible=False)
        box.append(spinner)
        row.set_child(box)
        row._kt_id = page_id  # type: ignore[attr-defined]
        row._kt_cat = category  # type: ignore[attr-defined]
        row._kt_spinner = spinner  # type: ignore[attr-defined]
        self.nav.append(row)
        self.page_rows[page_id] = row

    def _nav_header(self, row: Gtk.ListBoxRow, before: Gtk.ListBoxRow | None) -> None:
        cat = getattr(row, "_kt_cat", "")
        prev = getattr(before, "_kt_cat", None) if before else None
        if cat and cat != prev:
            lbl = Gtk.Label(label=cat, xalign=0, margin_start=12, margin_top=12, margin_bottom=2)
            lbl.add_css_class("heading")
            lbl.add_css_class("dim-label")
            row.set_header(lbl)
        else:
            row.set_header(None)

    def _on_nav(self, _lb, row: Gtk.ListBoxRow) -> None:
        self.show_page(getattr(row, "_kt_id", "home"))

    def _create_page(self, page_id: str) -> Gtk.Widget:
        if page_id == "home":
            return HomePage(self.scripts, self.show_page, self.icon_path)
        if page_id == "log":
            return LogPage()
        if page_id == "deps":
            return DepsPage(self._deps_changed)
        if page_id == "timing-calculator":
            return TimingPage()
        script = next(s for s in self.scripts if s.id == page_id)
        return WorkPanel(self, script)

    def show_page(self, page_id: str) -> None:
        if page_id not in self.page_rows:
            page_id = "home"
        if page_id not in self.pages:
            page = self._create_page(page_id)
            self.pages[page_id] = page
            self.stack.add_named(page, page_id)
        self.stack.set_visible_child_name(page_id)
        row = self.page_rows[page_id]
        if self.nav.get_selected_row() is not row:
            self.nav.select_row(row)
        title = next((s.name for s in self.scripts if s.id == page_id),
                     {"home": "Главная", "log": "Журнал", "deps": "Зависимости"}.get(page_id, APP_NAME))
        self.content_page.set_title(title)
        self.split.set_show_content(True)

    def current_panel(self) -> WorkPanel | None:
        child = self.stack.get_visible_child()
        return child if isinstance(child, WorkPanel) else None

    def set_page_busy(self, page_id: str, busy: bool) -> None:
        row = self.page_rows.get(page_id)
        if row is None:
            return
        sp: Gtk.Spinner = row._kt_spinner  # type: ignore[attr-defined]
        sp.set_visible(busy)
        if busy:
            sp.start()
            self.busy.add(page_id)
        else:
            sp.stop()
            self.busy.discard(page_id)

    def _deps_changed(self) -> None:
        home = self.pages.get("home")
        if isinstance(home, HomePage):
            home.refresh_status()

    def open_files(self, paths: list[str], keep_current: bool = False) -> None:
        """Файлы, переданные из файлового менеджера: открыть подходящий инструмент."""
        paths = [p for p in paths if os.path.exists(p)]
        if not paths:
            return
        panel = self.current_panel()
        if (panel is None or panel.script.input_mode != "files") and not (keep_current and panel is not None):
            from ..core.script import SUBTITLE_EXTS, VIDEO_CONTAINERS  # noqa: PLC0415

            ext = os.path.splitext(paths[0])[1].lower()
            target = "subtitles-convert" if ext in SUBTITLE_EXTS else (
                "video-encode" if ext in VIDEO_CONTAINERS else "audio-encode")
            self.show_page(target)
            panel = self.current_panel()
        if panel is not None:
            panel.add_paths(paths)

    # --- действия ---
    def _add_files(self) -> None:
        panel = self.current_panel()
        if panel is not None:
            panel.add_files_dialog()

    def _start(self) -> None:
        panel = self.current_panel()
        if panel is not None:
            panel.toggle_run()

    def show_preferences(self) -> None:
        def changed() -> None:
            for p in self.pages.values():
                if isinstance(p, WorkPanel):
                    p._refresh_out_label()

        PreferencesDialog(changed).present(self)

    def show_about(self) -> None:
        about(self)

    def _setup_help(self) -> None:
        xml = """<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <object class="GtkShortcutsWindow" id="help_overlay">
    <property name="modal">1</property>
    <child>
      <object class="GtkShortcutsSection">
        <property name="section-name">main</property>
        <child>
          <object class="GtkShortcutsGroup">
            <property name="title">Обработка</property>
            <child><object class="GtkShortcutsShortcut"><property name="accelerator">&lt;Ctrl&gt;o</property><property name="title">Добавить файлы</property></object></child>
            <child><object class="GtkShortcutsShortcut"><property name="accelerator">&lt;Ctrl&gt;Return</property><property name="title">Запуск / остановка</property></object></child>
          </object>
        </child>
        <child>
          <object class="GtkShortcutsGroup">
            <property name="title">Приложение</property>
            <child><object class="GtkShortcutsShortcut"><property name="accelerator">&lt;Ctrl&gt;comma</property><property name="title">Настройки</property></object></child>
            <child><object class="GtkShortcutsShortcut"><property name="accelerator">&lt;Ctrl&gt;l</property><property name="title">Журнал</property></object></child>
            <child><object class="GtkShortcutsShortcut"><property name="accelerator">&lt;Ctrl&gt;q</property><property name="title">Выход</property></object></child>
          </object>
        </child>
      </object>
    </child>
  </object>
</interface>"""
        builder = Gtk.Builder.new_from_string(xml, -1)
        self.set_help_overlay(builder.get_object("help_overlay"))

    def do_close_request(self) -> bool:  # noqa: D401 - сигнал GTK
        if self.busy:
            util.confirm(self, "Идёт обработка", "Закрыть hktools и прервать текущие задачи?",
                         self._force_close, "Прервать и выйти", destructive=True)
            return True
        return False

    def _force_close(self) -> None:
        for p in self.pages.values():
            if isinstance(p, WorkPanel) and p.runner is not None:
                p.runner.cancel()
        self.busy.clear()
        self.close()
