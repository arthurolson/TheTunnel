# Assets from the asset repository

The Tunnel reads a GitHub repository at runtime, so new molecules and media need no new build. The repository is
[arthurolson/TheTunnelAsset](https://github.com/arthurolson/TheTunnelAsset), a fork of autinlab/TheTunnelAsset.

| What's in the repository | Where it appears in the headset |
|---|---|
| molecules and viruses (`molecule/<category>/`, `virus/capsids/`) | the TS Menu's Molecules list, under their category, next to the built-in ones |
| images and videos (`image/`, `video/`) | the Viewer's Images, Videos and Spatial lists |
| 3D portraits (`mesh/`) | a splat-box's menu |

A molecule from the repository becomes a toy just like a built-in one. Every headset downloads the model, or takes its
own copy, and makes the toy itself with the Editor's code (`MoleculeToyGeometry`):
- the same ångström-to-meter scale;
- the same colours by category: 2PLV's chain colours for surfaces, the model's own for assemblies, element colours for
  atoms and bonds;
- surfaces simplified on a 2 Å grid and kept under 200k vertices;
- the tightest collider, a box or an ellipsoid;
- the same grab, scale and gravity.

Until its model arrives, the toy shows the download's progress, and a sphere of the model's size holds its place.

- **Colliders.** A toy's collider is fitted once: ahead of time in the repository (a `collider` in its sidecar, fitted
  in the Unity Editor), or on each headset the first time, in the background, and kept there. A flat molecule, such as
  the Membrane, gets a grid of boxes that follows its surface instead of one box, so things can come close to it.
- **Freezing.** Holding a molecule, or pointing at it, shows a Freeze checkbox just above it, for a few seconds after
  too.
  - Ticked, the molecule stays where it is put: you can still grab and move it, and it parks wherever you let go.
  - Inside a container, it keeps its place in the container when that is moved.
  - Unticked, it moves with the physics again.
  - This works for built-in molecules too, and everyone in a session sees it.
- **The spawn gun** takes repository molecules too. Its copies share one set of geometry on each headset.

## Adding a molecule, end to end

You can ask Claude in one line, for example "load the attached molecule in the proteins folder and call it HIV
protease". By hand, in a clone of TheTunnelAsset:

1. **Drop in the model.** Put a Mol* glTF export (`.glb`, in ångströms) in `molecule/<category>/`, for example
   `molecule/proteins/hiv_protease.glb`. The category is the TS Menu heading it goes under: proteins, ligands,
   chemicals, assemblies, nucleic acids. Viruses go in `virus/capsids/`.
2. **Make its thumbnail:**
   ```bash
   python3 tools/glb_thumbnail.py molecule/proteins/hiv_protease.glb thumbs/hiv_protease.jpg
   ```
3. **Write its sidecar**, `molecule/proteins/hiv_protease.glb.meta.json`:
   ```json
   {"id": "hiv_protease", "name": "HIV protease", "category": "proteins", "subject": "molecules",
    "title": "HIV-1 protease", "tags": ["protein", "enzyme"], "thumbnail": "thumbs/hiv_protease.jpg"}
   ```
   `name` is what the Molecules list shows.
4. **Run the script and push:**
   ```bash
   python3 tools/build_manifest.py
   git add -A && git commit -m "Add HIV protease" && git push
   ```
5. **In the headset:** open the Molecules list again (close it and open it). It re-reads the repository each time it
   opens, and HIV protease is there under Proteins. GitHub can take up to 5 minutes to serve a list you just pushed.

For an image or video the steps are the same, in `image/` or `video/`. It appears in the Viewer's lists the next time
they're opened. The repository's README covers each kind, files over 95 MB, and the schema.

## In the app

- **Where the list comes from:** `Assets/Tunnel/Resources/TunnelAssetSettings.asset`. It defaults to
  `https://raw.githubusercontent.com/arthurolson/TheTunnelAsset/main/assets.json`.
- **Using another list on one headset, without a build:** write the URL to the file `catalog-url.txt`:
  ```bash
  adb shell "echo https://raw.githubusercontent.com/someone/TheTunnelAsset/dev/assets.json > /sdcard/Android/data/edu.scripps.tunnel/files/TunnelAssets/catalog-url.txt"
  ```
  To go back to the default, delete the file.
- **In a session** every repository toy carries the host's list URL, so all headsets make it from the same list.
- **The pieces:**
  - `RepositoryMolecule` is the toy, one network prefab for every repository molecule.
  - Tunnel > Molecules > Build Repository Toy makes that prefab.
  - `TunnelMoleculeMenu.ChooseFromRepository(assetId)` places one; other scripts can call it.
  - `TunnelAssets.Load(assetId)` loads any asset on one headset.
  - Downloads are cached by sha256 in `files/TunnelAssets/`.
- **What isn't covered yet:**
  - A repository molecule can't be saved in a movie's starting state.
  - Binding sites and ligand pockets are made by their own builders for built-in molecules only.
  - The Viewer doesn't show 360 images and videos, so they aren't listed.
- **David Goodsell** (`goodsell_portrait_mesh`) still comes from the app itself.
