using System;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A splat-box: a box from the TS Menu's Containers list that frames a 3D portrait, chosen from the small menu beside
/// it (see <see cref="SplatBoxPanel"/>): one built into the app (see <see cref="SplatLibrary"/>, David Goodsell) or any
/// mesh in the Tunnel's asset repository (see <see cref="TunnelAssetCatalog"/>), loaded while the app runs. Other
/// scripts put one in the tunnel with <see cref="TunnelAssets.Spawn"/>.
/// The box is the container's (see <see cref="TunnelContainer"/>, shape SplatBox): it is grabbed by its edges, two
/// hands scale it, and it can be a mixed reality box set into a tunnel wall, but it has no walls, medium or heat, so
/// molecules pay it no attention. A portrait reads well only from the side it was captured from, so that side is
/// the box's +Z: as a mixed reality box its front is always +Z, upright, set into a wall facing into the tunnel, and a
/// new one is placed facing the middle of the tunnel.
/// The portrait always fills the box, a small margin all round, and keeps its own proportions: scaling the box scales
/// it with it, never stretched. Choosing another gives the box its proportions, keeping its largest side (set into a
/// wall, its front stays on the wall), or making it a set height for assets captured at no particular scale.
/// In a session only the asset's id and height are shared, and the box's size and pose come from its network
/// transform; each headset loads the model itself, from the app, its own copy or a download, and shows the asset's
/// picture and how the download is going until then. If it can't be loaded on one headset, that headset says why in
/// the box, and the box still moves and scales with everyone else's. Works locally when not connected.
/// </summary>
[RequireComponent(typeof(TunnelContainer))]
public class SplatBox : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("The portraits built into the app (made by Tunnel > Containers > Build Splats).")]
    SplatLibrary m_Library;

    [SerializeField]
    [Tooltip("The menu beside the box that lists the portraits.")]
    SplatBoxPanel m_PanelPrefab;

    [SerializeField]
    [Tooltip("Room around the splat, as a fraction of its size (shared between the two sides).")]
    float m_Margin = 0.06f;

    [SerializeField]
    [Tooltip("Gap between the box's side and its menu (meters).")]
    float m_PanelGap = 0.04f;

    // The asset shown ("" for the library's first) and the height it is made (meters; 0 keeps the box's largest side).
    readonly NetworkVariable<FixedString128Bytes> m_Asset = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> m_Height = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    string m_LocalAsset = "";
    float m_LocalHeight;

    TunnelContainer m_Container;
    Transform m_Content;
    string m_ContentFor;
    int m_Loading;
    bool m_HasModel;
    TMP_Text m_PlaceholderText;
    Material m_PlaceholderMaterial;
    Bounds m_Bounds = new(Vector3.zero, Vector3.one);
    Vector3 m_FittedFor;
    SplatBoxPanel m_Panel;
    Vector3 m_Facing;

    /// <summary>The id of the asset it shows (see <see cref="TunnelAssetCatalog"/>), the library's first by default.</summary>
    public string assetId
    {
        get
        {
            string id = IsSpawned ? m_Asset.Value.ToString() : m_LocalAsset;
            return string.IsNullOrEmpty(id) && m_Library != null ? m_Library.firstAssetId : id ?? "";
        }
    }

    /// <summary>How tall the asset is made (meters), or 0 to keep the box's largest side as it is.</summary>
    public float targetHeight => IsSpawned ? m_Height.Value : m_LocalHeight;

    public SplatLibrary library => m_Library;

    /// <summary>Room around the splat in the box, as a fraction of its size.</summary>
    public float margin => m_Margin;

    /// <summary>The name of the portrait shown.</summary>
    public string shownName => TunnelAssets.DisplayName(assetId);

    /// <summary>Its menu's latest message (what was chosen, or why nothing was).</summary>
    public string message { get; private set; } = "";

    /// <summary>How loading the portrait is going on this headset ("" once it is shown), or why it couldn't be.</summary>
    public string status { get; private set; } = "";

    /// <summary>The credit its license asks for, to show beside it, or "".</summary>
    public string attribution { get; private set; } = "";

    /// <summary>Its model has arrived on this headset (not the placeholder).</summary>
    public bool isShown => m_HasModel;

    void Awake()
    {
        m_Container = GetComponent<TunnelContainer>();
        TunnelAssets.Register(m_Library);
    }

    void Start()
    {
        ShowSplat();
        if (m_PanelPrefab != null && m_Panel == null)
        {
            m_Panel = Instantiate(m_PanelPrefab);
            m_Panel.name = $"{name.Replace("(Clone)", "")} Splat Menu";
            m_Panel.Bind(this);
            PlacePanel(true);
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Whoever placed it chose its asset before it was shared.
        if (IsServer && m_Asset.Value.IsEmpty && !string.IsNullOrEmpty(m_LocalAsset))
        {
            m_Asset.Value = new FixedString128Bytes(m_LocalAsset);
            m_Height.Value = m_LocalHeight;
        }
        m_Asset.OnValueChanged += OnAssetChanged;
        m_Height.OnValueChanged += OnHeightChanged;
        ShowSplat();
    }

    public override void OnNetworkDespawn()
    {
        m_Asset.OnValueChanged -= OnAssetChanged;
        m_Height.OnValueChanged -= OnHeightChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        m_Loading++;
        if (m_Panel != null)
            Destroy(m_Panel.gameObject);
        if (m_PlaceholderMaterial != null)
            Destroy(m_PlaceholderMaterial);
        base.OnDestroy();
    }

    void OnEnable()
    {
        if (m_Panel != null)
            m_Panel.gameObject.SetActive(true);
    }

    void OnDisable()
    {
        if (m_Panel != null)
            m_Panel.gameObject.SetActive(false);
    }

    void OnAssetChanged(FixedString128Bytes previous, FixedString128Bytes current) => ShowSplat();

    void OnHeightChanged(float previous, float current)
    {
        if (IsOwner)
            FitBoxToSplat();
    }

    /// <summary>
    /// Shows this asset in the box, for everyone: a portrait built into the app or any mesh in the asset repository.
    /// Its height in meters, 0 to keep the box's largest side, or -1 for the asset's own (see
    /// <see cref="TunnelAssets.targetHeights"/>).
    /// </summary>
    public void Choose(string id, float height = -1f)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 120)
        {
            Debug.LogWarning($"[Splat] {name}: no asset \"{id}\"");
            return;
        }
        if (height < 0f)
            height = TunnelAssets.TargetHeight(id);
        message = $"Showing {TunnelAssets.DisplayName(id)}";
        if (id == assetId && Mathf.Approximately(height, targetHeight))
            return;
        Debug.Log($"[Splat] {name}: {id} chosen{(height > 0f ? $", {height:F2} m tall" : "")}");
        if (IsSpawned)
            ChooseRpc(new FixedString128Bytes(id), height);
        else
        {
            bool same = id == assetId;
            m_LocalAsset = id;
            m_LocalHeight = height;
            if (same)
                FitBoxToSplat();
            else
                ShowSplat();
        }
    }

    /// <summary>The asset a new box starts with, set before it is shared (see <see cref="TunnelContainerMenu"/>).</summary>
    internal void Preset(string id, float height)
    {
        m_LocalAsset = id ?? "";
        m_LocalHeight = Mathf.Max(0f, height);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ChooseRpc(FixedString128Bytes id, float height)
    {
        m_Asset.Value = id;
        m_Height.Value = Mathf.Max(0f, height);
    }

    /// <summary>
    /// Puts the chosen portrait in the box (once per change): at once if it is built into the app or already loaded,
    /// otherwise its picture and how its download is going, until it arrives.
    /// </summary>
    void ShowSplat()
    {
        string id = assetId;
        if (id == m_ContentFor)
            return;
        m_ContentFor = id;
        int loading = ++m_Loading;
        if (m_Content != null)
            Destroy(m_Content.gameObject);
        m_Content = null;
        m_HasModel = false;
        status = "";
        attribution = TunnelAssetCatalog.Resolve(id)?.attribution ?? "";
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogWarning($"[Splat] {name}: nothing to show");
            return;
        }

        // A holder that undoes any uneven scale of the box, and the model in it.
        var holder = new GameObject("Splat").transform;
        holder.SetParent(transform, false);
        m_Content = holder;
        if (TunnelAssets.BuiltIn(id) == null)
            ShowPlaceholder(id, holder);
        _ = LoadModel(id, holder, loading);
    }

    async Awaitable LoadModel(string id, Transform holder, int loading)
    {
        string title = TunnelAssets.DisplayName(id);
        try
        {
            var model = await TunnelAssets.Load(id, holder, progress =>
            {
                if (loading != m_Loading || this == null)
                    return;
                status = progress < 1f ? $"Downloading {title}: {progress * 100f:F0}%" : $"Opening {title}...";
                SetPlaceholderText(status);
            });
            if (this == null || loading != m_Loading || holder == null)
            {
                if (model != null)
                    Destroy(model);
                return;
            }
            // The placeholder goes, the model stays.
            for (int i = holder.childCount - 1; i >= 0; i--)
                if (holder.GetChild(i) != model.transform)
                    Destroy(holder.GetChild(i).gameObject);
            m_PlaceholderText = null;
            m_HasModel = true;
            status = "";
            m_Bounds = MeshBounds(holder, Matrix4x4.identity, model.transform);
            m_FittedFor = Vector3.zero;
            FitContent();
            // Whoever moves the box gives it the portrait's proportions; the network transform shares them.
            if (!IsSpawned || IsOwner)
                FitBoxToSplat();
            Debug.Log($"[Splat] {name}: showing {title}, {m_Bounds.size.x:F2} x {m_Bounds.size.y:F2} x {m_Bounds.size.z:F2} in its own units");
        }
        catch (Exception e)
        {
            if (this == null || loading != m_Loading)
                return;
            status = $"Couldn't show {title}: {e.Message}";
            SetPlaceholderText(status);
            if (e is TunnelAssets.LoadException)
                Debug.LogWarning($"[Splat] {name}: {status}");
            else
                Debug.LogException(e);
        }
    }

    /// <summary>
    /// What the box shows while its portrait loads: the asset's picture, and under it how the download is going (or
    /// why it failed). Read from the box's front (+Z).
    /// </summary>
    void ShowPlaceholder(string id, Transform holder)
    {
        var placeholder = new GameObject("Placeholder").transform;
        placeholder.SetParent(holder, false);
        placeholder.localRotation = Quaternion.Euler(0f, 180f, 0f); // a quad and text read from -Z; the box's front is +Z

        var picture = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(picture.GetComponent<Collider>());
        picture.name = "Picture";
        picture.transform.SetParent(placeholder, false);
        picture.transform.localPosition = new Vector3(0f, 0.1f, 0f);
        picture.transform.localScale = new Vector3(1f, 0.75f, 1f);
        if (m_PlaceholderMaterial != null)
            Destroy(m_PlaceholderMaterial);
        m_PlaceholderMaterial = TunnelAssets.PictureMaterial(null);
        var renderer = picture.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = m_PlaceholderMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _ = ShowThumbnail(id, picture.transform);

        var label = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshPro>();
        label.transform.SetParent(placeholder, false);
        label.rectTransform.sizeDelta = new Vector2(1f, 0.2f);
        label.transform.localPosition = new Vector3(0f, -0.4f, 0f);
        label.enableAutoSizing = true;
        label.fontSizeMin = 0.05f;
        label.fontSizeMax = 0.6f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        m_PlaceholderText = label;
        SetPlaceholderText($"Loading {TunnelAssets.DisplayName(id)}...");

        // Fitted as the model will be, until it comes.
        m_Bounds = new Bounds(new Vector3(0f, -0.0125f, 0f), new Vector3(1f, 0.975f, 0.05f));
        m_FittedFor = Vector3.zero;
        FitContent();
    }

    async Awaitable ShowThumbnail(string id, Transform picture)
    {
        await TunnelAssetCatalog.Ready();
        var texture = await TunnelAssets.Thumbnail(TunnelAssetCatalog.Resolve(id));
        if (texture == null || picture == null || m_PlaceholderMaterial == null)
            return;
        m_PlaceholderMaterial.SetTexture("_MainTex", texture);
        // Its own proportions, within the same space.
        float aspect = texture.height / (float)Mathf.Max(1, texture.width);
        picture.localScale = aspect > 0.75f ? new Vector3(0.75f / aspect, 0.75f, 1f) : new Vector3(1f, aspect, 1f);
    }

    void SetPlaceholderText(string text)
    {
        if (m_PlaceholderText != null)
            m_PlaceholderText.text = text;
    }

    /// <summary>
    /// The bounds of every mesh under a transform, in its own coordinates taken through a further matrix (identity for
    /// its own coordinates).
    /// </summary>
    public static Bounds MeshBounds(Transform root, Matrix4x4 then, Transform only = null)
    {
        bool any = false;
        var bounds = new Bounds();
        var toRoot = then * root.worldToLocalMatrix;
        foreach (var filter in (only != null ? only : root).GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            if (mesh == null)
                continue;
            var m = toRoot * filter.transform.localToWorldMatrix;
            var b = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (!any)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else
                    bounds.Encapsulate(p);
            }
        }
        if (!any)
            return new Bounds(Vector3.zero, Vector3.one);
        bounds.size = Vector3.Max(bounds.size, Vector3.one * 1e-4f);
        return bounds;
    }

    /// <summary>
    /// The box's size that fits the portrait shown, a margin all round: the asset's set height if it has one, otherwise
    /// its largest side kept as it is.
    /// </summary>
    Vector3 FittingSize()
    {
        var need = m_Bounds.size * (1f + m_Margin);
        float height = targetHeight;
        if (height > 0f && m_Bounds.size.y > 1e-4f)
            return need * (height / m_Bounds.size.y);
        var s = transform.lossyScale;
        float largest = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        float longest = Mathf.Max(need.x, Mathf.Max(need.y, need.z));
        return need * (largest / longest);
    }

    void FitBoxToSplat()
    {
        if (m_Content == null || m_Container == null || !m_HasModel)
            return;
        var size = FittingSize();
        if ((size - transform.localScale).sqrMagnitude > 1e-8f)
            m_Container.Resize(size);
    }

    /// <summary>
    /// Scales the splat evenly to fill the box (a margin all round, on its tightest axis) and centres it. Its holder
    /// undoes the box's own scale on each axis, so the splat is never stretched even if the box were.
    /// </summary>
    void FitContent()
    {
        if (m_Content == null)
            return;
        var lossy = transform.lossyScale;
        if (lossy == m_FittedFor)
            return;
        m_FittedFor = lossy;
        var s = new Vector3(Mathf.Max(Mathf.Abs(lossy.x), 1e-4f), Mathf.Max(Mathf.Abs(lossy.y), 1e-4f), Mathf.Max(Mathf.Abs(lossy.z), 1e-4f));
        var need = m_Bounds.size * (1f + m_Margin);
        float k = Mathf.Min(s.x / need.x, Mathf.Min(s.y / need.y, s.z / need.z)); // meters per unit of the splat
        var undo = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
        m_Content.localScale = k * undo;
        m_Content.localPosition = Vector3.Scale(-m_Bounds.center * k, undo);
        m_Content.localRotation = Quaternion.identity;
    }

    void LateUpdate()
    {
        FitContent();
        if (m_Panel != null && m_Panel.gameObject.activeSelf)
            PlacePanel(false);
    }

    /// <summary>
    /// Its menu beside the box, on its right as whoever looks at it sees it, half way up, facing them (turning to face
    /// them smoothly). On a mixed reality box set into a wall it lies flat on the wall, right of the box's front.
    /// </summary>
    void PlacePanel(bool snap)
    {
        var t = transform;
        var half = t.lossyScale * 0.5f;
        float Extent(Vector3 direction) => Mathf.Abs(Vector3.Dot(t.right, direction)) * Mathf.Abs(half.x)
            + Mathf.Abs(Vector3.Dot(t.up, direction)) * Mathf.Abs(half.y) + Mathf.Abs(Vector3.Dot(t.forward, direction)) * Mathf.Abs(half.z);
        float halfWidth = m_Panel.width * 0.5f;

        if (m_Container != null && m_Container.isSetInWall)
        {
            var normal = m_Container.frontNormal; // out of the wall, into the tunnel
            var right = -Vector3.Cross(Vector3.up, normal).normalized;
            var front = t.position + normal * Extent(normal);
            m_Panel.transform.SetPositionAndRotation(front + right * (Extent(right) + m_PanelGap + halfWidth) + normal * 0.01f,
                Quaternion.LookRotation(-normal, Vector3.up));
            m_Facing = normal;
            return;
        }

        var head = Camera.main != null ? Camera.main.transform.position : t.position - t.forward;
        var toHead = head - t.position;
        toHead.y = 0f;
        if (toHead.sqrMagnitude > 1e-4f)
        {
            toHead.Normalize();
            m_Facing = snap || m_Facing == Vector3.zero ? toHead : Vector3.Slerp(m_Facing, toHead, 1f - Mathf.Exp(-3f * Time.deltaTime)).normalized;
        }
        var facing = m_Facing != Vector3.zero ? m_Facing : -t.forward;
        var side = -Vector3.Cross(Vector3.up, facing).normalized; // their right
        m_Panel.transform.SetPositionAndRotation(t.position + side * (Extent(side) + m_PanelGap + halfWidth),
            Quaternion.LookRotation(-facing, Vector3.up));
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Containers > Build Containers).</summary>
    public void Configure(SplatLibrary library, SplatBoxPanel panel)
    {
        m_Library = library;
        m_PanelPrefab = panel;
    }
#endif
}
