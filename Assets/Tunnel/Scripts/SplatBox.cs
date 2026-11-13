using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A splat-box: a box from the TS Menu's Containers list that frames a 3D portrait (a Gaussian splat baked to a
/// coloured mesh), chosen from the small menu beside it (see <see cref="SplatBoxPanel"/> and <see cref="SplatLibrary"/>).
/// The box is the container's (see <see cref="TunnelContainer"/>, shape SplatBox): it is grabbed by its edges, two
/// hands scale it, and it can be a mixed reality box set into a tunnel wall, but it has no walls, medium or heat, so
/// molecules pay it no attention. A baked splat reads well only from the side it was captured from, so that side is
/// the box's +Z: as a mixed reality box its front is always +Z, upright, set into a wall facing into the tunnel, and a
/// new one is placed facing the middle of the tunnel.
/// The splat always fills the box, a small margin all round, and keeps its own proportions: scaling the box scales the
/// splat with it, never stretched. Choosing another splat gives the box that splat's proportions, keeping its largest
/// side (set into a wall, its front stays on the wall).
/// In a session everyone sees the same splat, and the box's size and pose come from its network transform. Each
/// headset draws the one shared mesh (one instance per box, nothing copied). Works locally when not connected.
/// </summary>
[RequireComponent(typeof(TunnelContainer))]
public class SplatBox : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("The splats it can show, by name (made by Tunnel > Containers > Build Splats).")]
    SplatLibrary m_Library;

    [SerializeField]
    [Tooltip("The menu beside the box that lists the splats.")]
    SplatBoxPanel m_PanelPrefab;

    [SerializeField]
    [Tooltip("Room around the splat, as a fraction of its size (shared between the two sides).")]
    float m_Margin = 0.06f;

    [SerializeField]
    [Tooltip("Gap between the box's side and its menu (meters).")]
    float m_PanelGap = 0.04f;

    readonly NetworkVariable<int> m_Shown = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    int m_LocalShown;

    TunnelContainer m_Container;
    Transform m_Content;
    int m_ContentFor = int.MinValue;
    Bounds m_Bounds = new(Vector3.zero, Vector3.one);
    Vector3 m_FittedFor;
    SplatBoxPanel m_Panel;
    Vector3 m_Facing;

    /// <summary>Which splat it shows: its place in <see cref="library"/>.</summary>
    public int shown => IsSpawned ? m_Shown.Value : m_LocalShown;

    public SplatLibrary library => m_Library;

    /// <summary>Room around the splat in the box, as a fraction of its size.</summary>
    public float margin => m_Margin;

    /// <summary>The splat shown's name, or "" if there is none.</summary>
    public string shownName => m_Library != null && m_Library.Get(shown) != null ? m_Library.Get(shown).name : "";

    /// <summary>Its menu's latest message (what was chosen, or why nothing was).</summary>
    public string message { get; private set; } = "";

    void Awake() => m_Container = GetComponent<TunnelContainer>();

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
        m_Shown.OnValueChanged += OnShownChanged;
        ShowSplat();
    }

    public override void OnNetworkDespawn()
    {
        m_Shown.OnValueChanged -= OnShownChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        if (m_Panel != null)
            Destroy(m_Panel.gameObject);
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

    void OnShownChanged(int previous, int current)
    {
        ShowSplat();
        // Whoever moves the box gives it the new splat's proportions; the network transform shares them.
        if (IsOwner)
            FitBoxToSplat();
    }

    /// <summary>Shows the splat at this place in the library, for everyone.</summary>
    public void Choose(int index)
    {
        var splat = m_Library != null ? m_Library.Get(index) : null;
        if (splat == null || splat.prefab == null)
        {
            Debug.LogWarning($"[Splat] {name}: no splat {index} in the library");
            return;
        }
        message = $"Showing {splat.name}";
        if (index == shown)
            return;
        Debug.Log($"[Splat] {name}: {splat.name} chosen");
        if (IsSpawned)
            ChooseRpc(index);
        else
        {
            m_LocalShown = index;
            ShowSplat();
            FitBoxToSplat();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ChooseRpc(int index)
    {
        if (m_Library != null && m_Library.Get(index) != null)
            m_Shown.Value = index;
    }

    /// <summary>Puts the chosen splat in the box (once per change: the model shares its mesh, nothing is copied).</summary>
    void ShowSplat()
    {
        int index = shown;
        if (index == m_ContentFor)
            return;
        m_ContentFor = index;
        if (m_Content != null)
            Destroy(m_Content.gameObject);
        m_Content = null;

        var splat = m_Library != null ? m_Library.Get(index) : null;
        if (splat == null || splat.prefab == null)
        {
            Debug.LogWarning($"[Splat] {name}: nothing to show for splat {index}");
            return;
        }

        // A holder that undoes any uneven scale of the box, and the model in it.
        var holder = new GameObject("Splat").transform;
        holder.SetParent(transform, false);
        var model = Instantiate(splat.prefab, holder, false);
        model.name = splat.name;
        model.transform.localRotation = Quaternion.Euler(splat.rotation);
        m_Bounds = MeshBounds(holder, Matrix4x4.identity);
        m_Content = holder;
        m_FittedFor = Vector3.zero;
        FitContent();
        Debug.Log($"[Splat] {name}: showing {splat.name}, {m_Bounds.size.x:F2} x {m_Bounds.size.y:F2} x {m_Bounds.size.z:F2} in its own units");
    }

    /// <summary>
    /// The bounds of every mesh under a transform, in its own coordinates taken through a further matrix (identity for
    /// its own coordinates).
    /// </summary>
    public static Bounds MeshBounds(Transform root, Matrix4x4 then)
    {
        bool any = false;
        var bounds = new Bounds();
        var toRoot = then * root.worldToLocalMatrix;
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
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

    /// <summary>The box's size that fits the splat shown, a margin all round, its largest side kept as it is.</summary>
    Vector3 FittingSize()
    {
        var need = m_Bounds.size * (1f + m_Margin);
        var s = transform.lossyScale;
        float largest = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        float longest = Mathf.Max(need.x, Mathf.Max(need.y, need.z));
        return need * (largest / longest);
    }

    void FitBoxToSplat()
    {
        if (m_Content == null || m_Container == null)
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
