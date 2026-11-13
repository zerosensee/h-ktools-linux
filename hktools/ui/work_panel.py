"""Рабочая панель инструмента: очередь файлов (drag&drop), настройки, запуск и прогресс."""

from __future__ import annotations

import os
import threading
from typing import Any
from urllib.parse import unquote, urlparse

from gi.repository import Adw, Gdk, Gio, GLib, GObject, Gtk, Pango

from .. import APP_NAME
from ..config import settings as app_settings
from ..core import desktop
from ..core.runner import BatchRunner, BatchSummary
from ..core.script import QueueItem, Result, Script
from ..media.probe import probe
from . import util
from .settings_form import SettingsForm

STATUS_ICONS = {
    "pending": ("content-loading-symbolic", ""),
    "running": ("media-playback-start-symbolic", ""),
    "done": ("emblem-ok-symbolic", "kt-status-done"),
    "partial": ("dialog-warning-symbolic", "kt-status-partial"),
    "failed": ("dialog-error-symbolic", "kt-status-failed"),
    "skipped": ("media-skip-forward-symbolic", "kt-status-skipped"),
    "cancelled": ("process-stop-symbolic", "kt-status-cancelled"),
}


class ItemRow(Gtk.ListBoxRow):
    def __init__(self, panel: "WorkPanel", item: QueueItem) -> None:
        super().__init__(activatable=False, selectable=False)
        self.panel = panel
        self.item = item
        box = Gtk.Box(spacing=10)
        box.add_css_class("kt-item-row")
        self.icon_stack = Gtk.Stack()
        self.status_icon = Gtk.Image(pixel_size=16)
        self.spinner = Gtk.Spinner()
        self.icon_stack.add_named(self.status_icon, "icon")
        self.icon_stack.add_named(self.spinner, "spin")
        box.append(self.icon_stack)

        mid = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, hexpand=True)
        self.title = Gtk.Label(label=item.name, xalign=0, ellipsize=Pango.EllipsizeMode.MIDDLE)
        self.title.add_css_class("kt-item-title")
        self.title.set_tooltip_text(item.path)
        self.subtitle = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END)
        self.subtitle.add_css_class("dim-label")
        self.subtitle.add_css_class("caption")
        self.bar = Gtk.ProgressBar(visible=False)
        mid.append(self.title)
        mid.append(self.subtitle)
        mid.append(self.bar)
        box.append(mid)

        self.badge = Gtk.Label(visible=False, valign=Gtk.Align.CENTER)
        self.badge.add_css_class("kt-badge")
        box.append(self.badge)
        self.percent = Gtk.Label(valign=Gtk.Align.CENTER, width_chars=6, xalign=1)
        self.percent.add_css_class("numeric")
        box.append(self.percent)

        self.extras = Gtk.Box(spacing=2, valign=Gtk.Align.CENTER)
        box.append(self.extras)
        self.open_btn = util.icon_button("folder-open-symbolic", "Показать результат", self._show_result)
        self.open_btn.set_visible(False)
        box.append(self.open_btn)
        self.remove_btn = util.icon_button("window-close-symbolic", "Убрать из списка",
                                           lambda: panel.remove_item(item))
        box.append(self.remove_btn)
        self.set_child(box)
        panel.build_row_extras(self)
        self.refresh()

    def _show_result(self) -> None:
        if self.item.outputs:
            desktop.show_in_folder(self.item.outputs[0])

    def refresh(self) -> None:
        it = self.item
        icon, css = STATUS_ICONS.get(it.status, STATUS_ICONS["pending"])
        for c in ("kt-status-done", "kt-status-partial", "kt-status-failed", "kt-status-skipped", "kt-status-cancelled"):
            self.status_icon.remove_css_class(c)
        if css:
            self.status_icon.add_css_class(css)
        running = it.status == "running"
        if running:
            self.icon_stack.set_visible_child_name("spin")
            self.spinner.start()
        else:
            self.spinner.stop()
            self.icon_stack.set_visible_child_name("icon")
            self.status_icon.set_from_icon_name(icon)
            if it.status == "pending":
                self.status_icon.set_from_icon_name(self.panel.pending_icon(it))
        self.bar.set_visible(running)
        self.bar.set_fraction(it.progress / 100)
        self.percent.set_label(f"{it.progress:.0f}%" if running else "")
        sub = it.message if it.status != "pending" else self.panel.row_subtitle(it)
        self.subtitle.set_label(sub.splitlines()[0] if sub else "")
        self.subtitle.set_tooltip_text(sub or None)
        self.badge.set_visible(bool(it.badge))
        self.badge.set_label(it.badge)
        self.open_btn.set_visible(bool(it.outputs) and not running)
        self.remove_btn.set_sensitive(not running)
        self.title.set_label(self.panel.row_title(it))


class WorkPanel(Gtk.Box):
    """Универсальная страница инструмента."""

    def __init__(self, window, script: Script) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.window = window
        self.script = script
        self.items: list[QueueItem] = []
        self.rows: dict[int, ItemRow] = {}
        self.runner: BatchRunner | None = None
        self.out_dir: str | None = None
        self.results: dict[int, Result] = {}

        self.toolbar = Adw.ToolbarView()
        self.append(self.toolbar)
        self.toolbar.set_vexpand(True)

        # --- шапка ---
        header = Adw.HeaderBar()
        header.set_title_widget(Adw.WindowTitle(title=script.name, subtitle=script.category))
        self.header = header
        if script.input_mode == "files":
            add = Gtk.Button(icon_name="list-add-symbolic", tooltip_text="Добавить файлы (Ctrl+O)")
            add.connect("clicked", lambda *_: self.add_files_dialog())
            add_dir = Gtk.Button(icon_name="folder-new-symbolic", tooltip_text="Добавить папку")
            add_dir.connect("clicked", lambda *_: self.add_folder_dialog())
            header.pack_start(add)
            header.pack_start(add_dir)
        self.settings_toggle = Gtk.ToggleButton(icon_name="sidebar-show-right-symbolic", tooltip_text="Настройки",
                                                active=bool(app_settings.get("show_settings_sidebar")))
        header.pack_end(self.settings_toggle)
        menu = Gio.Menu()
        menu.append("Очистить список", "panel.clear")
        menu.append("Убрать завершённые", "panel.clear-done")
        menu.append("Сбросить статусы", "panel.reset")
        menu_btn = Gtk.MenuButton(icon_name="view-more-symbolic", menu_model=menu, tooltip_text="Ещё")
        header.pack_end(menu_btn)
        group = Gio.SimpleActionGroup()
        for name, cb in (("clear", self.clear), ("clear-done", self.clear_done), ("reset", self.reset_statuses)):
            act = Gio.SimpleAction.new(name, None)
            act.connect("activate", lambda *_a, f=cb: f())
            group.add_action(act)
        self.insert_action_group("panel", group)
        if script.id == "audio-transplant":
            sync = Gtk.Button(tooltip_text="Подобрать сдвиг по осциллограмме")
            sync.set_child(Adw.ButtonContent(icon_name="media-playlist-shuffle-symbolic", label="Синхронизация"))
            sync.connect("clicked", lambda *_: self.open_waveform())
            header.pack_end(sync)
        if script.id == "speech-recognition":
            models = Gtk.Button(tooltip_text="Скачать или удалить модели whisper.cpp")
            models.set_child(Adw.ButtonContent(icon_name="folder-download-symbolic", label="Модели"))
            models.connect("clicked", lambda *_: self.open_models())
            header.pack_end(models)
        self.toolbar.add_top_bar(header)

        if script.input_mode == "urls":
            self.toolbar.add_top_bar(self._build_url_bar())

        # --- содержимое: очередь + настройки ---
        self.split = Adw.OverlaySplitView(sidebar_position=Gtk.PackType.END, min_sidebar_width=340,
                                          max_sidebar_width=460, sidebar_width_fraction=0.36)
        self.split.set_show_sidebar(self.settings_toggle.get_active())
        self.settings_toggle.bind_property("active", self.split, "show-sidebar",
                                           GObject.BindingFlags.BIDIRECTIONAL | GObject.BindingFlags.SYNC_CREATE)
        self.settings_toggle.connect("toggled", lambda b: app_settings.set("show_settings_sidebar", b.get_active()))
        self.form = SettingsForm(script, self._on_setting_changed)
        side_scroll = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        side_scroll.add_css_class("background")
        side_scroll.append(self.form)
        self.split.set_sidebar(side_scroll)

        self.list_stack = Gtk.Stack(transition_type=Gtk.StackTransitionType.CROSSFADE)
        self.list_stack.add_named(self._build_empty(), "empty")
        self.listbox = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE, valign=Gtk.Align.START)
        self.listbox.add_css_class("boxed-list")
        clamp = Adw.Clamp(maximum_size=1100, child=self.listbox, margin_top=12, margin_bottom=12, margin_start=12,
                          margin_end=12)
        scroller = Gtk.ScrolledWindow(child=clamp, vexpand=True)
        self.list_stack.add_named(scroller, "list")
        self.drop_frame = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        self.drop_frame.append(self.list_stack)
        self.list_stack.set_vexpand(True)
        self.split.set_content(self.drop_frame)

        bp_bin = Adw.BreakpointBin(width_request=360, height_request=300, child=self.split)
        bp = Adw.Breakpoint.new(Adw.BreakpointCondition.parse("max-width: 820sp"))
        bp.add_setter(self.split, "collapsed", True)
        bp_bin.add_breakpoint(bp)
        self.toast_overlay = Adw.ToastOverlay(child=bp_bin)
        self.toolbar.set_content(self.toast_overlay)
        self.toolbar.add_bottom_bar(self._build_bottom())
        self._setup_dnd()
        self._update_summary()

    # --- построение ---
    def _build_empty(self) -> Gtk.Widget:
        if self.script.input_mode == "urls":
            page = Adw.StatusPage(paintable=self._status_paintable(), title="Вставьте ссылку",
                                  description="Вставьте одну или несколько ссылок в поле выше (Ctrl+V) или "
                                              "перетащите их из браузера.")
            return page
        exts = sorted(self.script.extensions or [])
        desc = "Перетащите файлы или папки сюда\n"
        if exts:
            desc += "Форматы: " + " ".join(e.lstrip(".") for e in exts[:24]) + (" …" if len(exts) > 24 else "")
        page = Adw.StatusPage(paintable=self._status_paintable(), title=self.script.name, description=desc)
        btns = Gtk.Box(spacing=12, halign=Gtk.Align.CENTER)
        b1 = Gtk.Button(label="Добавить файлы")
        b1.add_css_class("pill")
        b1.add_css_class("suggested-action")
        b1.connect("clicked", lambda *_: self.add_files_dialog())
        b2 = Gtk.Button(label="Добавить папку")
        b2.add_css_class("pill")
        b2.connect("clicked", lambda *_: self.add_folder_dialog())
        btns.append(b1)
        btns.append(b2)
        page.set_child(btns)
        return page

    def _status_paintable(self):
        theme = Gtk.IconTheme.get_for_display(self.get_display() if self.get_display() else Gdk.Display.get_default())
        return theme.lookup_by_gicon(util.gicon(self.script.icon), 128, 1, Gtk.TextDirection.NONE, 0)

    def _build_url_bar(self) -> Gtk.Widget:
        box = Gtk.Box(spacing=6, margin_start=12, margin_end=12, margin_top=6, margin_bottom=6)
        self.url_entry = Gtk.Entry(hexpand=True, placeholder_text="https://… — ссылка на видео (можно несколько через пробел)")
        self.url_entry.set_icon_from_icon_name(Gtk.EntryIconPosition.PRIMARY, "web-browser-symbolic")
        self.url_entry.connect("activate", lambda *_: self._add_urls_from_entry())
        add = Gtk.Button(label="Добавить")
        add.add_css_class("suggested-action")
        add.connect("clicked", lambda *_: self._add_urls_from_entry())
        paste = util.icon_button("edit-paste-symbolic", "Вставить из буфера", self._paste_urls, flat=False)
        box.append(self.url_entry)
        box.append(paste)
        box.append(add)
        return box

    def _build_bottom(self) -> Gtk.Widget:
        box = Gtk.Box(spacing=10)
        box.add_css_class("kt-bottom-bar")
        left = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, hexpand=True, valign=Gtk.Align.CENTER)
        self.status_label = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END)
        self.status_label.add_css_class("caption")
        self.total_bar = Gtk.ProgressBar()
        left.append(self.status_label)
        left.append(self.total_bar)
        box.append(left)
        self.out_btn = Gtk.Button(tooltip_text="Папка результатов")
        self.out_btn_content = Adw.ButtonContent(icon_name="folder-symbolic", label="Рядом с исходником")
        self.out_btn.set_child(self.out_btn_content)
        self.out_btn.add_css_class("flat")
        self.out_btn.connect("clicked", lambda *_: self._choose_out_dir())
        self.out_reset = util.icon_button("edit-clear-symbolic", "Сбросить папку результатов", self._reset_out_dir)
        self.out_reset.set_visible(False)
        if self.script.input_mode != "none":
            box.append(self.out_btn)
            box.append(self.out_reset)
        self.start_btn = Gtk.Button()
        self.start_btn.set_child(Adw.ButtonContent(icon_name="media-playback-start-symbolic", label="Запуск"))
        self.start_btn.add_css_class("suggested-action")
        self.start_btn.add_css_class("pill")
        self.start_btn.connect("clicked", lambda *_: self.toggle_run())
        box.append(self.start_btn)
        self._refresh_out_label()
        return box

    def _setup_dnd(self) -> None:
        target = Gtk.DropTarget.new(Gdk.FileList, Gdk.DragAction.COPY)
        target.connect("drop", self._on_drop_files)
        target.connect("enter", lambda *_: (self.drop_frame.add_css_class("kt-drop-highlight"), Gdk.DragAction.COPY)[1])
        target.connect("leave", lambda *_: self.drop_frame.remove_css_class("kt-drop-highlight"))
        self.drop_frame.add_controller(target)
        if self.script.input_mode == "urls":
            t2 = Gtk.DropTarget.new(GObject.TYPE_STRING, Gdk.DragAction.COPY)
            t2.connect("drop", lambda _t, val, *_a: (self.add_urls(str(val).split()), True)[1])
            self.drop_frame.add_controller(t2)

    # --- хуки инструментов ---
    def row_title(self, item: QueueItem) -> str:
        if self.script.input_mode == "urls":
            return (item.extra.get("meta") or {}).get("title") or item.path
        return item.name

    def row_subtitle(self, item: QueueItem) -> str:
        if self.script.id == "mkv-assembly":
            return self.script.describe_group(item, self.items)  # type: ignore[attr-defined]
        sub = self.script.row_subtitle(item)
        if not sub and item.info is not None:
            info = item.info
            bits = [util.human_size(info.size)]
            if info.duration:
                bits.append(util.human_duration(info.duration))
            if info.video:
                bits.append(info.video[0].resolution)
            bits.append(f"дорожек: {len(info.media_tracks)}")
            sub = " · ".join(b for b in bits if b)
        elif not sub and self.script.input_mode == "files":
            try:
                sub = util.human_size(os.path.getsize(item.path)) + " · " + os.path.dirname(item.path)
            except OSError:
                sub = os.path.dirname(item.path)
        if item.extra.get("loading"):
            sub = "Анализ…"
        if item.extra.get("error"):
            sub = "⚠ " + item.extra["error"]
        return sub

    def pending_icon(self, item: QueueItem) -> str:
        if self.script.id == "mkv-assembly":
            from ..core.script import AUDIO_ALL, SUBTITLE_EXTS  # noqa: PLC0415

            if item.ext in AUDIO_ALL:
                return "audio-x-generic-symbolic"
            if item.ext in SUBTITLE_EXTS:
                return "font-x-generic-symbolic"
            return "video-x-generic-symbolic"
        return "content-loading-symbolic"

    def build_row_extras(self, row: ItemRow) -> None:
        item = row.item
        sid = self.script.id
        if self.script.track_selection:
            btn = util.icon_button("view-list-bullet-symbolic", "Дорожки…", lambda: self.open_tracks(item))
            row.extras.append(btn)
        if sid == "subtitles-convert":
            row.extras.append(util.icon_button("view-reveal-symbolic", "Предпросмотр и фильтр реплик",
                                               lambda: self.open_sub_preview(item)))
        if sid == "bitrate-viewer":
            btn = util.icon_button("utilities-system-monitor-symbolic", "Открыть график", lambda: self.open_chart(item))
            btn.set_visible(False)
            row.chart_btn = btn  # type: ignore[attr-defined]
            row.extras.append(btn)
        if sid == "mkv-assembly":
            from ..core.script import VIDEO_CONTAINERS  # noqa: PLC0415

            if item.ext not in VIDEO_CONTAINERS:
                row.extras.append(self._mux_menu(item))
        if sid == "media-downloader":
            self._downloader_controls(row)

    # --- сборка MKV: меню сопутствующего файла ---
    def _mux_menu(self, item: QueueItem) -> Gtk.Widget:
        from ..core.script import SUBTITLE_EXTS, VIDEO_CONTAINERS  # noqa: PLC0415

        btn = Gtk.MenuButton(icon_name="document-properties-symbolic", tooltip_text="Роль и привязка",
                             valign=Gtk.Align.CENTER)
        btn.add_css_class("flat")
        pop = Gtk.Popover()
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, margin_top=6, margin_bottom=6,
                      margin_start=6, margin_end=6)

        def add(label: str, cb) -> None:
            b = Gtk.Button(label=label, halign=Gtk.Align.FILL)
            b.add_css_class("flat")
            b.get_child().set_xalign(0) if isinstance(b.get_child(), Gtk.Label) else None
            b.connect("clicked", lambda *_: (pop.popdown(), cb(), self.refresh_rows()))
            box.append(b)

        def build(*_a) -> None:
            child = box.get_first_child()
            while child:
                nxt = child.get_next_sibling()
                box.remove(child)
                child = nxt
            if item.ext in SUBTITLE_EXTS:
                for role, label in (("signs", "Роль: надписи"), ("full", "Роль: полные"), ("other", "Роль: прочие"),
                                    (None, "Роль: авто (по имени)")):
                    add(("● " if item.extra.get("mux_role") == role else "  ") + label,
                        lambda r=role: item.extra.__setitem__("mux_role", r))
            else:
                add(("● " if item.extra.get("mux_main") else "  ") + "Основная аудиодорожка",
                    lambda: item.extra.__setitem__("mux_main", not item.extra.get("mux_main")))
                add("  Заголовок дорожки…", lambda: self._ask_mux_title(item))
            box.append(Gtk.Separator())
            videos = [i for i in self.items if i.ext in VIDEO_CONTAINERS]
            for v in videos[:30]:
                add(("● " if item.extra.get("mux_pinned") == v.stem else "  ") + f"Привязать к: {v.name}",
                    lambda s=v.stem: item.extra.__setitem__("mux_pinned", s))
            add("  Привязка: авто (по имени)", lambda: item.extra.pop("mux_pinned", None))

        pop.connect("show", build)
        pop.set_child(box)
        btn.set_popover(pop)
        return btn

    def _ask_mux_title(self, item: QueueItem) -> None:
        from .dialogs import ask_text  # noqa: PLC0415

        ask_text(self.window, "Заголовок аудиодорожки", item.name, item.extra.get("mux_title", ""),
                 lambda t: (item.extra.__setitem__("mux_title", t.strip()), self.refresh_rows()))

    # --- загрузчик: качество и субтитры у ссылки ---
    def _downloader_controls(self, row: ItemRow) -> None:
        fmt_dd = Gtk.DropDown(valign=Gtk.Align.CENTER, tooltip_text="Качество")
        sub_dd = Gtk.DropDown(valign=Gtk.Align.CENTER, tooltip_text="Субтитры")
        row.extras.append(fmt_dd)
        row.extras.append(sub_dd)
        row.fmt_dd = fmt_dd  # type: ignore[attr-defined]
        row.sub_dd = sub_dd  # type: ignore[attr-defined]
        self._fill_downloader(row)

    def _fill_downloader(self, row: ItemRow) -> None:
        item = row.item
        meta = item.extra.get("meta") or {}
        formats = meta.get("formats") or [("Лучшее видео + аудио", "bv*+ba/b", False), ("Только аудио", "ba/b", True)]
        subs = ["none"] + list(meta.get("subtitles") or []) + [f"{s} (авто)" for s in meta.get("auto_subtitles") or []]
        fmt_dd: Gtk.DropDown = row.fmt_dd  # type: ignore[attr-defined]
        sub_dd: Gtk.DropDown = row.sub_dd  # type: ignore[attr-defined]
        fmt_dd.set_model(Gtk.StringList.new([f[0] for f in formats]))
        labels = ["Без субтитров" if s == "none" else s for s in subs]
        sub_dd.set_model(Gtk.StringList.new(labels))
        cur = item.extra.get("format_arg")
        idx = next((i for i, f in enumerate(formats) if f[1] == cur), 0)
        fmt_dd.set_selected(idx)

        def on_fmt(dd, _p) -> None:
            i = dd.get_selected()
            if 0 <= i < len(formats):
                item.extra["format_label"] = formats[i][0]
                item.extra["format_arg"] = formats[i][1]
                item.extra["audio_only"] = formats[i][2]

        def on_sub(dd, _p) -> None:
            i = dd.get_selected()
            if 0 <= i < len(subs):
                item.extra["sub_lang"] = subs[i].replace(" (авто)", "")

        for dd, cb in ((fmt_dd, on_fmt), (sub_dd, on_sub)):
            hid = getattr(dd, "_kt_handler", None)
            if hid:
                dd.disconnect(hid)
            dd._kt_handler = dd.connect("notify::selected", cb)  # type: ignore[attr-defined]
        on_fmt(fmt_dd, None)
        sub_dd.set_sensitive(len(subs) > 1)

    # --- добавление элементов ---
    def add_files_dialog(self) -> None:
        if self.script.input_mode != "files":
            return
        patterns = [f"*{e}" for e in sorted(self.script.extensions or [])] or None
        util.pick_files(self.window, "Добавить файлы", self.add_paths, start=app_settings.get("last_dir"),
                        patterns=patterns)

    def add_folder_dialog(self) -> None:
        util.pick_folder(self.window, "Добавить папку", lambda p: self.add_paths([p]),
                         start=app_settings.get("last_dir"), multiple=True)

    def _on_drop_files(self, _target, value, _x, _y) -> bool:
        self.drop_frame.remove_css_class("kt-drop-highlight")
        files = value.get_files() if hasattr(value, "get_files") else []
        paths = [f.get_path() for f in files if f.get_path()]
        if self.script.input_mode == "urls":
            self.add_urls([f.get_uri() for f in files])
        else:
            self.add_paths(paths)
        return True

    def add_paths(self, paths: list[str]) -> None:
        if self.running:
            self.toast("Нельзя менять список во время обработки")
            return
        if not paths:
            return
        app_settings.set("last_dir", os.path.dirname(paths[0]) if os.path.isfile(paths[0]) else paths[0])
        all_files = util.collect_files(paths)
        accepted = [p for p in all_files if self.script.accepts(p)]
        rejected = len(all_files) - len(accepted)
        if app_settings.get("clear_list_on_add") and accepted:
            self.clear()
        known = {i.path for i in self.items}
        new_items = [QueueItem(os.path.abspath(p)) for p in accepted if os.path.abspath(p) not in known]
        for it in new_items:
            self._append_item(it)
        if rejected:
            self.toast(f"Пропущено неподходящих файлов: {rejected}")
        if new_items:
            self._after_add(new_items)
        self._update_summary()

    def _add_urls_from_entry(self) -> None:
        text = self.url_entry.get_text().strip()
        if text:
            self.add_urls(text.split())
            self.url_entry.set_text("")

    def _paste_urls(self) -> None:
        clip = self.get_clipboard()

        def done(c, res) -> None:
            try:
                text = c.read_text_finish(res)
            except GLib.Error:
                return
            if text:
                self.add_urls(text.split())

        clip.read_text_async(None, done)

    def add_urls(self, urls: list[str]) -> None:
        urls = [u.strip() for u in urls if u.strip().startswith(("http://", "https://"))]
        known = {i.path for i in self.items}
        new = [QueueItem(u) for u in dict.fromkeys(urls) if u not in known]
        for it in new:
            self._append_item(it)
        if new:
            self._after_add(new)
        elif urls:
            self.toast("Ссылки уже в списке")
        else:
            self.toast("Не найдено ссылок http(s)")
        self._update_summary()

    def _append_item(self, item: QueueItem) -> None:
        self.items.append(item)
        row = ItemRow(self, item)
        self.rows[item.id] = row
        self.listbox.append(row)
        self.list_stack.set_visible_child_name("list")

    def _after_add(self, items: list[QueueItem]) -> None:
        sid = self.script.id
        if self.script.track_selection or sid in ("video-encode", "bitrate-viewer", "metadata-cleanup",
                                                  "container-convert"):
            self._probe_items(items)
        if sid == "media-downloader":
            for it in items:
                self._fetch_url(it)
        if sid == "mkv-assembly":
            from ..core.script import AUDIO_ALL  # noqa: PLC0415

            audio = [i for i in items if i.ext in AUDIO_ALL]
            vals = self.form.values
            if audio and vals.get("audio_title_prompt"):
                from ..core.fields import active_words  # noqa: PLC0415
                from .dialogs import ask_audio_title  # noqa: PLC0415

                ask_audio_title(self.window, len(audio), active_words(vals.get("audio_title_presets")),
                                str(vals.get("audio_title_default", "")),
                                lambda t: ([a.extra.__setitem__("mux_title", t) for a in audio if t], self.refresh_rows()))
            self.refresh_rows()

    def _probe_items(self, items: list[QueueItem]) -> None:
        from ..scripts.streams import default_selection  # noqa: PLC0415

        for it in items:
            it.extra["loading"] = True
            self._refresh_row(it)

        def work() -> None:
            for it in items:
                try:
                    it.info = probe(it.path)
                    if self.script.track_selection and "selected" not in it.extra:
                        it.extra["selected"] = default_selection(self.script, it.info)
                    it.extra.pop("error", None)
                except Exception as exc:  # noqa: BLE001
                    it.extra["error"] = str(exc)[:200]
                it.extra.pop("loading", None)
                util.idle(self._refresh_row, it)

        threading.Thread(target=work, daemon=True).start()

    def _fetch_url(self, item: QueueItem) -> None:
        from ..scripts.downloader import fetch_info  # noqa: PLC0415

        item.extra["loading"] = True
        self._refresh_row(item)

        def done(meta, err) -> None:
            item.extra.pop("loading", None)
            if err:
                item.extra["error"] = f"не удалось получить данные: {err}"[:200]
            else:
                item.extra["meta"] = meta
                item.extra.pop("error", None)
            row = self.rows.get(item.id)
            if row is not None:
                self._fill_downloader(row)
            self._refresh_row(item)

        util.run_bg(lambda: fetch_info(item.path), done)

    # --- управление списком ---
    def remove_item(self, item: QueueItem) -> None:
        if self.running:
            return
        row = self.rows.pop(item.id, None)
        if row is not None:
            self.listbox.remove(row)
        if item in self.items:
            self.items.remove(item)
        if not self.items:
            self.list_stack.set_visible_child_name("empty")
        self.refresh_rows()
        self._update_summary()

    def clear(self) -> None:
        if self.running:
            return
        for row in list(self.rows.values()):
            self.listbox.remove(row)
        self.rows.clear()
        self.items.clear()
        self.results.clear()
        self.list_stack.set_visible_child_name("empty")
        self.total_bar.set_fraction(0)
        self._update_summary()

    def clear_done(self) -> None:
        for it in [i for i in self.items if i.status in ("done", "skipped")]:
            self.remove_item(it)

    def reset_statuses(self) -> None:
        if self.running:
            return
        for it in self.items:
            it.reset()
        self.refresh_rows()
        self.total_bar.set_fraction(0)
        self._update_summary()

    def _refresh_row(self, item: QueueItem) -> None:
        row = self.rows.get(item.id)
        if row is not None:
            row.refresh()

    def refresh_rows(self) -> None:
        for row in self.rows.values():
            row.refresh()

    def _update_summary(self) -> None:
        if self.running:
            return
        n = len(self.items)
        if self.script.input_mode == "none":
            return
        if not n:
            self.status_label.set_label("Очередь пуста")
        else:
            done = sum(1 for i in self.items if i.status in ("done", "partial"))
            failed = sum(1 for i in self.items if i.status == "failed")
            txt = f"В очереди: {n}"
            if done or failed:
                txt += f" · готово {done}, ошибок {failed}"
            self.status_label.set_label(txt)
        self.start_btn.set_sensitive(n > 0)

    # --- папка результатов ---
    def _choose_out_dir(self) -> None:
        start = self.out_dir or (os.path.dirname(self.items[0].path) if self.items and
                                 self.script.input_mode == "files" else app_settings.get("last_dir"))
        util.pick_folder(self.window, "Папка для результатов", self._set_out_dir, start)

    def _set_out_dir(self, path: str) -> None:
        self.out_dir = path
        self._refresh_out_label()

    def _reset_out_dir(self) -> None:
        self.out_dir = None
        self._refresh_out_label()

    def _refresh_out_label(self) -> None:
        if self.out_dir:
            self.out_btn_content.set_label(os.path.basename(self.out_dir.rstrip("/")) or self.out_dir)
            self.out_btn.set_tooltip_text(self.out_dir)
        elif self.script.input_mode == "urls":
            self.out_btn_content.set_label("Загрузки")
            self.out_btn.set_tooltip_text("По умолчанию — папка «Загрузки»")
        elif app_settings.get("use_auto_subfolder"):
            self.out_btn_content.set_label(f"Подпапка «{app_settings.get('output_subfolder')}»")
            self.out_btn.set_tooltip_text("Результаты сохраняются в подпапку рядом с исходником")
        else:
            self.out_btn_content.set_label("Рядом с исходником")
            self.out_btn.set_tooltip_text("Результаты сохраняются рядом с исходным файлом")
        self.out_reset.set_visible(bool(self.out_dir))

    # --- запуск ---
    @property
    def running(self) -> bool:
        return self.runner is not None

    def toggle_run(self) -> None:
        if self.running:
            self.runner.cancel()  # type: ignore[union-attr]
            self.status_label.set_label("Отмена…")
            return
        self.start()

    def start(self) -> None:
        if not self.items:
            self.toast("Добавьте файлы в очередь")
            return
        missing = self.script.missing_deps()
        if missing:
            from ..tools import TOOLS  # noqa: PLC0415

            names = ", ".join(TOOLS[m].title for m in missing if m in TOOLS)
            util.confirm(self.window, "Не хватает утилит", f"Для «{self.script.name}» нужно: {names}.",
                         lambda: self.window.show_page("deps"), "Открыть «Зависимости»")
            return
        if any(i.extra.get("loading") for i in self.items):
            self.toast("Дождитесь окончания анализа файлов")
            return
        if not any(i.status == "pending" for i in self.items):
            for it in self.items:
                it.reset()
            self.refresh_rows()
        values = dict(self.form.values)
        self.results.clear()
        self.runner = BatchRunner(
            self.script, list(self.items), values, self.out_dir,
            on_item=lambda it: util.idle(self._refresh_row, it),
            on_overall=lambda pct, msg: util.idle(self._on_overall, pct, msg),
            on_result=lambda it, res: util.idle(self._on_result, it, res),
            on_finish=lambda s: util.idle(self._on_finish, s),
            ask=self._ask_from_worker,
        )
        self._set_running_ui(True)
        self.runner.start()

    def _set_running_ui(self, running: bool) -> None:
        content: Adw.ButtonContent = self.start_btn.get_child()  # type: ignore[assignment]
        if running:
            content.set_icon_name("media-playback-stop-symbolic")
            content.set_label("Стоп")
            self.start_btn.remove_css_class("suggested-action")
            self.start_btn.add_css_class("destructive-action")
            self.total_bar.set_fraction(0)
        else:
            content.set_icon_name("media-playback-start-symbolic")
            content.set_label("Запуск")
            self.start_btn.remove_css_class("destructive-action")
            self.start_btn.add_css_class("suggested-action")
        self.form.set_sensitive(not running)
        self.window.set_page_busy(self.script.id, running)

    def _on_overall(self, pct: float, msg: str) -> None:
        self.total_bar.set_fraction(pct / 100)
        self.status_label.set_label(f"{pct:.0f}% · {msg}")

    def _on_result(self, item: QueueItem, res: Result) -> None:
        self.results[item.id] = res
        self._refresh_row(item)
        if self.script.id == "bitrate-viewer" and res.data is not None:
            row = self.rows.get(item.id)
            if row is not None:
                row.chart_btn.set_visible(True)  # type: ignore[attr-defined]
            if self.form.values.get("open_chart", True):
                self.open_chart(item)

    def _on_finish(self, summary: BatchSummary) -> None:
        self.runner = None
        self._set_running_ui(False)
        self.total_bar.set_fraction(1.0 if not summary.cancelled else self.total_bar.get_fraction())
        self._update_summary()
        text = (f"Готово: {summary.ok}" + (f", ошибок {summary.failed}" if summary.failed else "")
                + (f", пропущено {summary.skipped}" if summary.skipped else "") + (" — отменено" if summary.cancelled else ""))
        self.toast(text)
        if app_settings.get("notify_on_finish") and not summary.cancelled:
            if not (app_settings.get("notify_only_unfocused") and self.window.is_active()):
                desktop.notify(f"{APP_NAME}: {self.script.name}", text,
                               urgency="critical" if summary.failed else "normal")

    def _ask_from_worker(self, kind: str, *args: Any) -> Any:
        """Синхронный вопрос пользователю из рабочего потока."""
        ev = threading.Event()
        box: dict[str, Any] = {}

        def show() -> None:
            if kind == "choose_subtitle":
                from .dialogs import choose_subtitle  # noqa: PLC0415

                choose_subtitle(self.window, args[0], args[1], lambda t: (box.__setitem__("v", t), ev.set()))
            else:
                ev.set()

        util.idle(show)
        while not ev.wait(0.2):
            if self.runner is None or self.runner.token.cancelled:
                return None
        return box.get("v")

    # --- окна инструментов ---
    def open_tracks(self, item: QueueItem) -> None:
        if item.info is None:
            self.toast("Файл ещё анализируется")
            return
        from .dialogs import TrackDialog  # noqa: PLC0415

        TrackDialog(self.script, item, self.items, self.refresh_rows).present(self.window)

    def open_sub_preview(self, item: QueueItem) -> None:
        from .subtitle_preview import SubtitlePreview  # noqa: PLC0415

        SubtitlePreview(self, item).present(self.window)

    def open_chart(self, item: QueueItem) -> None:
        res = self.results.get(item.id)
        if res is None or res.data is None:
            return
        from .bitrate_window import BitrateWindow  # noqa: PLC0415

        BitrateWindow(self.window, res.data).present()

    def open_waveform(self) -> None:
        if not self.items:
            self.toast("Сначала добавьте видео, в которое пересаживается аудио")
            return
        from .waveform_sync import WaveformSync  # noqa: PLC0415

        WaveformSync(self).present()

    def open_models(self) -> None:
        from .dialogs import WhisperModelsDialog  # noqa: PLC0415

        WhisperModelsDialog(self.form.rebuild).present(self.window)

    def _on_setting_changed(self, key: str, value: Any) -> None:
        if self.script.id == "mkv-assembly":
            self.refresh_rows()

    def toast(self, text: str) -> None:
        self.toast_overlay.add_toast(Adw.Toast(title=text, timeout=3))


def uri_to_path(uri: str) -> str:
    p = urlparse(uri)
    return unquote(p.path) if p.scheme == "file" else uri
