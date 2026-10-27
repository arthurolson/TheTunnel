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
    }

    public GameObject tunnelSegment;
    public GameObject browserPanel;

    [Tooltip("Filled in by Tunnel > Molecules > Update Molecule Library from the folders in Assets/Molecules.")]
    public List<Molecule> molecules = new();
}
