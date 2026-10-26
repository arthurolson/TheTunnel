#!/bin/bash
# Copies wall-texture images to the Textures folder of the MR Tunnel app on connected Quest headsets.
# Textures that everyone should have belong in the build instead: put them in Assets/Tunnel/Resources/TunnelTextures.
# This folder is for extra images without a new build (needs developer mode). The headset that hosts a session
# shares these with everyone who joins, so only hosts need them.
#
#   ./push-textures.sh                      push everything in ./tunnel-textures
#   ./push-textures.sh a.tif b.jpg dir/     push these files / folders (and .zip downloads)
#   ./push-textures.sh -l                   list the textures on the headset(s)
#   ./push-textures.sh -d "bricks.png"      delete a texture from the headset(s)
#   -s SERIAL                               only this headset (see: adb devices)
#
# PNG and JPEG are copied as they are (scaled down if over 2048 px; the app would do that anyway).
# TIFF, HEIC, BMP, GIF and WebP are converted to PNG with macOS's sips, since the app can only read PNG/JPEG.
# For a .zip (e.g. a Poly Haven / ambientCG download) only the colour map (diff/albedo/basecolor/color) is used.
# After pushing, press Refresh in the TS Menu > Textures list.

set -euo pipefail

PACKAGE="edu.scripps.tunnel"
DEST="/sdcard/Android/data/$PACKAGE/files/Textures"
MAX=2048
HERE="$(cd "$(dirname "$0")" && pwd)"

ADB="${ADB:-$(command -v adb || true)}"
[ -z "$ADB" ] && ADB="$(ls /Applications/Unity/Hub/Editor/*/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb 2>/dev/null | tail -1)"
[ -z "$ADB" ] && { echo "adb not found (install Android platform-tools or Unity's Android module)"; exit 1; }

SERIAL=""; MODE=push; TARGET=""
while getopts "s:ld:h" opt; do
  case $opt in
    s) SERIAL="$OPTARG" ;;
    l) MODE=list ;;
    d) MODE=delete; TARGET="$OPTARG" ;;
    *) sed -n '2,17p' "$0"; exit 0 ;;
  esac
done
shift $((OPTIND - 1))

if [ -n "$SERIAL" ]; then
  DEVICES="$SERIAL"
else
  DEVICES="$("$ADB" devices | awk 'NR>1 && $2=="device" {print $1}')"
fi
if [ -z "$DEVICES" ]; then
  echo "No authorized headset. Plug one in, put it on and accept 'Allow USB debugging', then try again."
  "$ADB" devices | awk 'NR>1 && NF'
  exit 1
fi

if [ "$MODE" = list ]; then
  for d in $DEVICES; do echo "== $d"; "$ADB" -s "$d" shell "ls -l '$DEST' 2>/dev/null || echo '(no Textures folder yet)'"; done
  exit 0
fi
if [ "$MODE" = delete ]; then
  for d in $DEVICES; do "$ADB" -s "$d" shell "rm -f '$DEST/$TARGET'" && echo "$d: removed $TARGET"; done
  exit 0
fi

[ $# -eq 0 ] && set -- "$HERE/tunnel-textures"

STAGE="$(mktemp -d)"
WORK="$(mktemp -d)"
trap 'rm -rf "$STAGE" "$WORK"' EXIT

longest() { sips -g pixelWidth -g pixelHeight "$1" 2>/dev/null | awk '/pixel(Width|Height)/ {if ($2>m) m=$2} END {print m+0}'; }

stage_image() {
  local f="$1" base ext out
  base="$(basename "$f")"; ext="$(echo "${base##*.}" | tr '[:upper:]' '[:lower:]')"
  case "$ext" in
    png|jpg|jpeg)
      out="$STAGE/$base"
      if [ "$(longest "$f")" -gt $MAX ]; then sips -Z $MAX "$f" --out "$out" >/dev/null; else cp "$f" "$out"; fi ;;
    tif|tiff|heic|heif|bmp|gif|webp)
      out="$STAGE/${base%.*}.png"
      if [ "$(longest "$f")" -gt $MAX ]; then sips -s format png -Z $MAX "$f" --out "$out" >/dev/null
      else sips -s format png "$f" --out "$out" >/dev/null; fi ;;
    *) echo "  skip $base (not an image the app can use)"; return ;;
  esac
  echo "  + $(basename "$out")"
}

stage_zip() {
  local z="$1" dir found=0
  dir="$WORK/$(basename "$z" .zip)"; mkdir -p "$dir"
  unzip -q -o "$z" -d "$dir" -x "*.blend" "*.exr" "*.fbx" "*.gltf" "*.bin" "*.usd*" 2>/dev/null || true
  while IFS= read -r -d '' f; do stage_image "$f"; found=1
  done < <(find "$dir" -type f \( -iname "*diff*" -o -iname "*albedo*" -o -iname "*basecolor*" -o -iname "*base_color*" -o -iname "*_col_*" -o -iname "*_col.*" -o -iname "*color*" \) \
             \( -iname "*.jpg" -o -iname "*.jpeg" -o -iname "*.png" -o -iname "*.tif" -o -iname "*.tiff" \) -print0)
  [ $found = 0 ] && echo "  no colour map found in $(basename "$z")"
  return 0
}

stage_any() {
  local f="$1"
  if [ -d "$f" ]; then
    while IFS= read -r -d '' g; do stage_any "$g"; done < <(find "$f" -maxdepth 1 -type f ! -name ".*" -print0 | sort -z)
  elif [[ "$(echo "$f" | tr '[:upper:]' '[:lower:]')" == *.zip ]]; then
    stage_zip "$f"
  else
    stage_image "$f"
  fi
}

echo "Preparing images..."
for f in "$@"; do
  [ -e "$f" ] || { echo "  missing: $f"; continue; }
  stage_any "$f"
done

if [ -z "$(ls -A "$STAGE")" ]; then echo "Nothing to push."; exit 1; fi

for d in $DEVICES; do
  echo "Pushing to $d..."
  "$ADB" -s "$d" shell "mkdir -p '$DEST'"
  for f in "$STAGE"/*; do "$ADB" -s "$d" push "$f" "$DEST/" >/dev/null && echo "  $d: $(basename "$f")"; done
done
echo "Done. In the tunnel: TS Menu > Textures > Refresh."
