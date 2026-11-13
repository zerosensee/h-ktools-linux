"""Анализ медиафайлов через ffprobe (+ mkvmerge -J при необходимости)."""

from __future__ import annotations

import json
import os
import threading
from dataclasses import dataclass, field
from fractions import Fraction

from .. import tools
from . import process

FONT_MIMES = {
    "application/x-truetype-font", "application/x-font-ttf", "application/x-font-otf", "font/ttf", "font/otf",
    "font/sfnt", "application/vnd.ms-opentype", "application/font-sfnt", "application/x-font-opentype",
    "font/collection", "application/x-font", "font/woff", "font/woff2",
}
FONT_EXTS = (".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2", ".fon", ".pfb")
BITMAP_SUB_CODECS = {"hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub"}

TYPE_RU = {"video": "Видео", "audio": "Аудио", "subtitle": "Субтитры", "attachment": "Вложение", "data": "Данные"}


@dataclass
class Track:
    index: int                # абсолютный индекс потока (0:N в ffmpeg)
    type: str                 # video | audio | subtitle | attachment | data
    type_index: int           # индекс внутри типа (0:a:N)
    codec: str = ""
    codec_long: str = ""
    profile: str = ""
    language: str = "und"
    title: str = ""
    default: bool = False
    forced: bool = False
    hearing_impaired: bool = False
    commentary: bool = False
    original: bool = False
    visual_impaired: bool = False
    channels: int = 0
    channel_layout: str = ""
    sample_rate: int = 0
    width: int = 0
    height: int = 0
    fps: float = 0.0
    pix_fmt: str = ""
    bit_depth: int = 0
    bitrate: int = 0
    duration: float = 0.0
    filename: str = ""        # для вложений
    mimetype: str = ""

    @property
    def is_font(self) -> bool:
        if self.type != "attachment":
            return False
        return self.mimetype.lower() in FONT_MIMES or self.filename.lower().endswith(FONT_EXTS)

    @property
    def is_bitmap_sub(self) -> bool:
        return self.type == "subtitle" and self.codec in BITMAP_SUB_CODECS

    @property
    def resolution(self) -> str:
        return f"{self.width}x{self.height}" if self.width and self.height else ""

    def describe(self) -> str:
        """Человекочитаемое описание дорожки (для списков выбора)."""
        bits: list[str] = []
        if self.type == "video":
            bits.append(self.codec.upper())
            if self.resolution:
                bits.append(self.resolution)
            if self.fps:
                bits.append(f"{self.fps:.3f}".rstrip("0").rstrip(".") + " fps")
            if self.bit_depth and self.bit_depth > 8:
                bits.append(f"{self.bit_depth}-bit")
        elif self.type == "audio":
            bits.append(self.profile if self.codec == "dts" and self.profile else self.codec.upper())
            if self.channel_layout or self.channels:
                bits.append(self.channel_layout or f"{self.channels} кан.")
            if self.sample_rate:
                bits.append(f"{self.sample_rate // 1000 if self.sample_rate % 1000 == 0 else self.sample_rate / 1000} кГц")
            if self.bitrate:
                bits.append(f"{self.bitrate // 1000} кбит/с")
        elif self.type == "subtitle":
            bits.append(self.codec.upper())
        elif self.type == "attachment":
            bits.append(self.filename or self.mimetype)
        if self.language and self.language != "und":
            bits.append(self.language)
        if self.title:
            bits.append(f"«{self.title}»")
        flags = []
        if self.default:
            flags.append("по умолч.")
        if self.forced:
            flags.append("принуд.")
        if flags:
            bits.append("[" + ", ".join(flags) + "]")
        return " · ".join(b for b in bits if b)


@dataclass
class MediaInfo:
    path: str
    duration: float = 0.0
    format_name: str = ""
    size: int = 0
    bitrate: int = 0
    title: str = ""
    tracks: list[Track] = field(default_factory=list)
    chapters: int = 0

    def of_type(self, kind: str) -> list[Track]:
        return [t for t in self.tracks if t.type == kind]

    @property
    def video(self) -> list[Track]:
        return [t for t in self.of_type("video") if t.codec not in ("mjpeg", "png", "bmp") or len(self.of_type("video")) == 1]

    @property
    def audio(self) -> list[Track]:
        return self.of_type("audio")

    @property
    def subtitles(self) -> list[Track]:
        return self.of_type("subtitle")

    @property
    def attachments(self) -> list[Track]:
        return self.of_type("attachment")

    @property
    def fonts(self) -> list[Track]:
        return [t for t in self.attachments if t.is_font]

    @property
    def media_tracks(self) -> list[Track]:
        return [t for t in self.tracks if t.type in ("video", "audio", "subtitle")]

    def track(self, index: int) -> Track | None:
        for t in self.tracks:
            if t.index == index:
                return t
        return None

    def signature(self) -> tuple:
        """Структура файла (для применения выбора дорожек к похожим файлам)."""
        return tuple((t.type, t.codec, t.language) for t in self.media_tracks)


def _fps(stream: dict) -> float:
    for key in ("avg_frame_rate", "r_frame_rate"):
        val = stream.get(key) or ""
        try:
            fr = Fraction(val)
            if 0 < fr < 1000:
                return float(fr)
        except (ValueError, ZeroDivisionError):
            continue
    return 0.0


def _int(v, default: int = 0) -> int:
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return default


def _float(v, default: float = 0.0) -> float:
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def _bit_depth(stream: dict) -> int:
    raw = stream.get("bits_per_raw_sample")
    if raw:
        return _int(raw)
    pix = stream.get("pix_fmt", "")
    for depth in (16, 12, 10):
        if f"p{depth}" in pix:
            return depth
    return 8 if pix else 0


_cache: dict[tuple, MediaInfo] = {}
_cache_lock = threading.Lock()


def probe(path: str, use_cache: bool = True) -> MediaInfo:
    """Проанализировать файл. Исключение RuntimeError, если ffprobe не смог."""
    try:
        st = os.stat(path)
        key = (os.path.abspath(path), st.st_mtime_ns, st.st_size)
    except OSError as exc:
        raise RuntimeError(f"Файл недоступен: {exc}") from exc
    if use_cache:
        with _cache_lock:
            if key in _cache:
                return _cache[key]

    ffprobe = tools.require("ffprobe")
    rc, out, err = process.capture(
        [ffprobe, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path],
        timeout=120,
    )
    if rc != 0 or not out.strip():
        raise RuntimeError(f"ffprobe не смог прочитать файл: {err.strip()[:400]}")
    data = json.loads(out)
    fmt = data.get("format", {})
    info = MediaInfo(
        path=path,
        duration=_float(fmt.get("duration")),
        format_name=fmt.get("format_name", ""),
        size=_int(fmt.get("size"), st.st_size),
        bitrate=_int(fmt.get("bit_rate")),
        title=(fmt.get("tags") or {}).get("title", ""),
        chapters=len(data.get("chapters") or []),
    )
    counters: dict[str, int] = {}
    for s in data.get("streams", []):
        kind = s.get("codec_type", "data")
        tags = {k.lower(): v for k, v in (s.get("tags") or {}).items()}
        disp = s.get("disposition") or {}
        idx_in_type = counters.get(kind, 0)
        counters[kind] = idx_in_type + 1
        duration = _float(s.get("duration"))
        if not duration:
            dur_tag = tags.get("duration", "")
            if dur_tag:
                try:
                    h, m, sec = dur_tag.split(":")
                    duration = int(h) * 3600 + int(m) * 60 + float(sec)
                except ValueError:
                    duration = 0.0
        bitrate = _int(s.get("bit_rate")) or _int(tags.get("bps")) or _int(tags.get("bps-eng"))
        info.tracks.append(Track(
            index=_int(s.get("index"), len(info.tracks)),
            type=kind,
            type_index=idx_in_type,
            codec=s.get("codec_name", "") or ("ttf" if kind == "attachment" else ""),
            codec_long=s.get("codec_long_name", ""),
            profile=s.get("profile", "") or "",
            language=tags.get("language", "und") or "und",
            title=tags.get("title", "") or "",
            default=bool(disp.get("default")),
            forced=bool(disp.get("forced")),
            hearing_impaired=bool(disp.get("hearing_impaired")),
            commentary=bool(disp.get("comment")),
            original=bool(disp.get("original")),
            visual_impaired=bool(disp.get("visual_impaired")),
            channels=_int(s.get("channels")),
            channel_layout=s.get("channel_layout", "") or "",
            sample_rate=_int(s.get("sample_rate")),
            width=_int(s.get("width")),
            height=_int(s.get("height")),
            fps=_fps(s) if kind == "video" else 0.0,
            pix_fmt=s.get("pix_fmt", "") or "",
            bit_depth=_bit_depth(s) if kind == "video" else _int(s.get("bits_per_raw_sample")),
            bitrate=bitrate,
            duration=duration,
            filename=tags.get("filename", "") or "",
            mimetype=tags.get("mimetype", "") or "",
        ))
    if not info.duration:
        durations = [t.duration for t in info.tracks if t.duration]
        if durations:
            info.duration = max(durations)
    if not info.duration and tools.which("mkvmerge"):
        info.duration = _mkvmerge_duration(path)
    with _cache_lock:
        if len(_cache) > 256:
            _cache.clear()
        _cache[key] = info
    return info


def _mkvmerge_duration(path: str) -> float:
    rc, out, _ = process.capture([tools.which("mkvmerge") or "mkvmerge", "-J", path], timeout=60)
    if rc not in (0, 1):
        return 0.0
    try:
        data = json.loads(out)
        dur = data.get("container", {}).get("properties", {}).get("duration")
        return int(dur) / 1e9 if dur else 0.0
    except (ValueError, TypeError):
        return 0.0


def mkvmerge_identify(path: str) -> dict:
    mkvmerge = tools.require("mkvmerge")
    rc, out, err = process.capture([mkvmerge, "-J", path], timeout=60)
    if rc not in (0, 1):
        raise RuntimeError(f"mkvmerge не смог прочитать файл: {err.strip()[:300]}")
    return json.loads(out)


# --- языки --------------------------------------------------------------
ISO_LANG = {
    "rus": "ru", "eng": "en", "jpn": "ja", "spa": "es", "fra": "fr", "fre": "fr", "deu": "de", "ger": "de",
    "ita": "it", "por": "pt", "zho": "zh", "chi": "zh", "ara": "ar", "kor": "ko", "pol": "pl", "ukr": "uk",
    "hin": "hi", "tur": "tr", "heb": "he", "vie": "vi", "tha": "th", "nld": "nl", "dut": "nl", "swe": "sv",
    "dan": "da", "fin": "fi", "nob": "no", "nor": "no", "ces": "cs", "cze": "cs", "hun": "hu", "ron": "ro",
    "rum": "ro", "ell": "el", "gre": "el", "ind": "id", "msa": "ms", "may": "ms", "bul": "bg", "srp": "sr",
}


def normalize_lang(lang: str) -> str:
    if not lang or lang.lower() == "und":
        return "und"
    base = lang.split("-")[0].strip().lower()
    return ISO_LANG.get(base, base)


# --- расширения для извлечения «сырых» потоков ------------------------------------
RAW_EXT = {
    # аудио
    "ac3": ".ac3", "eac3": ".eac3", "dts": ".dts", "aac": ".aac", "aac_latm": ".aac", "opus": ".opus",
    "flac": ".flac", "vorbis": ".ogg", "mp3": ".mp3", "mp2": ".mp2", "mp1": ".mp2", "truehd": ".thd",
    "mlp": ".mlp", "alac": ".m4a", "wavpack": ".wv", "ape": ".mka", "tta": ".tta",
    # субтитры
    "subrip": ".srt", "srt": ".srt", "ass": ".ass", "ssa": ".ass", "webvtt": ".vtt", "mov_text": ".srt",
    "text": ".srt", "hdmv_pgs_subtitle": ".sup", "dvd_subtitle": ".mks", "dvb_subtitle": ".mks",
    # видео
    "h264": ".h264", "hevc": ".h265", "mpeg2video": ".m2v", "mpeg1video": ".m1v", "vc1": ".vc1",
    "vp8": ".ivf", "vp9": ".ivf", "av1": ".ivf", "mjpeg": ".mjpeg", "prores": ".mov", "mpeg4": ".m4v",
}
# Кодеки субтитров, которые при извлечении конвертируются (ffmpeg не умеет их «копировать» в файл)
SUB_CONVERT = {"mov_text": "srt", "text": "srt"}


def raw_extension(track: Track) -> str:
    if track.codec.startswith("pcm_"):
        return ".wav"
    ext = RAW_EXT.get(track.codec)
    if ext:
        return ext
    return {"video": ".mkv", "audio": ".mka", "subtitle": ".mks"}.get(track.type, ".bin")
