using UnityEditor;
using UnityEngine;

/// <summary>
/// Tunnel > Players > Add Shared Lasers: puts a <see cref="PlayerLaser"/> on the networked player
/// (XRI_Network_Player_Avatar), so every player sees the others' lasers. Running it again changes nothing.
/// </summary>
public static class PlayerLaserBuilder
{
    const string k_PlayerPath = "Assets/XRMP/Prefabs/PlayerPrefabs/XRI_Network_Player_Avatar.prefab";

    [MenuItem("Tunnel/Players/Add Shared Lasers")]
    public static void Build()
    {
        var root = PrefabUtility.LoadPrefabContents(k_PlayerPath);
        try
        {
            if (root.GetComponent<PlayerLaser>() != null)
            {
                Debug.Log("[Laser] the networked player already shares its lasers");
                return;
            }
            root.AddComponent<PlayerLaser>();
            PrefabUtility.SaveAsPrefabAsset(root, k_PlayerPath);
            Debug.Log("[Laser] the networked player now shares its lasers with the other players");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
