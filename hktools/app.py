"""Точка входа GUI: Adw.Application."""

from __future__ import annotations

import os
import sys

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk  # noqa: E402

from . import APP_ID, APP_NAME, VERSION  # noqa: E402
from .core import desktop  # noqa: E402
from .log import log  # noqa: E402
from .media.process import tracker  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))


def _data_dirs() -> list[str]:
    dirs = [os.environ.get("HKTOOLS_DATA_DIR", ""), os.path.join(HERE, "..", "data"),
            os.path.expanduser("~/.local/share/hktools"), "/usr/local/share/hktools", "/usr/share/hktools",
            os.path.join(sys.prefix, "share", "hktools")]
    return [d for d in dirs if d]


def _find_icon() -> str | None:
    for d in _data_dirs():
        for name in ("icons/hktools-256.png", "hktools-256.png", "icons/hicolor/256x256/apps/io.github.zerosensee.HKTools.png"):
            p = os.path.join(d, name)
            if os.path.exists(p):
                return os.path.abspath(p)
    p = f"/usr/share/icons/hicolor/256x256/apps/{APP_ID}.png"
    return p if os.path.exists(p) else None


class KToolsApp(Adw.Application):
    def __init__(self) -> None:
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE)
        GLib.set_application_name(APP_NAME)
        self.window = None
        self.pending_files: list[str] = []
        self.pending_tool: str | None = None

    def do_startup(self) -> None:
        Adw.Application.do_startup(self)
        css = Gtk.CssProvider()
        css.load_from_path(os.path.join(HERE, "ui", "style.css"))
        Gtk.StyleContext.add_provider_for_display(Gdk.Display.get_default(), css,
                                                  Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        theme = Gtk.IconTheme.get_for_display(Gdk.Display.get_default())
        for d in _data_dirs():
            hicolor = os.path.join(d, "icons", "hicolor")
            if os.path.isdir(hicolor):
                theme.add_search_path(os.path.join(d, "icons"))
        from .ui.prefs import apply_theme  # noqa: PLC0415

        apply_theme()
        for name, cb, accels in (
            ("quit", lambda *_: self.quit_safely(), ["<Ctrl>q"]),
            ("preferences", lambda *_: self.window and self.window.show_preferences(), ["<Ctrl>comma"]),
            ("about", lambda *_: self.window and self.window.show_about(), ["F1"]),
        ):
            act = Gio.SimpleAction.new(name, None)
            act.connect("activate", cb)
            self.add_action(act)
            self.set_accels_for_action(f"app.{name}", accels)
        self.set_accels_for_action("win.add-files", ["<Ctrl>o"])
        self.set_accels_for_action("win.start", ["<Ctrl>Return"])
        self.set_accels_for_action("win.open-log", ["<Ctrl>l"])
        self.set_accels_for_action("win.show-help-overlay", ["<Ctrl>question"])
        log.info(f"{APP_NAME} {VERSION} запущен (GTK {Gtk.get_major_version()}.{Gtk.get_minor_version()}, "
                 f"libadwaita {Adw.get_major_version()}.{Adw.get_minor_version()}, "
                 f"сессия: {os.environ.get('XDG_SESSION_TYPE', '?')}, "
                 f"рабочий стол: {os.environ.get('XDG_CURRENT_DESKTOP', '?')})")

    def do_activate(self) -> None:
        if self.window is None:
            from .ui.window import MainWindow  # noqa: PLC0415

            self.window = MainWindow(self, _find_icon())
        self.window.present()
        tool, self.pending_tool = self.pending_tool, None
        files, self.pending_files = self.pending_files, []
        if tool:
            self.window.show_page(tool)
        if files:
            self.window.open_files(files, keep_current=bool(tool))

    def do_command_line(self, cmdline: Gio.ApplicationCommandLine) -> int:
        """Аргументы первого и повторных запусков (повторный запуск передаёт файлы в уже открытое окно)."""
        for arg in cmdline.get_arguments()[1:]:
            if arg.startswith("tool:"):
                self.pending_tool = arg[5:]
                continue
            f = cmdline.create_file_for_arg(arg)
            if f.get_path():
                self.pending_files.append(f.get_path())
        self.activate()
        return 0

    def quit_safely(self) -> None:
        if self.window is not None:
            self.window.close()
        else:
            self.quit()

    def do_shutdown(self) -> None:
        tracker.kill_all()
        desktop.status.clear()
        from .config import settings  # noqa: PLC0415

        settings.save_now()
        Adw.Application.do_shutdown(self)


def run_gui(files: list[str] | None = None, tool: str | None = None) -> int:
    app = KToolsApp()
    argv = [sys.argv[0]] + ([f"tool:{tool}"] if tool else []) + [os.path.abspath(f) for f in (files or [])]
    return app.run(argv)
