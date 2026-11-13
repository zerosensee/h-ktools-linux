"""Распознавание речи через whisper.cpp (whisper-cli) + менеджер моделей."""

from __future__ import annotations

import os
import re
import threading
import urllib.request
from dataclasses import dataclass
from typing import Callable

from .. import paths, tools
from ..config import settings
from ..core.fields import CHECK, COMBO, FLOAT, INT, Field
from ..core.script import ALL_MEDIA, Context, QueueItem, Result, Script
from ..media import process
from ..media.process import Cancelled

HF = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main"


@dataclass(frozen=True)
class WhisperModel:
    key: str
    size_mb: float
    description: str

    @property
    def filename(self) -> str:
        return f"ggml-{self.key}.bin"

    @property
    def url(self) -> str:
        return f"{HF}/{self.filename}"


MODELS = [
    WhisperModel("tiny", 75, "Минимальная: максимальная скорость, базовое качество"),
    WhisperModel("base", 142, "Быстрое распознавание чистой речи"),
    WhisperModel("small", 466, "Баланс скорости и качества для диалогов"),
    WhisperModel("small-q5_1", 182, "Квантованная Small, экономит память"),
    WhisperModel("medium", 1530, "Высокое качество: термины, акценты, шум"),
    WhisperModel("medium-q5_0", 515, "Квантованная Medium"),
    WhisperModel("large-v3", 3100, "Флагман с наивысшей точностью"),
    WhisperModel("large-v3-q5_0", 1080, "Квантованная Large v3"),
    WhisperModel("large-v3-turbo", 1620, "До 4× быстрее Large v3 при близком качестве"),
    WhisperModel("large-v3-turbo-q5_0", 574, "Квантованная Turbo — лучший выбор для слабых GPU"),
]
LANGS = ["Авто", "Русский (ru)", "English (en)", "Deutsch (de)", "Français (fr)", "Español (es)", "Italiano (it)",
         "日本語 (ja)", "中文 (zh)", "한국어 (ko)", "Português (pt)", "Українська (uk)", "Polski (pl)", "Türkçe (tr)"]
BACKENDS = ["Авто (GPU, если сборка поддерживает)", "Только CPU"]


def models_dir() -> str:
    return str(paths.whisper_models_dir(settings.get("whisper_models_dir") or None))


def model_path(key: str) -> str:
    return os.path.join(models_dir(), f"ggml-{key}.bin")


def downloaded_models() -> list[str]:
    return [m.key for m in MODELS if os.path.isfile(model_path(m.key))] + sorted(
        f[5:-4] for f in os.listdir(models_dir())
        if f.startswith("ggml-") and f.endswith(".bin") and f[5:-4] not in {m.key for m in MODELS}
    )


def download_model(key: str, on_progress: Callable[[float, str], None], cancel: threading.Event) -> str:
    """Скачать модель (с докачкой через .part). Возвращает путь."""
    model = next((m for m in MODELS if m.key == key), None)
    if model is None:
        raise ValueError(f"Неизвестная модель: {key}")
    target = model_path(key)
    part = target + ".part"
    have = os.path.getsize(part) if os.path.exists(part) else 0
    req = urllib.request.Request(model.url, headers={"User-Agent": "hktools-linux"})
    if have:
        req.add_header("Range", f"bytes={have}-")
    with urllib.request.urlopen(req, timeout=30) as resp:
        if have and resp.status != 206:
            have = 0
        total = int(resp.headers.get("Content-Length", 0)) + have
        with open(part, "ab" if have else "wb") as fh:
            done = have
            while True:
                if cancel.is_set():
                    raise Cancelled()
                chunk = resp.read(1 << 20)
                if not chunk:
                    break
                fh.write(chunk)
                done += len(chunk)
                if total:
                    on_progress(done / total * 100, f"{done / 1e6:.0f} / {total / 1e6:.0f} МБ")
    os.replace(part, target)
    return target


class SpeechRecognition(Script):
    id = "speech-recognition"
    name = "Распознавание речи"
    description = "Транскрибация в SRT/VTT/TXT/LRC/JSON через whisper.cpp (CPU, CUDA, Vulkan, ROCm)"
    category = "Субтитры"
    icon = "audio-input-microphone-symbolic"
    extensions = ALL_MEDIA
    deps = ("ffmpeg", "ffprobe", "whisper")

    def fields(self, values=None):
        have = downloaded_models()
        default = "large-v3-turbo" if "large-v3-turbo" in have else (have[0] if have else "large-v3-turbo")
        M, S, E = "Основные:Модель и язык", "Субтитры:Форматы экспорта", "Движок:Производительность"
        P = "Субтитры:Параметры субтитров"
        return [
            Field("whisper_model", "Модель Whisper", COMBO, default, M, options=have or ["(нет загруженных моделей)"],
                  comment="Скачать модели — кнопка «Модели» в заголовке"),
            Field("whisper_language", "Язык речи", COMBO, "Авто", M, options=LANGS,
                  comment="«Авто» определяет язык по первым 30 секундам"),
            Field("whisper_translate", "Перевести на английский", CHECK, False, M),
            Field("output_srt", "SubRip (.srt)", CHECK, True, S),
            Field("output_vtt", "WebVTT (.vtt)", CHECK, False, S),
            Field("output_txt", "Текст (.txt)", CHECK, True, S),
            Field("output_lrc", "Караоке LRC (.lrc)", CHECK, False, S),
            Field("output_json", "JSON (.json)", CHECK, False, S),
            Field("max_segment_length", "Макс. длина строки (символы)", INT, 35, P, minimum=0, maximum=200,
                  comment="35–42 — стандарт для кино и сериалов; 0 — без переноса"),
            Field("split_on_word", "Переносить строго по словам", CHECK, True, P),
            Field("whisper_max_context", "Контекст предыдущих токенов", INT, 0, P, minimum=0, maximum=448,
                  comment="0 — защита от зацикливания на музыке и тишине"),
            Field("whisper_no_speech_thold", "Порог тишины/музыки", FLOAT, 0.65, P, minimum=0.0, maximum=1.0,
                  step=0.05),
            Field("whisper_suppress_nst", "Подавлять неречевые галлюцинации (--suppress-nst)", CHECK, True, P),
            Field("whisper_backend", "Вычислительный движок", COMBO, BACKENDS[0], E, options=BACKENDS,
                  comment="GPU зависит от пакета: whisper.cpp-cuda / -vulkan / -hip из AUR"),
            Field("whisper_threads", "Потоков CPU", INT, min(os.cpu_count() or 4, 8), E, minimum=1, maximum=64),
            Field("whisper_flash_attn", "Flash Attention (GPU)", CHECK, True, E),
            Field("whisper_beam", "Beam size", INT, 5, E, minimum=1, maximum=16),
        ]

    def process(self, item: QueueItem, ctx: Context) -> Result:
        whisper = tools.require("whisper")
        model = str(ctx.get("whisper_model", ""))
        mpath = model_path(model)
        if not os.path.isfile(mpath):
            return Result.failed("❌ Модель Whisper не загружена — откройте «Модели» и скачайте её")
        info = ctx.probe()
        if not info.audio:
            return Result.failed("❌ В файле нет аудиодорожки")
        tmp = ctx.temp_dir("kt_whisper_")
        wav = os.path.join(tmp, "audio.wav")
        ctx.progress(0, "Подготовка аудио 16 кГц…")
        res = ctx.ffmpeg(["-i", item.path, "-map", f"0:{info.audio[0].index}", "-vn", "-ac", "1", "-ar", "16000",
                          "-c:a", "pcm_s16le", wav], info.duration, "Подготовка аудио", span=10)
        if not res.ok:
            return Result.failed(f"❌ Не удалось подготовить аудио\n{res.error_text()}")
        out_base = ctx.output_path(item.path, ext="")
        lang = str(ctx.get("whisper_language", "Авто"))
        m = re.search(r"\((\w+)\)", lang)
        cmd = [whisper, "-m", mpath, "-f", wav, "-l", m.group(1) if m else "auto", "-of", out_base, "-pp"]
        if ctx.get("whisper_translate"):
            cmd.append("--translate")
        flags = {"output_srt": "-osrt", "output_vtt": "-ovtt", "output_txt": "-otxt", "output_lrc": "-olrc",
                 "output_json": "-oj"}
        exts = {"output_srt": ".srt", "output_vtt": ".vtt", "output_txt": ".txt", "output_lrc": ".lrc",
                "output_json": ".json"}
        wanted = [k for k in flags if ctx.get(k)]
        if not wanted:
            wanted = ["output_srt"]
        cmd += [flags[k] for k in wanted]
        ml = int(ctx.get("max_segment_length", 35))
        if ml > 0:
            cmd += ["-ml", str(ml)]
            if ctx.get("split_on_word", True):
                cmd.append("-sow")
        cmd += ["-mc", str(int(ctx.get("whisper_max_context", 0))), "-t", str(int(ctx.get("whisper_threads", 4))),
                "-bs", str(int(ctx.get("whisper_beam", 5))),
                "-nth", f"{float(ctx.get('whisper_no_speech_thold', 0.65)):.2f}"]
        if ctx.get("whisper_suppress_nst", True):
            cmd.append("-sns")
        if str(ctx.get("whisper_backend", "")).startswith("Только CPU"):
            cmd.append("-ng")
        else:
            cmd.append("-fa" if ctx.get("whisper_flash_attn", True) else "-nfa")

        def on_line(line: str) -> None:
            pm = re.search(r"progress\s*=\s*(\d+)%", line)
            if pm:
                pct = int(pm.group(1))
                ctx.progress(10 + pct * 0.9, f"Распознавание · {pct}%")

        ctx.progress(10, "Загрузка модели…")
        r = process.run(cmd, ctx.token, on_stderr=on_line, on_stdout=on_line, source=self.name)
        if r.cancelled:
            raise Cancelled()
        outputs = [out_base + exts[k] for k in wanted if os.path.isfile(out_base + exts[k])]
        if r.ok and outputs:
            ctx.progress(100, "Готово")
            return Result.done(*outputs, msg=f"✅ Распознано: {', '.join(os.path.basename(o) for o in outputs)}")
        hint = ""
        if "unknown argument" in r.error_text().lower() or "error: unknown" in r.error_text().lower():
            hint = "\nВозможно, установлена старая версия whisper.cpp — обновите пакет."
        return Result.failed(f"❌ Ошибка whisper.cpp{hint}\n{r.error_text()}")
