"""Юнит-тесты ядра (без GUI): pytest tests/"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
os.environ.setdefault("XDG_CONFIG_HOME", "/tmp/kt-test-config")

from hktools import encoders  # noqa: E402
from hktools.core.fields import CHECK, COMBO, INT, KEYWORDS, Cond, Field, coerce  # noqa: E402
from hktools.core.script import QueueItem, apply_rename  # noqa: E402
from hktools.media import subtitles as subs  # noqa: E402
from hktools.scripts.audio_channels import layout_channels  # noqa: E402
from hktools.scripts.mkv_assembly import infer_title, is_companion, subs_role  # noqa: E402
from hktools.scripts.streams import TrackExtractor  # noqa: E402
from hktools.scripts.subtitles import apply_filters  # noqa: E402
from hktools.scripts.video_encode import compute_scale  # noqa: E402

ASS = """[Script Info]
ScriptType: v4.00+

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1
Style: Ital,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,1,0,0,100,100,0,0,1,3,1,2,60,60,50,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:01.00,0:00:02.50,Default,Alice,0,0,0,,Привет, {\\i1}мир{\\i0}, а, б
Dialogue: 0,0:00:03.00,0:00:04.00,Ital,Signs,0,0,0,,ВЫВЕСКА
Dialogue: 0,0:00:05.00,0:00:06.00,Default,Bob,0,0,0,,Строка\\NКАПС ТУТ

[Fonts]
fontname: x.ttf
"""


def test_parse_time_and_shift():
    assert subs.parse_time("0:01:02.34") == 62340
    assert subs.parse_time("00:01:02,345") == 62345
    assert subs.parse_time("01:02.5") == 62500
    assert subs.parse_shift("1500") == 1500
    assert subs.parse_shift("-0:00:01.50") == -1500
    assert subs.parse_shift("2,5") == 2500
    assert subs.fmt_ass_time(62345) == "0:01:02.35"
    assert subs.fmt_srt_time(62345) == "00:01:02,345"


def test_ass_parse_and_roundtrip():
    doc = subs.parse_ass(ASS)
    assert len(doc.dialogues) == 3
    d = doc.dialogues[0]
    assert d.actor == "Alice" and d.text.endswith("а, б")  # запятые в тексте сохраняются
    assert doc.styles["Ital"].italic
    out = subs.to_ass(doc)
    assert "[Fonts]" in out and out.count("Dialogue:") == 3


def test_srt_vtt_render():
    doc = subs.parse_ass(ASS)
    srt = subs.to_srt(doc, keep_styles=True)
    assert "Привет, <i>мир</i>, а, б" in srt
    assert "<i>ВЫВЕСКА</i>" in srt  # курсив из стиля
    vtt = subs.to_vtt(doc)
    assert vtt.startswith("WEBVTT") and "00:00:01.000 --> 00:00:02.500" in vtt


def test_srt_parse():
    doc = subs.parse_srt("1\n00:00:01,000 --> 00:00:02,000\n<i>Hi</i>\nthere\n\n2\n00:00:03,000 --> 00:00:04,000\nX\n")
    assert len(doc.dialogues) == 2
    assert doc.dialogues[0].text == "{\\i1}Hi{\\i0}\\Nthere"


def test_shift_text_preserves_formatting():
    text, n = subs.shift_text(ASS, "ass", 1000)
    assert n == 3
    assert "Dialogue: 0,0:00:02.00,0:00:03.50,Default,Alice" in text
    assert "[Fonts]" in text
    srt = "1\n00:00:01,000 --> 00:00:02,000\n<font color=red>Hi</font>\n"
    out, n = subs.shift_text(srt, "srt", -1500)
    assert "00:00:00,000 --> 00:00:00,500" in out and "<font color=red>" in out


def test_strip_caps():
    assert subs.strip_caps("Строка\\NКАПС ТУТ") == "Строка"
    assert subs.strip_caps("ВСЁ КАПСОМ") == ""
    assert subs.strip_caps("Normal OK") == "Normal OK"


def test_apply_filters():
    doc = subs.parse_ass(ASS)
    vals = {"exclude_actors": [{"word": "Signs", "active": True}], "strip_caps": True, "strip_formatting": True,
            "text_patterns": [{"word": ", а, б", "active": True, "only_part": True}]}
    states = apply_filters(doc, vals)
    kept = [s for s in states if not s.deleted]
    assert [s.text for s in kept] == ["Привет, мир", "Строка"]
    states = apply_filters(doc, vals, manual_in={1})
    assert not next(s for s in states if s.index == 1).deleted


def test_rename():
    cfg = {"local_rename_override": True, "local_rename_search": r" - (\d+)", "local_rename_replace": " [${num:2}-$1]"}
    assert apply_rename("Show - 07", 3, cfg) == "Show [03-07]"
    assert apply_rename("Show", 1, {}) == "Show"


def test_scale():
    assert compute_scale(1920, 800, "0.5") == (864, 360)
    assert compute_scale(1920, 1080, "0.5") == (852, 480)
    assert isinstance(compute_scale(1920, 1080, "1"), str)


def test_mux_matching():
    assert is_companion("Ep01", "Ep01.dub")
    assert is_companion("Ep01", "ep01 [signs]")
    assert not is_companion("Ep01", "Ep012")
    item = QueueItem("/x/Ep01.signs.ass")
    assert subs_role("Ep01", item) == "signs"
    assert subs_role("Ep01", QueueItem("/x/Ep01.ass")) == "full"
    assert subs_role("Ep01", QueueItem("/x/Ep01.en.ass")) == "other"
    assert infer_title("Ep01", "Ep01.AniLibria") == "AniLibria"
    assert infer_title("Ep01", "Ep01.rus") == ""


def test_fields():
    f = Field("x", "X", INT, 5, minimum=0, maximum=10)
    assert coerce(f, "12") == 10
    assert coerce(Field("c", "C", CHECK, False), "yes") is True
    assert coerce(Field("o", "O", COMBO, "A", options=["A", "Bb"]), "bb") == "Bb"
    assert coerce(Field("k", "K", KEYWORDS, []), "rus|eng") == [{"word": "rus", "active": True},
                                                               {"word": "eng", "active": True}]
    assert Cond("mode", ("a", "b")).test({"mode": "b"})
    assert Cond("flag", True, negate=True).test({"flag": False})


def test_encoder_args():
    cpu = encoders.CpuEncoder()
    ctx = encoders.EncContext(False, True, ".mp4")
    v = {"cpu_codec": "HEVC / H.265", "x265_preset": "slow", "x265_rc": "CRF", "x265_crf": 20,
         "x265_advanced_options": True, "x265_aq_mode": "3", "x265_no_sao": True}
    args = cpu.args(v, ctx)
    assert args[:4] == ["-c:v", "libx265", "-pix_fmt", "yuv420p10le"]
    assert "-x265-params" in args and "aq-mode=3:no-sao=1" in args
    assert cpu.tag(v, ctx) == "hvc1"
    nv = encoders.NvencEncoder()
    a = nv.args({"nvenc_codec": "AV1", "nvenc_rc": "vbr", "v_bitrate": 3000, "auto_bitrate": True,
                 "nv_b_ref_mode": "each"}, encoders.EncContext(False, False, ".mkv"))
    assert "-maxrate" in a and a[a.index("-maxrate") + 1] == "6000k"
    assert a[a.index("-b_ref_mode") + 1] == "middle"
    va = encoders.VaapiEncoder()
    assert va.filter_suffix({"vaapi_codec": "HEVC / H.265"}, encoders.EncContext(False, True, ".mkv")) == \
        ["format=p010", "hwupload"]


def test_layouts():
    assert layout_channels("5.1(side)", 6) == ["FL", "FR", "FC", "LFE", "SL", "SR"]
    assert layout_channels("", 2) == ["FL", "FR"]


def test_extractor_name():
    from hktools.media.probe import Track

    t = Track(index=3, type="subtitle", type_index=0, codec="ass", language="rus", title="Надписи")
    assert TrackExtractor.format_name("{original}_{id}_{lang}", "Ep 01", t, "") == "Ep 01_3_rus"
    t2 = Track(index=1, type="audio", type_index=0, codec="ac3", language="und")
    assert TrackExtractor.format_name("{original}_{lang}_{id}", "Ep", t2, "") == "Ep_1"
