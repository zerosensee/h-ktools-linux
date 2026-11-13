#!/usr/bin/env bash
# Функциональная проверка инструментов через CLI на тестовых файлах.
set -uo pipefail
HK=${HK:-"dotnet build/hktools/hktools.dll"}
S=${1:-/tmp/kt}
O=$(mktemp -d)
export XDG_CONFIG_HOME=$O/cfg XDG_CACHE_HOME=$O/cache XDG_STATE_HOME=$O/state XDG_RUNTIME_DIR=$O/run
mkdir -p "$XDG_RUNTIME_DIR"
FAIL=0
run() {  # имя_проверки ожидаемый_файл -- аргументы hktools run
  local name=$1 expect=$2; shift 3
  if $HK run "$@" >"$O/log.txt" 2>&1 && [ -s "$expect" ]; then echo "✓ $name"
  else echo "✗ $name"; sed 's/^/    /' "$O/log.txt"; FAIL=1; fi
}
run metadata-cleanup  "$O/meta/sample.mkv"            -- metadata-cleanup "$S/sample.mkv" -o "$O/meta"
run audio-encode-flac "$O/aenc/sample_a1.flac"        -- audio-encode "$S/sample.mkv" -o "$O/aenc" --set target_format=FLAC --set "audio_track=Все (отдельные файлы)"
run audio-encode-opus "$O/aenc/sample.opus"           -- audio-encode "$S/sample.mkv" -o "$O/aenc" --set target_format=OPUS
run audio-downmix     "$O/dm/sample_stereo.eac3"      -- audio-downmix "$S/sample.mkv" -o "$O/dm"
run audio-speed       "$O/sp/sample.flac"             -- audio-speed "$S/sample.mkv" -o "$O/sp"
run audio-channels    "$O/ch/sample.SLSR.wav"         -- audio-channels "$S/sample.mkv" -o "$O/ch"
run audio-shift       "$O/sh/sample_shift+250ms.mka"  -- audio-shift "$S/sample.mkv" -o "$O/sh" --set ShiftMs=250
run audio-transplant  "$O/tr/sample.mkv"              -- audio-transplant "$S/sample.mkv" -o "$O/tr" --set "transplant_source=$S/sample.dub.flac" --set transplant_shift_ms=-250
run container-mp4     "$O/cc/sample.mp4"              -- container-convert "$S/sample.mkv" -o "$O/cc"
run mkv-assembly      "$O/asm/sample.mkv"             -- mkv-assembly "$S/sample.mkv" "$S/sample.dub.flac" "$S/sample.signs.ass" -o "$O/asm"
run stream-management "$O/sm/sample.mkv"              -- stream-management "$S/sample.mkv" -o "$O/sm" --tracks a:1,s:1
run stream-replace    "$O/sr/sample.mkv"              -- stream-replacement "$S/sample.mkv" -o "$O/sr" --replace "4=$S/rep.srt"
run track-extractor   "$O/te/sample_2_eng.aac"        -- track-extractor "$S/sample.mkv" -o "$O/te" --set "name_format={original}_{id}_{lang}"
run subtitles-convert "$O/sc/sample.srt"              -- subtitles-convert "$S/sample.ass" -o "$O/sc" --set target_format=SRT
run subtitle-shift    "$O/ss/sample.ass"              -- subtitle-shift "$S/sample.ass" -o "$O/ss" --set ShiftMs=1500
run video-encode      "$O/ve/sample.mkv"              -- video-encode "$S/sample.mkv" -o "$O/ve" --set "cpu_codec=AVC / H.264" --set x264_preset=ultrafast --set autocrop_enabled=true --set resize_enabled=true
run bitrate-png       "$O/br/sample_bitrate.png"      -- bitrate-viewer "$S/sample.mkv" -o "$O/br" --set save_png_export=true
if [ -s "$O/ve/sample.mkv" ]; then
  RES=$(ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 "$O/ve/sample.mkv")
  [ "$RES" = "864,360" ] && echo "✓ автокроп+масштаб: $RES" || { echo "✗ автокроп+масштаб: $RES (ожидалось 864,360)"; FAIL=1; }
fi
TITLE=$(ffprobe -v error -show_entries stream_tags=title -of csv=p=0 "$O/tr/sample.mkv" 2>/dev/null | head -3 | tr '\n' ' ')
echo "  дорожки после пересадки: $TITLE"
exit $FAIL
