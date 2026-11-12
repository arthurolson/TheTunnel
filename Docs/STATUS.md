# The Tunnel: status

What has been built, what is in progress, and where each build is. Updated as work lands; the plan ahead is in
[TODO.md](TODO.md). Last updated 12 November 2026 (version 12.11.2026.2).

## Accomplished

### Platform and delivery
- Unity 6000.6.7f1 (URP) with OpenXR for Meta Quest 3 and 3S, XR Interaction Toolkit 3, Netcode for GameObjects
  multiplayer and Vivox voice.
- Store builds signed with the release key and stamped with a version of the form DD.MM.YYYY.N; uploaded to the
  Meta store's ArtDev channel (app edu.scripps.tunnel) or installed on headsets directly.
- Editor builders (the Tunnel menu in Unity) regenerate the prefabs for molecules, containers, the 3D movie, paint,
  the Viewer and the SARS-CoV-2 virion, so changes are repeatable.

### The tunnel and the room
- Tunnel segments placed in the real room. Added segments lock to the tunnel's end. The upper walls and arch always
  cover the real ceiling, even a low one.
- Mixed reality occlusion from the Space Setup room mesh. Real people and objects hide virtual ones, using the
  headset's live depth, with smoothed, slightly widened edges.
- Co-location: a printed QR code on the floor (Printables/) lines up every player's headset with the same real room.
  Players aligned this way see each other's real bodies instead of avatars.
- Shared TS Menu: everyone sees the same menus and objects.

### Molecules
- Molecule toys built from Assets/Molecules, chosen from the TS Menu's Molecules list. A slider magnifies them
  from 1x to 10x, and new molecules don't overlap.
- Physics:
  - each molecule's mass comes from its volume;
  - molecules bounce, and drift when pushed;
  - gravity can be turned on or off;
  - the host can toggle gravity from a palm button.
- IgG with a tightest-fit ellipsoid collider.
- SARS-CoV-2 virion built from its bead model: about 310,000 triangles, with colliders on its membrane and on each of
  its 30 spikes.
- The molecule library simplifies any model over 200,000 vertices.
- Delete mode in the TS Menu.

### Containers
- Box and sphere containers. A box has quarter-inch rod edges, and a Collider check box sets whether its walls hold
  molecules in.
- Viscosity (0 to 1) and temperature (0 to 100 °C). Molecules inside move randomly (Brownian) or ballistically, and
  random motion spins them too.
- Mixed reality boxes:
  - opaque walls and a glass front;
  - let go against a tunnel wall, a box sets itself into the wall, which opens there;
  - Unsnap from wall pulls the box back out, with the molecules inside;
  - balls on the front corners stretch the front.

### 3D movie (in a container)
- Capsids carry invisible antibody binding sites, found from each capsid's surface: TBSV on its 2-folds, poliovirus
  on its 5-folds, Zika on its 3-folds.
- SARS-CoV-2 takes part too: each of its 30 spikes is a binding site, and an IgG binds at the spike's tip.
- How binding works:
  - an IgG that touches a site is drawn in by a Fab tip and binds;
  - each binding darkens the capsid and adds the antibody's mass;
  - a fourth antibody neutralizes the capsid, which turns see-through but still collides;
  - the movie ends when every capsid is neutralized.
- Movie controls on a panel beside the container (flat on the wall next to a mixed reality box):
  - Start, Pause and Play;
  - Show binding sites, and a slider for the sites' size;
  - save the starting state by name to 3D Movies.

### Painting
- The TS Menu's Paint button places a palette and a marker:
  - the palette has a colour chooser and five colour disks;
  - its modes are Paint, Pencil (straight or Bezier lines), Airbrush and Eraser, plus Clear all marks;
  - the marker's diameter sets the size of its marks.
- Marks go on the tunnel walls and arch, shared with every player and with players who join later.
- The palette snaps flat onto a wall.
- Double-clicking the holder calls the marker back from anywhere.
- The marker's grip lights up while it's marking.
- The airbrush sprays from up to 30 cm away: wider and fainter the farther it is.
- The virtual marker follows the hand every frame instead of in physics steps (09.11.2026.3).
- Real marker (10.11.2026.1; first tried 09.11.2026.3–.4 on a whiteboard found by a QR code): the palette's Real
  marker button tracks a physical marker by its blue cap through the passthrough camera.
  - Its colour and diameter are learned with the marker held sideways against white (the cap is 1.5 in long).
  - The surface is set by touching the cap's tip to three spots, each held still for 4 s until a beep and a dot.
  - The canvas is a white panel with a dark lettered box on each corner, outlined dark; with no marks, a tunnel wall
    lying on the touched surface (that paint is shared).
  - The tip is the cap's end away from the hand holding it. A disk at the tip shows the brush's colour and size;
    pinching it with the other hand and moving away or back sizes the brush.
  - Copy to wall puts the panel's painting (see-through where unpainted) on the tunnel wall in view, for everyone;
    its corners size and turn it.

- AR marker cube (11.11.2026.2, tracking test; the real-marker code is left in place, unused): the palette's AR
  marker cube row places, for everyone, a 60 mm wireframe cube on a printed 60 mm artoolkitX marker
  (Printables/TheTunnel-AR-marker-60mm-id1.pdf). Every headset tracks it with both passthrough cameras (artoolkitX
  stereo) and shares its sightings; the host publishes the best, each headset draws its own pose (green) or the
  shared one (blue). Log and snapshots in files/ARMarker on the headset.

### Viewer
- Shared panel for images, videos and spatial (stereo) photos and videos. Quest stereo captures and screenshots are
  recognized as spatial.
- Lists the media built into the app and the headset's own captures by date, with < and > to step through them.
- Save, Delete and Rename.
- Video controls, including seeking, kept in step for every player.
- Copy an address from the browser and paste it into the Viewer. For a web page, the Viewer finds the page's main
  image or video.
- Images with transparency show what's behind them (cells, DNA, E. coli and viruses images included).
- Content only (10.11.2026.2): a button on the bar's top row hides the frame, menus, keyboard, video controls and
  backdrop for everyone, leaving just the media in place; a small button at the media's lower right corner brings them
  back. Kept when the media changes; video keeps playing. The panel can't be moved or resized until the frame is back.
- The dark backdrop no longer covers videos and opaque images (it had since the transparency change); it shows only
  for an empty screen, loading or errors.
- Microphone:
  - an indicator shows whether the mic is being recorded;
  - Mic to recording frees the mic from voice chat, so Quest recordings capture your voice;
  - other players' voices play at their heads and are recorded too.

### Browser
- Shared web browser panels.
- Content only (11.11.2026.2): hides the frame, address bar and keyboard for everyone; a small button brings them back.
- Videos on a shared page play together on every headset (play, pause, seek, speed; late joiners catch up), except
  inside another site's embedded player or DRM video; a Loop button repeats them for everyone (11.11.2026.2).
- Close, Unsnap/Snap, copy, bookmarks, a shared search bar and address bar, and on-panel keyboards.
- Menus and keyboards can be moved.

## In progress

| Item | State |
|---|---|
| Real marker on a panel or tunnel wall | First headset test of the new design (10.11.2026.1 on 4004X): the points and the disk were nowhere near the cap, and the corner marks weren't found; the headset's log was lost. 10.11.2026.2 adds a step that aligns the camera image with the tracked hand holding the marker (which way the image is read, and the cap's distance), and saves a log and annotated camera snapshots on the headset (files/RealMarker). Next: install, test, pull the folder and fix. |
| AR marker cube | Works on one headset (11.11.2026.3, on both 340YC Quest 3S): artoolkitX finds the marker with both cameras (about 93% of sightings), the outline sits on the printed marker in both camera images, and the cube sits on the marker in the user's view. Not yet tried: two headsets in one session (shared pose, handoff when a player looks away). |
| Browser content only, shared video, Loop | Built (11.11.2026.2), not yet tried on a headset. Next: two players, a direct .mp4 and YouTube; listen for echo between headsets in one room. |
| AR canvas | 12.11.2026.1 on 4041K and 4004X: with canvasMarkerRotation=90 in settings.txt (the marker is taped turned) the canvas lines up with the panel, but the marker was found only sporadically with both cameras open. artoolkitX's own stereo fusion needs both views to agree within ~3 px through the lens calibration, and Camera2's is off by ~0.7°, so it often reported nothing. 12.11.2026.2 searches each camera's image on its own (halved, faster) and joins the poses itself, learning the right camera's turn per headset (files/ARMarker/stereo-offset.txt); on 4004X's own snapshots both cameras then find the marker in all 8 clean pairs (before: 1). Next: headset test of the canvas, painting and Copy to wall. |
| Headset checks still owed | SARS-CoV-2 in the 3D movie (09.11.2026.1); the movie panel's placement on the wall beside a mixed reality box; transparent Viewer images (09.11.2026.2); Viewer Content only with two players and the backdrop fix (10.11.2026.2); the virtual marker following the hand instantly (09.11.2026.3). |
| Grab diagnostics | Every grab and release is logged (since 08.11.2026.1), to track down grabs that don't take. Still on. |

## Builds and headsets

| Where | Version |
|---|---|
| Latest build (Builds/TheTunnelDev.apk) | 12.11.2026.2 |
| Meta store, ArtDev channel | 09.11.2026.2 |
| Quest 3S 340YC10GBJ0HBJ | 11.11.2026.2 |
| Quest 3S 340YC10GBK08KX | 11.11.2026.2 |
| Quest 3S 3497C10HC4041K | 12.11.2026.2 |
| Quest 3S 3497C10HC4004X | 12.11.2026.2 |
| Quest 3 2G0YC1ZF760BMQ | 10.11.2026.2 |
