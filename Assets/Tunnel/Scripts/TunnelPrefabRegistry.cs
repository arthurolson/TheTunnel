using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The prefabs that tunnel segments spawn at runtime. Kept in an asset rather than on the segment itself: a prefab
/// field pointing back at its own prefab gets remapped to the instance, and spawning would clone the live segment.
/// All of them must also be in a network prefab list the Network Manager uses.
/// </summary>
[CreateAssetMenu(menuName = "Tunnel/Tunnel Prefab Registry")]
public class TunnelPrefabRegistry : ScriptableObject
{
    [Serializable]
    public class Molecule
    {
        [Tooltip("Shown in the TS Menu's Molecules list (the molecule's folder name).")]
        public string name;
        public GameObject prefab;

        [Tooltip("The heading it's listed under: the category folder it is in (Assets/Molecules/<category>/<molecule>), " +
            "such as \"viral capsids\" or \"proteins\"; empty for a molecule folder directly in Assets/Molecules.")]
        public string category;
    }

    /// <summary>
    /// The molecule categories, in the order the Molecules list shows them: each is a folder in Assets/Molecules.
    /// Any other category folder comes after these, in alphabetical order, and molecules with no category last.
    /// </summary>
    public static readonly string[] categories = { "viral capsids", "proteins", "ligands", "chemicals", "nucleic acids" };

    /// <summary>Where a category comes in the Molecules list (see <see cref="categories"/>).</summary>
    public static int CategoryOrder(string category)
    {
        if (string.IsNullOrEmpty(category))
            return int.MaxValue;
        int index = Array.FindIndex(categories, c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : categories.Length;
    }

    /// <summary>A category's heading in the Molecules list: its folder name with a capital first letter
    /// ("viral capsids" reads "Viral capsids"), or "Other" for molecules with none.</summary>
    public static string CategoryHeading(string category) =>
        string.IsNullOrEmpty(category) ? "Other" : char.ToUpperInvariant(category[0]) + category.Substring(1);

    public GameObject tunnelSegment;
    public GameObject browserPanel;
    public GameObject viewerPanel;

    [Tooltip("The TS Menu's Containers (made by Tunnel > Containers > Build Containers).")]
    public GameObject containerBox;
    public GameObject containerSphere;

    [Tooltip("The TS Menu's Paint: a palette and its marker (made by Tunnel > Paint > Build Paint).")]
    public GameObject palette;
    public GameObject marker;
    public GameObject paintCopy;

    [Tooltip("Filled in by Tunnel > Molecules > Update Molecule Library from the folders in Assets/Molecules, " +
        "grouped by category in the order of TunnelPrefabRegistry.categories.")]
    public List<Molecule> molecules = new();
}
