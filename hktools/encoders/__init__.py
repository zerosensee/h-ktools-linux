"""Видеоэнкодеры: CPU (x265 / x264 / SVT-AV1), NVENC (NVIDIA), VAAPI (AMD / Intel)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from .. import hw
from ..core.fields import CHECK, COMBO, EXPANDER, FLOAT, INT, Cond, Field


@dataclass
class EncContext:
    lossless: bool
    force_10bit: bool
    container: str  # ".mkv" / ".mp4"
    vaapi_device: str = ""


def _br_fields(prefix: str, group: str, rc_key: str, abr_value: str, extra_disabled: list[Cond]) -> list[Field]:
    """Поля битрейта (основной/мин/макс/буфер) с автоматическим расчётом границ."""
    vis = [Cond(rc_key, abr_value)]
    dis = [Cond("lossless", True), *extra_disabled]
    return [
        Field(f"{prefix}auto_bitrate", "Автовычисление границ битрейтов", CHECK, True, group,
              comment="Мин = основной, макс = ×2, буфер = макс×2", visible=vis, disabled=dis),
        Field(f"{prefix}v_bitrate", "Битрейт видео (кбит/с)", INT, 4000, group, visible=vis, disabled=dis,
              minimum=100, maximum=500000, step=100),
        Field(f"{prefix}min_bitrate", "Мин. битрейт (кбит/с)", INT, 4000, group, visible=vis,
              disabled=[*dis, Cond(f"{prefix}auto_bitrate", True)], minimum=0, maximum=500000, step=100),
        Field(f"{prefix}max_bitrate", "Макс. битрейт (кбит/с)", INT, 8000, group, visible=vis,
              disabled=[*dis, Cond(f"{prefix}auto_bitrate", True)], minimum=0, maximum=500000, step=100),
        Field(f"{prefix}bufsize", "Размер буфера VBV (кбит)", INT, 16000, group, visible=vis,
              disabled=[*dis, Cond(f"{prefix}auto_bitrate", True)], minimum=0, maximum=1000000, step=500),
    ]


def _br_args(v: dict[str, Any], prefix: str) -> list[str]:
    vbr = int(v.get(f"{prefix}v_bitrate", 4000))
    auto = bool(v.get(f"{prefix}auto_bitrate", True))
    minr = vbr if auto else int(v.get(f"{prefix}min_bitrate", 4000))
    maxr = vbr * 2 if auto else int(v.get(f"{prefix}max_bitrate", 8000))
    buf = maxr * 2 if auto else int(v.get(f"{prefix}bufsize", 16000))
    args = ["-b:v", f"{vbr}k"]
    if minr > 0:
        args += ["-minrate", f"{minr}k"]
    if maxr > 0:
        args += ["-maxrate", f"{maxr}k"]
    if buf > 0:
        args += ["-bufsize", f"{buf}k"]
    return args


class Encoder:
    id = ""
    title = ""

    def available(self) -> bool:
        return True

    def fields(self) -> list[Field]:
        return []

    def codec_key(self) -> str:
        return ""

    def input_args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        return []

    def filter_suffix(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        """Фильтры, добавляемые в конец цепочки -vf (например hwupload для VAAPI)."""
        return []

    def args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        raise NotImplementedError

    def tag(self, v: dict[str, Any], ctx: EncContext) -> str | None:
        return None


# --- CPU ------------------------------------------------------------------
CPU_CODECS = {"HEVC / H.265": "libx265", "AVC / H.264": "libx264", "AV1 (SVT-AV1)": "libsvtav1"}
X26X_PRESETS = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow",
                "placebo"]


class CpuEncoder(Encoder):
    id = "cpu"
    title = "CPU"

    def codec_key(self) -> str:
        return "cpu_codec"

    def fields(self) -> list[Field]:
        caps = hw.get_caps()
        codecs = [name for name, enc in CPU_CODECS.items() if not caps.encoders or caps.has_encoder(enc)]
        G, B, A = "Видео:Кодирование", "Видео:Битрейт", "Видео:Расширенные параметры"
        is265, is264, isav1 = (Cond("cpu_codec", "HEVC / H.265"), Cond("cpu_codec", "AVC / H.264"),
                               Cond("cpu_codec", "AV1 (SVT-AV1)"))
        out = [Field("cpu_codec", "Кодек CPU", COMBO, codecs[0] if codecs else "HEVC / H.265", G,
                     options=codecs or list(CPU_CODECS))]
        # x265
        out += [
            Field("x265_preset", "Пресет x265", COMBO, "medium", G, options=X26X_PRESETS, visible=[is265],
                  comment="Баланс скорости кодирования и сжатия"),
            Field("x265_rc", "Режим качества", COMBO, "CRF", B, options=["CRF", "Битрейт (ABR)"], visible=[is265],
                  disabled=[Cond("lossless", True)]),
            Field("x265_crf", "CRF / Качество", INT, 23, B, comment="0–51, меньше = лучше",
                  visible=[is265, Cond("x265_rc", "CRF")], disabled=[Cond("lossless", True)], minimum=0, maximum=51),
            *[_with_vis(f, is265) for f in _br_fields("x265_", B, "x265_rc", "Битрейт (ABR)", [])],
            Field("x265_advanced_options", "Расширенные параметры x265", EXPANDER, False, A, visible=[is265],
                  comment="Выключено — чистые значения по умолчанию x265", children=[
                      Field("x265_tune", "Tune", COMBO, "Нет", A,
                            options=["Нет", "grain", "animation", "fastdecode", "zerolatency", "psnr", "ssim"]),
                      Field("x265_aq_mode", "AQ Mode", COMBO, "2", A, options=["0", "1", "2", "3", "4"],
                            comment="0 — выкл, 1 — обычный, 2 — auto-variance, 3 — тёмные сцены, 4 — с контурами"),
                      Field("x265_aq_strength", "Сила AQ", FLOAT, 1.0, A, minimum=0.0, maximum=3.0, step=0.1),
                      Field("x265_lookahead", "RC Lookahead", INT, 20, A, minimum=0, maximum=250),
                      Field("x265_bframes", "B-кадры", INT, 4, A, minimum=0, maximum=16),
                      Field("x265_b_adapt", "Адаптация B-кадров", COMBO, "2", A, options=["0", "1", "2"]),
                      Field("x265_psy_rd", "Psy-RD", FLOAT, 2.0, A, minimum=0.0, maximum=5.0, step=0.1),
                      Field("x265_psy_rdoq", "Psy-RDOQ", FLOAT, 0.0, A, minimum=0.0, maximum=50.0, step=0.5),
                      Field("x265_deblock", "Деблокинг (tC,Beta)", COMBO, "0,0", A,
                            options=["0,0", "-1,-1", "-2,-2", "1,1"]),
                      Field("x265_no_sao", "Отключить SAO", CHECK, False, A,
                            comment="Сохраняет зерно и мелкие текстуры"),
                      Field("x265_no_fast_pskip", "Отключить Fast P-Skip", CHECK, False, A),
                  ]),
        ]
        # x264
        out += [
            Field("x264_preset", "Пресет x264", COMBO, "medium", G, options=X26X_PRESETS, visible=[is264]),
            Field("x264_rc", "Режим качества", COMBO, "CRF", B, options=["CRF", "Битрейт (ABR)"], visible=[is264],
                  disabled=[Cond("lossless", True)]),
            Field("x264_crf", "CRF / Качество", INT, 23, B, visible=[is264, Cond("x264_rc", "CRF")],
                  disabled=[Cond("lossless", True)], minimum=0, maximum=51),
            *[_with_vis(f, is264) for f in _br_fields("x264_", B, "x264_rc", "Битрейт (ABR)", [])],
            Field("x264_advanced_options", "Расширенные параметры x264", EXPANDER, False, A, visible=[is264],
                  children=[
                      Field("x264_tune", "Tune", COMBO, "Нет", A,
                            options=["Нет", "film", "animation", "grain", "stillimage", "fastdecode", "zerolatency",
                                     "psnr", "ssim"]),
                      Field("x264_profile", "Профиль", COMBO, "Нет", A, options=["Нет", "high", "main", "baseline"]),
                  ]),
        ]
        # SVT-AV1
        out += [
            Field("svt_preset", "Пресет SVT-AV1", INT, 6, G, comment="0 — медленно/лучше … 13 — быстро",
                  visible=[isav1], minimum=0, maximum=13),
            Field("svt_crf", "CRF / Качество", INT, 32, B, comment="0–63, меньше = лучше", visible=[isav1],
                  minimum=0, maximum=63),
            Field("svt_film_grain", "Синтез зерна (film-grain)", INT, 0, A, comment="0 — выкл, 4–10 для аниме/кино",
                  visible=[isav1], minimum=0, maximum=50),
        ]
        return out

    def _codec(self, v: dict[str, Any]) -> str:
        return CPU_CODECS.get(str(v.get("cpu_codec", "HEVC / H.265")), "libx265")

    def args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        codec = self._codec(v)
        ten = ctx.force_10bit and codec != "libx264"
        if codec == "libsvtav1":
            if ctx.lossless:
                raise RuntimeError("Lossless не поддерживается SVT-AV1: выберите HEVC или H.264")
            args = ["-c:v", codec, "-pix_fmt", "yuv420p10le" if ten else "yuv420p",
                    "-preset", str(int(v.get("svt_preset", 6))), "-crf", str(int(v.get("svt_crf", 32)))]
            grain = int(v.get("svt_film_grain", 0))
            if grain > 0:
                args += ["-svtav1-params", f"film-grain={grain}"]
            return args
        if codec == "libx264":
            args = ["-c:v", codec, "-pix_fmt", "yuv420p", "-preset", str(v.get("x264_preset", "medium"))]
            if ctx.lossless:
                args += ["-qp", "0"]
            elif v.get("x264_rc", "CRF") == "CRF":
                args += ["-crf", str(int(v.get("x264_crf", 23)))]
            else:
                args += _br_args(v, "x264_")
            if v.get("x264_advanced_options"):
                if v.get("x264_tune", "Нет") != "Нет":
                    args += ["-tune", str(v["x264_tune"])]
                if v.get("x264_profile", "Нет") != "Нет":
                    args += ["-profile:v", str(v["x264_profile"])]
            return args
        # libx265
        args = ["-c:v", codec, "-pix_fmt", "yuv420p10le" if ten else "yuv420p",
                "-preset", str(v.get("x265_preset", "medium"))]
        params: list[str] = []
        if ctx.lossless:
            params.append("lossless=1")
        elif v.get("x265_rc", "CRF") == "CRF":
            args += ["-crf", str(int(v.get("x265_crf", 23)))]
        else:
            args += _br_args(v, "x265_")
        if v.get("x265_advanced_options"):
            if v.get("x265_tune", "Нет") != "Нет":
                args += ["-tune", str(v["x265_tune"])]
            params.append(f"aq-mode={v.get('x265_aq_mode', '2')}")
            aq = float(v.get("x265_aq_strength", 1.0))
            if abs(aq - 1.0) > 0.01:
                params.append(f"aq-strength={min(3.0, max(0.0, aq)):.1f}")
            la = int(v.get("x265_lookahead", 20))
            if la != 20:
                params.append(f"rc-lookahead={min(250, max(0, la))}")
            bf = int(v.get("x265_bframes", 4))
            if bf != 4:
                params.append(f"bframes={min(16, max(0, bf))}")
            if str(v.get("x265_b_adapt", "2")) != "2":
                params.append(f"b-adapt={v['x265_b_adapt']}")
            prd = float(v.get("x265_psy_rd", 2.0))
            if abs(prd - 2.0) > 0.01:
                params.append(f"psy-rd={min(5.0, max(0.0, prd)):.1f}")
            prdoq = float(v.get("x265_psy_rdoq", 0.0))
            if abs(prdoq) > 0.01:
                params.append(f"psy-rdoq={min(50.0, max(0.0, prdoq)):.1f}")
            deb = str(v.get("x265_deblock", "0,0")).replace(":", ",")
            if deb and deb != "0,0":
                params.append(f"deblock={deb}")
            if v.get("x265_no_sao"):
                params.append("no-sao=1")
            if v.get("x265_no_fast_pskip"):
                params.append("no-fast-pskip=1")
        if params:
            args += ["-x265-params", ":".join(params)]
        return args

    def tag(self, v: dict[str, Any], ctx: EncContext) -> str | None:
        if self._codec(v) == "libx265" and ctx.container.endswith("mp4"):
            return "hvc1"
        return None


def _with_vis(f: Field, cond: Cond) -> Field:
    f.visible = [cond, *f.visible]
    return f


# --- NVENC ----------------------------------------------------------------
NVENC_CODECS = {"HEVC / H.265": "hevc_nvenc", "AVC / H.264": "h264_nvenc", "AV1": "av1_nvenc"}


class NvencEncoder(Encoder):
    id = "nvenc"
    title = "NVENC (NVIDIA)"

    def available(self) -> bool:
        return hw.get_caps().nvenc_ok

    def codec_key(self) -> str:
        return "nvenc_codec"

    def fields(self) -> list[Field]:
        caps = hw.get_caps()
        codecs = [n for n, e in NVENC_CODECS.items() if caps.nvenc.get(e)] or list(NVENC_CODECS)
        G, B, A = "Видео:Кодирование", "Видео:Битрейт", "Видео:Расширенные параметры"
        not_qp = Cond("nvenc_rc", "constqp", negate=True)
        lossless = Cond("lossless", True)
        out = [
            Field("nvenc_codec", "Кодек NVENC", COMBO, codecs[0], G, options=codecs),
            Field("nvenc_preset", "Пресет скорости", COMBO, "p4", G, options=[f"p{i}" for i in range(1, 8)],
                  comment="p1 — быстрее всего, p7 — лучшее сжатие"),
            Field("nvenc_rc", "Режим управления битрейтом", COMBO, "vbr", B, options=["cbr", "vbr", "constqp"],
                  disabled=[lossless]),
            Field("v_qp", "QP / Качество", INT, 23, B, comment="0–51, меньше = лучше",
                  visible=[Cond("nvenc_rc", "constqp")], disabled=[lossless], minimum=0, maximum=51),
        ]
        for f in _br_fields("", B, "nvenc_rc", "constqp", []):
            f.visible = [not_qp]
            out.append(f)
        out += [
            Field("nv_lookahead", "Lookahead (кадров)", INT, 32, A, minimum=0, maximum=32, disabled=[lossless]),
            Field("nv_spatial_aq", "Spatial AQ", CHECK, True, A, disabled=[lossless]),
            Field("nv_aq_strength", "Сила AQ", INT, 8, A, minimum=1, maximum=15,
                  visible=[Cond("nv_spatial_aq", True)], disabled=[lossless]),
            Field("nv_temporal_aq", "Temporal AQ", CHECK, True, A, disabled=[lossless]),
            Field("nv_multipass", "Multipass", COMBO, "fullres", A, options=["disabled", "qres", "fullres"],
                  disabled=[lossless]),
            Field("nv_b_ref_mode", "Опорные B-кадры", COMBO, "middle", A, options=["disabled", "each", "middle"],
                  comment="middle — B-пирамида", disabled=[lossless]),
        ]
        return out

    def _codec(self, v: dict[str, Any]) -> str:
        return NVENC_CODECS.get(str(v.get("nvenc_codec", "HEVC / H.265")), "hevc_nvenc")

    def input_args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        # Декодирование на GPU; кадры автоматически выгружаются в RAM для CPU-фильтров
        return ["-hwaccel", "cuda"]

    def args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        codec = self._codec(v)
        ten = ctx.force_10bit and codec != "h264_nvenc"
        args = ["-c:v", codec, "-pix_fmt", "p010le" if ten else "yuv420p", "-preset", str(v.get("nvenc_preset", "p4"))]
        if ctx.lossless:
            if codec == "av1_nvenc":
                raise RuntimeError("Lossless не поддерживается AV1 NVENC: выберите HEVC или H.264")
            return args + ["-rc", "constqp", "-tune", "lossless", "-qp", "0"]
        rc = str(v.get("nvenc_rc", "vbr"))
        args += ["-rc", rc]
        if rc == "constqp":
            args += ["-qp", str(int(v.get("v_qp", 23)))]
        else:
            args += _br_args(v, "")
        la = int(v.get("nv_lookahead", 32))
        if la > 0:
            args += ["-rc-lookahead", str(min(32, la))]
        if v.get("nv_spatial_aq", True):
            args += ["-spatial-aq", "1", "-aq-strength", str(min(15, max(1, int(v.get("nv_aq_strength", 8)))))]
        else:
            args += ["-spatial-aq", "0"]
        args += ["-temporal-aq", "1" if v.get("nv_temporal_aq", True) else "0"]
        mp = str(v.get("nv_multipass", "fullres"))
        if mp:
            args += ["-multipass", mp]
        bref = str(v.get("nv_b_ref_mode", "middle"))
        if codec == "av1_nvenc" and bref == "each":
            bref = "middle"
        if bref:
            args += ["-b_ref_mode", bref]
        return args

    def tag(self, v: dict[str, Any], ctx: EncContext) -> str | None:
        if self._codec(v) == "hevc_nvenc" and ctx.container.endswith("mp4"):
            return "hvc1"
        return None


# --- VAAPI ----------------------------------------------------------------
VAAPI_CODECS = {"HEVC / H.265": "hevc_vaapi", "AVC / H.264": "h264_vaapi", "AV1": "av1_vaapi"}


class VaapiEncoder(Encoder):
    id = "vaapi"
    title = "VAAPI (AMD / Intel)"

    def available(self) -> bool:
        return hw.get_caps().vaapi_ok

    def codec_key(self) -> str:
        return "vaapi_codec"

    def fields(self) -> list[Field]:
        caps = hw.get_caps()
        codecs = [n for n, e in VAAPI_CODECS.items() if caps.vaapi.get(e)] or list(VAAPI_CODECS)
        G, B, A = "Видео:Кодирование", "Видео:Битрейт", "Видео:Расширенные параметры"
        lossless = Cond("lossless", True)
        out = [
            Field("vaapi_codec", "Кодек VAAPI", COMBO, codecs[0], G, options=codecs),
            Field("vaapi_rc", "Режим управления битрейтом", COMBO, "CQP", B, options=["CQP", "VBR", "CBR", "ICQ"],
                  comment="ICQ — только Intel; CQP — постоянное качество", disabled=[lossless]),
            Field("vaapi_qp", "QP / Качество", INT, 24, B, comment="Меньше = лучше",
                  visible=[Cond("vaapi_rc", ("CQP", "ICQ"))], minimum=0, maximum=51, disabled=[lossless]),
        ]
        for f in _br_fields("va_", B, "vaapi_rc", ("VBR", "CBR"), []):
            f.visible = [Cond("vaapi_rc", ("VBR", "CBR"))]
            out.append(f)
        out += [
            Field("vaapi_compression", "Уровень сжатия (compression_level)", INT, 0, A,
                  comment="0 — по умолчанию драйвера; больше — медленнее и лучше (Intel/AMD по-разному)",
                  minimum=0, maximum=7),
            Field("vaapi_hwdecode", "Аппаратное декодирование", CHECK, False, A,
                  comment="Быстрее, но недоступны вшивание субтитров и обрезка на CPU"),
        ]
        return out

    def _codec(self, v: dict[str, Any]) -> str:
        return VAAPI_CODECS.get(str(v.get("vaapi_codec", "HEVC / H.265")), "hevc_vaapi")

    def input_args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        dev = ctx.vaapi_device or hw.pick_vaapi_device()
        if v.get("vaapi_hwdecode"):
            return ["-hwaccel", "vaapi", "-hwaccel_device", dev, "-hwaccel_output_format", "vaapi"]
        return ["-vaapi_device", dev]

    def filter_suffix(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        if v.get("vaapi_hwdecode"):
            ten = ctx.force_10bit and self._codec(v) != "h264_vaapi"
            return [f"scale_vaapi=format={'p010' if ten else 'nv12'}"]
        ten = ctx.force_10bit and self._codec(v) != "h264_vaapi"
        return [f"format={'p010' if ten else 'nv12'}", "hwupload"]

    def args(self, v: dict[str, Any], ctx: EncContext) -> list[str]:
        if ctx.lossless:
            raise RuntimeError("Lossless не поддерживается VAAPI: выберите CPU-энкодер")
        codec = self._codec(v)
        args = ["-c:v", codec]
        rc = str(v.get("vaapi_rc", "CQP"))
        args += ["-rc_mode", rc]
        if rc in ("CQP", "ICQ"):
            args += ["-qp" if rc == "CQP" else "-global_quality", str(int(v.get("vaapi_qp", 24)))]
        else:
            args += _br_args(v, "va_")
        comp = int(v.get("vaapi_compression", 0))
        if comp > 0:
            args += ["-compression_level", str(comp)]
        if ctx.force_10bit and codec == "hevc_vaapi":
            args += ["-profile:v", "main10"]
        return args

    def tag(self, v: dict[str, Any], ctx: EncContext) -> str | None:
        if self._codec(v) == "hevc_vaapi" and ctx.container.endswith("mp4"):
            return "hvc1"
        return None


ALL_ENCODERS: list[Encoder] = [CpuEncoder(), NvencEncoder(), VaapiEncoder()]


def available_encoders() -> list[Encoder]:
    return [e for e in ALL_ENCODERS if e.available()]


def by_id(enc_id: str) -> Encoder | None:
    enc_id = {"x265": "cpu"}.get(enc_id, enc_id)
    for e in ALL_ENCODERS:
        if e.id == enc_id or e.title == enc_id:
            return e
    return None
