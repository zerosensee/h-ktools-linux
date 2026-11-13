"""Дымовой тест GUI: открывает страницы и сохраняет скриншоты (запуск под Xvfb/Wayland).

    xvfb-run -s "-screen 0 1400x900x24" python3 tests/gui_smoke.py /tmp/shots
"""

from __future__ import annotations

import faulthandler
import os
import sys
import traceback

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import gi  # noqa: E402

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import GLib, Gtk  # noqa: E402

from hktools.app import KToolsApp  # noqa: E402

faulthandler.dump_traceback_later(int(os.environ.get("KT_HANG", "0")) or 10**6, exit=True)
OUT = sys.argv[1] if len(sys.argv) > 1 else "/tmp/kt-shots"
SAMPLE = os.environ.get("KT_SAMPLE", "/tmp/kt/sample.mkv")
os.makedirs(OUT, exist_ok=True)
errors: list[str] = []


def shot(widget: Gtk.Widget, name: str) -> None:
    w, h = widget.get_width(), widget.get_height()
    if w <= 0 or h <= 0:
        errors.append(f"{name}: нулевой размер")
        return
    paintable = Gtk.WidgetPaintable.new(widget)
    snap = Gtk.Snapshot()
    paintable.snapshot(snap, w, h)
    node = snap.to_node()
    if node is None:
        errors.append(f"{name}: пустой снимок")
        return
    renderer = widget.get_native().get_renderer()
    tex = renderer.render_texture(node, None)
    tex.save_to_png(os.path.join(OUT, f"{name}.png"))
    print("saved", name, w, h)


def main() -> int:
    app = KToolsApp()
    steps: list = []

    def step(fn, delay=700):
        steps.append((fn, delay))

    def run_steps():
        if not steps:
            app.quit()
            return False
        fn, delay = steps.pop(0)
        try:
            fn()
        except Exception:  # noqa: BLE001
            errors.append(traceback.format_exc())
        GLib.timeout_add(delay, run_steps)
        return False

    def on_activate(_a):
        win = app.window
        win.set_default_size(1360, 860)

        step(lambda: shot(win, "01_home"), 2500)
        for pid in ("video-encode", "audio-encode", "mkv-assembly", "track-extractor", "subtitles-convert",
                    "speech-recognition", "media-downloader", "audio-transplant", "timing-calculator", "deps", "log"):
            step(lambda p=pid: win.show_page(p), 900)
            step(lambda p=pid: shot(win, f"page_{p}"), 300)
        if os.path.exists(SAMPLE):
            def add_sample():
                win.show_page("track-extractor")
                win.current_panel().add_paths([SAMPLE])
            step(add_sample, 2500)
            step(lambda: shot(win, "q_track_extractor_queue"), 300)

            def open_tracks():
                p = win.current_panel()
                p.open_tracks(p.items[0])
            step(open_tracks, 1200)
            step(lambda: shot(win, "q_track_dialog"), 300)

            def close_dialog():
                d = win.get_visible_dialog() if hasattr(win, "get_visible_dialog") else None
                if d:
                    d.close()
            step(close_dialog, 600)

            def run_extract():
                p = win.current_panel()
                p.out_dir = "/tmp/kt/out/gui_extract"
                p.start()
            step(run_extract, 4000)
            step(lambda: shot(win, "q_track_extractor_done"), 300)

            def video_queue():
                win.show_page("video-encode")
                win.current_panel().add_paths([SAMPLE])
            step(video_queue, 2000)
            step(lambda: shot(win, "q_video_queue"), 300)

            def bitrate():
                win.show_page("bitrate-viewer")
                p = win.current_panel()
                p.add_paths([SAMPLE])
            step(bitrate, 2000)

            def bitrate_run():
                p = win.current_panel()
                p.out_dir = "/tmp/kt/out/gui_br"
                p.start()
            step(bitrate_run, 4000)

            def shot_chart():
                for w in Gtk.Window.list_toplevels():
                    if w is not win and w.get_visible() and w.get_title() and "Битрейт" in w.get_title():
                        shot(w, "w_bitrate_chart")
                        w.close()
            step(shot_chart, 500)

            def subs_preview():
                win.show_page("subtitles-convert")
                p = win.current_panel()
                p.add_paths(["/tmp/kt/signs.ass"])
                p.open_sub_preview(p.items[0])
            step(subs_preview, 1500)
            step(lambda: shot(win, "q_sub_preview"), 300)
            step(close_dialog, 500)

            def transplant():
                win.show_page("audio-transplant")
                p = win.current_panel()
                p.form.set_value("transplant_source", "/tmp/kt/sample.dub.flac")
                p.add_paths([SAMPLE])
                p.open_waveform()
            step(transplant, 2500)

            def wave_load():
                for w in Gtk.Window.list_toplevels():
                    if w is not win and w.get_visible() and "осциллограмм" in (w.get_title() or ""):
                        w._load()
            step(wave_load, 3500)

            def wave_shot():
                for w in Gtk.Window.list_toplevels():
                    if w is not win and w.get_visible() and "осциллограмм" in (w.get_title() or ""):
                        print("wave", w.get_width(), w.get_height(), w.ref_env is not None, w.don_env is not None,
                              w.hint.get_label())
                        w._set_shift(250)
                        w.span = 4000
                        w.area.queue_draw()
            step(wave_shot, 600)

            def wave_shot2():
                for w in Gtk.Window.list_toplevels():
                    if w is not win and w.get_visible() and "осциллограмм" in (w.get_title() or ""):
                        shot(w, "w_waveform")
            step(wave_shot2, 600)

            def wave_close():
                for w in Gtk.Window.list_toplevels():
                    if w is not win and "осциллограмм" in (w.get_title() or ""):
                        w.close()
            step(wave_close, 600)

        if os.path.exists(SAMPLE):
            def mkv():
                win.show_page("mkv-assembly")
                p = win.current_panel()
                p.add_paths([SAMPLE, "/tmp/kt/sample.dub.flac", "/tmp/kt/sample.signs.ass", "/tmp/kt/sample.ass"])
            step(mkv, 1500)
            step(lambda: shot(win, "q_mkv_queue"), 300)

            def mkv_run():
                p = win.current_panel()
                p.out_dir = "/tmp/kt/out/gui_mkv"
                p.start()
            step(mkv_run, 3500)
            step(lambda: shot(win, "q_mkv_done"), 300)

            def repl():
                win.show_page("stream-replacement")
                p = win.current_panel()
                p.add_paths([SAMPLE])
            step(repl, 2000)

            def repl_dialog():
                p = win.current_panel()
                p.open_tracks(p.items[0])
            step(repl_dialog, 1200)
            step(lambda: shot(win, "q_replace_dialog"), 300)
            step(close_dialog, 500)

            def dl():
                win.show_page("media-downloader")
                p = win.current_panel()
                p.add_urls(["http://127.0.0.1:8765/sample.mp4"])
            step(dl, 3500)
            step(lambda: shot(win, "q_downloader"), 300)

        def prefs():
            win.show_preferences()
        step(prefs, 1200)
        step(lambda: shot(win, "z_prefs"), 300)
        GLib.timeout_add(800, run_steps)

    app.connect_after("activate", on_activate)
    app.run([sys.argv[0]])
    for e in errors:
        print("ERROR:", e)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
