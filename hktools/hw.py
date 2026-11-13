"""Определение аппаратного кодирования: NVENC (NVIDIA) и VAAPI (AMD / Intel)."""

from __future__ import annotations

import glob
import json
import os
import subprocess
import threading
from dataclasses import asdict, dataclass, field

from . import paths, tools
from .config import settings
from .log import log

VENDORS = {"0x10de": "NVIDIA", "0x1002": "AMD", "0x8086": "Intel"}


@dataclass
class HwCaps:
    ffmpeg_version: str = ""
    encoders: list[str] = field(default_factory=list)
    filters: list[str] = field(default_factory=list)
    nvenc: dict[str, bool] = field(default_factory=dict)   # {"hevc_nvenc": True, ...}
    vaapi: dict[str, bool] = field(default_factory=dict)   # {"hevc_vaapi": True, ...}
    vaapi_device: str = ""
    gpus: list[str] = field(default_factory=list)

    def has_encoder(self, name: str) -> bool:
        return name in self.encoders

    @property
    def nvenc_ok(self) -> bool:
        return any(self.nvenc.values())

    @property
    def vaapi_ok(self) -> bool:
        return any(self.vaapi.values())


_lock = threading.Lock()
_caps: HwCaps | None = None


def render_devices() -> list[str]:
    return sorted(glob.glob("/dev/dri/renderD*"))


def gpu_vendors() -> list[str]:
    out = []
    for dev in render_devices():
        node = os.path.basename(dev)
        try:
            with open(f"/sys/class/drm/{node}/device/vendor", encoding="ascii") as fh:
                vid = fh.read().strip()
            out.append(f"{VENDORS.get(vid, vid)} ({node})")
        except OSError:
            out.append(node)
    return out


def pick_vaapi_device() -> str:
    configured = (settings.get("vaapi_device") or "").strip()
    if configured:
        return configured
    devices = render_devices()
    # Предпочитаем не-NVIDIA устройство для VAAPI
    for dev in devices:
        node = os.path.basename(dev)
        try:
            with open(f"/sys/class/drm/{node}/device/vendor", encoding="ascii") as fh:
                if fh.read().strip() != "0x10de":
                    return dev
        except OSError:
            continue
    return devices[0] if devices else ""


def _ffmpeg_list(ffmpeg: str, what: str) -> list[str]:
    try:
        out = subprocess.run([ffmpeg, "-hide_banner", f"-{what}"], capture_output=True, text=True, timeout=15,
                             errors="replace").stdout
    except (OSError, subprocess.SubprocessError):
        return []
    names = []
    started = False
    for line in out.splitlines():
        if line.strip().startswith("---") or line.strip() == "------":
            started = True
            continue
        if what == "filters":
            parts = line.split()
            if len(parts) >= 2 and len(parts[0]) <= 4 and "->" in line:
                names.append(parts[1])
            continue
        if not started:
            continue
        parts = line.split()
        if len(parts) >= 2:
            names.append(parts[1])
    return names


def _test_encode(cmd: list[str]) -> bool:
    try:
        res = subprocess.run(cmd, capture_output=True, timeout=20)
        return res.returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def _detect() -> HwCaps:
    caps = HwCaps()
    ffmpeg = tools.which("ffmpeg")
    if not ffmpeg:
        return caps
    status = tools.probe_version("ffmpeg")
    caps.ffmpeg_version = status.version
    caps.gpus = gpu_vendors()

    cache_file = paths.cache_dir() / "hwcaps.json"
    key = f"{ffmpeg}|{caps.ffmpeg_version}|{','.join(caps.gpus)}|{settings.get('vaapi_device')}"
    try:
        cached = json.loads(cache_file.read_text(encoding="utf-8"))
        if cached.get("key") == key:
            data = cached["caps"]
            return HwCaps(**data)
    except (OSError, ValueError, KeyError, TypeError):
        pass

    caps.encoders = _ffmpeg_list(ffmpeg, "encoders")
    caps.filters = _ffmpeg_list(ffmpeg, "filters")
    src = ["-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.4"]
    for enc in ("hevc_nvenc", "h264_nvenc", "av1_nvenc"):
        if enc in caps.encoders:
            caps.nvenc[enc] = _test_encode([ffmpeg, "-hide_banner", "-loglevel", "error", *src,
                                            "-c:v", enc, "-f", "null", "-"])
    dev = pick_vaapi_device()
    caps.vaapi_device = dev
    if dev:
        for enc in ("hevc_vaapi", "h264_vaapi", "av1_vaapi"):
            if enc in caps.encoders:
                caps.vaapi[enc] = _test_encode([ffmpeg, "-hide_banner", "-loglevel", "error",
                                                "-vaapi_device", dev, *src, "-vf", "format=nv12,hwupload",
                                                "-c:v", enc, "-f", "null", "-"])
    try:
        cache_file.write_text(json.dumps({"key": key, "caps": asdict(caps)}), encoding="utf-8")
    except OSError:
        pass
    log.info(
        "Аппаратное кодирование: "
        f"NVENC={[k for k, v in caps.nvenc.items() if v] or 'нет'}, "
        f"VAAPI={[k for k, v in caps.vaapi.items() if v] or 'нет'} ({dev or 'нет устройства'})",
        "Оборудование",
    )
    return caps


def get_caps(refresh: bool = False) -> HwCaps:
    global _caps
    with _lock:
        if refresh:
            try:
                (paths.cache_dir() / "hwcaps.json").unlink()
            except OSError:
                pass
            _caps = None
        if _caps is None:
            _caps = _detect()
        return _caps


def has_filter(name: str) -> bool:
    return name in get_caps().filters
