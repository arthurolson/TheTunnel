using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The TS Menu's "Molecules" list: one row per molecule in Assets/Molecules (see <see cref="TunnelPrefabRegistry"/>).
/// Tapping a name places that molecule for everyone inside the tunnel, beside the list's far edge at the list's height,
/// a little out from the wall the menu is on. It stays there until someone grabs it.
/// Works locally when not connected.
/// </summary>
public class TunnelMoleculeMenu : NetworkBehaviour
{
    [SerializeField]
    TunnelPrefabRegistry m_Prefabs;

    [SerializeField]
    TunnelSegmentGeometry m_Geometry;

    [Header("List UI")]
    [SerializeField]
    GameObject m_ListPanel;

    [SerializeField]
    RectTransform m_ListContent;

    [SerializeField]
    [Tooltip("Inactive row used as the template for list entries (a Button with a TMP label).")]
    Button m_RowTemplate;

    [SerializeField]
    TMP_Text m_StatusText;

    [SerializeField]
    float m_RowSpacing = 58f;

    [SerializeField]
    [Tooltip("Gap between the list's far edge and a new molecule (meters).")]
    float m_GapFromList = 0.05f;

    [SerializeField]
    [Tooltip("Gap between a new molecule's surface and the wall (meters), so it's clearly inside the tunnel.")]
    float m_GapFromWall = 0.1f;

    public bool isListOpen => m_ListPanel != null && m_ListPanel.activeSelf;

    void Awake()
    {
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
    }

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        if (show)
            BuildList();
    }

    void BuildList()
    {
        if (m_ListContent == null || m_RowTemplate == null)
            return;

        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);

        var molecules = m_Prefabs != null ? m_Prefabs.molecules : new List<TunnelPrefabRegistry.Molecule>();
        for (int i = 0; i < molecules.Count; i++)
        {
            int index = i;
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            row.GetComponentInChildren<TMP_Text>(true).text = molecules[i].name;
            row.onClick.AddListener(() => Choose(index));
        }

        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(1, molecules.Count) * m_RowSpacing);
        SetStatus(molecules.Count == 0
            ? "No molecules yet. Add a folder with a model to Assets/Molecules and rebuild the app."
            : "Tap a molecule to place it on the wall beside this list.");
    }

    void Choose(int index)
    {
        var molecules = m_Prefabs != null ? m_Prefabs.molecules : null;
        if (molecules == null || index < 0 || index >= molecules.Count)
            return;

        SetStatus($"Placing {molecules[index].name}...");
        if (IsSpawned)
            SpawnMoleculeRpc(index);
        else
            SpawnMolecule(index);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnMoleculeRpc(int index) => SpawnMolecule(index);

    void SpawnMolecule(int index)
    {
        if (m_Prefabs == null || index < 0 || index >= m_Prefabs.molecules.Count || m_Geometry == null)
            return;
        var prefab = m_Prefabs.molecules[index].prefab;
        if (prefab == null)
            return;

        var molecule = Instantiate(prefab);
        molecule.transform.rotation = m_Geometry.transform.rotation;
        molecule.transform.position = PlaceOnWall(molecule);
        Debug.Log($"[Molecules] placed {m_Prefabs.molecules[index].name} at {m_Geometry.transform.InverseTransformPoint(molecule.transform.position):F3} in the segment " +
            $"(walls at x = ±{m_Geometry.width * 0.5f:F3})");

        var networkObject = molecule.GetComponent<NetworkObject>();
        if (IsSpawned && networkObject != null)
            networkObject.Spawn(true);
    }

    /// <summary>Beside the menu's wall, at the list's height, just past the list's far edge, inside the tunnel.</summary>
    Vector3 PlaceOnWall(GameObject molecule)
    {
        Transform frame = m_Geometry.transform;
        float radius = WorldRadius(molecule);

        // The menu sits on one of the side walls; use that one.
        Vector3 listCenter = frame.InverseTransformPoint(m_ListPanel.transform.position);
        float side = listCenter.x >= 0f ? 1f : -1f;

        // The list's corners in the segment's frame: the new molecule goes beyond the corner furthest along the
        // tunnel from the menu (the list opens away from the near end).
        var corners = new Vector3[4];
        ((RectTransform)m_ListPanel.transform).GetWorldCorners(corners);
        float farZ = float.MinValue;
        foreach (var corner in corners)
            farZ = Mathf.Max(farZ, frame.InverseTransformPoint(corner).z);

        float halfLength = m_Geometry.length * 0.5f;
        float z = Mathf.Min(farZ + m_GapFromList + radius, halfLength - radius);
        float x = side * (m_Geometry.width * 0.5f - radius - m_GapFromWall);
        return frame.TransformPoint(new Vector3(x, listCenter.y, z));
    }

    static float WorldRadius(GameObject molecule)
    {
        // Molecules carry one sphere collider at their outer radius (see Tunnel > Molecules > Update Molecule Library).
        var sphere = molecule.GetComponentInChildren<SphereCollider>();
        if (sphere != null)
        {
            var scale = sphere.transform.lossyScale;
            return sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
        }

        var renderer = molecule.GetComponentInChildren<Renderer>();
        return renderer != null ? renderer.bounds.extents.magnitude : 0.05f;
    }

    void SetStatus(string text)
    {
        if (m_StatusText != null)
            m_StatusText.text = text;
    }
}
