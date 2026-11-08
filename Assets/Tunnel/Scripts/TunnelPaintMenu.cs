using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// The TS Menu's Paint button: puts a palette (<see cref="TunnelPalette"/>) with its marker
/// (<see cref="TunnelMarker"/>) in the holder beside the menu, for everyone (the host places them). Each press places
/// another, so several players can paint at once; Delete removes a palette with its marker.
/// Works locally when not connected.
/// </summary>
public class TunnelPaintMenu : NetworkBehaviour
{
    [SerializeField]
    TunnelPrefabRegistry m_Prefabs;

    [SerializeField]
    TunnelSegmentGeometry m_Geometry;

    [SerializeField]
    Button m_PaintButton;

    [SerializeField]
    [Tooltip("The menu the palette is placed beside.")]
    RectTransform m_Beside;

    [SerializeField]
    float m_GapFromMenu = 0.05f;

    [SerializeField]
    float m_GapFromWall = 0.15f;

    void Awake()
    {
        if (m_PaintButton != null)
            m_PaintButton.onClick.AddListener(Place);
    }

    void Place()
    {
        if (IsSpawned)
            PlaceRpc();
        else
            PlacePalette();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PlaceRpc() => PlacePalette();

    void PlacePalette()
    {
        if (m_Prefabs == null || m_Prefabs.palette == null || m_Prefabs.marker == null || m_Geometry == null || m_Beside == null)
        {
            Debug.LogWarning("[Paint] can't place a palette: its prefabs or the menu are missing");
            return;
        }

        // Placed before they wake up, as the molecules are (the network transforms take their poses when created).
        var holder = new GameObject("Palette Holder");
        holder.SetActive(false);
        var palette = Instantiate(m_Prefabs.palette, holder.transform);
        var frame = m_Geometry.transform;
        float side = frame.InverseTransformPoint(m_Beside.position).x >= 0f ? 1f : -1f;
        // Upright, its face toward the tunnel's middle (its forward into the menu's wall).
        palette.transform.rotation = Quaternion.LookRotation(frame.TransformDirection(Vector3.right * side), frame.up);
        palette.transform.position = TunnelMoleculeMenu.PlaceBesideList(m_Geometry, m_Beside, palette, 0.25f, m_GapFromMenu, m_GapFromWall);
        var marker = Instantiate(m_Prefabs.marker, holder.transform);
        var paletteComponent = palette.GetComponent<TunnelPalette>();
        if (paletteComponent.dock != null)
            marker.transform.SetPositionAndRotation(paletteComponent.dock.position, paletteComponent.dock.rotation);
        palette.transform.SetParent(null, true);
        marker.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Paint] placed a palette at {palette.transform.position:F2}");
        PlayerHudNotification.Instance?.ShowText("Palette placed beside the menu: take the marker from its holder", 3f);

        var markerComponent = marker.GetComponent<TunnelMarker>();
        if (IsSpawned && palette.TryGetComponent(out NetworkObject paletteObject) && marker.TryGetComponent(out NetworkObject markerObject))
        {
            paletteObject.Spawn(true);
            markerObject.Spawn(true);
        }
        markerComponent.SetPalette(paletteComponent);
    }
}
