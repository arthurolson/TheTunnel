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
- TS Menu > Clear all (pressed twice within 4 s) takes away, for everyone, everything not on a tunnel wall: molecules
  (except inside a box set into a wall), containers and splat-boxes not set into a wall, Viewers and browsers not
  hanging on a wall, signs, palettes and painting copies not lying on a wall, and the spawn nozzle
  (TunnelClearAll; button made by Tunnel > Menu > Add Clear All Button).
- See-through walls and floor draw before every other see-through thing (queue 2900), so a Viewer or browser hung
  on a wall is never painted over by the wall (it used to vanish until pulled toward the middle).
- Wall textures (TS Menu > Textures): built-in ones ship in the app; a fine surface (White Stucco) repeats at about
  0.5 m (16 times round the profile) instead of one image stretched round the whole tunnel (TunnelTextureLibrary
  ImageSizeOnWall). Its repeat is hidden: the wall shows small hexagonal patches of the image, each randomly
  rotated and shifted, blended at their edges (OcclusionLit's _WALL_TILE_BREAKUP dynamic branch, three texture
  reads instead of one; only on such textures).

### Molecules
- Molecule toys built from Assets/Molecules, chosen from the TS Menu's Molecules list. A slider magnifies them
  from x1 to x20 (15.11.2026: its stops go x1 to x10 one at a time, then x12, x14, x16, x18, x20, so the low end keeps
  two thirds of the slider; ticks rebuilt by Tunnel > Molecules > Build Magnification Slider), and new molecules don't
  overlap. Everything that scales with it (colliders, the ligand's grab size, capture spheres, the binding glide, the
  nozzle's spray spacing) follows the molecule's own scale, so x20 needs nothing else; the nozzle's triangle budget
  doesn't depend on size.
- Sorted into category folders (15.11.2026): Assets/Molecules/viral capsids (2PLV, SARS, TBSV, Zika), proteins (EGFR,
  IGG), ligands (EGFR ligand), chemicals and nucleic acids (empty, each with a README). The Molecules list shows them
  under headings in that order, skipping empty categories; names, network ids and prefab references are unchanged
  (moved with AssetDatabase). Adding a molecule is still dropping a folder with a model into a category folder.
- Assemblies (16.11.2026.1): a sixth category, Assets/Molecules/assemblies, listed after Viral capsids, with the
  E. coli 50S ribosome (PDB 10QM, from Mol*): 31 chains, about 3.7 cm across at x1, a box collider. Since
  16.11.2026.4 it is the low-resolution one again, made from the first, coarse Mol* export (10QM.glb, 8.5k vertices /
  17k triangles); from 16.11.2026.2 to .3 it had been made from the Gaussian export, so it looked like Hi-res Ribosome.
  The Gaussian export (10QM-gaussian.glb, kept out of git like SARS's model) is in its Source~. An
  assembly is a surface (simplified like the others) but keeps the model's own colours, one per chain, since 2PLV's
  few colours can't tell dozens of chains apart. Editor test: Test Molecule Library passes with it.
  Builders find molecules by name wherever they are (MoleculeLibraryBuilder.ToyPath / FolderOf). Editor test:
  Tunnel > Molecules > Test Molecule Library (Edit or Play mode). The SARS model's .gitignore line follows the move.
- Hi-res Ribosome (16.11.2026.3, assemblies, after E. coli 50S ribosome): the Gaussian-surface export at full
  detail, 563k vertices / 1.13M triangles, never merged (MoleculeLibraryBuilder.k_FullDetail), for a close look at one
  or two at a time (the spawn nozzle's 3M-triangle budget allows 2). Its model is kept out of git; its meshes
  (49 MB) are in git. Not yet looked at on a headset.
- EGFR (proteins; egfr.glb from Mol*, PDB 9S3X): true scale, 1.24 x 0.98 x 1.16 cm at x1; surface merged on a 2 A
  grid, 61,984 to 8,409 triangles (IgG has 18,996); coloured like the other surfaces (2PLV's first chain colour, as
  the GLB's own colour is a single grey). Its body collider is 8 convex pieces of its surface with its ligand's
  pocket left open within 14 Å (Tunnel > Movie > Build Ligand Binding), not one ellipsoid. Even so the pocket lies
  at the end of a channel about 10 Å across: its centre is 9 Å from EGFR's centre and 21 Å below the channel's mouth,
  and a free ligand (9 x 12 x 18 Å) never got closer than 12–19 Å to it in Editor runs. So the capture sphere is now
  centred on the mouth (found by the builder from the body: the widest way out, and the point from which the body
  stays 9 Å or more away), 28 Å from EGFR's centre.
- EGFR ligand (ligands; 9S3X-ligand.glb from Mol*, the inhibitor NCL-26007 in PDB coordinates; the older OBJ and zip
  are in Source~): the free ligand is the inhibitor alone, spacefill in Mol*'s atom colours (carbon grey, nitrogen
  blue, oxygen red), 42,240 triangles, 0.16 x 0.21 x 0.32 cm at x1 (about 18 Å long). Ligands and chemicals keep the
  model's own atom colours; a model's tiny atoms-and-bonds toy keeps its collider at least 2 cm across in the headset
  until magnified past that (MoleculeGrabSize), drawn at true size; while a 3D movie plays round it, the collider fits
  the molecule instead.
- The spawn nozzle sprays them like any other (budget: EGFR 100, ligand 60 per spray).
- Physics:
  - each molecule's mass comes from its volume;
  - the heat's random kicks can no longer be infinite (15.11.2026: Random.value can be exactly 1, and the Gaussian's
    log(0) gave "angularVelocity ... Infinity" errors in the log);
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

### Saved states
- TS Menu > Saved states (under New figure) opens a list beside the menu. Save now keeps everything set up in every
  segment as a numbered state (State 1, State 2, ...), listed newest first with when it was saved and what it holds
  ("22 Nov 10:23 · 2 segments · 14 things · paint"). Tap one, then Restore, pressed twice within 4 s, puts it back for
  everyone, replacing what's in the tunnel now (on the walls too, unlike Clear all). Delete removes the checked one.
- Kept on the host in files/Saved States/<id>.json (with a small <id>.txt: name, date, summary), so states survive
  restarts. In a session everyone sees the host's list; Save, Restore and Delete from any headset are done by the
  host, and what it puts back is spawned for everyone. Not connected, it works locally.
- Shared between headsets through the asset repository (TunnelSharedStates; written 22.11.2026, not yet built):
  - a state's id is when it was saved (UTC) and three random letters (20261122-140531-k7q), so two headsets never
    make the same file; the list shows its name (State N, one more than the highest in the merged list);
  - Save now writes the state on the headset, then puts its .json and .txt in saved-states/ of the repository the
    asset list comes from (github.com/arthurolson/TheTunnelAsset, main, by default; catalog-url.txt or the host's
    list changes it) through GitHub's Contents API. Offline, or if GitHub refuses, it stays on the headset, marked
    "not uploaded", and is tried again on the next start or list open; it only moves out once GitHub has both files;
  - every headset reads the repository's list at start and on list open (at most once a minute; no token needed) and
    keeps a copy of the list and of every state in files/Saved States/Shared, so any of them restores offline too;
    a state not downloaded yet is downloaded when restored;
  - uploading and deleting need a fine-grained GitHub token (only TheTunnelAsset, Contents read and write) in
    files/github-token.txt on the headset, put there by hand with adb; it's never in either repository or the build
    (github-token.txt is git-ignored) and never logged, and goes only to api.github.com. Without it a headset reads
    and restores everything; its own saves stay on it, and the line under the list says so;
  - Delete: one not uploaded goes from the headset; a shared one is deleted from the repository (so from every
    headset, which drops its copy at the next read), which needs the token; without it Delete says so and keeps it;
  - the repository is public: a state holds no headset or player names; web addresses lose a user name and password
    and query parameters such as token, key, sig or session;
  - states saved by 22.11.2026.1 (State 1.json) are given an id and keep their name, and upload like new ones;
  - the line under the list (blue) says how sharing stands: shared through the repository, no token, offline, token
    refused, can't write;
  - Editor test: Tunnel > Menu > Test Shared Saved States (Edit mode, a fake GitHub in memory, two "headsets") passes;
    Read Shared Saved States (real repository) read the real repository without a token (no states yet).
- What a state holds:
  - the number of segments: Restore adds them at the tunnel's end or removes them from it;
  - each segment's colour, wall texture and wall paint (replayed stroke by stroke);
  - molecules (built-in and from the asset repository): size, frozen or parked, motion;
  - boxes, spheres and splat-boxes: size, mixed reality and which face is the front (a box set into a wall opens
    the wall again), viscosity, heat and motion, the movie chosen (kept whole, so a movie someone made comes back
    even if it was deleted), its binding time, sites' size and capture radius, whether its controls show, and the
    molecules inside, in the container's frame (the 3D movie's starting-state format, MovieSnapshot, reused and
    extended with repository ids, Freeze and the front face);
  - Viewers (what they show, content only, video loop, time and pause) and browsers (page, content only), with
    their size and whether they hang on a wall or stand free;
  - signs (text, style, size), palettes with their markers, painting copies with their marks, New figures and their
    size, and the spawn nozzle with its Copies setting and the molecule on its cup;
  - the Molecules list's magnification.
- Poses are kept in the frame of the segment each thing is in, counted along the tunnel from its first segment, so a
  state comes back in the same place in the tunnel even if the tunnel stands somewhere else in the room.
- Code: TunnelSavedState (the data and the store), TunnelSaveState (save and restore, on the host), TunnelSaveStateMenu
  (the list). Built by Tunnel > Menu > Add Saved States (run it again after Build Figure, Build Signage or Build Spawn
  Nozzle so it has their latest prefabs). Log tag [SaveState].
- Editor test: Tunnel > Menu > Test Saved States (in Play mode in SampleScene). It sets up 14 things in two segments
  plus paint and a segment colour, saves through the list, then clears all, adds a third segment and changes the
  colour and paint, restores through the list and compares. It passes not connected and with the Editor hosting a
  session (22.11.2026): 14 of 14 things in the same segment, within 2 mm and 1°, the same size and settings;
  segments, colours and paint as saved.
- Not kept: anything bound or playing in a 3D movie (a state comes back with the movie chosen but not started, and
  bound antibodies or ligands come back free where they were); a palette's colours, mode and the marker's size; the
  New movie menu and the movie panel's place; each molecule kind's Freeze checkbox in the Molecules list; gravity;
  the eraser; a browser's scroll position and history; Space Setup and where the tunnel stands in the room.

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
- Chosen on the movie menu's picker (see 3D movies below); the Containers list's row reads 3D movie controls.
- EGFR carries one binding site: a capture sphere on the mouth of its ligand's pocket (15.11.2026; it was on the
  ligand's bound place, which free ligands can't reach), radius set by the Capture radius slider, 2 to 30 Å at the
  molecules' true scale (10 Å at first), shown by Show capture spheres. A free EGFR ligand touching it glides (0.6 s)
  down the channel into its exact pose in the crystal structure, at EGFR's size, and stays for the binding time (10 s
  to 1 min); then it is pushed out through the mouth and rests 3 s. While bound, EGFR is light blue, and Cys775 (the
  cysteine the inhibitor is covalently bonded to) shows once the ligand has settled, hidden again the moment it leaves.
  IgGs don't bind in the pocket; ligands don't bind to viruses.
- What was wrong (15.11.2026, after the user saw no binding in 15.11.2026.1): (1) the capture sphere was centred
  20 Å down a 10 Å channel, which free ligands never entered (closest approach 12–19 Å in 30–90 s runs, 0 bindings in
  40 s at x10 with 15 ligands); (2) a new container has no heat (absolute zero), so after being let go of in it
  nothing moves; Start now brings a container at absolute zero to 20 °C and says so; (3) once a movie had started its
  choice was fixed for good (no way back to Ready), so a box that had played Antibody action could never play Ligand
  binding; choosing another movie now stops the one playing (everything bound lets go); (4) the user's own log
  (760BMQ) shows Start pressed with Antibody action still chosen ("Put a virus ... first"): the choice row wasn't
  found or understood. Now the picker shows the movie's name and "2 of 2" between ◄ and ►, Start reads "Start Ligand
  binding", Start with another movie's molecules in the box (EGFR and ligands while Antibody action is chosen) chooses
  that movie and asks for Start again, and the not-ready message names the movie chosen, exactly what it needs, how
  many of them are outside the box, and the picker. Placing a molecule from the Molecules list now says how large it
  is ("EGFR placed beside this list at x1: only 1.2 cm across. Raise the magnification to see it better"). The Editor scenes also run
  with gravity on (the app starts with it off), which had piled every molecule on the box's floor in the earlier
  Editor numbers; tests now turn it off. And Build Ligand Binding no longer leaves the ligand weighing as much as 2PLV
  (its rebuilt toy's volume is saved before the binder is added).
- Logs ([Movie]): each touch ("touched ... capture sphere: gliding into its pocket"), binding ("binding N since it
  started, T s in"), and release; every 30 s on the host while it plays, how it goes (receptors, free and bound
  binders, bindings so far, heat, radius), and a warning naming the likely cause if nothing has bound after 50 s.
- Rates in Editor runs (gravity off, room temperature, 15 ligands, ligands started 2 cm clear of EGFR, 5 runs each,
  default 10 Å): x10 in a 30 cm box, first binding at 0–3 s (and a new one soon after each release); x20 in a 60 cm
  box, 0–5 s; x5 in a 30 cm box, 2–33 s; x1 (EGFR 1.2 cm) in a 30 cm box, none in a minute (30 Å: a few seconds). The
  default radius stays 10 Å.
- Play again, pause, saving ("Ligand binding N") and the movable menu work as in Antibody action. Built by Tunnel >
  Movie > Build Ligand Binding (also part of Build 3D Movie); Editor tests: Tunnel > Movie > Test Ligand Binding and
  Test Ligand Binding as a Person Would (both in Play mode; the second places the box, EGFR and 15 ligands from the
  TS Menu's lists at x1, x5, x10 and x20, 10 Å and 30 Å, and once from the spawn nozzle, and starts it from the movie
  menu).

### 3D movies as data, and the New movie menu (15.11.2026)
- A movie is a definition (MovieDefinition): receptor (a molecule with binding sites; empty for "any virus"), binder,
  pocket or surface sites, how many of each, capture radius or sites' size, binding time, the bound colour effect
  (shift toward a colour by the share of sites taken, or a solid colour while bound) and colour, and starting
  viscosity, heat and motion. Antibody action and Ligand binding are the two built-in ones, with their behaviour
  unchanged (green shift; light blue while bound).
- The movie menu's top row is a picker of every movie (◄ name ►, "2 of 3"); under it New movie... and Delete movie (a
  made movie only; a second tap within 4 s deletes it for everyone; containers with it chosen and not playing go back
  to Antibody action). The menu grew from 40 to 42 cm.
- New movie... opens the New movie menu (48 x 35 cm, two columns) beside the movie menu, on the side away from the
  container, for whoever pressed it; it can be carried by its bar and lies flat on a wall when let go within 25 cm.
  Left: name (tap, then the keyboard), receptor (◄ ►: 2PLV, SARS, TBSV, Zika, EGFR), binder (only those with site
  data: IGG for the viruses, EGFR ligand for EGFR), receptors 1–10, binders 1–100 (capped by the 3M-triangle budget:
  at most 70 EGFR ligands), capture radius (pocket) or sites' size (virus), binding time. Right: colour effect, six
  colours, viscosity, heat (°C), motion, Create and Close. Starting conditions come from the container (20 °C if it
  has no heat).
- Create stores the movie on the host (files/3D Movies/Movie definitions/<id>.json), lists it on every movie menu,
  chooses it on that container, empties the container and fills it with the receptors and binders at the Molecules
  list's magnification, scattered where they don't overlap and moving (a container too small is made larger). Choosing
  a made movie sets the container's medium and heat as made. Play again, Save starting state (named after it) and
  pausing work as for the built-in two. Shared in a session (the host's list on each container's movie; creating and
  deleting go to the host); works when not connected.
- Each movie has a magnification (15.11.2026.3), on the Molecules list's stops: Ligand binding x20,
  Antibody action x1 (at x1 the capsids are 6 to 8 cm across, SARS-CoV-2 32 cm with its spikes and an IgG 2.6 cm:
  large enough to watch, and the size it has always been played at), a made movie
  whatever its New movie menu's Magnification slider said (it starts at the Molecules list's value; movies stored
  before this have none and use the list's). Create fills the container at the movie's own magnification. Choosing a
  movie (with ◄ ►, or for you at Start) sets the Molecules list's Magnification to it, so molecules added by hand from
  the list come out at the movie's size; the slider is shared (the host's value is everyone's), as it already was.
  The picker shows it ("Ligand binding x20 · 2 of 2"). Molecules already in the container are not resized: when any
  is at another size, the status says so in yellow ("EGFR here is x10; this movie is x20. Match size resizes them.")
  and Match size (between New movie... and Delete) makes everything in the container the movie's size for everyone,
  each staying where it was in the container (the container grows first if they would crowd it; each headset resizes
  what it moves, so it is the same for all). Only before Start. Capture spheres and the rest still follow each
  molecule's actual size. Saved starting states record the movie's magnification too (each molecule's own scale was
  already kept).
- Built by Tunnel > Movie > Build 3D Movie (or Build Movie Menus); Editor test: Tunnel > Movie > Test New Movie (in
  Play mode; works in a test folder, the app's movies are untouched). Log tag [Movie].

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
- A Viewer hung on a see-through tunnel wall shows its picture (19.11.2026.2: the wall used to be drawn over it).
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
- Videos from the asset repository (TheTunnelAsset video/, each with a .meta.json sidecar) are in every headset's
  Videos list with no build. 23.11.2026: three cellPACK videos added (HIV ingredient list, B cell, a cellPACK cell at
  2880×2160, 18 Mbit/s); they play in the Viewer on the Quest 3S. Since 24.11.2026.1 the same three are also built in
  (StreamingAssets/Viewer/Videos, named with their catalog titles so each is listed once and plays without the
  network); the APK grew from 418 MB to 541 MB. Installed on the Quest 3s …X0DTC and …BMQ and the Quest 3S …4041K and launch-tested (starts, reads the
  asset catalog); playing the built-in copies not yet tried.

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
| Molecule categories, EGFR, EGFR ligand, Ligand binding | 15.11.2026.1 on a headset: the user saw no binding (no log). Fixed after it (capture sphere on the pocket's mouth, Start warms a container with no heat, a movie can be changed after it has played); Editor runs now bind within seconds at x10–x20 with the default 10 Å. Next: headset (Ligand binding at x10 and x20 with ~15 ligands: a binding within half a minute, the glide down the channel, Cys775, the light blue; the [Movie] reports in adb logcat; grabbing EGFR at x1 and the ligand's 2 cm grab collider; frame rate with many 42k-triangle ligands on Quest 3S) and two players (movie choice, capture radius, bindings, colour and Cys775 the same for both; late joiner). |
| Magnification x20, New movie menu, movies as data | Built and passing their Editor tests, not connected (15.11.2026.2, runs on both Quest 3s). 15.11.2026.3: each movie has its own magnification (Ligand binding x20, Antibody action x1), choosing one sets the Molecules list to it, and Match size; passing their Editor tests, not yet launch-tested. Next: headset (choosing Ligand binding turning the list to x20; the yellow size note and Match size on molecules placed at another size; the Magnification slider's stops by ray and poke; molecules at x20, about 25 cm for EGFR; New movie... and the New movie menu: its place beside the movie menu, the keyboard for the name, ◄ ►, the swatches, Create filling the box, Delete's second tap; the picker stopping a playing movie) and two players (a client creating and deleting a movie, the list the same for both, the host keeping it after a restart; late joiner). |
| New figure (a little person on the left stick) | 21.11.2026.1 on a Quest 3: looks right in passthrough. Since then the face is a high-resolution face scan of the same person (24k triangles, its own 2048 texture FigureFace.png), set into the front of the head and weighted to the head and neck bones; the old face is pushed behind it and the front of the neck slimmed so the collar stays clear in walk, run and jump poses (Editor renders). 21.11.2026.3 on the Quest 3: the face looked bigger than the head behind it, like a mask. Since then the old head above the neck is replaced by a smooth new skull as wide as the face (its own 512 texture FigureSkull.png, painted from the scan's skin and hair colours, fading to grainy grey-brown hair), and the figure wears black-rimmed semi-round glasses (their own black material Glasses, on the head bone). 21.11.2026.4 on the Quest 3: the glasses sat too high. Since then they are 13 mm lower with the bridge on the nose and tilted 10° so the arms rise to ear height, and the arms are new straight tubes that bend down behind the ears; 21.11.2026.5 on the Quest 3: the glasses look right. A 3D model of a real person made from a phone video with open-source tools only (COLMAP camera poses, carving from the person's outlines, MediaPipe for the joints; work files and scripts in ~/tunnel/figure-work and ~/tunnel/rig-tools, outside the repo), rigged in Blender to Quaternius's CC0 animation skeleton and exported to Assets/Tunnel/Figure/Model. Tunnel > Figure > Build Figure makes the 30 cm prefab, its animator (idle, walk, jog, sprint, jump, fall, land) and the TS Menu's New figure button under Clear all. Editor test, not connected: New figure in SampleScene puts the figure in front of the presser; it walks, runs, jumps, lands and steps up a 4 cm ledge, with its feet on the ground. Rough edges: the right foot hangs a little higher than the left, and there's a dark band at the waist. Next: headset (stick and X on a real controller, walking on Space Setup floors and tables, picking up and two-hand scaling, frame rate with 55k triangles on Quest 3S) and two players (the driver's motion and animation seen by the other, ownership moving with a grab, late joiner). Since 22.11.2026.3, the tunnel's walls and arch and the Containers' walls stop it: it slides along a wall it walks into, a box it is put into keeps it in (a mixed reality box's glass front too, and there the tunnel's walls don't touch it), it is pushed out of a wall it is put down across, and its head stops at a box's lid when it jumps; Editor test in Play mode (a 0.6 × 0.5 × 0.6 m box in SampleScene): it stops 4 cm (its body radius) from every wall, inside and out. Not yet on a headset. |
| Saved states | Built in 22.11.2026.1; sharing through the asset repository built in 22.11.2026.2. On the two Quest 3s in 22.11.2026.2, each with a token: …X0DTC saved a state (an MR box in the wall holding TBSV, a membrane and the figure, a loose TBSV, a browser panel) and uploaded it; …BMQ uploaded a save it had made before it had a token, as soon as it got one; …BMQ restored …X0DTC's save and it worked. Not yet tried: Delete from the repository, a restore with a few dozen things or a lot of paint (and how long it takes on Quest 3S), and two players (a client saving and restoring through the host, everything appearing for both, a late joiner). Each headset numbers its own saves, so two can both be called State 1. |
| Grab diagnostics | Every grab and release is logged (since 08.11.2026.1), to track down grabs that don't take. Still on. |
| Teleport along the tunnel | A Teleport check button under the Laser button over the left palm (Tunnel > Teleport > Add Teleport Button puts it in SampleScene). While it's on, a hand or controller pointed at a tunnel segment's floor shows a cyan line and ring where you'd land, and a pinch (or trigger or grip) takes you there through the XR Origin's Teleportation provider, still facing the same way; pointing at a toy, a panel or a wall, or holding something, does what it always did. Editor test in Play mode: the floor counts, a wall doesn't, the player lands on the spot. Built in 23.11.2026.1; works on the Quest 3 in 23.11.2026.1, and on ArtDev. On another headset in 23.11.2026.1 a teleport dropped the view so the tunnel sat about 1.5 m above the floor (the Teleportation provider also set the rig's height); since 23.11.2026.2 a teleport slides the rig along the floor only and logs the rig and head height before and after. 23.11.2026.2 on that headset (Quest 3 2G0YC1ZF8300NP): teleport keeps the height. That headset's tunnel floor sat 1.2 m below the real floor from the start (no Space Setup floor, so TunnelFloorAnchor never moved it); since 23.11.2026.3 the tunnel stands on the headset's own tracked floor when no Space Setup floor arrives within 5 s (logged as [Floor]), but only for a rig tracking from the floor, and this rig tracks from the head (Device), so it doesn't apply: a headset needs Space Setup in the room. On the Quest 3S 3497C10HC4041K (no Space Setup, tunnel too low) after running Space Setup in 23.11.2026.3: [Floor] stands on the Space Setup floor, the tunnel meets the real floor, and teleport works. Three players lined up with the room (24.11.2026.1): when one teleported, the tunnel moved against the others' real bodies (their avatars are hidden for aligned players, so the teleporter saw the real people next to them); wanted: the tunnel and the others stay put, and the teleporter sees them fall behind. Since 25.11.2026.1 (built, signed) each headset keeps how far its player has teleported (TunnelTeleport.offset, shared through CoLocation): the QR code and AR marker are measured without it, so a teleport never moves anyone's room alignment or the shared marker, and two aligned players at different places in the tunnel (more than 0.3 m apart in teleport) see each other's avatars ([Teleport] player N's avatar shown/hidden). Editor test: the offset is kept and reaches the host's list. Decided after that test: teleport is meant for one person in the tunnel, and players lined up with the room by the QR code teleport together. Since 25.11.2026.2 a teleport by an aligned player who has other aligned players at the same place in the tunnel goes to the host, which sends the same step to all of them (CoLocation.TeleportGroupRpc, logged as [Teleport] player N teleported … moving players …), so their real bodies stay where their places in the tunnel are; a player alone, or not aligned, still moves alone. Aligning to the QR code now puts a player at the host's place in the tunnel (takes on the host's teleport), and aligned players' avatars only show after they've been apart for 1.5 s, so a group teleport doesn't flash them. Editor test as host: alone, a teleport moves only this player; the host's relay reaches the group and moves along the floor only. Not yet tried on headsets. Not covered: the real-marker painting and whiteboard for players who have teleported. Next: controllers, several segments. |

## Builds and headsets

| Where | Version |
|---|---|
| Latest build (Builds/TheTunnelDev.apk) | 25.11.2026.2 (players lined up by the QR code teleport together) |
| Meta store, ArtDev channel | 23.11.2026.3 (uploaded with Developer Hub) |
| Quest 3S 340YC10GBJ0HBJ | 11.11.2026.2 |
| Quest 3S 340YC10GBK08KX | 11.11.2026.2 |
| Quest 3S 3497C10HC4041K | 24.11.2026.1 (Space Setup done 23.11.2026) |
| Quest 3S 3497C10HC4004X | 16.11.2026.4 |
| Quest 3 2G0YC1ZF760BMQ | 24.11.2026.1 |
| Quest 3 2G0YC1ZF7X0DTC | 24.11.2026.1 |
| Quest 3 2G0YC1ZF8300NP | 23.11.2026.3 |
