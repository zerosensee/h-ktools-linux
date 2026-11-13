"""Командная строка: запуск инструментов без GUI, проверка зависимостей, модуль Waybar."""

from __future__ import annotations

import argparse
import json
import os
import signal
import sys
import time

from . import APP_NAME, VERSION, paths, tools
from .core import desktop
from .core.fields import COMBO, EXPANDER, KEYWORDS, walk
from .core.runner import BatchRunner
from .core.script import QueueItem
from .log import log

COMMANDS = {"list", "run", "options", "deps", "hw", "waybar", "status", "gui", "models"}

STATUS_ICON = {"done": "✓", "partial": "≈", "failed": "✗", "skipped": "⏭", "cancelled": "×"}


def _scripts():
    from . import scripts  # noqa: PLC0415

    return scripts


def cmd_list(_args) -> int:
    from .core.script import CATEGORIES  # noqa: PLC0415

    sc = _scripts().all_scripts()
    for cat in CATEGORIES:
        items = [s for s in sc if s.category == cat]
        if not items:
            continue
        print(f"\n\033[1m{cat}\033[0m")
        for s in items:
            missing = s.missing_deps()
            mark = "" if not missing else f"  \033[33m(нет: {', '.join(missing)})\033[0m"
            print(f"  {s.id:<22} {s.name}{mark}")
            print(f"  {'':<22} \033[2m{s.description}\033[0m")
    print("\nПример: hktools run audio-encode --set target_format=FLAC ~/Music/*.wav")
    return 0


def cmd_options(args) -> int:
    script = _scripts().get(args.tool)
    if not script:
        print(f"Неизвестный инструмент: {args.tool}", file=sys.stderr)
        return 2
    values = script.values()
    print(f"\033[1m{script.name}\033[0m ({script.id})\n")
    for f in walk(script.fields(values)):
        if f.type == EXPANDER and not f.toggle:
            continue
        cur = values.get(f.key)
        if f.type == KEYWORDS:
            cur = "|".join(i["word"] for i in (cur or []) if i.get("active"))
        line = f"  {f.key:<28} = {cur!s:<20} {f.label}"
        if f.type == COMBO and f.options:
            line += f"\n  {'':<28}   варианты: {', '.join(f.options)}"
        print(line)
    return 0


def _expand_inputs(paths_in: list[str], script) -> list[str]:
    out: list[str] = []
    for p in paths_in:
        if script.input_mode == "urls":
            out.append(p)
            continue
        p = os.path.expanduser(p)
        if os.path.isdir(p):
            for root, _dirs, files in os.walk(p):
                for f in sorted(files):
                    full = os.path.join(root, f)
                    if script.accepts(full):
                        out.append(full)
        elif os.path.exists(p):
            out.append(os.path.abspath(p))
        else:
            print(f"⚠ не найден: {p}", file=sys.stderr)
    return out


def cmd_run(args) -> int:
    script = _scripts().get(args.tool)
    if not script:
        print(f"Неизвестный инструмент: {args.tool}. Список: hktools list", file=sys.stderr)
        return 2
    if script.input_mode == "none":
        print("Этот инструмент работает только в графическом интерфейсе.", file=sys.stderr)
        return 2
    missing = script.missing_deps()
    if missing:
        print(f"Не хватает утилит: {', '.join(missing)}\nУстановка: {tools.install_command(missing)}", file=sys.stderr)
        return 3
    overrides = {}
    for kv in args.set or []:
        if "=" not in kv:
            print(f"Ожидается key=value: {kv}", file=sys.stderr)
            return 2
        k, v = kv.split("=", 1)
        overrides[k.strip()] = v
    values = script.values(overrides)
    if args.overwrite:
        from .config import settings  # noqa: PLC0415

        settings._data["general"]["overwrite_existing"] = True  # только для этого запуска
    inputs = _expand_inputs(args.inputs, script)
    if not inputs:
        print("Нет подходящих файлов.", file=sys.stderr)
        return 1
    items = [QueueItem(p) for p in inputs]
    if script.track_selection:
        from .media.probe import probe  # noqa: PLC0415
        from .scripts.streams import default_selection  # noqa: PLC0415

        for it in items:
            try:
                it.info = probe(it.path)
            except RuntimeError as exc:
                print(f"⚠ {it.name}: {exc}", file=sys.stderr)
                continue
            if args.tracks:
                wanted = _parse_tracks(args.tracks, it.info)
                it.extra["selected"] = wanted
            else:
                it.extra["selected"] = default_selection(script, it.info)
            if script.track_selection == "replace" and args.replace:
                reps = {}
                for spec in args.replace:
                    idx, _, path = spec.partition("=")
                    reps[str(int(idx))] = os.path.abspath(os.path.expanduser(path))
                it.extra["replacements"] = reps
    if script.id == "media-downloader":
        for it in items:
            if args.format:
                it.extra["format_arg"] = args.format
            it.extra["audio_only"] = bool(args.audio_only)
            if args.audio_only and not args.format:
                it.extra["format_arg"] = "ba/b"

    tty = sys.stdout.isatty()
    last_line = [""]

    def on_item(it: QueueItem) -> None:
        if it.status == "running":
            line = f"[{it.progress:5.1f}%] {it.name}: {it.message}"
            if tty:
                sys.stdout.write("\r\033[K" + line[: os.get_terminal_size().columns - 1])
                sys.stdout.flush()
            last_line[0] = line

    def on_result(it: QueueItem, res) -> None:
        if tty:
            sys.stdout.write("\r\033[K")
        icon = STATUS_ICON.get(res.status, "?")
        print(f"{icon} {it.name}")
        for m in res.messages:
            for ln in str(m).splitlines():
                print(f"    {ln}")

    runner = BatchRunner(script, items, values, args.out, on_item=on_item, on_result=on_result,
                         parallel=args.parallel)
    signal.signal(signal.SIGINT, lambda *_: (print("\nОтмена…"), runner.cancel()))
    summary = runner.run_blocking()
    print(f"\nГотово за {summary.elapsed:.1f} с: успешно {summary.ok}, ошибок {summary.failed}, "
          f"пропущено {summary.skipped}")
    if args.notify:
        desktop.notify(f"{APP_NAME}: {script.name}", f"Успешно {summary.ok}, ошибок {summary.failed}")
    return 0 if summary.failed == 0 and not summary.cancelled else 1


def _parse_tracks(spec: str, info) -> list[int]:
    """'all', 'v,a', 'a:0,s:1', '0,2,5' → индексы потоков."""
    out: list[int] = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if part == "all":
            out += [t.index for t in info.media_tracks] + [t.index for t in info.fonts]
        elif part in ("v", "a", "s"):
            kind = {"v": "video", "a": "audio", "s": "subtitle"}[part]
            out += [t.index for t in info.of_type(kind)]
        elif part in ("t", "fonts"):
            out += [t.index for t in info.fonts]
        elif ":" in part:
            k, _, n = part.partition(":")
            kind = {"v": "video", "a": "audio", "s": "subtitle"}.get(k)
            lst = info.of_type(kind) if kind else []
            if n.isdigit() and int(n) < len(lst):
                out.append(lst[int(n)].index)
        elif part.isdigit():
            out.append(int(part))
    return sorted(set(out))


def cmd_deps(args) -> int:
    missing = []
    for key, tool in tools.TOOLS.items():
        st = tools.probe_version(key)
        mark = "\033[32m✓\033[0m" if st.found else ("\033[31m✗\033[0m" if tool.required else "\033[33m–\033[0m")
        pkg = f"{tool.package}{' (AUR)' if tool.aur else ''}"
        print(f" {mark} {tool.title:<34} {st.version or 'не найден':<28} пакет: {pkg}")
        if not st.found:
            missing.append(key)
    if missing:
        cmd = tools.install_command(missing)
        print(f"\nУстановить недостающее:\n  {cmd}")
        if args.install:
            return os.system(cmd) >> 8
    return 0


def cmd_hw(args) -> int:
    from . import hw  # noqa: PLC0415

    caps = hw.get_caps(refresh=args.refresh)
    print(f"FFmpeg: {caps.ffmpeg_version}")
    print(f"GPU: {', '.join(caps.gpus) or 'не найдено render-устройств'}")
    print(f"NVENC: {', '.join(k for k, v in caps.nvenc.items() if v) or 'недоступен'}")
    print(f"VAAPI ({caps.vaapi_device or '—'}): {', '.join(k for k, v in caps.vaapi.items() if v) or 'недоступен'}")
    print(f"CPU: {', '.join(e for e in ('libx265', 'libx264', 'libsvtav1') if caps.has_encoder(e))}")
    return 0


def cmd_waybar(args) -> int:
    """Непрерывный вывод JSON для custom-модуля Waybar (return-type: json)."""
    target = paths.status_file()
    last = None
    idle = json.dumps({"text": "", "class": "idle", "tooltip": "hktools: нет задач"}, ensure_ascii=False)
    while True:
        try:
            raw = target.read_text(encoding="utf-8")
            data = json.loads(raw)
            if data.get("class") in ("done", "failed", "cancelled") and time.time() - data.get("ts", 0) > 20:
                raw = idle
            else:
                raw = json.dumps({k: data[k] for k in ("text", "alt", "class", "tooltip", "percentage") if k in data},
                                 ensure_ascii=False)
        except (OSError, ValueError):
            raw = idle
        if raw != last:
            print(raw, flush=True)
            last = raw
        if args.once:
            return 0
        time.sleep(1.0)


def cmd_status(_args) -> int:
    try:
        print(paths.status_file().read_text(encoding="utf-8"))
    except OSError:
        print("{}")
    return 0


def cmd_models(args) -> int:
    import threading  # noqa: PLC0415

    from .scripts import speech  # noqa: PLC0415

    if not args.download:
        have = set(speech.downloaded_models())
        print(f"Папка моделей: {speech.models_dir()}")
        for m in speech.MODELS:
            mark = "✓" if m.key in have else " "
            print(f" [{mark}] {m.key:<22} ~{m.size_mb:>5.0f} МБ  {m.description}")
        return 0
    ev = threading.Event()

    def prog(pct: float, msg: str) -> None:
        sys.stdout.write(f"\r{args.download}: {pct:5.1f}% {msg}   ")
        sys.stdout.flush()

    try:
        path = speech.download_model(args.download, prog, ev)
    except KeyboardInterrupt:
        ev.set()
        return 130
    print(f"\nСохранено: {path}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="hktools", description=f"{APP_NAME} {VERSION} — пакетная обработка медиа")
    p.add_argument("--version", action="version", version=f"{APP_NAME} {VERSION}")
    sub = p.add_subparsers(dest="cmd")
    g = sub.add_parser("gui", help="Запустить графический интерфейс (по умолчанию)")
    g.add_argument("files", nargs="*")
    g.add_argument("--tool", "-t", help="сразу открыть инструмент (id из hktools list)")
    sub.add_parser("list", help="Список инструментов")
    o = sub.add_parser("options", help="Настройки инструмента и их значения")
    o.add_argument("tool")
    r = sub.add_parser("run", help="Запустить инструмент без GUI")
    r.add_argument("tool")
    r.add_argument("inputs", nargs="+", help="файлы, папки или ссылки")
    r.add_argument("--set", "-s", action="append", metavar="KEY=VALUE", help="переопределить настройку")
    r.add_argument("--out", "-o", help="папка результатов")
    r.add_argument("--parallel", "-j", type=int, default=None, help="число параллельных задач")
    r.add_argument("--overwrite", action="store_true", help="перезаписывать существующие результаты")
    r.add_argument("--tracks", help="выбор дорожек: all | v,a,s | a:0,s:1 | 0,2,5")
    r.add_argument("--replace", action="append", metavar="IDX=FILE", help="замена потока (stream-replacement)")
    r.add_argument("--format", help="формат yt-dlp (-f), для media-downloader")
    r.add_argument("--audio-only", action="store_true", help="только аудио (media-downloader)")
    r.add_argument("--notify", action="store_true", help="уведомление по завершении")
    d = sub.add_parser("deps", help="Проверить внешние утилиты")
    d.add_argument("--install", action="store_true", help="установить недостающее (pacman/AUR)")
    h = sub.add_parser("hw", help="Аппаратное кодирование (NVENC/VAAPI)")
    h.add_argument("--refresh", action="store_true")
    w = sub.add_parser("waybar", help="JSON-поток для custom-модуля Waybar")
    w.add_argument("--once", action="store_true")
    sub.add_parser("status", help="Текущий статус обработки (JSON)")
    m = sub.add_parser("models", help="Модели whisper.cpp")
    m.add_argument("--download", metavar="MODEL", help="скачать модель (например large-v3-turbo)")
    return p


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    parser = build_parser()
    args = parser.parse_args(argv)
    handlers = {"list": cmd_list, "options": cmd_options, "run": cmd_run, "deps": cmd_deps, "hw": cmd_hw,
                "waybar": cmd_waybar, "status": cmd_status, "models": cmd_models}
    if args.cmd in handlers:
        try:
            return handlers[args.cmd](args)
        except KeyboardInterrupt:
            return 130
    from .app import run_gui  # noqa: PLC0415

    return run_gui(getattr(args, "files", []) or [], getattr(args, "tool", None))


if __name__ == "__main__":
    log.debug("CLI")
    sys.exit(main())
