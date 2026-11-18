using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// A molecule toy whose molecule comes from the asset repository (see <see cref="TunnelAssetCatalog"/>), so a molecule
/// added there is in the TS Menu's Molecules list with no new build. It is a copy of 2PLV's toy (made by Tunnel >
/// Molecules > Build Repository Toy), and everything else about it is the same as for the molecules built into the
/// app: grabbed and scaled with the same gestures, weightless and damped, at the same ångström-to-meter scale.
/// Only its asset id is shared. Each headset downloads the model (or takes its own copy) and makes the toy's geometry,
/// colours and collider from it as the Editor makes the built-in toys (see <see cref="MoleculeToyGeometry"/>), by the
/// molecule's category (a surface, an assembly, or atoms and bonds). Until then it shows how the download is going,
/// and a sphere the model's size holds its place. The host's asset list goes with it, so every headset reads the id
/// from the same list. Works locally when not connected.
/// </summary>
public class RepositoryMolecule : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("2PLV's chain materials, for surfaces (one per kind of subunit) and as the material atoms are coloured from.")]
    Material[] m_ChainMaterials;

    [SerializeField]
    [Tooltip("Meters per toy unit at ×1 (2PLV's toy's scale), for the minimum grab size of small atoms and bonds.")]
    float m_MetersPerUnit = 1f;

    [SerializeField]
    [Tooltip("The toy's visual, whose parts are made from the model.")]
    Transform m_Visual;

    readonly NetworkVariable<FixedString128Bytes> m_Asset = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<FixedString512Bytes> m_Catalog = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    string m_LocalAsset = "";
    string m_BuiltFor;
    int m_Building;
    TextMeshPro m_Label;
    readonly List<UnityEngine.Object> m_Owned = new();

    /// <summary>The id of its molecule in the asset repository.</summary>
    public string assetId => IsSpawned ? m_Asset.Value.ToString() : m_LocalAsset;

    /// <summary>Its toy is made (geometry and collider are the molecule's).</summary>
    public bool isBuilt { get; private set; }

    /// <summary>How making it is going ("" once made), or why it couldn't be.</summary>
    public string status { get; private set; } = "";

    /// <summary>Raised on this headset once its toy is made.</summary>
    public event Action built;

    /// <summary>The molecule it becomes, set before it is shared (see <see cref="TunnelMoleculeMenu"/>).</summary>
    internal void Preset(string id) => m_LocalAsset = id ?? "";

    /// <summary>The 2PLV toy's parts this copy needs, set by the builder.</summary>
    public void Configure(Material[] chainMaterials, float metersPerUnit, Transform visual)
    {
        m_ChainMaterials = chainMaterials;
        m_MetersPerUnit = metersPerUnit;
        m_Visual = visual;
    }

    void Start()
    {
        if (!IsSpawned)
            Build();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            if (m_Asset.Value.IsEmpty && !string.IsNullOrEmpty(m_LocalAsset))
                m_Asset.Value = new FixedString128Bytes(m_LocalAsset);
            string url = TunnelAssetCatalog.source;
            if (url.Length < 500)
                m_Catalog.Value = new FixedString512Bytes(url);
        }
        else
        {
            // A guest reads the host's asset list, so the id means the same molecule to everyone.
            string url = m_Catalog.Value.ToString();
            if (!string.IsNullOrEmpty(url) && url != TunnelAssetCatalog.source)
            {
                Debug.Log($"[Molecules] {name}: using the host's asset list, {url}");
                _ = TunnelAssetCatalog.UseSource(url);
            }
        }
        m_Asset.OnValueChanged += OnAssetChanged;
        Build();
    }

    public override void OnNetworkDespawn()
    {
        m_Asset.OnValueChanged -= OnAssetChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        m_Building++;
        foreach (var thing in m_Owned)
            if (thing != null)
                Destroy(thing);
        base.OnDestroy();
    }

    void OnAssetChanged(FixedString128Bytes previous, FixedString128Bytes current) => Build();

    void Build()
    {
        string id = assetId;
        if (string.IsNullOrEmpty(id) || id == m_BuiltFor)
            return;
        m_BuiltFor = id;
        _ = BuildToy(id, ++m_Building);
    }

    async Awaitable BuildToy(string id, int building)
    {
        await TunnelAssetCatalog.Ready();
        var asset = TunnelAssetCatalog.Resolve(id);
        string title = asset != null ? TunnelAssetCatalog.MenuName(asset) : id;
        name = $"Toy_{title}_Repository";
        ShowLabel($"Loading {title}...");
        SizePlaceholder(asset);

        GameObject model = null;
        try
        {
            model = await TunnelAssets.Load(id, null, progress =>
            {
                if (building == m_Building && this != null)
                    ShowLabel(progress < 1f ? $"Downloading {title}: {progress * 100f:F0}%" : $"Making {title}...");
            });
            if (this == null || building != m_Building)
                return;
            model.SetActive(false); // only its meshes are wanted
            var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
            if (filters.Count == 0)
                throw new TunnelAssets.LoadException($"{title} has nothing to draw");
            string category = TunnelAssetCatalog.CategoryOf(asset);
            var geometry = MoleculeToyGeometry.Build(model.transform, filters, title, category, m_ChainMaterials, false,
                angstromsPerUnit: AngstromsPerUnit(asset));
            var visual = m_Visual != null ? m_Visual : transform.GetChild(0);
            string report = MoleculeToyGeometry.Assemble(gameObject, visual, geometry, title, m_MetersPerUnit, m_Owned.Add);
            m_Owned.AddRange(geometry.meshes);
            m_Owned.AddRange(geometry.materials.Where(m => m_ChainMaterials == null || !m_ChainMaterials.Contains(m)));
            if (TryGetComponent(out MoleculeGravity gravity))
                gravity.volume = (float)MoleculeToyGeometry.EnclosedVolume(gameObject);
            HideLabel();
            // The placeholder sphere goes at the end of this frame; then hands and rays take hold of the new collider.
            await Awaitable.NextFrameAsync();
            if (this == null || building != m_Building)
                return;
            UseNewCollider();
            isBuilt = true;
            status = "";
            Debug.Log($"[Molecules] made {title} ({id}, {category}) from the asset repository: {geometry.detail}{geometry.meshes.Count} part(s), " +
                $"{geometry.subunits} subunits, {report}");
            built?.Invoke();
        }
        catch (Exception e)
        {
            if (this == null || building != m_Building)
                return;
            status = $"Couldn't make {title}: {e.Message}";
            ShowLabel(status);
            if (e is TunnelAssets.LoadException)
                Debug.LogWarning($"[Molecules] {status}");
            else
                Debug.LogException(e);
        }
        finally
        {
            if (model != null)
                Destroy(model); // its toy has its own meshes now; the model's are freed
        }
    }

    /// <summary>
    /// Tells the parts that found the toy's collider when it appeared (the placeholder sphere) about the fitted one:
    /// the grab interactable (hands and rays find a toy by the colliders it registered), the network physics (which
    /// turns its collider off and on when the toy changes hands) and the gravity (bounce, walls).
    /// </summary>
    void UseNewCollider()
    {
        var colliders = GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToList();
        foreach (var interactable in GetComponents<XRBaseInteractable>())
        {
            bool wasEnabled = interactable.enabled;
            interactable.enabled = false; // unregisters its old colliders
            interactable.colliders.Clear();
            interactable.colliders.AddRange(colliders);
            interactable.enabled = wasEnabled; // registers the new ones
        }
        foreach (var behaviour in GetComponents<MonoBehaviour>())
        {
            var field = behaviour.GetType().GetField("m_Collider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(Collider) && behaviour is not MoleculeGrabSize)
                field.SetValue(behaviour, colliders.FirstOrDefault());
        }
        if (TryGetComponent(out MoleculeGravity gravity))
            gravity.CollidersChanged();
        Debug.Log($"[Molecules] {name}: hands and rays now take hold of its {string.Join(", ", colliders.Select(c => c.GetType().Name))}");
    }

    /// <summary>
    /// Ångströms per unit of the model that is loaded for this asset (its surface's, for an atomic model): 1 for
    /// models in ångströms; others by their coordinates' meters_per_unit.
    /// </summary>
    public static float AngstromsPerUnit(TunnelAssetCatalog.Asset asset)
    {
        var model = asset != null && !asset.isGlb && !string.IsNullOrEmpty(asset.surface) ? TunnelAssetCatalog.Get(asset.surface) ?? asset : asset;
        double mpu = model?.coordinates != null && model.coordinates.meters_per_unit > 0 ? model.coordinates.meters_per_unit : 1e-10;
        return (float)(mpu / 1e-10);
    }

    /// <summary>The model's size along x, y and z in ångströms, from the asset list, or zero if it doesn't say.</summary>
    public static Vector3 ExtentInAngstroms(TunnelAssetCatalog.Asset asset)
    {
        if (asset?.extent == null || asset.extent.Length != 3)
            return Vector3.zero;
        // An atomic model's extent is of its atoms, already in ångströms; a glb's in its own units.
        float scale = asset.isGlb ? AngstromsPerUnit(asset) : 1f;
        return new Vector3(asset.extent[0], asset.extent[1], asset.extent[2]) * scale;
    }

    /// <summary>Until the model arrives, a sphere its size (from the asset list) holds its place and can be grabbed.</summary>
    void SizePlaceholder(TunnelAssetCatalog.Asset asset)
    {
        var size = ExtentInAngstroms(asset);
        if (size == Vector3.zero)
            return;
        var holder = MoleculeToyGeometry.ColliderHolder(gameObject);
        if (!holder.TryGetComponent(out SphereCollider sphere))
            return;
        // Half its diagonal, in toy units.
        sphere.radius = 0.5f * size.magnitude * MoleculeToyGeometry.k_UnitsPerAngstrom;
        sphere.center = Vector3.zero;
        holder.transform.localPosition = Vector3.zero;
    }

    void ShowLabel(string text)
    {
        if (m_Label == null)
        {
            m_Label = new GameObject("Status", typeof(RectTransform)).AddComponent<TextMeshPro>();
            m_Label.transform.SetParent(transform, false);
            m_Label.rectTransform.sizeDelta = new Vector2(0.4f, 0.08f);
            m_Label.enableAutoSizing = true;
            m_Label.fontSizeMin = 0.05f;
            m_Label.fontSizeMax = 0.3f;
            m_Label.alignment = TextAlignmentOptions.Center;
            m_Label.color = Color.white;
        }
        m_Label.text = text;
    }

    void HideLabel()
    {
        if (m_Label != null)
            Destroy(m_Label.gameObject);
        m_Label = null;
    }

    void LateUpdate()
    {
        // The label faces whoever looks at it, at a readable size whatever the toy's scale.
        if (m_Label == null || Camera.main == null)
            return;
        var head = Camera.main.transform;
        var t = m_Label.transform;
        t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);
        float s = transform.lossyScale.x;
        t.localScale = Vector3.one / Mathf.Max(Mathf.Abs(s), 1e-4f) * Mathf.Clamp(Vector3.Distance(head.position, t.position) * 0.25f, 0.05f, 1f);
    }
}
