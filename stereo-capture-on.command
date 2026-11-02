#!/bin/bash
# Double-click (in Finder) to make the Quest's own screenshots and recordings spatial: both eyes side by side, which
# the Viewer lists under Spatial Images / Spatial Videos. Needs each headset plugged in over USB (developer mode).
# The Quest forgets this whenever it restarts or runs out of power, so run it again after that.

PATH="/opt/local/bin:/opt/homebrew/bin:/usr/local/bin:$HOME/Library/Android/sdk/platform-tools:$PATH"

finish() {
    echo
    read -n 1 -s -r -p "Press any key to close."
    echo
    exit "$1"
}

if ! command -v adb >/dev/null; then
    echo "adb not found. Install Android platform tools, then try again."
    finish 1
fi

headsets=$(adb devices | awk 'NR > 1 && $2 == "device" { print $1 }')
unauthorized=$(adb devices | awk 'NR > 1 && $2 == "unauthorized" { print $1 }')
if [ -n "$unauthorized" ]; then
    echo "Put on the headset and allow USB debugging for this Mac, then run this again."
fi
if [ -z "$headsets" ]; then
    echo "No headset found. Plug one in over USB and wake it up, then run this again."
    finish 1
fi

for serial in $headsets; do
    model=$(adb -s "$serial" shell getprop ro.product.model | tr -d '\r')
    # Both eyes, 2880 x 1440 videos (1440 x 1440 each), at the headset's usual capture rate and a high bit rate.
    adb -s "$serial" shell 'setprop debug.oculus.screenCaptureEye 2;
        setprop debug.oculus.capture.width 2880; setprop debug.oculus.capture.height 1440;
        setprop debug.oculus.fullRateCapture 0; setprop debug.oculus.capture.bitrate 40000000'
    if [ "$(adb -s "$serial" shell getprop debug.oculus.screenCaptureEye | tr -d '\r')" = "2" ]; then
        echo "$model ($serial): stereo capture on."
    else
        echo "$model ($serial): couldn't turn stereo capture on."
    fi
done
echo "Until the headset restarts, its screenshots and recordings will be spatial."
finish 0
