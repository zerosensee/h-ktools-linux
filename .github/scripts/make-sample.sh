#!/usr/bin/env bash
# Тестовые медиафайлы для CI: видео 8 с + 5.1 AC3 + моно AAC + две дорожки ASS + шрифт, донор FLAC, внешние субтитры.
set -euo pipefail
OUT=${1:-/tmp/kt}
mkdir -p "$OUT" && cd "$OUT"
FONT=$(fc-match -f '%{file}' 'DejaVu Sans' 2>/dev/null || true)
cat > sample.ass <<'ASS'
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,DejaVu Sans,60,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1
Style: Italic,DejaVu Sans,60,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,1,0,0,100,100,0,0,1,3,1,2,60,60,50,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.50,0:00:02.00,Default,Signs,0,0,0,,{\an8}ВЫВЕСКА МАГАЗИНА
Dialogue: 0,0:00:01.00,0:00:03.00,Default,Alice,0,0,0,,Привет, {\i1}мир{\i0}!
Dialogue: 0,0:00:03.00,0:00:05.00,Italic,Bob,0,0,0,,Как дела?\NЧТО ЭТО
Dialogue: 0,0:00:06.00,0:00:07.00,Default,Alice,0,0,0,,[музыка]
ASS
grep -v 'Alice\|Bob' sample.ass > signs.ass
cp signs.ass sample.signs.ass
printf '1\n00:00:01,000 --> 00:00:03,000\nЗамена\n' > rep.srt
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=1920x1080:rate=24000/1001:duration=8,drawbox=y=0:h=140:color=black:t=fill,drawbox=y=940:h=140:color=black:t=fill" \
  -f lavfi -i "sine=frequency=440:duration=8:sample_rate=48000" \
  -f lavfi -i "sine=frequency=880:duration=8:sample_rate=48000" \
  -i sample.ass -i signs.ass \
  -filter_complex "[1:a]asplit=6[a][b][c][d][e][f];[a][b][c][d][e][f]join=inputs=6:channel_layout=5.1(side)[s51]" \
  -map 0:v -map "[s51]" -map 2:a -map 4:s -map 3:s \
  -c:v libx264 -preset ultrafast -crf 30 -c:a:0 ac3 -b:a:0 384k -c:a:1 aac -c:s ass \
  -metadata:s:a:0 language=rus -metadata:s:a:0 title=Дубляж -metadata:s:a:1 language=eng \
  -metadata:s:s:0 language=rus -metadata:s:s:0 title=Надписи -metadata:s:s:1 language=rus -metadata:s:s:1 title=Полные \
  ${FONT:+-attach "$FONT" -metadata:s:t mimetype=application/x-truetype-font} \
  -metadata title="Тестовый файл" sample.mkv
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:duration=8:sample_rate=48000" -af adelay=250 -c:a flac sample.dub.flac
ls -la "$OUT"
