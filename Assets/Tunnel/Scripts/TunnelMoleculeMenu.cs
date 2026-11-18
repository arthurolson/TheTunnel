using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The TS Menu's "Molecules" list: one row per molecule in Assets/Molecules (see <see cref="TunnelPrefabRegistry"/>),
/// grouped under a heading for each category that has any (Viral capsids, Proteins, Ligands, Chemicals, Nucleic
/// acids, in that order: the category folders in Assets/Molecules). Headings are only labels; a category with nothing
/// in it yet has no heading.
/// Tapping a name places that molecule for everyone inside the tunnel, beside the list's far edge at the list's height,
/// a little out from the wall the menu is on. It stays there until someone grabs it.
/// The Magnification slider (×1 to ×20) under the list sets how large new molecules are: every molecule keeps its true
/// size relative to the others (one ångström-to-meter scale for all), times the magnification, so two molecules placed
/// at the same magnification are in proportion. The slider steps through ×1 to ×10 one at a time, then ×12, ×14, ×16,
/// ×18 and ×20 (see <see cref="k_Steps"/>), so the small magnifications keep most of its length and stay easy to set. In a session everyone shares the one slider and sees the same
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
    [Tooltip("Height of a category heading in the list (it is shorter than a molecule's row).")]
    float m_HeadingSpacing = 40f;

    [SerializeField]
    [Tooltip("Colour of the category headings' text.")]
    Color m_HeadingColor = new(0.62f, 0.83f, 1f, 1f);

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
    const int k_MaxMagnification = 20;

    /// <summary>The magnifications the slider stops at, left to right: one step at a time to ×10, then two at a time to ×20.</summary>
    public static readonly int[] k_Steps = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 18, 20 };

    /// <summary>The slider's stop for a magnification (the nearest one).</summary>
    public static int StepOf(int magnification)
    {
        int best = 0;
        for (int i = 1; i < k_Steps.Length; i++)
            if (Mathf.Abs(k_Steps[i] - magnification) < Mathf.Abs(k_Steps[best] - magnification))
                best = i;
        return best;
    }

    /// <summary>The magnification at a slider stop.</summary>
    public static int MagnificationAt(float step) => k_Steps[Mathf.Clamp(Mathf.RoundToInt(step), 0, k_Steps.Length - 1)];

    static readonly List<TunnelMoleculeMenu> s_Menus = new();

    /// <summary>
    /// The Molecules list's magnification now (the shared one in a session), for what else makes molecules: the New
    /// movie menu fills a container at it. ×1 if there is no Molecules list.
    /// </summary>
    public static int currentMagnification
    {
        get
        {
            foreach (var menu in s_Menus)
                if (menu != null && menu.IsSpawned)
                    return menu.magnification;
            foreach (var menu in s_Menus)
                if (menu != null)
                    return menu.magnification;
            return k_MinMagnification;
        }
    }
    const float k_FreeSpotStep = 0.05f; // meters along the tunnel between tries for a free spot

    public bool isListOpen => m_ListPanel != null && m_ListPanel.activeSelf;

    readonly NetworkVariable<byte> m_SharedMagnification = new(k_MinMagnification, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    int magnification => IsSpawned
        ? Mathf.Clamp(m_SharedMagnification.Value, k_MinMagnification, k_MaxMagnification)
        : m_MagnificationSlider != null
            ? MagnificationAt(m_MagnificationSlider.value)
            : k_MinMagnification;

    void Awake()
    {
        // Each molecule's Freeze checkbox (one on this headset, shown above the molecule held or pointed at).
        if (m_Prefabs != null)
            MoleculeFreezeCheckbox.EnsureFrom(m_Prefabs.freezeCheckbox);
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);

        if (m_MagnificationSlider != null)
        {
            // The slider counts stops (see k_Steps), not the magnification itself.
            m_MagnificationSlider.minValue = 0;
            m_MagnificationSlider.maxValue = k_Steps.Length - 1;
            m_MagnificationSlider.wholeNumbers = true;
            m_MagnificationSlider.SetValueWithoutNotify(StepOf(k_MinMagnification));
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

    /// <summary>
    /// Sets the Magnification slider on every Molecules list (a 3D movie chosen sets it to its own), as if someone had
    /// moved it: shared with everyone in a session (the host's value is everyone's), here only when not connected.
    /// </summary>
    public static void SetMagnification(int magnification)
    {
        magnification = MagnificationAt(StepOf(Mathf.Clamp(magnification, k_MinMagnification, k_MaxMagnification)));
        foreach (var menu in s_Menus)
            if (menu != null)
                menu.ApplyMagnification(magnification);
    }

    void ApplyMagnification(int value)
    {
        if (IsSpawned)
        {
            if (IsServer)
                m_SharedMagnification.Value = (byte)value;
            else
                SetMagnificationRpc((byte)value);
        }
        if (m_MagnificationSlider != null)
            m_MagnificationSlider.SetValueWithoutNotify(StepOf(value));
        ShowMagnification();
        Debug.Log($"[Molecules] magnification set to ×{value} by a 3D movie");
    }

    void OnEnable() => s_Menus.Add(this);

    void OnDisable() => s_Menus.Remove(this);

    void OnMagnificationMoved(float step)
    {
        int value = MagnificationAt(step);
        Debug.Log($"[Molecules] magnification ×{value}");
        if (IsSpawned)
            SetMagnificationRpc((byte)Mathf.Clamp(value, k_MinMagnification, k_MaxMagnification));
        ShowMagnification();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetMagnificationRpc(byte value) => m_SharedMagnification.Value = (byte)Mathf.Clamp(value, k_MinMagnification, k_MaxMagnification);

    // Another player moved the slider (or this one's move came back from the host).
    void OnMagnificationChanged(byte previous, byte current)
    {
        if (m_MagnificationSlider != null && MagnificationAt(m_MagnificationSlider.value) != current)
            m_MagnificationSlider.SetValueWithoutNotify(StepOf(current));
        ShowMagnification();
    }

    public override void OnDestroy()
    {
        TunnelAssetCatalog.changed -= OnCatalogChanged;
        base.OnDestroy();
        if (m_MagnificationSlider != null)
            m_MagnificationSlider.onValueChanged.RemoveAllListeners();
        if (m_NozzleRow != null)
            m_NozzleRow.onClick.RemoveAllListeners();
    }

    void ShowMagnification()
    {
        if (m_MagnificationText != null)
            m_MagnificationText.text = $"Magnification: ×{(m_MagnificationSlider != null ? MagnificationAt(m_MagnificationSlider.value) : magnification)}";
    }

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        TunnelAssetCatalog.changed -= OnCatalogChanged;
        if (show)
        {
            BuildList();
            // Molecules just added to the asset repository: read its list again, and show them when it comes.
            TunnelAssetCatalog.changed += OnCatalogChanged;
            _ = TunnelAssetCatalog.Refresh();
        }
    }

    void OnCatalogChanged()
    {
        if (this != null && m_ListPanel != null && m_ListPanel.activeSelf)
            BuildList();
    }

    /// <summary>
    /// A row of the Molecules list: a molecule built into the app (its place in the registry), or one from the asset
    /// repository (its asset id), which becomes a <see cref="RepositoryMolecule"/> toy.
    /// </summary>
    struct Entry
    {
        public string name;
        public string category;
        public int builtIn;     // -1 for one from the repository
        public string assetId;
    }

    /// <summary>
    /// The built-in molecules, then those in the asset repository under the same headings (a repository molecule with
    /// a built-in one's name is that one, so it isn't listed twice), each category's in its order.
    /// </summary>
    List<Entry> Entries()
    {
        var entries = new List<Entry>();
        var builtIn = m_Prefabs != null ? m_Prefabs.molecules : new List<TunnelPrefabRegistry.Molecule>();
        for (int i = 0; i < builtIn.Count; i++)
            entries.Add(new Entry { name = builtIn[i].name, category = builtIn[i].category ?? "", builtIn = i });
        if (m_Prefabs != null && m_Prefabs.repositoryMolecule != null)
        {
            var names = new HashSet<string>(builtIn.Select(m => m.name), System.StringComparer.OrdinalIgnoreCase);
            foreach (var asset in TunnelAssetCatalog.Molecules())
            {
                string name = TunnelAssetCatalog.MenuName(asset);
                if (names.Add(name))
                    entries.Add(new Entry { name = name, category = TunnelAssetCatalog.CategoryOf(asset), builtIn = -1, assetId = asset.id });
            }
        }
        // Stable: within a category, built-in ones first, as the library listed them.
        return entries.Select((e, i) => (e, i))
            .OrderBy(x => TunnelPrefabRegistry.CategoryOrder(x.e.category))
            .ThenBy(x => TunnelPrefabRegistry.CategoryOrder(x.e.category) == TunnelPrefabRegistry.categories.Length ? x.e.category : "")
            .ThenBy(x => x.i)
            .Select(x => x.e)
            .ToList();
    }

    /// <summary>
    /// Molecules placed from the list arrive frozen where they are put (see <see cref="MoleculeGravity.isFrozen"/>),
    /// for scripts; in the headset each molecule has its own Freeze checkbox (<see cref="MoleculeFreezeCheckbox"/>).
    /// </summary>
    public bool placeFrozen { get; set; }

    void BuildList()
    {
        if (m_ListContent == null || m_RowTemplate == null)
            return;

        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);

        var molecules = Entries();
        float y = 0f;

        string category = null;
        for (int i = 0; i < molecules.Count; i++)
        {
            // A heading where a new category starts (the list keeps each category's molecules together).
            string next = molecules[i].category ?? "";
            if (next != category)
            {
                category = next;
                AddHeading(TunnelPrefabRegistry.CategoryHeading(category), ref y);
            }

            var entry = molecules[i];
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.name = $"Row {entry.name}";
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -y);
            row.GetComponentInChildren<TMP_Text>(true).text = entry.name;
            if (entry.builtIn >= 0)
                row.onClick.AddListener(() => Choose(entry.builtIn));
            else
                row.onClick.AddListener(() => ChooseFromRepository(entry.assetId, entry.name));
            y += m_RowSpacing;
        }

        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(m_RowSpacing, y));
        SetStatus(molecules.Count == 0
            ? "No molecules yet. Add one to the asset repository (or a folder with a model to Assets/Molecules and rebuild)."
            : "Set the magnification, then tap a molecule to place it beside this list.");
    }

    // A category's heading: the row template as a plain label (no button, no background), shorter than a row.
    void AddHeading(string text, ref float y)
    {
        var heading = Instantiate(m_RowTemplate, m_ListContent);
        heading.name = $"Heading {text}";
        heading.gameObject.SetActive(true);
        heading.interactable = false;
        heading.transition = Selectable.Transition.None;
        if (heading.targetGraphic != null)
            heading.targetGraphic.enabled = false;
        var rect = (RectTransform)heading.transform;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, m_HeadingSpacing - 4f);
        rect.anchoredPosition = new Vector2(0f, -y);
        var label = heading.GetComponentInChildren<TMP_Text>(true);
        label.text = $"<b>{text}</b>";
        label.color = m_HeadingColor;
        label.alignment = TextAlignmentOptions.BottomLeft;
        y += m_HeadingSpacing;
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
            SpawnMoleculeRpc(index, mag, placeFrozen);
        else
            SpawnMolecule(index, mag, placeFrozen);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnMoleculeRpc(int index, int mag, bool frozen) => SpawnMolecule(index, mag, frozen);

    /// <summary>
    /// Places a molecule from the asset repository, as a built-in one is placed: a <see cref="RepositoryMolecule"/>
    /// toy at the magnification, beside this list, which every headset then makes from the molecule's model.
    /// Other scripts can call it with any molecule's asset id.
    /// </summary>
    public void ChooseFromRepository(string assetId, string displayName = null)
    {
        if (string.IsNullOrEmpty(assetId) || assetId.Length > 120)
            return;
        int mag = magnification;
        displayName ??= TunnelAssetCatalog.MenuName(TunnelAssetCatalog.Get(assetId));
        ShareStatus($"Placing {displayName} at ×{mag}...");
        Debug.Log($"[Molecules] chose {displayName} ({assetId}, from the asset repository) at ×{mag}: " +
            (IsSpawned ? (IsServer ? "placing it for everyone" : "asking the host to place it") : "not connected, placing it here only"));
        if (IsSpawned)
            SpawnRepositoryRpc(new FixedString128Bytes(assetId), mag, placeFrozen);
        else
            SpawnRepository(assetId, mag, placeFrozen);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnRepositoryRpc(FixedString128Bytes assetId, int mag, bool frozen) => SpawnRepository(assetId.ToString(), mag, frozen);

    void SpawnRepository(string assetId, int mag, bool frozen = false)
    {
        mag = Mathf.Clamp(mag, k_MinMagnification, k_MaxMagnification);
        var prefab = m_Prefabs != null ? m_Prefabs.repositoryMolecule : null;
        if (prefab == null || m_Geometry == null)
        {
            Debug.LogWarning("[Molecules] no repository toy to place (run Tunnel > Molecules > Build Repository Toy)");
            return;
        }
        var asset = TunnelAssetCatalog.Get(assetId);
        string name = asset != null ? TunnelAssetCatalog.MenuName(asset) : assetId;

        // Placed, sized and given its molecule before it wakes up, as the built-in ones are.
        var holder = new GameObject("Molecule Holder");
        holder.SetActive(false);
        var molecule = Instantiate(prefab, holder.transform);
        molecule.transform.localScale = prefab.transform.localScale * mag;
        molecule.transform.rotation = m_Geometry.transform.rotation;
        var repository = molecule.GetComponent<RepositoryMolecule>();
        repository.Preset(assetId);
        if (frozen && molecule.TryGetComponent(out MoleculeGravity physics))
            physics.PresetFrozen(true);
        // Its size from the asset list, so it is placed clear of the list and other molecules before its model comes.
        var extent = RepositoryMolecule.ExtentInAngstroms(asset);
        if (extent != Vector3.zero)
        {
            var collider = MoleculeToyGeometry.ColliderHolder(molecule);
            if (collider.TryGetComponent(out SphereCollider sphere))
            {
                sphere.radius = 0.5f * extent.magnitude * MoleculeToyGeometry.k_UnitsPerAngstrom;
                sphere.center = Vector3.zero;
                collider.transform.localPosition = Vector3.zero;
            }
        }
        molecule.transform.position = PlaceOnWall(molecule);
        molecule.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Molecules] placed {name} ({assetId}) from the asset repository at ×{mag}, radius {WorldRadius(molecule):F3} m");

        var networkObject = molecule.GetComponent<NetworkObject>();
        if (IsSpawned && networkObject != null)
            networkObject.Spawn(true);
        ShareStatus($"{name} placed beside this list at ×{mag}{(frozen ? ", frozen" : "")}; each headset is making it from the asset repository.");
    }

    void SpawnMolecule(int index, int mag, bool frozen = false)
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
        if (frozen && molecule.TryGetComponent(out MoleculeGravity physics))
            physics.PresetFrozen(true);
        molecule.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Molecules] placed {m_Prefabs.molecules[index].name} at ×{mag}{(frozen ? ", frozen" : "")}, radius {WorldRadius(molecule):F3} m, at {m_Geometry.transform.InverseTransformPoint(molecule.transform.position):F3} in the segment " +
            $"(walls at x = ±{m_Geometry.width * 0.5f:F3})");

        var networkObject = molecule.GetComponent<NetworkObject>();
        if (IsSpawned && networkObject != null)
            networkObject.Spawn(true);

        // Its size, so a small one (EGFR is 1.2 cm at ×1, its ligand a few millimetres) is looked for, not missed.
        float across = TunnelMovie.SizeOf(prefab, mag) * 100f;
        string name = m_Prefabs.molecules[index].name;
        ShareStatus(across < 3f
            ? $"{name} placed beside this list at ×{mag}{(frozen ? ", frozen" : "")}: only {across:0.#} cm across. Raise the magnification to see it better."
            : $"{name} placed beside this list at ×{mag}{(frozen ? ", frozen" : "")}, {across:0} cm across.");
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

    /// <summary>The radius of a sphere around the molecule's colliders, from its middle (read from the colliders' own
    /// settings: it is still inactive while it's placed, so it has no bounds yet).</summary>
    public static float WorldRadius(GameObject molecule)
    {
        // Molecules carry a sphere at their outer radius, an oriented box or ellipsoid round their surface (see Tunnel >
        // Molecules > Update Molecule Library), or several pieces (SARS-CoV-2's membrane and spikes, EGFR's body): the
        // sphere that holds them all.
        float radius = 0f;
        bool any = false;
        foreach (var collider in molecule.GetComponentsInChildren<Collider>(true))
        {
            if (collider.isTrigger)
                continue;
            var scale = collider.transform.lossyScale;
            float reach;
            switch (collider)
            {
                case SphereCollider sphere:
                    reach = (collider.transform.TransformPoint(sphere.center) - molecule.transform.position).magnitude
                        + sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                    break;
                case BoxCollider box:
                    reach = (collider.transform.TransformPoint(box.center) - molecule.transform.position).magnitude
                        + Vector3.Scale(box.size, scale).magnitude * 0.5f;
                    break;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    var extents = Vector3.Scale(mesh.sharedMesh.bounds.extents, scale);
                    reach = (collider.transform.TransformPoint(mesh.sharedMesh.bounds.center) - molecule.transform.position).magnitude
                        + Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
                    break;
                default:
                    continue;
            }
            radius = Mathf.Max(radius, reach);
            any = true;
        }
        if (any)
            return radius;

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
