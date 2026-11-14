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
/// The Magnification slider (1 to 10) under the list sets how large new molecules are: every molecule keeps its true
/// size relative to the others (one ångström-to-meter scale for all), times the magnification, so two molecules placed
/// at the same magnification are in proportion. In a session everyone shares the one slider and sees the same
/// messages. The Spawn nozzle check box under the slider calls up the spawn nozzle (see <see cref="SpawnNozzle"/>)
/// in front of whoever ticks it, for everyone, or removes it. Works locally when not connected.
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

    [Header("Magnification")]
    [SerializeField]
    Slider m_MagnificationSlider;

    [SerializeField]
    TMP_Text m_MagnificationText;

    [Header("Spawn nozzle")]
    [SerializeField]
    [Tooltip("The Spawn nozzle check box (made by Tunnel > Molecules > Build Spawn Nozzle).")]
    Button m_NozzleRow;

    [SerializeField]
    [Tooltip("The spawn nozzle it calls up.")]
    SpawnNozzle m_NozzlePrefab;

    [SerializeField]
    [Tooltip("How far in front of whoever ticks the box a new nozzle appears (meters).")]
    float m_NozzleDistance = 0.45f;

    [SerializeField]
    [Tooltip("How far below their eyes it appears (meters).")]
    float m_NozzleDrop = 0.2f;

    const int k_MinMagnification = 1;
    const int k_MaxMagnification = 10;
    const float k_FreeSpotStep = 0.05f; // meters along the tunnel between tries for a free spot

    public bool isListOpen => m_ListPanel != null && m_ListPanel.activeSelf;

    readonly NetworkVariable<byte> m_SharedMagnification = new(k_MinMagnification, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    int magnification => IsSpawned
        ? Mathf.Clamp(m_SharedMagnification.Value, k_MinMagnification, k_MaxMagnification)
        : m_MagnificationSlider != null
            ? Mathf.Clamp(Mathf.RoundToInt(m_MagnificationSlider.value), k_MinMagnification, k_MaxMagnification)
            : k_MinMagnification;

    void Awake()
    {
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);

        if (m_MagnificationSlider != null)
        {
            m_MagnificationSlider.minValue = k_MinMagnification;
            m_MagnificationSlider.maxValue = k_MaxMagnification;
            m_MagnificationSlider.wholeNumbers = true;
            m_MagnificationSlider.SetValueWithoutNotify(k_MinMagnification);
            m_MagnificationSlider.onValueChanged.AddListener(OnMagnificationMoved);
        }

        ShowMagnification();
        if (m_NozzleRow != null)
            m_NozzleRow.onClick.AddListener(ToggleNozzle);
    }

    void Update()
    {
        if (isListOpen)
            ShowNozzleRow();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_SharedMagnification.OnValueChanged += OnMagnificationChanged;
        OnMagnificationChanged(0, m_SharedMagnification.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_SharedMagnification.OnValueChanged -= OnMagnificationChanged;
        base.OnNetworkDespawn();
    }

    void OnMagnificationMoved(float value)
    {
        if (IsSpawned)
            SetMagnificationRpc((byte)Mathf.Clamp(Mathf.RoundToInt(value), k_MinMagnification, k_MaxMagnification));
        ShowMagnification();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetMagnificationRpc(byte value) => m_SharedMagnification.Value = (byte)Mathf.Clamp(value, k_MinMagnification, k_MaxMagnification);

    // Another player moved the slider (or this one's move came back from the host).
    void OnMagnificationChanged(byte previous, byte current)
    {
        if (m_MagnificationSlider != null && Mathf.RoundToInt(m_MagnificationSlider.value) != current)
            m_MagnificationSlider.SetValueWithoutNotify(current);
        ShowMagnification();
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (m_MagnificationSlider != null)
            m_MagnificationSlider.onValueChanged.RemoveAllListeners();
        if (m_NozzleRow != null)
            m_NozzleRow.onClick.RemoveAllListeners();
    }

    void ShowMagnification()
    {
        if (m_MagnificationText != null)
            m_MagnificationText.text = $"Magnification: ×{(m_MagnificationSlider != null ? Mathf.RoundToInt(m_MagnificationSlider.value) : magnification)}";
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
            : "Set the magnification, then tap a molecule to place it beside this list.");
    }

    void Choose(int index)
    {
        var molecules = m_Prefabs != null ? m_Prefabs.molecules : null;
        if (molecules == null || index < 0 || index >= molecules.Count)
            return;

        int mag = magnification;
        ShareStatus($"Placing {molecules[index].name} at ×{mag}...");
        Debug.Log($"[Molecules] chose {molecules[index].name} at ×{mag}: " +
            (IsSpawned ? (IsServer ? "placing it for everyone" : "asking the host to place it") : "not connected, placing it here only"));
        if (IsSpawned)
            SpawnMoleculeRpc(index, mag);
        else
            SpawnMolecule(index, mag);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnMoleculeRpc(int index, int mag) => SpawnMolecule(index, mag);

    void SpawnMolecule(int index, int mag)
    {
        mag = Mathf.Clamp(mag, k_MinMagnification, k_MaxMagnification);
        if (m_Prefabs == null || index < 0 || index >= m_Prefabs.molecules.Count || m_Geometry == null)
            return;
        var prefab = m_Prefabs.molecules[index].prefab;
        if (prefab == null)
            return;

        // Placed and sized before it wakes up: its network transform takes its pose when it's created, so moving it
        // afterwards would snap it back to the prefab's own spot and size once it spawns for everyone.
        var holder = new GameObject("Molecule Holder");
        holder.SetActive(false);
        var molecule = Instantiate(prefab, holder.transform);
        // Every molecule toy shares one ångström-to-meter scale, so the same factor keeps them in proportion.
        molecule.transform.localScale = prefab.transform.localScale * mag;
        molecule.transform.rotation = m_Geometry.transform.rotation;
        molecule.transform.position = PlaceOnWall(molecule);
        molecule.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Molecules] placed {m_Prefabs.molecules[index].name} at ×{mag}, radius {WorldRadius(molecule):F3} m, at {m_Geometry.transform.InverseTransformPoint(molecule.transform.position):F3} in the segment " +
            $"(walls at x = ±{m_Geometry.width * 0.5f:F3})");

        var networkObject = molecule.GetComponent<NetworkObject>();
        if (IsSpawned && networkObject != null)
            networkObject.Spawn(true);
    }

    /// <summary>
    /// Beside the menu's wall, at the list's height, just past the list's far edge, inside the tunnel; if another
    /// molecule is already there, the first free spot further along the tunnel.
    /// </summary>
    Vector3 PlaceOnWall(GameObject molecule) =>
        PlaceBesideList(m_Geometry, (RectTransform)m_ListPanel.transform, molecule, WorldRadius(molecule), m_GapFromList, m_GapFromWall);

    /// <summary>
    /// Where to put something new of this radius beside a TS Menu list: on the menu's wall, at the list's height,
    /// past the list's far edge, the first spot along the tunnel that no molecule takes up.
    /// </summary>
    public static Vector3 PlaceBesideList(TunnelSegmentGeometry geometry, RectTransform list, GameObject placing, float radius, float gapFromList, float gapFromWall)
    {
        Transform frame = geometry.transform;

        // The menu sits on one of the side walls; use that one.
        Vector3 listCenter = frame.InverseTransformPoint(list.position);
        float side = listCenter.x >= 0f ? 1f : -1f;

        // The list's corners in the segment's frame: the new thing goes beyond the corner furthest along the tunnel
        // from the menu (the list opens away from the near end).
        var corners = new Vector3[4];
        list.GetWorldCorners(corners);
        float farZ = float.MinValue;
        foreach (var corner in corners)
            farZ = Mathf.Max(farZ, frame.InverseTransformPoint(corner).z);

        float halfLength = geometry.length * 0.5f;
        float firstZ = Mathf.Min(farZ + gapFromList + radius, halfLength - radius);
        float x = side * (geometry.width * 0.5f - radius - gapFromWall);

        Physics.SyncTransforms(); // molecules placed moments ago
        for (float z = firstZ; z <= halfLength - radius; z += k_FreeSpotStep)
        {
            var position = frame.TransformPoint(new Vector3(x, listCenter.y, z));
            if (!OverlapsOtherMolecule(placing, position, radius))
                return position;
        }

        return frame.TransformPoint(new Vector3(x, listCenter.y, firstZ));
    }

    static readonly Collider[] s_Overlaps = new Collider[32];

    static bool OverlapsOtherMolecule(GameObject molecule, Vector3 position, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(position, radius + 0.01f, s_Overlaps, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var body = s_Overlaps[i].attachedRigidbody;
            if (body != null && body.gameObject != molecule && (body.GetComponent<MoleculeGravity>() != null || body.GetComponent<TunnelContainer>() != null))
                return true;
        }

        return false;
    }

    /// <summary>The radius of a sphere around the molecule's collider (read from the collider's own settings: it is
    /// still inactive while it's placed, so it has no bounds yet).</summary>
    public static float WorldRadius(GameObject molecule)
    {
        // Molecules carry one collider: a sphere at their outer radius, or an oriented box or ellipsoid around their
        // surface (see Tunnel > Molecules > Update Molecule Library).
        var collider = molecule.GetComponentInChildren<Collider>(true);
        if (collider != null)
        {
            var scale = collider.transform.lossyScale;
            float offset = (collider.transform.position - molecule.transform.position).magnitude;
            switch (collider)
            {
                case SphereCollider sphere:
                    return offset + sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                case BoxCollider box:
                    return offset + Vector3.Scale(box.size, scale).magnitude * 0.5f;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    var extents = Vector3.Scale(mesh.sharedMesh.bounds.extents, scale);
                    return offset + Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            }
        }

        var renderer = molecule.GetComponentInChildren<Renderer>(true);
        return renderer != null ? renderer.bounds.extents.magnitude : 0.05f;
    }

    #region Spawn nozzle

    // ■ while there is a nozzle in the tunnel, □ when there isn't.
    void ShowNozzleRow()
    {
        if (m_NozzleRow == null)
            return;
        bool on = SpawnNozzle.current != null;
        string text = on ? "■  Spawn nozzle  <color=#AAA>untick to remove it</color>" : "□  Spawn nozzle  <color=#AAA>sprays copies of a molecule</color>";
        var label = m_NozzleRow.GetComponentInChildren<TMP_Text>(true);
        if (label != null && label.text != text)
            label.text = text;
    }

    void ToggleNozzle()
    {
        bool on = SpawnNozzle.current == null;
        var head = Camera.main != null ? Camera.main.transform : null;
        var pose = head != null ? NozzlePose(head.position, head.forward, m_NozzleDistance, m_NozzleDrop)
            : new Pose(m_ListPanel.transform.position - m_ListPanel.transform.forward * m_NozzleDistance, Quaternion.LookRotation(-m_ListPanel.transform.forward));
        Debug.Log($"[Nozzle] Spawn nozzle {(on ? "ticked" : "unticked")}: " +
            (IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only"));
        ShareStatus(on ? "Spawn nozzle placed in front of you: let go of a molecule at the cup on its back, then Spawn on its menu."
            : "Spawn nozzle removed.");
        if (IsSpawned)
            NozzleRpc(on, pose.position, pose.rotation);
        else
            SetNozzle(on, pose);
        ShowNozzleRow();
    }

    /// <summary>
    /// Where a new nozzle goes: in front of someone's eyes, level, a little below them, pointing the way they look.
    /// </summary>
    public static Pose NozzlePose(Vector3 eyes, Vector3 looking, float distance, float drop)
    {
        var forward = Vector3.ProjectOnPlane(looking, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;
        forward.Normalize();
        return new Pose(eyes + forward * distance + Vector3.down * drop, Quaternion.LookRotation(forward, Vector3.up));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NozzleRpc(bool on, Vector3 position, Quaternion rotation) => SetNozzle(on, new Pose(position, rotation));

    // On the host (or here when not connected): one nozzle at most.
    void SetNozzle(bool on, Pose pose)
    {
        var current = SpawnNozzle.current;
        if (!on)
        {
            if (current == null)
                return;
            Debug.Log("[Nozzle] removed");
            if (current.TryGetComponent(out NetworkObject currentObject) && currentObject.IsSpawned)
                currentObject.Despawn(true);
            else
                Destroy(current.gameObject);
            return;
        }
        if (current != null)
            return;
        if (m_NozzlePrefab == null)
        {
            Debug.LogWarning("[Nozzle] no spawn nozzle to place (run Tunnel > Molecules > Build Spawn Nozzle)");
            return;
        }

        // Placed before it wakes up, as the molecules are (its network transform takes its pose when it's created).
        var holder = new GameObject("Nozzle Holder");
        holder.SetActive(false);
        var nozzle = Instantiate(m_NozzlePrefab, holder.transform);
        nozzle.transform.SetPositionAndRotation(pose.position, pose.rotation);
        nozzle.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Nozzle] placed at {pose.position:F2}");
        if (IsSpawned && nozzle.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
    }

    #endregion

    /// <summary>A message on everyone's copy of the menu.</summary>
    void ShareStatus(string text)
    {
        if (IsSpawned)
            StatusRpc(text);
        else
            SetStatus(text);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void StatusRpc(string text) => SetStatus(text);

    void SetStatus(string text)
    {
        if (m_StatusText != null)
            m_StatusText.text = text;
    }
}
