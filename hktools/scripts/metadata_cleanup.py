from __future__ import annotations

import os

from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script
from .common import finish_ffmpeg, output_fields


class MetadataCleanup(Script):
    id = "metadata-cleanup"
    name = "Очистка метаданных"
    description = "Удаление глобальных тегов, служебных заголовков и названий дорожек без перекодирования"
    category = "Видео"
    icon = "edit-clear-all-symbolic"
    extensions = ALL_MEDIA

    def fields(self, values=None):
        return output_fields()

    def process(self, item: QueueItem, ctx: Context) -> Result:
        out = ctx.output_path(item.path)
        if skip := ctx.skip_if_exists(out):
            return skip
        info = ctx.probe()
        ctx.progress(0, "Очистка метаданных…")
        args = ["-i", item.path, "-map", "0", "-map_metadata", "-1", "-map_metadata:s", "-1",
                "-map_chapters", "-1", "-c", "copy"]
        # Вложениям (шрифтам) MKV обязательны имя файла и MIME-тип — возвращаем только их
        for t in info.attachments:
            args += [f"-metadata:s:{t.index}", f"filename={t.filename or f'attachment_{t.index}.ttf'}",
                     f"-metadata:s:{t.index}", f"mimetype={t.mimetype or 'application/x-truetype-font'}"]
        if out.lower().endswith((".mp4", ".mov", ".m4a")):
            args += ["-movflags", "+faststart"]
        args.append(out)
        res = ctx.ffmpeg(args, info.duration, "Очистка метаданных")
        return finish_ffmpeg(ctx, res, item.path, out, f"✅ Очищены метаданные: {os.path.basename(out)}")
