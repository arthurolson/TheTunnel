#!/bin/bash
# Copies images, videos and links into the Viewer's folders on connected Quest headsets, so they show in the
# Viewer's lists without a new build (needs developer mode). Media everyone should have belongs in the build instead:
# Assets/StreamingAssets/Viewer/<Images | Videos | Spatial Images | Spatial Videos>.
# Only headsets that have a file can show it; for sharing between headsets prefer built-in media or links.
#
#   ./push-media.sh images a.jpg b.png         push to Images        (kinds: images, videos, spatial-images, spatial-videos)
#   ./push-media.sh videos clip.mp4 folder/    push files / a folder's files to Videos
#   ./push-media.sh -l                         list the media on the headset(s)
#   ./push-media.sh -d videos "clip.mp4"       delete a file from a headset folder
#   -s SERIAL                                  only this headset (see: adb devices)
#
# A link is a .url, .webloc or .txt file containing the http(s) address of the image or video itself.
# After pushing, close and reopen the list in the Viewer (or press its Refresh button).

set -euo pipefail

PACKAGE="edu.scripps.tunnel"
ROOT="/sdcard/Android/data/$PACKAGE/files/Viewer"

ADB="${ADB:-$(command -v adb || true)}"
[ -z "$ADB" ] && ADB="$(ls /Applications/Unity/Hub/Editor/*/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb 2>/dev/null | tail -1)"
[ -z "$ADB" ] && { echo "adb not found (install Android platform-tools or Unity's Android module)"; exit 1; }

folder_for() {
  case "$(echo "$1" | tr '[:upper:]' '[:lower:]')" in
    images|image) echo "Images" ;;
    videos|video) echo "Videos" ;;
    spatial-images|spatial-image|spatialimages) echo "Spatial Images" ;;
    spatial-videos|spatial-video|spatialvideos) echo "Spatial Videos" ;;
    *) echo "" ;;
  esac
}

SERIAL=""; MODE=push
while getopts "s:ldh" opt; do
  case $opt in
    s) SERIAL="$OPTARG" ;;
    l) MODE=list ;;
    d) MODE=delete ;;
    *) sed -n '2,14p' "$0"; exit 0 ;;
  esac
done
shift $((OPTIND - 1))

if [ -n "$SERIAL" ]; then DEVICES="$SERIAL"; else DEVICES="$("$ADB" devices | awk 'NR>1 && $2=="device" {print $1}')"; fi
if [ -z "$DEVICES" ]; then
  echo "No authorized headset. Plug one in, put it on and accept 'Allow USB debugging', then try again."
  exit 1
fi

if [ "$MODE" = list ]; then
  for d in $DEVICES; do echo "== $d"; "$ADB" -s "$d" shell "cd '$ROOT' 2>/dev/null && ls -lR || echo '(no Viewer folders yet: open a Viewer once)'"; done
  exit 0
fi

[ $# -lt 2 ] && { sed -n '2,14p' "$0"; exit 1; }
FOLDER="$(folder_for "$1")"; shift
[ -z "$FOLDER" ] && { echo "Kind must be images, videos, spatial-images or spatial-videos"; exit 1; }
DEST="$ROOT/$FOLDER"

if [ "$MODE" = delete ]; then
  for d in $DEVICES; do "$ADB" -s "$d" shell "rm -f '$DEST/$1'" && echo "$d: removed $FOLDER/$1"; done
  exit 0
fi

FILES=()
for f in "$@"; do
  if [ -d "$f" ]; then
    while IFS= read -r -d '' g; do FILES+=("$g"); done < <(find "$f" -maxdepth 1 -type f ! -name ".*" -print0 | sort -z)
  elif [ -e "$f" ]; then
    FILES+=("$f")
  else
    echo "  missing: $f"
  fi
done
[ ${#FILES[@]} -eq 0 ] && { echo "Nothing to push."; exit 1; }

for d in $DEVICES; do
  echo "Pushing to $d ($FOLDER)..."
  "$ADB" -s "$d" shell "mkdir -p '$DEST'"
  for f in "${FILES[@]}"; do "$ADB" -s "$d" push "$f" "$DEST/" >/dev/null && echo "  $d: $(basename "$f")"; done
done
echo "Done. In the Viewer: reopen the list or press Refresh."
