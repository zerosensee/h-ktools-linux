"""Декларативная схема настроек инструментов (аналог SettingField из Windows-версии).

UI строит форму автоматически, CLI позволяет задавать значения через --set key=value.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Iterable

TEXT = "text"
COMBO = "combo"
CHECK = "check"
INT = "int"
FLOAT = "float"
KEYWORDS = "keywords"   # список {"word": str, "active": bool, ...}
EXPANDER = "expander"   # группа дочерних полей; если toggle=True — значение bool


@dataclass
class Cond:
    key: str
    values: tuple[str, ...]
    negate: bool = False

    def __init__(self, key: str, values: str | bool | Iterable[Any], negate: bool = False) -> None:
        self.key = key
        if isinstance(values, (str, bool, int, float)):
            values = (values,)
        self.values = tuple(_norm(v) for v in values)
        self.negate = negate

    def test(self, settings: dict[str, Any]) -> bool:
        result = _norm(settings.get(self.key)) in self.values
        return not result if self.negate else result


def _norm(v: Any) -> str:
    if isinstance(v, bool):
        return "True" if v else "False"
    return str(v)


@dataclass
class Field:
    key: str
    label: str
    type: str
    default: Any = None
    group: str = "Общие"          # "Вкладка:Группа" или просто "Группа"
    options: list[str] | None = None
    comment: str = ""
    visible: list[Cond] = field(default_factory=list)    # все условия должны выполняться
    disabled: list[Cond] = field(default_factory=list)   # любое условие отключает поле
    children: list["Field"] = field(default_factory=list)
    toggle: bool = True              # для EXPANDER: есть ли переключатель
    minimum: float | None = None
    maximum: float | None = None
    step: float | None = None
    digits: int = 2
    warning: tuple[str, str] | None = None   # (заголовок, текст) — подтверждение при включении
    extra_flag: tuple[str, str] | None = None  # для KEYWORDS: (ключ, подпись) доп. флага строки
    placeholder: str = ""
    browse: str | None = None        # для TEXT: "file" | "folder" — кнопка выбора в интерфейсе

    @property
    def tab(self) -> str:
        return self.group.split(":", 1)[0] if ":" in self.group else ""

    @property
    def section(self) -> str:
        return self.group.split(":", 1)[1] if ":" in self.group else self.group

    def is_visible(self, values: dict[str, Any]) -> bool:
        return all(c.test(values) for c in self.visible)

    def is_disabled(self, values: dict[str, Any]) -> bool:
        return any(c.test(values) for c in self.disabled)


def walk(fields: Iterable[Field]) -> Iterable[Field]:
    for f in fields:
        yield f
        if f.children:
            yield from walk(f.children)


def defaults(fields: Iterable[Field]) -> dict[str, Any]:
    out: dict[str, Any] = {}
    for f in walk(fields):
        if f.type == EXPANDER and not f.toggle:
            continue
        out[f.key] = f.default
    return out


def coerce(f: Field, raw: Any) -> Any:
    """Привести значение (например, из CLI или JSON) к типу поля."""
    if f.type == CHECK or (f.type == EXPANDER and f.toggle):
        if isinstance(raw, bool):
            return raw
        return str(raw).strip().lower() in ("1", "true", "yes", "on", "да")
    if f.type == INT:
        try:
            val = int(float(raw))
        except (TypeError, ValueError):
            return f.default
        if f.minimum is not None:
            val = max(int(f.minimum), val)
        if f.maximum is not None:
            val = min(int(f.maximum), val)
        return val
    if f.type == FLOAT:
        try:
            val = float(str(raw).replace(",", "."))
        except (TypeError, ValueError):
            return f.default
        if f.minimum is not None:
            val = max(f.minimum, val)
        if f.maximum is not None:
            val = min(f.maximum, val)
        return val
    if f.type == COMBO:
        sval = str(raw)
        if f.options and sval not in f.options:
            # допускаем регистронезависимое совпадение
            for opt in f.options:
                if opt.lower() == sval.lower():
                    return opt
            return f.default
        return sval
    if f.type == KEYWORDS:
        if isinstance(raw, list):
            out = []
            for item in raw:
                if isinstance(item, dict) and "word" in item:
                    out.append({**item, "word": str(item["word"]), "active": bool(item.get("active", True))})
                elif isinstance(item, str):
                    out.append({"word": item, "active": True})
            return out
        if isinstance(raw, str):
            return [{"word": w.strip(), "active": True} for w in raw.split("|") if w.strip()]
        return f.default
    return "" if raw is None else str(raw)


def active_words(items: Any, lower: bool = False) -> list[str]:
    out = []
    for item in items or []:
        if isinstance(item, dict) and item.get("active") and str(item.get("word", "")).strip() != "":
            w = str(item["word"])
            out.append(w.lower() if lower else w)
    return out
