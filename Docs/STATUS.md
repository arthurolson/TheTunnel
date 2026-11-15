# The Tunnel: status

What has been built, what is in progress, and where each build is. Updated as work lands; the plan ahead is in
[TODO.md](TODO.md). Last updated 12 November 2026 (version 12.11.2026.5).

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
- Wall textures (TS Menu > Textures): built-in ones ship in the app; a fine surface (White Stucco) repeats at about
  0.5 m (16 times round the profile) instead of one image stretched round the whole tunnel (TunnelTextureLibrary
  ImageSizeOnWall). Its repeat is hidden: the wall shows small hexagonal patches of the image, each randomly
  rotated and shifted, blended at their edges (OcclusionLit's _WALL_TILE_BREAKUP dynamic branch, three texture
  reads instead of one; only on such textures).

### Molecules
- Molecule toys built from Assets/Molecules, chosen from the TS Menu's Molecules list. A slider magnifies them
  from 1x to 10x, and new molecules don't overlap.
- Sorted into category folders (15.11.2026): Assets/Molecules/viral capsids (2PLV, SARS, TBSV, Zika), proteins (EGFR,
  IGG), ligands (EGFR ligand), chemicals and nucleic acids (empty, each with a README). The Molecules list shows them
  under headings in that order, skipping empty categories; names, network ids and prefab references are unchanged
  (moved with AssetDatabase). Adding a molecule is still dropping a folder with a model into a category folder.
  Builders find molecules by name wherever they are (MoleculeLibraryBuilder.ToyPath / FolderOf). Editor test:
  Tunnel > Molecules > Test Molecule Library (Edit or Play mode). The SARS model's .gitignore line follows the move.
- EGFR (proteins; egfr.glb from Mol*, PDB 9S3X): true scale, 1.24 x 0.98 x 1.16 cm at x1; surface merged on a 2 A
  grid, 61,984 to 8,409 triangles (IgG has 18,996); coloured like the other surfaces (2PLV's first chain colour, as
  the GLB's own colour is a single grey). Its body collider is 8 convex pieces of its surface with its ligand's
  pocket left open within 14 Å (Tunnel > Movie > Build Ligand Binding), not one ellipsoid: a single convex shape
  fills the cleft, putting the pocket 19 Å inside it, out of reach of a 10 Å capture sphere.
- EGFR ligand (ligands; 9S3X-ligand.glb from Mol*, the inhibitor NCL-26007 in PDB coordinates; the older OBJ and zip
  are in Source~): the free ligand is the inhibitor alone, spacefill in Mol*'s atom colours (carbon grey, nitrogen
  blue, oxygen red), 42,240 triangles, 0.16 x 0.21 x 0.32 cm at x1 (about 18 Å long). Ligands and chemicals keep the
  model's own atom colours; a model's tiny atoms-and-bonds toy keeps its collider at least 2 cm across in the headset
  until magnified past that (MoleculeGrabSize), drawn at true size; while a 3D movie plays round it, the collider fits
  the molecule instead.
- The spawn nozzle sprays them like any other (budget: EGFR 100, ligand 60 per spray).
- Physics:
  - each molecule's mass comes from its volume;
  - molecules bounce, and drift when pushed;
  - gravity can be turned on or off;
  - the host can toggle gravity from a palm button.
- IgG with a tightest-fit ellipsoid collider.
- SARS-CoV-2 virion built from its bead model: about 310,000 triangles, with colliders on its membrane and on each of
  its 30 spikes.
- The molecule library simplifies any model over 200,000 vertices.
- Delete in the TS Menu calls up an eraser (a felt pad on a wooden block) in front of whoever pressed it, for
  everyone; the label reads Delete: ON in red. Only while a player holds it, whatever deletable thing its felt touches
  (molecule, virus, container by its edges, palette and marker, painting copy, spawn nozzle, sign) is deleted for
  everyone, with a haptic tick, a soft rub and a flash. It never deletes itself, menus, panels or the tunnel. Delete
  again removes it. Grabbing no longer deletes. Built by Tunnel > Eraser > Build Eraser; log tag [Eraser].
- Spawn nozzle (TS Menu > Molecules > Spawn nozzle check box): a grabbable spray gun placed in front of whoever ticks
  it, shared by everyone; untick to remove it. Let go of a molecule at the cup on its back to attach it (the ring turns
  green when it will); pull it off to swap in another. Its menu's Copies slider (1–100) and Spawn button spray that
  many copies of the attached molecule at its size from the mouth for everyone, one at a time (at most 20 per second,
  waiting while the mouth is full); Stop ends a spray. Each spray is capped at 3M triangles ('at most N of these':
  2PLV 50, TBSV 27, Zika 24, SARS-CoV-2 9, IgG 100). Built by Tunnel > Molecules > Build Spawn Nozzle; Editor test
  Tunnel > Molecules > Test Spawn Nozzle (in Play mode); log tag [Nozzle].

### Signage
- TS Menu > Signage (under Containers): New sign puts a sign flat on the wall beside the list, with its menu open for
  whoever pressed it; Saved signs lists the signs stored on the host (tap one, then Place or Delete); "Edit buttons on
  signs" hides or shows every sign's Edit button for everyone.
- A sign is a shared, grabbable panel of text (two hands scale it). Let go within 25 cm of a flat wall, it lies flat on
  the wall; taking it in hand pulls it off. The TS Menu's eraser deletes it. Not connected, it works locally.
- Its menu (Edit button at its lower right) has Edit text (the browser's keyboard; Enter starts a new line, and each
  key shows for everyone), a list of 19 open-licence fonts each shown in its own font (sans, condensed, headline,
  monospace, serif, display, script and stencil), Regular / Bold / Italic, Left / Centre / Right, Drop shadow, a colour
  chooser for the letters, border or background (hue, saturation, brightness, opacity), and Border (on/off, width
  0.2–3 cm, space round the text 0–10 cm). Save keeps the sign on the host (files/Signs/<first line>.json) to call up
  again, also after a restart.
- Fonts live in Assets/Tunnel/Signage/Fonts with their licences (SIL OFL; Apache 2.0 for Roboto); Tunnel > Signage >
  Build Signage makes static 512x512 SDF atlases and the font library (drop in a new font folder and rebuild). Editor
  test: Tunnel > Signage > Test Signage (in Play mode). Log tag [Sign].
- The TS Menu's Align to QR status line sits under the menu panel again (it had overlapped the bottom buttons).

### Containers
- Box and sphere containers. A box has quarter-inch rod edges, and a Collider check box sets whether its walls hold
  molecules in.
- Viscosity (0 to 1) and temperature (0 to 100 °C). Molecules inside move randomly (Brownian) or ballistically, and
  random motion spins them too.
- Clear contents (Containers list): deletes everything inside the container placed or held last, for everyone, and
  leaves the container. Log tag [Container].
- Mixed reality boxes:
  - opaque walls and a glass front;
  - let go against a tunnel wall, a box sets itself into the wall, which opens there;
  - Unsnap from wall pulls the box back out, with the molecules inside;
  - balls on the front corners stretch the front.
- Splat-box (next to Box in the Containers list): a box that only frames a 3D portrait, a Gaussian splat baked to a
  vertex-coloured mesh. It has no walls, medium, heat or movie. The box fits the splat with a small margin and scales
  evenly with it. Its +Z side shows the splat, so it is placed facing the middle of the tunnel, and as a mixed reality
  box it always sets into a wall with that side facing in. A menu beside it lists the splats (for now "David
  Goodsell") and shows the one picked for everyone. Splats are listed in Assets/Tunnel/Splats/SplatLibrary.asset and
  made by Tunnel > Containers > Build Splats (also run by Build Containers). Log tag [Splat].
- The container, splat and movie builders work in a scratch scene, so running them leaves the open scene unchanged.

### Ligand binding (the second 3D movie in a container)
- Chosen on the movie's menu (top row, Movie: Antibody action / Ligand binding, only before it starts); the Containers
  list's row now reads 3D movie controls.
- EGFR carries one binding site: a capture sphere centred on its ligand's bound place (from the two GLBs, both in
  9S3X's coordinates; no rotation), radius set by the Capture radius slider, 2 to 30 Å at the molecules' true scale
  (10 Å at first), shown by Show capture spheres. A free EGFR ligand touching it glides (0.6 s) into its exact pose
  in the crystal structure, at EGFR's size, and stays for the binding time (10 s to 1 min); then it is pushed out of
  the pocket and rests 3 s. While bound, EGFR is light blue, and Cys775 (the cysteine the inhibitor is covalently
  bonded to) shows once the ligand has settled, hidden again the moment it leaves. IgGs don't bind in the pocket;
  ligands don't bind to viruses.
- Play again, pause, saving ("Ligand binding N") and the movable menu work as in Antibody action. Built by Tunnel >
  Movie > Build Ligand Binding (also part of Build 3D Movie); Editor test: Tunnel > Movie > Test Ligand Binding (in
  Play mode).
- In Editor runs at x10 (EGFR 12 cm): with 40 ligands in a 22 cm box, warm, about one binding every two minutes at
  10 Å; at 20 to 30 Å with 20 ligands in a 35 cm box, a new ligand bound soon after each release.

### Antibody action (the 3D movie in a container)
- Capsids carry invisible antibody binding sites, found from each capsid's surface: TBSV on its 2-folds, poliovirus
  on its 5-folds, Zika on its 3-folds.
- SARS-CoV-2 takes part too: each of its 30 spikes is a binding site, and an IgG binds at the spike's tip.
- How binding works:
  - an IgG that touches a site is drawn in by a Fab tip and binds, adding its mass to the virus;
  - no limit: every free site can take one;
  - the virus's colour shifts toward green in proportion to the sites taken, fully green when all are, and back as
    antibodies leave (everyone sees it from the shared bindings);
  - each IgG stays bound for the binding time (slider, 10 s to 1 min, counted only while playing; applies to new
    bindings), then lets go: pushed off along the site's axis, resting 3 s before it can bind again.
- Its menu (3D movie controls in the Containers list), beside the container or flat on the wall next to a
  mixed reality box:
  - Start, Pause and Play; Play again lets every IgG go at once, counts down 5 s on the menu, then puts back the
    starting state (the same molecules where and as they were, the container's viscosity, heat and motion) and plays;
  - Show binding sites, and sliders for the sites' size and the binding time;
  - save the starting state by name (default "Antibody action N") to 3D Movies; older saved states still read;
  - a bar along its top to carry it by, for everyone; let go within 25 cm of a flat wall it lies flat on the wall
    (the signs' rule, TunnelWallSnap). Hiding and showing the controls puts it back beside the container.
- Editor test: Tunnel > Movie > Test Antibody Action (in Play mode), also covering Clear contents and the eraser.

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
| AR canvas | Two-player test of 12.11.2026.2: both cameras tracked; the canvas jittered. 12.11.2026.3 locks it: once the panel is found, the host settles the canvas on a robust average of the first good sightings (about 1 s), shares it, and every headset stops tracking; the palette button then reads Re-find canvas. The canvas is drawn 2 mm in front of the real panel (canvasLift) so the panel doesn't hide the painting. Tested in the Editor; next: headset test. Two-player test of 13.11.2026.1: the player saw the canvas outline partly hidden, and host and player disagreed on the marker. Causes: a sighting made before hosting (on the app's own clock) looked 23 minutes in the future once the session's clock took over, so the shared pose froze for 6 minutes; the shared source flipped back and forth (a joining headset's sightings arrived ~250 ms old against a 200 ms limit), restarting the lock each time (58 s); the canvas sat ~2° tilted, so its far side sank 1–2.5 cm into the real panel and real-depth occlusion hid it. 13.11.2026.2: sightings from another clock are dropped; joining headsets stamp sightings on the host's clock; a source is replaced only when another's is clearly newer, and kept at least 0.5 s; each headset's sightings are gathered separately for the lock; a canvas within 3° of level or upright is made exactly so (canvasLevelWithin, 0 turns it off); real surfaces within a few cm of the canvas no longer hide it (a hand 4 cm above still does). Editor test: Tunnel > Paint > Test AR Two-Player Fixes. 13.11.2026.2 and .3 crashed at start on every headset (sharedassets0.assets corrupted: a texture was added mid-build, and the next incremental build reused the damaged data); 13.11.2026.4, built with a clean build cache, starts normally. Next: two-player headset test. |
| Copy to wall | One-headset test of 12.11.2026.4: painting on the locked AR canvas works well (the real panel's surface helps); the copy floated where the gaze missed a wall, and Clear all marks wiped it. 12.11.2026.5: the copy lands flat on the placed tunnel's wall (looked at, else nearest; eye height; off the palette); Clear all marks no longer clears copies; under each copy a bar with Save to Viewer (every headset saves 'painting yyyy-MM-dd HH-mm', transparent where unpainted, to the Viewer's Images) and Content only (a small Frame button brings the frame back). 13.11.2026.1: marks drawn with the pencil and not yet finished are finished before copying, and touching a colour disk finishes a pencil line in its own colour (the copy left out unfinished lines); Save to Viewer keeps see-through paint's transparency right. Editor test (Tunnel > Paint > Test Copy to Wall Matches the Canvas) passes. Next: headset. |
| Splat-box | Built (13.11.2026.1): tested in the Editor, not connected (placing, even scaling, mixed reality, setting into a wall, unsnapping, the menu). Next: headset (colours, frame rate with 250k triangles on Quest 3S, grabbing and scaling by hand, the menu by ray and poke) and two players (same splat, size and pose; late joiner). The source model is mostly room around the figure, so Goodsell is small in the box. |
| Spawn nozzle | Built and passing its Editor test, not connected (14.11.2026.3; 14.11.2026.2 crashed at start, sharedassets0 corrupted by an incremental build after the new assets; .3 is a clean build and starts normally). Next: headset (grip, aiming, attaching and pulling off by hand and ray, the menu, frame rate during a full spray on Quest 3S) and two players (attached molecule riding on the nozzle for both, shared count and status, a client spraying and stopping, a late joiner). |
| Signage | Built and passing its Editor test, not connected (14.11.2026.4). Next: headset (grab, scale and snap to the wall by hand and ray; menu and keyboard by poke and ray; how legible the letters are at 1–3 m; the Edit button's size; the sliders' dark fill) and two players (typing and styling from a client, Save / Place / Delete from a client storing on the host, wall pose, late joiner). |
| Co-location (floor QR) | Two-player test: after aligning to the floor QR, the joining headset's AR-marker sighting sat 23–24 cm (briefly 30 cm) from the host's. 12.11.2026.4: the floor QR is read by both cameras through the AR marker's verified camera path (corner squares refined, floor height measured from the code; Editor: under 1 mm and 0.05°), and whenever a joining player and the host both see the AR marker held still, the player's view glides onto the host's (Editor: a 24 cm / 3° error corrected to within 3 mm). Next: two-player headset test. 13.11.2026.1 test: the floor QR left the player ~5 cm off; the marker alignment then corrected 29 mm and 7 mm, leaving under 1 cm. But it ran only because the canvas took a minute to lock: tracking stopped at the lock, so normally it never would. 13.11.2026.2: with other players present, tracking stays on for up to 15 s after the lock until each player's room is lined up with the host's by the marker; the logs say whether alignment ran, each correction and the totals. |
| Headset checks still owed | SARS-CoV-2 in the 3D movie (09.11.2026.1); the movie panel's placement on the wall beside a mixed reality box; transparent Viewer images (09.11.2026.2); Viewer Content only with two players and the backdrop fix (10.11.2026.2); the virtual marker following the hand instantly (09.11.2026.3). |
| Antibody action, Clear contents, eraser | Built and passing its Editor test, not connected (14.11.2026.5). Next: headset (the menu's bar by hand and ray, its wall snap; the eraser in the hand, haptics and sound; the green shift on each virus) and two players (bindings, releases and colour the same for both; Play again's countdown and restore from a client; menu position shared while carried; a client erasing something another player holds; late joiner). |
| Molecule categories, EGFR, EGFR ligand, Ligand binding | Built and passing their Editor tests, not connected (15.11.2026.1, starts on a headset). Next: headset (the Molecules list's headings and scrolling; grabbing EGFR at x1, 1.2 cm, and the ligand, whose 2 cm grab collider is far larger than it at x1; Ligand binding at x5 to x10 with 10 to 20 ligands: how often they bind at 10 Å, the glide, Cys775 appearing, the light blue; frame rate with many 42k-triangle ligands on Quest 3S) and two players (scenario choice, capture radius, bindings, colour and Cys775 the same for both; late joiner). |
| Grab diagnostics | Every grab and release is logged (since 08.11.2026.1), to track down grabs that don't take. Still on. |

## Builds and headsets

| Where | Version |
|---|---|
| Latest build (Builds/TheTunnelDev.apk) | 15.11.2026.1 |
| Meta store, ArtDev channel | 09.11.2026.2 |
| Quest 3S 340YC10GBJ0HBJ | 11.11.2026.2 |
| Quest 3S 340YC10GBK08KX | 11.11.2026.2 |
| Quest 3S 3497C10HC4041K | 13.11.2026.4 |
| Quest 3S 3497C10HC4004X | 13.11.2026.4 |
| Quest 3 2G0YC1ZF760BMQ | 15.11.2026.1 |
| Quest 3 2G0YC1ZF7X0DTC | 14.11.2026.5 |
