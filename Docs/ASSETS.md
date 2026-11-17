# Assets from the asset repository

The Tunnel shows assets from a GitHub repository at runtime, with no new build. The repository is
[arthurolson/TheTunnelAsset](https://github.com/arthurolson/TheTunnelAsset), a fork of autinlab/TheTunnelAsset. It
holds meshes, images, videos, molecules and viruses. A splat-box shows any of them, and everyone in a session sees the
same asset.

## Adding an asset, end to end

1. **Drop in the file.** In a clone of TheTunnelAsset, put the file in its kind's folder: `mesh/`, `image/`, `video/`,
   `molecule/<category>/` or `virus/capsids/`. Put a thumbnail in `thumbs/`. For a glb, the thumbnail can be drawn:
   `python3 tools/glb_thumbnail.py <file>.glb thumbs/<name>.jpg`.
2. **Write its sidecar** next to it, as `<file>.meta.json`:
   ```json
   {"id": "my_asset", "title": "What the menu shows", "subject": "molecules", "tags": ["protein"], "thumbnail": "thumbs/my_asset.jpg"}
   ```
3. **Run the script and push:**
   ```bash
   python3 tools/build_manifest.py
   git add -A && git commit -m "Add my asset" && git push
   ```
   The script writes `assets.json` and stops on any problem: a file over 95 MB, a duplicate id, a missing thumbnail,
   a checksum mismatch, or a video that won't stream. The repository's GitHub Action also regenerates `assets.json` on
   every push.
4. **It appears in the headset.** The next time The Tunnel starts, the asset is a row in every splat-box's menu.
   raw.githubusercontent.com can take up to 5 minutes to show a new `assets.json`.

The repository's README covers each kind, files too large for GitHub, and the schema.

## In the app

- **Where the list comes from:** `Assets/Tunnel/Resources/TunnelAssetSettings.asset`. It defaults to
  `https://raw.githubusercontent.com/arthurolson/TheTunnelAsset/main/assets.json`.
- **Using another list on one headset, without a build:** write the URL to the file `catalog-url.txt`:
  ```bash
  adb shell "echo https://raw.githubusercontent.com/someone/TheTunnelAsset/dev/assets.json > /sdcard/Android/data/edu.scripps.tunnel/files/TunnelAssets/catalog-url.txt"
  ```
  Or call `TunnelAssetCatalog.UseSource(url, remember: true)`. To go back to the default, delete the file.
- **In a session**, each splat-box carries the host's list URL, so every headset reads the same list.
- **How each kind is shown** (`TunnelAssets.Load(assetId)` returns a GameObject):

  | kind | shown as |
  |---|---|
  | mesh, molecule or virus `.glb` | the model (glTFast, as for David Goodsell); molecules and viruses are shaded from their normals |
  | image | a quad of its proportions; a 360 panorama (tag `equirect`) on an 8 m sphere around the box, its centre straight ahead |
  | video | the same, with spatial sound. It plays from the headset's copy up to 20 MB, otherwise it streams. It loops, and play, pause and position are shared |
  | `.pdb` / `.cif` | its ready-made `surface` glb |
  | virus instance list | its protein glbs, put together at their positions and rotations |
  | splat, `.usdz` | not shown (there's no splat renderer in the project) |
- **Caching:** every downloaded file is kept on the headset by its sha256 (`files/TunnelAssets/`), so it downloads
  once.
- **From other scripts:**
  ```csharp
  TunnelAssets.SpawnInFront("zika_capsid_5ire");              // a shared splat-box, 1 m in front of the caller
  TunnelAssets.Spawn("equirect_test_360", position, rotation);
  GameObject model = await TunnelAssets.Load("egfr", parent);  // this headset only; destroy it to free memory
  splatBox.SetVideoPaused(true);                               // pauses its video for everyone
  ```
- **David Goodsell** (`goodsell_portrait_mesh`) still comes from the app itself, with no download.
