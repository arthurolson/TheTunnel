# The Tunnel: to do

The plan ahead. It changes as work goes on; what's done is in [STATUS.md](STATUS.md). Each area has its own agent
(see [AGENTS.md](AGENTS.md)); each piece of work starts from a detailed request.

Items under "Ideas" are suggestions only, not yet agreed.

## 1. Painting: better tracking and sensory feedback

From the project's list: improve the painting activity to enable better tracking and sensory feedback.

Open now:
- Install 10.11.2026.1 and test the real marker: colour, the three touches, the panel's corner marks, painting,
  sizing the brush by pinching, a tunnel wall as the canvas, and Copy to wall (with a second player). Adjust the
  touch thresholds, hold stillness and mark detection from the [RealMarker] log.

Ideas:
- Touch you can feel on the virtual marker: a controller vibration when it touches the wall, and a soft drawing
  sound.
- If judging touch from one camera isn't reliable enough: true depth from both passthrough cameras (Meta's MRUK
  package; a large new dependency).
- Show real-marker drawings to other players aligned to the same room.

## 2. 3D movies and scenarios in the containers

From the project's list: improve the 3D movie and scenario activities in the containers.

Open now:
- Check SARS-CoV-2 in the movie and the movie panel's wall placement on a headset.

Ideas:
- Load a saved starting state from 3D Movies, not only save one.
- Record a run and replay it.
- More actors and binding rules beyond antibodies on capsids.

## 3. Viewer and browser

From the project's list: advance the viewer and browser capabilities.

Open now:
- Check the transparent images on a headset.

Ideas:
- Videos that only play inside their own site's player (YouTube and the like): today the Viewer can show only
  their picture.
- A slideshow of a media folder, for presenting.

## Housekeeping

- Local changes not yet committed, from before this list: the chess scene, XR settings, the package manifest and
  lock, physics settings and other Project Settings lines. Decide whether to keep or drop each.
- Leftovers that can be removed: Assets/_Recovery/, mono_crash.0.0.json, mybuildtest.apk and its backup folder.
- Turn the grab diagnostics logging off once grabbing is reliable.
