"""Общие поля и помощники для инструментов."""

from __future__ import annotations

import os

from ..core.fields import CHECK, Cond, Field
from ..core.script import Context, Result, remove_quiet
from ..media.process import ProcResult

REPLACE_WARNING = (
    "Замена исходного файла",
    "Исходный файл заменяется готовым результатом только после успешной проверки (ffprobe). "
    "До подтверждения результата исходный файл сохраняется.",
)


def output_fields(group: str = "Вывод") -> list[Field]:
    return [
        Field("overwrite_source", "Заменить исходный файл результатом", CHECK, False, group,
              comment="Только после успешной проверки результата", warning=REPLACE_WARNING),
        Field("delete_source", "Удалить оригинал после обработки (в корзину)", CHECK, False, group,
              visible=[Cond("overwrite_source", False)]),
    ]


def delete_original_field(group: str = "Общие", key: str = "delete_original") -> Field:
    return Field(key, "Удалить исходный файл (в корзину)", CHECK, False, group)


def finish_ffmpeg(ctx: Context, res: ProcResult, source: str, output: str, ok_msg: str) -> Result:
    """Типовая обработка результата ffmpeg: проверка выхода, очистка, опции исходника."""
    if res.ok and os.path.isfile(output) and os.path.getsize(output) > 0:
        result = Result.done(msg=ok_msg)
        final = ctx.finalize_source(source, output, result)
        result.outputs = [final]
        ctx.progress(100, "Готово")
        return result
    remove_quiet(output)
    return Result.failed(f"❌ Ошибка: {os.path.basename(source)}\n{res.error_text()}")
