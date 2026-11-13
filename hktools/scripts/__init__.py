"""Реестр инструментов."""

from __future__ import annotations

from ..core.script import Script
from .audio_channels import AudioChannels
from .audio_downmix import AudioDownmix
from .audio_encode import AudioEncode
from .audio_shift import AudioShift
from .audio_speed import AudioSpeed
from .audio_transplant import AudioTransplant
from .bitrate import BitrateViewer
from .container_convert import ContainerConvert
from .downloader import MediaDownloader
from .metadata_cleanup import MetadataCleanup
from .mkv_assembly import MkvAssembly
from .speech import SpeechRecognition
from .streams import StreamManagement, StreamReplacement, TrackExtractor
from .subtitles import SubtitlesConvert, SubtitleShift
from .video_encode import VideoEncode


class TimingCalculator(Script):
    """Калькулятор таймингов в стиле Aegisub (только интерфейс, без обработки файлов)."""

    id = "timing-calculator"
    name = "Калькулятор таймингов"
    description = "Разница между двумя таймкодами для сдвига субтитров и аудио, история расчётов"
    category = "Инструменты"
    icon = "accessories-calculator-symbolic"
    input_mode = "none"
    deps = ()


_SCRIPTS: list[Script] = [
    MetadataCleanup(), VideoEncode(), ContainerConvert(),
    AudioEncode(), AudioDownmix(), AudioSpeed(), AudioChannels(), AudioShift(), AudioTransplant(),
    MkvAssembly(), StreamManagement(), StreamReplacement(), TrackExtractor(),
    SubtitlesConvert(), SubtitleShift(), SpeechRecognition(),
    MediaDownloader(),
    BitrateViewer(), TimingCalculator(),
]


def all_scripts() -> list[Script]:
    return list(_SCRIPTS)


def get(script_id: str) -> Script | None:
    for s in _SCRIPTS:
        if s.id == script_id:
            return s
    return None
