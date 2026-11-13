"""Конвертация и сдвиг субтитров."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, replace
from typing import Any

from ..core.fields import CHECK, COMBO, KEYWORDS, TEXT, Field, active_words
from ..core.script import Context, QueueItem, Result, Script
from ..media import subtitles as subs
from .common import delete_original_field

SUB_IN = {".ass", ".ssa", ".srt", ".vtt"}
TARGET_EXT = {"WebVTT": ".vtt", "SRT": ".srt", "ASS": ".ass"}


@dataclass
class LineState:
    index: int
    dialogue: subs.Dialogue
    text: str          # текст после фильтров
    deleted: bool
    reason: str        # empty | actor | style | effect | regex | caps-empty | manual-ex | manual-in | empty


def apply_filters(doc: subs.SubDoc, values: dict[str, Any], manual_in: set[int] | None = None,
                  manual_ex: set[int] | None = None) -> list[LineState]:
    """Применить фильтры конвертации субтитров к документу (используется и в предпросмотре)."""
    manual_in = manual_in or set()
    manual_ex = manual_ex or set()
    ex_actors = set(active_words(values.get("exclude_actors")))
    ex_styles = set(active_words(values.get("exclude_styles")))
    ex_effects = set(active_words(values.get("exclude_effects")))
    strip_fmt = bool(values.get("strip_formatting", True))
    strip_caps = bool(values.get("strip_caps", False))
    patterns: list[tuple[re.Pattern, bool]] = []
    for p in values.get("text_patterns") or []:
        if isinstance(p, dict) and p.get("active") and p.get("word"):
            try:
                patterns.append((re.compile(str(p["word"])), bool(p.get("only_part"))))
            except re.error:
                continue
    out: list[LineState] = []
    for i, d in enumerate(doc.dialogues):
        if d.comment:
            continue
        if not subs.strip_tags(d.text).strip():
            out.append(LineState(i, d, d.text, True, "empty"))
            continue
        reason = ""
        if d.actor and d.actor in ex_actors:
            reason = "actor"
        elif d.style and d.style in ex_styles:
            reason = "style"
        elif d.effect and d.effect in ex_effects:
            reason = "effect"
        text = d.text
        regex_deleted = False
        for rx, only_part in patterns:
            if rx.search(text):
                if only_part:
                    text = rx.sub("", text)
                else:
                    regex_deleted = True
                    break
        if strip_caps:
            text = subs.strip_caps(text)
        if strip_fmt:
            text = subs.TAG_RE.sub("", text)
        empty_after = not subs.strip_tags(text).strip()
        if i in manual_in and empty_after:
            text, empty_after = d.text, False
        if i in manual_ex:
            out.append(LineState(i, d, text, True, "manual-ex"))
        elif i in manual_in:
            out.append(LineState(i, d, text, False, "manual-in"))
        elif regex_deleted:
            out.append(LineState(i, d, text, True, "regex"))
        elif reason:
            out.append(LineState(i, d, text, True, reason))
        elif empty_after:
            out.append(LineState(i, d, text, True, "caps-empty"))
        else:
            out.append(LineState(i, d, text, False, ""))
    return out


class SubtitlesConvert(Script):
    id = "subtitles-convert"
    name = "Конвертация субтитров"
    description = "ASS/SSA/SRT/VTT → WebVTT, SRT или ASS с фильтром по актёрам и стилям, очисткой тегов и предпросмотром"
    category = "Субтитры"
    icon = "font-x-generic-symbolic"
    extensions = SUB_IN
    deps = ()

    def fields(self, values=None):
        return [
            Field("target_format", "Целевой формат", COMBO, "WebVTT", "Экспорт", options=list(TARGET_EXT)),
            Field("strip_formatting", "Удалять теги оформления", CHECK, True, "Очистка",
                  comment="Полная очистка override-тегов {\\…}"),
            Field("keep_styles", "Сохранять курсив/жирный (SRT/VTT)", CHECK, False, "Очистка",
                  comment="Переносит \\i1, \\b1 и курсив стилей ASS в <i>/<b>"),
            Field("strip_caps", "Удалять реплики в верхнем регистре (КАПС)", CHECK, False, "Очистка"),
            Field("text_patterns", "Регулярные выражения", KEYWORDS, [], "Очистка",
                  comment="Строки с совпадением удаляются; с флагом — удаляется только совпадение",
                  extra_flag=("only_part", "только совпадение")),
            Field("exclude_actors", "Исключённые актёры", KEYWORDS, [], "Фильтр",
                  comment="Удобнее отмечать в окне «Предпросмотр и фильтр»"),
            Field("exclude_styles", "Исключённые стили", KEYWORDS, [], "Фильтр"),
            Field("exclude_effects", "Исключённые эффекты", KEYWORDS, [], "Фильтр"),
            delete_original_field(),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        target = str(ctx.get("target_format", "WebVTT"))
        ext = TARGET_EXT.get(target, ".vtt")
        out = ctx.output_path(item.path, ext=ext)
        if skip := ctx.skip_if_exists(out):
            return skip
        doc = subs.load(item.path)
        if not doc.dialogues:
            return Result.skipped("⏭ В файле нет реплик")
        states = apply_filters(doc, ctx.values, set(item.extra.get("manual_in", [])),
                               set(item.extra.get("manual_ex", [])))
        kept = [replace(s.dialogue, text=s.text) for s in states if not s.deleted]
        removed = sum(1 for s in states if s.deleted and s.reason not in ("empty",))
        text = subs.render(doc, ext, kept, keep_styles=bool(ctx.get("keep_styles")))
        subs.write_text(out, text)
        result = Result.done(out, msg=f"✅ {target}: {os.path.basename(out)} (реплик: {len(kept)}, удалено: {removed})")
        ctx.finalize_source(item.path, out, result)
        ctx.progress(100, "Готово")
        return result


class SubtitleShift(Script):
    id = "subtitle-shift"
    name = "Сдвиг субтитров"
    description = "Сдвиг тайминга ASS, SSA, SRT, VTT на заданное время с сохранением оформления"
    category = "Субтитры"
    icon = "preferences-system-time-symbolic"
    extensions = SUB_IN
    deps = ()

    def fields(self, values=None):
        return [
            Field("ShiftMs", "Величина сдвига (мс или Ч:ММ:СС.сс)", TEXT, "1000", "Настройки сдвига"),
            Field("ShiftDirection", "Направление сдвига", COMBO, "Вперед", "Настройки сдвига",
                  options=["Вперед", "Назад"]),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        try:
            ms = subs.parse_shift(str(ctx.get("ShiftMs", "0")))
        except ValueError as exc:
            return Result.failed(f"❌ {exc}")
        if ctx.get("ShiftDirection") == "Назад":
            ms = -ms
        out = ctx.output_path(item.path)
        if skip := ctx.skip_if_exists(out):
            return skip
        raw = subs.read_text(item.path)
        kind = subs.detect_kind(item.path, raw)
        text, n = subs.shift_text(raw, kind, ms)
        subs.write_text(out, text)
        ctx.progress(100, "Готово")
        return Result.done(out, msg=f"✅ Сдвиг {ms:+d} мс ({n} реплик): {os.path.basename(out)}")
