"""Страницы: главная, калькулятор таймингов, журнал, зависимости."""

from __future__ import annotations

import os
from typing import Callable

from gi.repository import Adw, GLib, Gtk, Pango

from .. import APP_NAME, REPO_URL, UPSTREAM_NAME, UPSTREAM_URL, VERSION, hw, paths, tools
from ..config import settings as app_settings
from ..core import desktop
from ..core.script import CATEGORIES, Script
from ..log import LogEntry, log
from ..media import subtitles as subs
from . import util

CAT_CSS = {"Видео": "kt-cat-video", "Аудио": "kt-cat-audio", "Контейнеры": "kt-cat-containers",
           "Субтитры": "kt-cat-subtitles", "Сеть": "kt-cat-network", "Инструменты": "kt-cat-tools"}


def _plain_header(title: str, subtitle: str = "") -> Adw.HeaderBar:
    header = Adw.HeaderBar()
    header.set_title_widget(Adw.WindowTitle(title=title, subtitle=subtitle))
    return header


# --- Главная ------------------------------------------------------------------
class HomePage(Gtk.Box):
    def __init__(self, scripts: list[Script], open_cb: Callable[[str], None], icon_path: str | None) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.scripts = scripts
        self.open_cb = open_cb
        tv = Adw.ToolbarView()
        tv.set_vexpand(True)
        header = _plain_header(APP_NAME, "Пакетная обработка медиа")
        self.search = Gtk.SearchEntry(placeholder_text="Поиск инструмента…", width_chars=24)
        self.search.connect("search-changed", lambda *_: self._filter())
        header.pack_end(self.search)
        tv.add_top_bar(header)
        self.banner = Adw.Banner(button_label="Открыть")
        self.banner.connect("button-clicked", lambda *_: open_cb("deps"))
        tv.add_top_bar(self.banner)

        content = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, margin_top=24, margin_bottom=24,
                          margin_start=18, margin_end=18)
        hero = Gtk.Box(spacing=18, halign=Gtk.Align.START)
        if icon_path and os.path.exists(icon_path):
            img = Gtk.Image.new_from_file(icon_path)
            img.set_pixel_size(84)
            hero.append(img)
        txt = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, valign=Gtk.Align.CENTER, spacing=4)
        t = Gtk.Label(label=APP_NAME, xalign=0)
        t.add_css_class("kt-hero-title")
        self.sub = Gtk.Label(xalign=0, wrap=True)
        self.sub.add_css_class("dim-label")
        txt.append(t)
        txt.append(self.sub)
        hero.append(txt)
        content.append(hero)

        self.cards: list[tuple[Gtk.Widget, Script]] = []
        self.sections: list[tuple[Gtk.Label, Gtk.FlowBox, list[Script]]] = []
        for cat in CATEGORIES:
            items = [s for s in scripts if s.category == cat]
            if not items:
                continue
            title = Gtk.Label(label=cat, xalign=0)
            title.add_css_class("kt-section-title")
            flow = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, homogeneous=True, max_children_per_line=4,
                               min_children_per_line=1, column_spacing=12, row_spacing=12)
            for s in items:
                card = self._card(s)
                flow.append(card)
                self.cards.append((card, s))
            content.append(title)
            content.append(flow)
            self.sections.append((title, flow, items))
        clamp = Adw.Clamp(maximum_size=1240, child=content)
        tv.set_content(Gtk.ScrolledWindow(child=clamp, vexpand=True))
        self.append(tv)
        self.refresh_status()

    def _card(self, s: Script) -> Gtk.Widget:
        btn = Gtk.Button()
        btn.add_css_class("card")
        btn.add_css_class("kt-card")
        box = Gtk.Box(spacing=12)
        icon = util.image(s.icon, 24)
        icon.set_valign(Gtk.Align.START)
        icon.add_css_class("kt-card-icon")
        icon.add_css_class(CAT_CSS.get(s.category, ""))
        box.append(icon)
        col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, hexpand=True)
        name = Gtk.Label(label=s.name, xalign=0, wrap=True)
        name.add_css_class("kt-card-title")
        desc = Gtk.Label(label=s.description, xalign=0, wrap=True, lines=3, ellipsize=Pango.EllipsizeMode.END,
                         max_width_chars=34)
        desc.add_css_class("kt-card-desc")
        col.append(name)
        col.append(desc)
        box.append(col)
        btn.set_child(box)
        btn.connect("clicked", lambda *_: self.open_cb(s.id))
        missing = s.missing_deps()
        if missing:
            btn.add_css_class("kt-unavailable")
            btn.set_tooltip_text("Не установлено: " + ", ".join(tools.TOOLS[m].title for m in missing if m in tools.TOOLS))
        return btn

    def refresh_status(self) -> None:
        missing_required = [k for k, t in tools.TOOLS.items() if t.required and not tools.which(k)]
        if missing_required:
            self.banner.set_title("Не найден FFmpeg — большинство инструментов недоступно. Установите: sudo pacman -S ffmpeg")
            self.banner.set_revealed(True)
        else:
            missing_opt = [t.title for k, t in tools.TOOLS.items() if k in ("mkvmerge", "yt-dlp", "whisper", "fdkaac")
                           and not tools.which(k)]
            if missing_opt:
                self.banner.set_title("Часть инструментов недоступна: нет " + ", ".join(missing_opt))
                self.banner.set_revealed(True)
            else:
                self.banner.set_revealed(False)

        def work():
            return hw.get_caps()

        def done(caps, err) -> None:
            if err or caps is None:
                self.sub.set_label(f"{len(self.scripts)} инструментов · форк {UPSTREAM_NAME}")
                return
            accel = []
            if caps.nvenc_ok:
                accel.append("NVENC")
            if caps.vaapi_ok:
                accel.append("VAAPI")
            self.sub.set_label(f"{len(self.scripts)} инструментов · FFmpeg {caps.ffmpeg_version or '—'} · "
                               f"аппаратное кодирование: {', '.join(accel) or 'нет (CPU)'}\n"
                               f"Независимый Linux-форк {UPSTREAM_NAME} (krnzhnr)")

        self.sub.set_label(f"{len(self.scripts)} инструментов · определение оборудования…")
        util.run_bg(work, done)

    def _filter(self) -> None:
        q = self.search.get_text().strip().lower()
        for card, s in self.cards:
            hay = f"{s.name} {s.description} {s.category} {s.id}".lower()
            card.get_parent().set_visible(not q or q in hay)
        for title, flow, items in self.sections:
            vis = any(not q or q in f"{s.name} {s.description} {s.category} {s.id}".lower() for s in items)
            title.set_visible(vis)
            flow.set_visible(vis)


# --- Калькулятор таймингов ----------------------------------------------------
class TimingPage(Gtk.Box):
    """Разница таймкодов «до» и «после» (как в Aegisub) с историей."""

    def __init__(self) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        tv = Adw.ToolbarView()
        tv.set_vexpand(True)
        tv.add_top_bar(_plain_header("Калькулятор таймингов", "Инструменты"))
        page = Adw.PreferencesPage()
        group = Adw.PreferencesGroup(title="Таймкоды", description="Формат Ч:ММ:СС.ммм, ММ:СС.ммм или миллисекунды. "
                                                                    "Результат — сдвиг, который нужно применить.")
        self.before = Adw.EntryRow(title="До (исходное время)", text="0:00:00.000")
        self.after = Adw.EntryRow(title="После (нужное время)", text="0:00:00.000")
        for row, name in ((self.before, "before"), (self.after, "after")):
            row.add_css_class("kt-time-entry")
            row.connect("changed", lambda *_: self._calc())
            paste = util.icon_button("edit-paste-symbolic", "Вставить из буфера", lambda r=row: self._paste(r))
            row.add_suffix(paste)
            group.add(row)
        swap = Adw.ActionRow(title="Поменять местами")
        swap_btn = util.icon_button("object-flip-vertical-symbolic", "Поменять", self._swap)
        swap.add_suffix(swap_btn)
        swap.set_activatable_widget(swap_btn)
        group.add(swap)
        page.add(group)

        res_group = Adw.PreferencesGroup(title="Результат")
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, margin_top=12, margin_bottom=12)
        self.result = Gtk.Label(label="+0:00:00.000")
        self.result.add_css_class("kt-big-time")
        self.result_ms = Gtk.Label(label="0 мс")
        self.result_ms.add_css_class("dim-label")
        box.append(self.result)
        box.append(self.result_ms)
        btns = Gtk.Box(spacing=8, halign=Gtk.Align.CENTER, margin_top=6)
        for label, cb in (("Копировать время", lambda: util.copy_text(self, self.result.get_label())),
                          ("Копировать мс", lambda: util.copy_text(self, str(self._diff or 0))),
                          ("В историю", self._remember), ("Сброс", self._reset)):
            b = Gtk.Button(label=label)
            b.add_css_class("pill")
            b.connect("clicked", lambda _b, f=cb: f())
            btns.append(b)
        box.append(btns)
        frame = Gtk.Frame(child=box)
        frame.add_css_class("card")
        res_group.add(frame)
        page.add(res_group)

        self.hist_group = Adw.PreferencesGroup(title="История")
        clear = Gtk.Button(label="Очистить", valign=Gtk.Align.CENTER)
        clear.add_css_class("flat")
        clear.connect("clicked", lambda *_: self._clear_history())
        self.hist_group.set_header_suffix(clear)
        page.add(self.hist_group)
        self._hist_rows: list[Gtk.Widget] = []
        self._diff: int | None = 0
        tv.set_content(page)
        self.append(tv)
        self._render_history()

    def _parse(self, row: Adw.EntryRow) -> int | None:
        try:
            return subs.parse_time(row.get_text().strip().replace(" ", "")) if row.get_text().strip() else 0
        except ValueError:
            return None

    def _calc(self) -> None:
        a, b = self._parse(self.before), self._parse(self.after)
        for row, val in ((self.before, a), (self.after, b)):
            if val is None:
                row.add_css_class("error")
            else:
                row.remove_css_class("error")
        if a is None or b is None:
            self._diff = None
            self.result.set_label("—")
            self.result_ms.set_label("неверный формат времени")
            return
        d = b - a
        self._diff = d
        self.result.set_label(("+" if d >= 0 else "") + subs.fmt_human_time(d))
        self.result_ms.set_label(f"{d:+d} мс · {'вперёд (задержка)' if d >= 0 else 'назад'}")

    def _paste(self, row: Adw.EntryRow) -> None:
        def done(c, res) -> None:
            try:
                text = c.read_text_finish(res)
            except GLib.Error:
                return
            if text:
                row.set_text(text.strip().split()[0])

        self.get_clipboard().read_text_async(None, done)

    def _swap(self) -> None:
        a, b = self.before.get_text(), self.after.get_text()
        self.before.set_text(b)
        self.after.set_text(a)

    def _reset(self) -> None:
        self.before.set_text("0:00:00.000")
        self.after.set_text("0:00:00.000")

    def _remember(self) -> None:
        if self._diff is None:
            return
        hist = list(app_settings.get("timing_history") or [])
        hist.insert(0, {"before": self.before.get_text(), "after": self.after.get_text(), "diff": self._diff})
        app_settings.set("timing_history", hist[:30])
        self._render_history()

    def _clear_history(self) -> None:
        app_settings.set("timing_history", [])
        self._render_history()

    def _render_history(self) -> None:
        for r in self._hist_rows:
            self.hist_group.remove(r)
        self._hist_rows.clear()
        hist = app_settings.get("timing_history") or []
        if not hist:
            row = Adw.ActionRow(title="Пока пусто", subtitle="Нажмите «В историю», чтобы сохранить расчёт")
            self.hist_group.add(row)
            self._hist_rows.append(row)
            return
        for h in hist:
            d = int(h.get("diff", 0))
            row = Adw.ActionRow(title=("+" if d >= 0 else "") + subs.fmt_human_time(d) + f"  ({d:+d} мс)",
                                subtitle=f"{h.get('before')} → {h.get('after')}", use_markup=False)
            row.set_activatable(True)
            row.connect("activated", lambda _r, hh=h: (self.before.set_text(hh.get("before", "")),
                                                       self.after.set_text(hh.get("after", ""))))
            row.add_suffix(util.icon_button("edit-copy-symbolic", "Копировать мс",
                                            lambda dd=d: util.copy_text(self, str(dd))))
            self.hist_group.add(row)
            self._hist_rows.append(row)


# --- Журнал --------------------------------------------------------------------
class LogPage(Gtk.Box):
    COLORS = {"DEBUG": "#8a8a8a", "INFO": None, "SUCCESS": "#28CAC6", "WARNING": "#F5A623", "ERROR": "#EB6E4D"}

    def __init__(self) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        tv = Adw.ToolbarView()
        tv.set_vexpand(True)
        header = _plain_header("Журнал", "События и ошибки")
        self.level = Gtk.DropDown(model=Gtk.StringList.new(["Все", "Инфо и выше", "Предупреждения", "Ошибки"]),
                                  selected=1, tooltip_text="Уровень")
        self.level.connect("notify::selected", lambda *_: self._reload())
        header.pack_start(self.level)
        for icon, tip, cb in (("edit-clear-all-symbolic", "Очистить", self._clear),
                              ("edit-copy-symbolic", "Копировать всё", self._copy),
                              ("document-open-symbolic", "Открыть файл журнала",
                               lambda: desktop.open_path(str(paths.state_dir() / "hktools.log")))):
            header.pack_end(util.icon_button(icon, tip, cb, flat=False))
        tv.add_top_bar(header)
        self.buffer = Gtk.TextBuffer()
        for level, color in self.COLORS.items():
            tag = self.buffer.create_tag(level)
            if color:
                tag.set_property("foreground", color)
        self.buffer.create_tag("time", foreground="#8a8a8a")
        self.buffer.create_tag("source", weight=700)
        self.view = Gtk.TextView(buffer=self.buffer, editable=False, monospace=True, wrap_mode=Gtk.WrapMode.WORD_CHAR,
                                 left_margin=12, right_margin=12, top_margin=8, bottom_margin=8, cursor_visible=False)
        scr = Gtk.ScrolledWindow(child=self.view, vexpand=True)
        scr.add_css_class("kt-log")
        self.scroller = scr
        tv.set_content(scr)
        self.append(tv)
        self._reload()
        log.add_listener(self._on_entry)

    def _min_level(self) -> int:
        return [0, 1, 3, 4][self.level.get_selected()]

    def _accept(self, e: LogEntry) -> bool:
        order = {"DEBUG": 0, "INFO": 1, "SUCCESS": 2, "WARNING": 3, "ERROR": 4}
        lvl = order.get(e.level, 1)
        if self._min_level() == 1:
            return lvl >= 1
        return lvl >= self._min_level()

    def _append(self, e: LogEntry) -> None:
        if not self._accept(e):
            return
        end = self.buffer.get_end_iter()
        self.buffer.insert_with_tags_by_name(end, f"{e.time_str} ", "time")
        self.buffer.insert_with_tags_by_name(self.buffer.get_end_iter(), f"[{e.source}] ", "source")
        self.buffer.insert_with_tags_by_name(self.buffer.get_end_iter(), e.message + "\n", e.level)
        if e.details and e.level in ("ERROR", "WARNING", "DEBUG") and self._min_level() == 0:
            self.buffer.insert_with_tags_by_name(self.buffer.get_end_iter(), e.details + "\n", "time")

    def _on_entry(self, e: LogEntry) -> None:
        def add() -> None:
            adj = self.scroller.get_vadjustment()
            at_bottom = adj.get_value() >= adj.get_upper() - adj.get_page_size() - 30
            self._append(e)
            if self.buffer.get_line_count() > 6000:
                start = self.buffer.get_start_iter()
                cut = self.buffer.get_iter_at_line(1000)[1] if isinstance(self.buffer.get_iter_at_line(1000), tuple) \
                    else self.buffer.get_iter_at_line(1000)
                self.buffer.delete(start, cut)
            if at_bottom:
                GLib.idle_add(lambda: (adj.set_value(adj.get_upper()), False)[1])

        util.idle(add)

    def _reload(self) -> None:
        self.buffer.set_text("")
        for e in log.entries():
            self._append(e)

    def _clear(self) -> None:
        log.clear()
        self.buffer.set_text("")

    def _copy(self) -> None:
        start, end = self.buffer.get_bounds()
        util.copy_text(self, self.buffer.get_text(start, end, False))


# --- Зависимости --------------------------------------------------------------
class DepsPage(Gtk.Box):
    def __init__(self, on_changed: Callable[[], None]) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.on_changed = on_changed
        tv = Adw.ToolbarView()
        tv.set_vexpand(True)
        header = _plain_header("Зависимости", "Внешние утилиты и оборудование")
        refresh = util.icon_button("view-refresh-symbolic", "Перепроверить", self.refresh, flat=False)
        header.pack_end(refresh)
        self.install_all = Gtk.Button(label="Установить недостающее")
        self.install_all.add_css_class("suggested-action")
        self.install_all.connect("clicked", lambda *_: self._install(self._missing))
        header.pack_start(self.install_all)
        tv.add_top_bar(header)
        self.page = Adw.PreferencesPage()
        tv.set_content(self.page)
        self.append(tv)
        self._groups: list[Adw.PreferencesGroup] = []
        self._missing: list[str] = []
        self.refresh()

    def refresh(self) -> None:
        for g in self._groups:
            self.page.remove(g)
        self._groups.clear()
        loading = Adw.PreferencesGroup(title="Проверка…")
        self.page.add(loading)
        self._groups.append(loading)

        def work():
            statuses = [tools.probe_version(k) for k in tools.TOOLS]
            caps = hw.get_caps(refresh=True)
            return statuses, caps

        util.run_bg(work, self._render)

    def _render(self, res, err) -> None:
        for g in self._groups:
            self.page.remove(g)
        self._groups.clear()
        if err or res is None:
            g = Adw.PreferencesGroup(title="Ошибка проверки", description=str(err))
            self.page.add(g)
            self._groups.append(g)
            return
        statuses, caps = res
        self._missing = [s.tool.key for s in statuses if not s.found]
        req = Adw.PreferencesGroup(title="Утилиты", description="Пакеты Arch Linux / CachyOS / Ryoku. AUR-пакеты ставятся "
                                                                 "через paru или yay, если они установлены.")
        for st in statuses:
            t = st.tool
            row = Adw.ActionRow(title=t.title, use_markup=False,
                                subtitle=(st.version + " · " if st.found else "не найдено · ") + t.purpose)
            row.set_subtitle_lines(2)
            icon = Gtk.Image(icon_name="emblem-ok-symbolic" if st.found else
                             ("dialog-error-symbolic" if t.required else "dialog-warning-symbolic"))
            icon.add_css_class("success" if st.found else ("error" if t.required else "warning"))
            row.add_prefix(icon)
            pkg = Gtk.Label(label=t.package + (" (AUR)" if t.aur else ""))
            pkg.add_css_class("dim-label")
            pkg.add_css_class("caption")
            row.add_suffix(pkg)
            if not st.found:
                b = Gtk.Button(label="Установить", valign=Gtk.Align.CENTER)
                b.add_css_class("pill")
                b.connect("clicked", lambda _b, k=t.key: self._install([k]))
                row.add_suffix(b)
                if t.aur and t.aur_variants:
                    row.set_tooltip_text("Варианты сборки: " + ", ".join(t.aur_variants))
            else:
                row.set_tooltip_text(st.path)
            req.add(row)
        self.page.add(req)
        self._groups.append(req)
        self.install_all.set_visible(bool(self._missing))

        hwg = Adw.PreferencesGroup(title="Оборудование", description="Аппаратное кодирование проверяется тестовым "
                                                                       "кодированием через ffmpeg")
        rows = [
            ("Видеокарты", ", ".join(caps.gpus) or "render-устройства не найдены", None),
            ("NVENC (NVIDIA)", ", ".join(k for k, v in caps.nvenc.items() if v) or
             "недоступно — нужен драйвер nvidia и ffmpeg с nvenc", caps.nvenc_ok),
            (f"VAAPI ({caps.vaapi_device or '—'})", ", ".join(k for k, v in caps.vaapi.items() if v) or
             "недоступно — AMD: mesa; Intel: intel-media-driver", caps.vaapi_ok),
            ("Энкодеры CPU", ", ".join(e for e in ("libx265", "libx264", "libsvtav1") if caps.has_encoder(e)) or "—", None),
            ("Фильтр rubberband", "есть" if "rubberband" in caps.filters else "нет (будет использован atempo)", None),
        ]
        for title, sub, ok in rows:
            row = Adw.ActionRow(title=title, subtitle=sub, use_markup=False)
            if ok is not None:
                ic = Gtk.Image(icon_name="emblem-ok-symbolic" if ok else "window-close-symbolic")
                ic.add_css_class("success" if ok else "dim-label")
                row.add_prefix(ic)
            hwg.add(row)
        self.page.add(hwg)
        self._groups.append(hwg)
        self.on_changed()

    def _install(self, keys: list[str]) -> None:
        if not keys:
            return
        cmd = tools.install_command(keys)
        if not tools.run_in_terminal(cmd):
            util.copy_text(self, cmd)
            util.confirm(self, "Терминал не найден", f"Команда скопирована в буфер обмена:\n\n{cmd}", lambda: None, "OK")
        else:
            log.info(f"Запущена установка: {cmd}", "Зависимости")


def about(parent: Gtk.Window) -> None:
    dlg = Adw.AboutDialog(application_name=APP_NAME, version=VERSION, application_icon="io.github.zerosensee.HKTools",
                          developer_name="zerosensee",
                          comments="Пакетная обработка, транскодирование и ремуксинг медиа для Linux "
                                   "(GTK4 + libadwaita, ffmpeg, mkvtoolnix, yt-dlp, whisper.cpp).\n\n"
                                   f"Независимый форк {UPSTREAM_NAME} (автор krnzhnr) — не официальная версия.",
                          website=REPO_URL, license_type=Gtk.License.MIT_X11,
                          issue_url=REPO_URL + "/issues")
    dlg.add_credit_section("Оригинальный проект", [f"{UPSTREAM_NAME} (krnzhnr) {UPSTREAM_URL}"])
    dlg.add_legal_section(UPSTREAM_NAME, "Copyright © 2026 krnzhnr", Gtk.License.MIT_X11, None)
    dlg.present(parent)
