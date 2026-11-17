using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// On an asset loaded from the asset repository (see <see cref="TunnelAssets.Load"/>): which asset it is, whether it is
/// shown all round the viewer (a 360 image or video), its video if it has one, and, when it goes, frees what it used:
/// the asset's meshes and textures if no other model on this headset still uses them, and its own picture, video
/// texture and materials.
/// </summary>
public class TunnelAssetInstance : MonoBehaviour
{
    bool m_Bound;
    readonly List<Object> m_Owned = new();

    /// <summary>The id of the asset it shows (its Quest version's on a headset, if it has one).</summary>
    public string assetId { get; private set; }

    /// <summary>A 360 image or video on a sphere around its centre: shown at its own size, not fitted into a box.</summary>
    public bool surround { get; internal set; }

    /// <summary>Its video, for a video asset; null otherwise.</summary>
    public TunnelVideo video { get; internal set; }

    /// <summary>Frees this asset's shared file (its glTF import) when this goes.</summary>
    internal void Bind(string id)
    {
        assetId = id;
        m_Bound = true;
    }

    /// <summary>Only names the asset (nothing shared to free).</summary>
    internal void Name(string id) => assetId = id;

    /// <summary>Destroys this (a texture, material, mesh or render texture made for it) when it goes.</summary>
    internal T Own<T>(T thing) where T : Object
    {
        if (thing != null)
            m_Owned.Add(thing);
        return thing;
    }

    void OnDestroy()
    {
        if (m_Bound)
            TunnelAssets.Release(assetId);
        m_Bound = false;
        foreach (var thing in m_Owned)
        {
            if (thing is RenderTexture rt)
                rt.Release();
            if (thing != null)
                Destroy(thing);
        }
        m_Owned.Clear();
    }
}
