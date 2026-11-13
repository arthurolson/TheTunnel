using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The 3D portraits (Gaussian splats, baked to coloured meshes) a splat-box can show, by name: the list on the menu
/// beside every splat-box (see <see cref="SplatBox"/>). To add one, put its model in Assets/Tunnel/Splats and add a line
/// to SplatBuilder's list, then run Tunnel > Containers > Build Splats; or add an entry here by hand with any prefab.
/// Its order is shared by every headset (a splat-box shows its splat by its place in this list), so add new ones at the
/// end.
/// </summary>
[CreateAssetMenu(menuName = "Tunnel/Splat Library")]
public class SplatLibrary : ScriptableObject
{
    [Serializable]
    public class Splat
    {
        [Tooltip("Shown on the splat-box's menu.")]
        public string name;

        [Tooltip("What the box shows: the splat's mesh with an unlit vertex-colour material (Tunnel/Splat Unlit).")]
        public GameObject prefab;

        [Tooltip("How it is turned in its box (degrees), so the side it reads from faces the box's +Z (its front).")]
        public Vector3 rotation;
    }

    public List<Splat> splats = new();

    public int count => splats.Count;

    /// <summary>The splat at this place in the list, or null if there is none there.</summary>
    public Splat Get(int index) => index >= 0 && index < splats.Count ? splats[index] : null;

    /// <summary>Where the splat with this name is in the list, or -1.</summary>
    public int IndexOf(string splatName) => splats.FindIndex(s => s != null && s.name == splatName);
}
