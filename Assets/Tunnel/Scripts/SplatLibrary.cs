using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The 3D portraits (Gaussian splats, baked to coloured meshes) built into the app, which a splat-box can show without
/// a download: the first rows of the menu beside every splat-box (see <see cref="SplatBox"/>), above the asset
/// repository's meshes (see <see cref="TunnelAssetCatalog"/>). Each has the id of the same file in the repository, so a
/// splat-box asked for that id shows the built-in copy at once. To add one, put its model in Assets/Tunnel/Splats and
/// add a line to SplatBuilder's list, then run Tunnel > Containers > Build Splats; or add an entry here by hand with any
/// prefab. A new splat-box shows the first one.
/// </summary>
[CreateAssetMenu(menuName = "Tunnel/Splat Library")]
public class SplatLibrary : ScriptableObject
{
    [Serializable]
    public class Splat
    {
        [Tooltip("Shown on the splat-box's menu.")]
        public string name;

        [Tooltip("The id of the same model in the asset repository (assets.json), which is how splat-boxes name it.")]
        public string assetId;

        [Tooltip("What the box shows: the splat's mesh with an unlit vertex-colour material (Tunnel/Splat Unlit).")]
        public GameObject prefab;

        [Tooltip("How it is turned in its box (degrees), so the side it reads from faces the box's +Z (its front).")]
        public Vector3 rotation;
    }

    public List<Splat> splats = new();

    [Tooltip("What every mesh loaded from the asset repository is drawn with (Tunnel/Splat Unlit).")]
    public Material material;

    public int count => splats.Count;

    /// <summary>The splat at this place in the list, or null if there is none there.</summary>
    public Splat Get(int index) => index >= 0 && index < splats.Count ? splats[index] : null;

    /// <summary>Where the splat with this name is in the list, or -1.</summary>
    public int IndexOf(string splatName) => splats.FindIndex(s => s != null && s.name == splatName);

    /// <summary>The asset id a new splat-box shows: the first splat's, or "" if there is none.</summary>
    public string firstAssetId => splats.Count > 0 && splats[0] != null ? splats[0].assetId ?? "" : "";
}
