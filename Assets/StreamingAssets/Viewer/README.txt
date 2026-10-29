Media for the Viewer, built into the app. One folder per menu list:
  Images          PNG or JPEG
  Videos          MP4 (H.264/H.265), MOV, WebM
  Spatial Images  stereo PNG or JPEG
  Spatial Videos  stereo MP4/MOV/WebM
A folder can also hold a link instead of the file: a .url, .webloc or .txt file containing the http(s) address
of the image or video (a direct link to the file, not a web page such as YouTube).
The list shows each file's name without its extension.

Spatial media is side by side (left eye on the left) unless its name says otherwise:
  _tb / _ou / top-bottom / over-under   top and bottom (left eye on top)
  _sbs / _lr / side-by-side             side by side
  _rl                                   side by side, right eye first
  _hsbs / _half                         each eye squeezed to half width (shown at full width)
Without a tag, a frame taller than wide is taken as top and bottom.
Apple spatial photos (HEIC) and spatial videos (MV-HEVC) must be converted to side by side first.

Media files here are not committed to git (see .gitignore); large videos make the APK large.
For media without a rebuild, use push-media.sh to copy files onto a headset.
