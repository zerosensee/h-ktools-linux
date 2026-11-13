"""Разбор и запись субтитров ASS/SSA, SRT, WebVTT без внешних зависимостей."""

from __future__ import annotations

import html
import re
from dataclasses import dataclass, field, replace

TAG_RE = re.compile(r"\{[^}]*\}")
HTML_TAG_RE = re.compile(r"</?[a-zA-Z][^>]*>")
VTT_TS_TAG_RE = re.compile(r"<\d{1,2}:\d{2}(?::\d{2})?[.,]\d{3}>")
NL_SPLIT_RE = re.compile(r"(\\N|\\n)")
DUP_NL_RE = re.compile(r"(\\N|\\n){2,}")
TIME_RE = re.compile(r"(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?")


def read_text(path: str) -> str:
    """Прочитать текст: UTF-8 (с BOM/без), UTF-16, затем CP1251 как в Windows-версии."""
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8", "replace")
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16", "replace")
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("cp1251", "replace")


# --- время ---------------------------------------------------------------
def parse_time(text: str) -> int:
    """'0:01:02.34', '00:01:02,345', '01:02.345', '62' → миллисекунды."""
    text = text.strip()
    if re.fullmatch(r"-?\d+", text):
        return int(text)
    m = TIME_RE.fullmatch(text)
    if not m:
        raise ValueError(f"Неверный формат времени: {text}")
    h, mnt, s, frac = m.groups()
    ms = 0
    if frac:
        ms = int(frac.ljust(3, "0")[:3])
    return ((int(h or 0) * 60 + int(mnt)) * 60 + int(s)) * 1000 + ms


def parse_shift(text: str) -> int:
    """Сдвиг: целое число миллисекунд или Ч:ММ:СС.сс (как в Windows-версии)."""
    text = (text or "").strip().replace(" ", "")
    if not text:
        return 0
    sign = -1 if text.startswith("-") else 1
    text = text.lstrip("+-")
    if re.fullmatch(r"\d+", text):
        return sign * int(text)
    if re.fullmatch(r"\d+[.,]\d+", text):
        return sign * int(round(float(text.replace(",", ".")) * 1000))
    return sign * parse_time(text)


def fmt_ass_time(ms: int) -> str:
    ms = max(0, int(ms))
    cs = (ms + 5) // 10  # округление половин вверх (не банковское)
    h, rem = divmod(cs, 360000)
    m, rem = divmod(rem, 6000)
    s, cs = divmod(rem, 100)
    return f"{h:d}:{m:02d}:{s:02d}.{cs:02d}"


def fmt_srt_time(ms: int, sep: str = ",") -> str:
    ms = max(0, int(ms))
    h, rem = divmod(ms, 3_600_000)
    m, rem = divmod(rem, 60_000)
    s, ms = divmod(rem, 1000)
    return f"{h:02d}:{m:02d}:{s:02d}{sep}{ms:03d}"


def fmt_human_time(ms: int) -> str:
    """Формат калькулятора таймингов: Ч:ММ:СС.ммм"""
    sign = "-" if ms < 0 else ""
    ms = abs(int(ms))
    h, rem = divmod(ms, 3_600_000)
    m, rem = divmod(rem, 60_000)
    s, ms = divmod(rem, 1000)
    return f"{sign}{h:d}:{m:02d}:{s:02d}.{ms:03d}"


# --- модель --------------------------------------------------------------
@dataclass
class Dialogue:
    start: int
    end: int
    text: str
    style: str = "Default"
    actor: str = ""
    effect: str = ""
    layer: str = "0"
    margin_l: str = "0"
    margin_r: str = "0"
    margin_v: str = "0"
    comment: bool = False

    @property
    def plain(self) -> str:
        return strip_tags(self.text)


@dataclass
class StyleInfo:
    name: str
    bold: bool = False
    italic: bool = False
    underline: bool = False


@dataclass
class SubDoc:
    kind: str                     # ass | srt | vtt
    dialogues: list[Dialogue] = field(default_factory=list)
    header: str = ""              # для ASS: всё до [Events]+Format (включительно)
    styles: dict[str, StyleInfo] = field(default_factory=dict)
    events_format: list[str] = field(default_factory=list)
    trailer: str = ""             # для ASS: секции после [Events] (Fonts/Graphics)

    @property
    def actors(self) -> list[str]:
        return sorted({d.actor for d in self.dialogues if d.actor})

    @property
    def style_names(self) -> list[str]:
        return sorted({d.style for d in self.dialogues if d.style})

    @property
    def effects(self) -> list[str]:
        return sorted({d.effect for d in self.dialogues if d.effect})


DEFAULT_FORMAT = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"]

MINIMAL_ASS_HEADER = (
    "[Script Info]\n"
    "ScriptType: v4.00+\n"
    "WrapStyle: 0\n"
    "ScaledBorderAndShadow: yes\n"
    "PlayResX: 1920\n"
    "PlayResY: 1080\n"
    "\n"
    "[V4+ Styles]\n"
    "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, "
    "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, "
    "MarginR, MarginV, Encoding\n"
    "Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1\n"
    "\n"
    "[Events]\n"
    "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
)


# --- чтение --------------------------------------------------------------
def detect_kind(path: str, text: str | None = None) -> str:
    low = path.lower()
    if low.endswith(".srt"):
        return "srt"
    if low.endswith(".vtt"):
        return "vtt"
    if text is not None and text.lstrip("\ufeff \r\n\t").upper().startswith("WEBVTT"):
        return "vtt"
    return "ass"


def load(path: str) -> SubDoc:
    text = read_text(path)
    kind = detect_kind(path, text)
    if kind == "srt":
        return parse_srt(text)
    if kind == "vtt":
        return parse_vtt(text)
    return parse_ass(text)


def parse_ass(text: str) -> SubDoc:
    doc = SubDoc("ass")
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    header_lines: list[str] = []
    trailer_lines: list[str] = []
    section = ""
    fmt = list(DEFAULT_FORMAT)
    style_fmt: list[str] = []
    events_done = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            if section == "[events]":
                events_done = True
            section = stripped.lower()
            (trailer_lines if events_done else header_lines).append(line)
            continue
        if events_done:
            trailer_lines.append(line)
            continue
        if section in ("[v4+ styles]", "[v4 styles]"):
            if stripped.lower().startswith("format:"):
                style_fmt = [f.strip().lower() for f in stripped[7:].split(",")]
            elif stripped.lower().startswith("style:") and style_fmt:
                parts = [p.strip() for p in stripped[6:].split(",", len(style_fmt) - 1)]
                vals = dict(zip(style_fmt, parts))
                name = vals.get("name", "")

                def flag(key: str) -> bool:
                    return vals.get(key, "0") not in ("0", "")

                doc.styles[name] = StyleInfo(name, flag("bold"), flag("italic"), flag("underline"))
            header_lines.append(line)
            continue
        if section == "[events]":
            low = stripped.lower()
            if low.startswith("format:"):
                fmt = [f.strip() for f in stripped[7:].split(",")]
                doc.events_format = fmt
                header_lines.append(line)
                continue
            is_dialogue = low.startswith("dialogue:")
            is_comment = low.startswith("comment:")
            if is_dialogue or is_comment:
                body = stripped.split(":", 1)[1].lstrip()
                parts = body.split(",", len(fmt) - 1)
                if len(parts) < len(fmt):
                    continue
                vals = {k.lower(): v for k, v in zip(fmt, parts)}
                try:
                    start = parse_time(vals.get("start", "0:00:00.00"))
                    end = parse_time(vals.get("end", "0:00:00.00"))
                except ValueError:
                    continue
                doc.dialogues.append(Dialogue(
                    start=start, end=end, text=vals.get("text", ""),
                    style=vals.get("style", "Default").strip() or "Default",
                    actor=(vals.get("name") or vals.get("actor") or "").strip(),
                    effect=vals.get("effect", "").strip(),
                    layer=vals.get("layer", vals.get("marked", "0")).strip() or "0",
                    margin_l=vals.get("marginl", "0").strip() or "0",
                    margin_r=vals.get("marginr", "0").strip() or "0",
                    margin_v=vals.get("marginv", "0").strip() or "0",
                    comment=is_comment,
                ))
                continue
            continue
        header_lines.append(line)
    if not doc.events_format:
        doc.events_format = list(DEFAULT_FORMAT)
        if not any(l.strip().lower() == "[events]" for l in header_lines):
            header_lines += ["", "[Events]"]
        header_lines.append("Format: " + ", ".join(DEFAULT_FORMAT))
    doc.header = "\n".join(header_lines).rstrip("\n") + "\n"
    doc.trailer = "\n".join(trailer_lines).strip("\n")
    return doc


_SRT_TIME_LINE = re.compile(
    r"(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}|\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}|\d{1,2}:\d{2}[,.]\d{1,3})"
)


def _html_to_ass(text: str) -> str:
    text = re.sub(r"<\s*i\s*>", r"{\\i1}", text, flags=re.I)
    text = re.sub(r"<\s*/\s*i\s*>", r"{\\i0}", text, flags=re.I)
    text = re.sub(r"<\s*b\s*>", r"{\\b1}", text, flags=re.I)
    text = re.sub(r"<\s*/\s*b\s*>", r"{\\b0}", text, flags=re.I)
    text = re.sub(r"<\s*u\s*>", r"{\\u1}", text, flags=re.I)
    text = re.sub(r"<\s*/\s*u\s*>", r"{\\u0}", text, flags=re.I)
    text = VTT_TS_TAG_RE.sub("", text)
    text = HTML_TAG_RE.sub("", text)
    return html.unescape(text)


def parse_srt(text: str) -> SubDoc:
    doc = SubDoc("srt", header=MINIMAL_ASS_HEADER, events_format=list(DEFAULT_FORMAT))
    blocks = re.split(r"\n\s*\n", text.replace("\r\n", "\n").replace("\r", "\n").strip("\ufeff\n "))
    for block in blocks:
        lines = block.strip("\n").split("\n")
        for i, line in enumerate(lines):
            m = _SRT_TIME_LINE.search(line)
            if m:
                try:
                    start, end = parse_time(m.group(1)), parse_time(m.group(2))
                except ValueError:
                    break
                body = "\\N".join(_html_to_ass(l) for l in lines[i + 1:])
                doc.dialogues.append(Dialogue(start, end, body))
                break
    return doc


def parse_vtt(text: str) -> SubDoc:
    doc = SubDoc("vtt", header=MINIMAL_ASS_HEADER, events_format=list(DEFAULT_FORMAT))
    blocks = re.split(r"\n\s*\n", text.replace("\r\n", "\n").replace("\r", "\n").lstrip("\ufeff"))
    for block in blocks:
        lines = block.strip("\n").split("\n")
        if not lines or lines[0].startswith(("WEBVTT", "NOTE", "STYLE", "REGION")):
            continue
        for i, line in enumerate(lines):
            m = _SRT_TIME_LINE.search(line)
            if m:
                try:
                    start, end = parse_time(m.group(1)), parse_time(m.group(2))
                except ValueError:
                    break
                body_lines = lines[i + 1:]
                actor = ""
                vm = re.match(r"<v(?:\.[^ >]*)?\s+([^>]+)>", body_lines[0]) if body_lines else None
                if vm:
                    actor = vm.group(1).strip()
                body = "\\N".join(_html_to_ass(l) for l in body_lines)
                doc.dialogues.append(Dialogue(start, end, body, actor=actor))
                break
    return doc


# --- очистка --------------------------------------------------------------
def strip_tags(text: str) -> str:
    cleaned = TAG_RE.sub("", text)
    cleaned = VTT_TS_TAG_RE.sub("", cleaned)
    cleaned = HTML_TAG_RE.sub("", cleaned)
    cleaned = html.unescape(cleaned)
    cleaned = cleaned.replace("\\N", "\n").replace("\\n", "\n").replace("\\h", " ")
    return cleaned.strip()


def is_full_caps(text: str) -> bool:
    letters = [c for c in strip_tags(text) if c.isalpha()]
    return len(letters) > 1 and all(c.isupper() for c in letters)


def strip_caps(text: str) -> str:
    """Удалить строки (по \\N) целиком набранные капсом — как в Windows-версии."""
    parts = NL_SPLIT_RE.split(text)
    out: list[str] = []
    for i in range(0, len(parts), 2):
        part = parts[i]
        if not is_full_caps(part):
            out.append(part)
            if i + 1 < len(parts):
                out.append(parts[i + 1])
    res = DUP_NL_RE.sub(r"\1", "".join(out))
    while res[:2] in ("\\N", "\\n"):
        res = res[2:]
    while res[-2:] in ("\\N", "\\n"):
        res = res[:-2]
    return res


# --- запись --------------------------------------------------------------
def _ass_line(d: Dialogue, fmt: list[str]) -> str:
    values = {
        "layer": d.layer, "marked": d.layer, "start": fmt_ass_time(d.start), "end": fmt_ass_time(d.end),
        "style": d.style, "name": d.actor, "actor": d.actor, "marginl": d.margin_l, "marginr": d.margin_r,
        "marginv": d.margin_v, "effect": d.effect, "text": d.text.replace("\n", "\\N"),
    }
    kind = "Comment" if d.comment else "Dialogue"
    return f"{kind}: " + ",".join(values.get(f.lower(), "") for f in fmt)


def to_ass(doc: SubDoc, dialogues: list[Dialogue] | None = None) -> str:
    dialogues = doc.dialogues if dialogues is None else dialogues
    header = doc.header if doc.kind == "ass" and doc.header else MINIMAL_ASS_HEADER
    fmt = doc.events_format if doc.kind == "ass" and doc.events_format else DEFAULT_FORMAT
    body = "\n".join(_ass_line(d, fmt) for d in dialogues)
    out = header.rstrip("\n") + "\n" + body + "\n"
    if doc.kind == "ass" and doc.trailer:
        out += "\n" + doc.trailer + "\n"
    return out


def _ass_to_html(d: Dialogue, styles: dict[str, StyleInfo], keep_styles: bool) -> str:
    text = d.text
    if keep_styles:
        def repl(m: re.Match) -> str:
            block = m.group(0)
            out = ""
            for tag, html_tag in (("i", "i"), ("b", "b"), ("u", "u")):
                tm = re.search(rf"\\{tag}(\d+)", block)
                if tm:
                    out += f"<{html_tag}>" if tm.group(1) != "0" else f"</{html_tag}>"
            return out

        text = TAG_RE.sub(repl, text)
        st = styles.get(d.style)
        if st:
            if st.italic:
                text = f"<i>{text}</i>"
            if st.bold:
                text = f"<b>{text}</b>"
    else:
        text = TAG_RE.sub("", text)
    text = text.replace("\\N", "\n").replace("\\n", "\n").replace("\\h", " ")
    lines = [l.strip() for l in text.split("\n")]
    return "\n".join(l for l in lines if l)


def to_srt(doc: SubDoc, dialogues: list[Dialogue] | None = None, keep_styles: bool = False) -> str:
    dialogues = [d for d in (doc.dialogues if dialogues is None else dialogues) if not d.comment]
    dialogues = sorted(dialogues, key=lambda d: (d.start, d.end))
    chunks = []
    n = 0
    for d in dialogues:
        body = _ass_to_html(d, doc.styles, keep_styles)
        if not body.strip():
            continue
        n += 1
        chunks.append(f"{n}\n{fmt_srt_time(d.start)} --> {fmt_srt_time(d.end)}\n{body}\n")
    return "\n".join(chunks)


def to_vtt(doc: SubDoc, dialogues: list[Dialogue] | None = None, keep_styles: bool = False) -> str:
    dialogues = [d for d in (doc.dialogues if dialogues is None else dialogues) if not d.comment]
    dialogues = sorted(dialogues, key=lambda d: (d.start, d.end))
    chunks = ["WEBVTT\n"]
    for d in dialogues:
        body = _ass_to_html(d, doc.styles, keep_styles)
        if not body.strip():
            continue
        body = body.replace("-->", "->")
        chunks.append(f"{fmt_srt_time(d.start, '.')} --> {fmt_srt_time(d.end, '.')}\n{body}\n")
    return "\n".join(chunks)


def render(doc: SubDoc, target: str, dialogues: list[Dialogue] | None = None, keep_styles: bool = False) -> str:
    target = target.lower()
    if target in ("srt", ".srt"):
        return to_srt(doc, dialogues, keep_styles)
    if target in ("vtt", ".vtt", "webvtt"):
        return to_vtt(doc, dialogues, keep_styles)
    return to_ass(doc, dialogues)


def shifted(doc: SubDoc, delta_ms: int) -> SubDoc:
    out = replace(doc, dialogues=[replace(d, start=max(0, d.start + delta_ms), end=max(0, d.end + delta_ms))
                                  for d in doc.dialogues])
    return out


def shift_text(text: str, kind: str, delta_ms: int) -> tuple[str, int]:
    """Сдвинуть тайминги прямо в тексте, сохранив всё остальное оформление файла.

    Возвращает (новый текст, количество сдвинутых реплик).
    """
    count = 0
    if kind in ("srt", "vtt"):
        sep = "," if kind == "srt" else "."

        def repl(m: re.Match) -> str:
            nonlocal count
            count += 1
            a = max(0, parse_time(m.group(1)) + delta_ms)
            b = max(0, parse_time(m.group(2)) + delta_ms)
            return f"{fmt_srt_time(a, sep)} --> {fmt_srt_time(b, sep)}"

        return _SRT_TIME_LINE.sub(repl, text), count

    fmt = list(DEFAULT_FORMAT)
    in_events = False
    out_lines = []
    newline = "\r\n" if "\r\n" in text else "\n"
    for line in text.splitlines():
        stripped = line.strip()
        low = stripped.lower()
        if stripped.startswith("[") and stripped.endswith("]"):
            in_events = low == "[events]"
        elif in_events and low.startswith("format:"):
            fmt = [f.strip().lower() for f in stripped[7:].split(",")]
        elif in_events and (low.startswith("dialogue:") or low.startswith("comment:")):
            prefix, body = line.split(":", 1)
            lead = body[: len(body) - len(body.lstrip())]
            parts = body.lstrip().split(",", len(fmt) - 1)
            lower_fmt = [f.lower() for f in fmt]
            try:
                for key in ("start", "end"):
                    if key in lower_fmt:
                        i = lower_fmt.index(key)
                        parts[i] = fmt_ass_time(max(0, parse_time(parts[i]) + delta_ms))
                line = f"{prefix}:{lead}" + ",".join(parts)
                count += 1
            except (ValueError, IndexError):
                pass
        out_lines.append(line)
    return newline.join(out_lines) + (newline if text.endswith(("\n", "\r")) else ""), count


def write_text(path: str, text: str, bom: bool = False) -> None:
    with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="\n") as fh:
        fh.write(text)
