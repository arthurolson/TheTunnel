using UnityEngine;

/// <summary>
/// On a model loaded from the asset repository (see <see cref="TunnelAssets.Load"/>): which asset it is, and, when it
/// goes, frees the asset's meshes and textures if no other model on this headset still uses them.
/// </summary>
public class TunnelAssetInstance : MonoBehaviour
{
    bool m_Bound;

    /// <summary>The id of the asset it shows (its Quest version's on a headset, if it has one).</summary>
    public string assetId { get; private set; }

    internal void Bind(string id)
    {
        assetId = id;
        m_Bound = true;
    }

    void OnDestroy()
    {
        if (m_Bound)
            TunnelAssets.Release(assetId);
        m_Bound = false;
    }
}
