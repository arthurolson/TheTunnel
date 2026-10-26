using UnityEngine;

/// <summary>
/// The prefabs that tunnel segments spawn at runtime. Kept in an asset rather than on the segment itself: a prefab
/// field pointing back at its own prefab gets remapped to the instance, and spawning would clone the live segment.
/// Both prefabs must also be in a network prefab list the Network Manager uses.
/// </summary>
[CreateAssetMenu(menuName = "Tunnel/Tunnel Prefab Registry")]
public class TunnelPrefabRegistry : ScriptableObject
{
    public GameObject tunnelSegment;
    public GameObject browserPanel;
}
