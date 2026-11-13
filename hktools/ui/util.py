"""Мелкие помощники GTK: потоки, диалоги выбора файлов, форматирование."""

from __future__ import annotations

import os
import threading
from typing import Any, Callable

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gio, GLib, Gtk  # noqa: E402


def idle(fn: Callable, *args: Any) -> None:
    """Выполнить fn(*args) в главном потоке GTK."""

    def _run() -> bool:
        fn(*args)
        return False

    GLib.idle_add(_run)


def run_bg(work: Callable[[], Any], done: Callable[[Any, BaseException | None], None] | None = None) -> None:
    """Выполнить work() в фоне, затем done(result, error) в главном потоке."""

    def _target() -> None:
        try:
            res = work()
            err = None
        except BaseException as exc:  # noqa: BLE001
            res, err = None, exc
        if done:
            idle(done, res, err)

    threading.Thread(target=_target, daemon=True).start()


ICON_FALLBACKS = {
    "utilities-system-monitor-symbolic": ["org.gnome.SystemMonitor-symbolic", "power-profile-performance-symbolic",
                                          "view-paged-symbolic"],
    "audio-card-symbolic": ["audio-x-generic-symbolic"],
    "preferences-system-time-symbolic": ["alarm-symbolic", "document-open-recent-symbolic"],
    "accessories-calculator-symbolic": ["org.gnome.Calculator-symbolic", "input-dialpad-symbolic"],
    "insert-object-symbolic": ["list-add-symbolic"],
}


def gicon(name: str) -> Gio.Icon:
    """Иконка с запасными вариантами (на случай тем вроде Papirus без нужного имени)."""
    names = [name, *ICON_FALLBACKS.get(name, []), "applications-multimedia-symbolic", "image-missing"]
    return Gio.ThemedIcon.new_from_names(names)


def image(name: str, size: int | None = None) -> Gtk.Image:
    img = Gtk.Image.new_from_gicon(gicon(name))
    if size:
        img.set_pixel_size(size)
    return img


def human_size(n: int) -> str:
    size = float(n)
    for unit in ("Б", "КБ", "МБ", "ГБ", "ТБ"):
        if size < 1024 or unit == "ТБ":
            return f"{size:.0f} {unit}" if unit == "Б" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{n} Б"


def human_duration(sec: float) -> str:
    sec = int(max(0, sec))
    h, rem = divmod(sec, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m}:{s:02d}"


def _start_folder(path: str | None) -> Gio.File | None:
    if path and os.path.isdir(path):
        return Gio.File.new_for_path(path)
    return None


def pick_files(parent: Gtk.Window, title: str, on_done: Callable[[list[str]], None], multiple: bool = True,
               start: str | None = None, patterns: list[str] | None = None) -> None:
    dialog = Gtk.FileDialog(title=title, modal=True)
    folder = _start_folder(start)
    if folder:
        dialog.set_initial_folder(folder)
    if patterns:
        filters = Gio.ListStore.new(Gtk.FileFilter)
        flt = Gtk.FileFilter(name="Поддерживаемые файлы")
        for p in patterns:
            flt.add_pattern(p)
            flt.add_pattern(p.upper())
        filters.append(flt)
        any_f = Gtk.FileFilter(name="Все файлы")
        any_f.add_pattern("*")
        filters.append(any_f)
        dialog.set_filters(filters)

    def finish(dlg: Gtk.FileDialog, result: Gio.AsyncResult) -> None:
        try:
            if multiple:
                model = dlg.open_multiple_finish(result)
                paths = [model.get_item(i).get_path() for i in range(model.get_n_items())]
            else:
                f = dlg.open_finish(result)
                paths = [f.get_path()] if f else []
        except GLib.Error:
            return
        on_done([p for p in paths if p])

    if multiple:
        dialog.open_multiple(parent, None, finish)
    else:
        dialog.open(parent, None, finish)


def pick_folder(parent: Gtk.Window, title: str, on_done: Callable[[str], None], start: str | None = None,
                multiple: bool = False) -> None:
    dialog = Gtk.FileDialog(title=title, modal=True)
    folder = _start_folder(start)
    if folder:
        dialog.set_initial_folder(folder)

    def finish(dlg: Gtk.FileDialog, result: Gio.AsyncResult) -> None:
        try:
            if multiple:
                model = dlg.select_multiple_folders_finish(result)
                for i in range(model.get_n_items()):
                    p = model.get_item(i).get_path()
                    if p:
                        on_done(p)
                return
            f = dlg.select_folder_finish(result)
        except GLib.Error:
            return
        if f and f.get_path():
            on_done(f.get_path())

    if multiple:
        dialog.select_multiple_folders(parent, None, finish)
    else:
        dialog.select_folder(parent, None, finish)


def save_file(parent: Gtk.Window, title: str, name: str, on_done: Callable[[str], None]) -> None:
    dialog = Gtk.FileDialog(title=title, modal=True, initial_name=name)

    def finish(dlg: Gtk.FileDialog, result: Gio.AsyncResult) -> None:
        try:
            f = dlg.save_finish(result)
        except GLib.Error:
            return
        if f and f.get_path():
            on_done(f.get_path())

    dialog.save(parent, None, finish)


def confirm(parent: Gtk.Widget, heading: str, body: str, on_yes: Callable[[], None],
            yes_label: str = "Продолжить", destructive: bool = False, on_no: Callable[[], None] | None = None) -> None:
    dlg = Adw.AlertDialog(heading=heading, body=body)
    dlg.add_response("cancel", "Отмена")
    dlg.add_response("ok", yes_label)
    dlg.set_response_appearance("ok", Adw.ResponseAppearance.DESTRUCTIVE if destructive
                                else Adw.ResponseAppearance.SUGGESTED)
    dlg.set_default_response("ok")
    dlg.set_close_response("cancel")

    def on_resp(_d: Adw.AlertDialog, resp: str) -> None:
        if resp == "ok":
            on_yes()
        elif on_no:
            on_no()

    dlg.connect("response", on_resp)
    dlg.present(parent)


def copy_text(widget: Gtk.Widget, text: str) -> None:
    widget.get_clipboard().set(text)


def icon_button(icon: str, tooltip: str, cb: Callable[..., Any] | None = None, flat: bool = True) -> Gtk.Button:
    btn = Gtk.Button(icon_name=icon, tooltip_text=tooltip, valign=Gtk.Align.CENTER)
    if flat:
        btn.add_css_class("flat")
    if cb:
        btn.connect("clicked", lambda *_: cb())
    return btn


def collect_files(paths: list[str], accept: Callable[[str], bool] | None = None, recursive: bool = True) -> list[str]:
    """Развернуть папки в список файлов (с фильтром)."""
    out: list[str] = []
    for p in paths:
        if os.path.isdir(p):
            if recursive:
                for root, dirs, files in os.walk(p):
                    dirs.sort()
                    for f in sorted(files):
                        full = os.path.join(root, f)
                        if accept is None or accept(full):
                            out.append(full)
            continue
        if os.path.isfile(p) and (accept is None or accept(p)):
            out.append(p)
    return out
