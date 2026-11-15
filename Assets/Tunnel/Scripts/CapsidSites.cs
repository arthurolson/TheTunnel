using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// A viral capsid's antibody binding sites, for a container's 3D movie (see <see cref="TunnelMovie"/>). Each site
/// is an invisible cylinder standing on the capsid's surface along one of its symmetry axes (the 2-folds of TBSV, the
/// 5-folds of poliovirus, the 3-folds of Zika, found from its surface by Tunnel > Movie > Build 3D Movie): a quarter of
/// the capsid's radius long, and across a fraction of its diameter set by the movie's slider (a tenth at first). The
/// sites are live only while the Antibody action of the container the capsid is in plays; an antibody that touches a
/// live one is drawn to its base (see <see cref="AntibodyBinder"/>), and when it reaches the capsid it binds there: it
/// becomes part of the capsid, which carries its weight, and that site is taken. There is no limit but the sites:
/// every free site can take one. The more of its sites are taken, the greener the capsid: its own colours shift
/// toward green in proportion, fully green when every site is taken (see <see cref="CoveredColour"/>).
/// Each antibody stays bound for the movie's binding time (10 s to 1 min, counted from when it bound, only while the
/// movie plays; see <see cref="TunnelMovie.bindingTime"/>): then it comes off as a free antibody again, pushed out
/// along the site's axis and resting a few seconds so it doesn't bind again straight away, and the capsid's colour
/// goes back a step. The movie's Play again lets them all go at once (see <see cref="ReleaseAll"/>).
/// The movie's Show binding sites makes the free sites visible.
/// In a session the host decides each binding and each release (the antibody's player asks to bind), and everyone,
/// late joiners too, sees the bound antibodies and the colour from the shared list of bindings.
/// A virion's sites can instead be its protrusions (the spikes of SARS-CoV-2): each site is then the cylinder that
/// encloses one spike (its collider, a little larger), the antibody is drawn to the middle of the spike's outer end
/// and binds there, and the movie's slider leaves those sites the size of their spikes.
/// </summary>
public class CapsidSites : NetworkBehaviour
{
    /// <summary>One antibody bound to the capsid, in the capsid's own coordinates.</summary>
    public struct Binding : INetworkSerializable, System.IEquatable<Binding>
    {
        public byte site;
        public Vector3 startPosition; // where it was when it touched, to slide from
        public Quaternion startRotation;
        public Vector3 position;      // where it binds: its Fab tip on the site's base, pointing out along the axis
        public Quaternion rotation;
        public float scale;           // its size relative to the capsid's
        public float volume;          // its volume, in the capsid's units

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref site);
            serializer.SerializeValue(ref startPosition);
            serializer.SerializeValue(ref startRotation);
            serializer.SerializeValue(ref position);
            serializer.SerializeValue(ref rotation);
            serializer.SerializeValue(ref scale);
            serializer.SerializeValue(ref volume);
        }

        public bool Equals(Binding other) => site == other.site && position == other.position && rotation == other.rotation;
    }

    [SerializeField]
    [Tooltip("Which symmetry axes carry the sites, for the log and the movie's messages.")]
    string m_Symmetry = "2-fold";

    [SerializeField]
    [Tooltip("The sites' directions from the capsid's centre (its own coordinates).")]
    Vector3[] m_Axes = new Vector3[0];

    [SerializeField]
    [Tooltip("How far out along each axis the capsid's surface is (its own units): the site's base.")]
    float[] m_BaseRadius = new float[0];

    [SerializeField]
    [Tooltip("Where each site's antibody binds (its own coordinates), when the sites are protrusions (spikes): the middle " +
        "of each one's outer end. Empty for sites on axes through the centre, at their base radius.")]
    Vector3[] m_Points = new Vector3[0];

    [SerializeField]
    [Tooltip("Each protrusion's length (its own units): its site runs that far in from its point. Empty for capsid sites.")]
    float[] m_Lengths = new float[0];

    [SerializeField]
    [Tooltip("Each protrusion's diameter (its own units). Empty for capsid sites, whose diameter follows the movie's slider.")]
    float[] m_Diameters = new float[0];

    [SerializeField]
    [Tooltip("How much larger than its protrusion a protrusion's site is, so an antibody that touches the protrusion is in it.")]
    float m_Margin = 1.15f;

    [SerializeField]
    [Tooltip("What it is, in the movie's messages.")]
    string m_Kind = "capsid";

    [SerializeField]
    [Tooltip("The capsid's outer radius (its own units).")]
    float m_Radius = 0.2f;

    [SerializeField]
    [Tooltip("A site's length, as a fraction of the capsid's radius.")]
    float m_LengthFraction = 0.25f;

    [SerializeField]
    [Tooltip("A site's diameter, as a fraction of the capsid's diameter.")]
    float m_DiameterFraction = 0.1f;

    [SerializeField]
    [Tooltip("The colour it shifts toward as its sites are taken: this green when every site is.")]
    Color m_CoveredColour = new(0.18f, 0.85f, 0.25f);

    [SerializeField]
    [Tooltip("How fast an antibody let go is pushed off its site (m/s), on top of the capsid's own motion.")]
    float m_ReleaseSpeed = 0.3f;

    [SerializeField]
    [Tooltip("How far out along the site's axis an antibody let go starts (meters), clear of the capsid.")]
    float m_ReleaseGap = 0.01f;

    [SerializeField]
    [Tooltip("Seconds an antibody let go rests before it can bind again, so it drifts away from the site first.")]
    float m_RestAfterRelease = 3f;

    [SerializeField]
    [Tooltip("The antibody (IGG) toy, whose meshes and collider a bound antibody is made of.")]
    GameObject m_AntibodyPrefab;

    [SerializeField]
    [Tooltip("How the sites look when shown.")]
    Material m_SiteMaterial;

    [SerializeField]
    [Tooltip("Seconds a bound antibody takes to settle into its place.")]
    float m_SettleTime = 0.3f;

    /// <summary>Every capsid with binding sites in the scene.</summary>
    public static readonly List<CapsidSites> all = new();

    const int k_IgnoreRaycastLayer = 2; // the hands and lasers don't see it

    readonly NetworkList<Binding> m_Bindings = new();
    readonly List<Binding> m_LocalBindings = new();
    readonly List<(Transform root, Binding binding, float since)> m_Bound = new();
    // On the host (or here, not connected): how long each taken site's antibody has left (seconds of play).
    readonly Dictionary<int, float> m_TimeLeft = new();
    bool m_BindingsChanged = true;
    bool m_AnnouncedCovered;
    Transform[] m_Sites;
    Collider[] m_SiteColliders;
    Renderer[] m_SiteRenderers;
    Rigidbody m_Body;
    MoleculeGravity m_Physics;
    XRGrabInteractable m_Grab;
    readonly List<(Material material, Color color)> m_Colours = new();
    float m_FractionShown;
    float m_DiameterShown;
    static Mesh s_Cylinder;

    public Rigidbody body => m_Body;

    /// <summary>The movie of the container the capsid is in, if any.</summary>
    public TunnelMovie movie { get; private set; }

    /// <summary>Whether its sites are live: the movie of the container it's in is playing.</summary>
    public bool sitesActive => movie != null && movie.isPlaying;

    public int siteCount => m_Axes.Length;

    public int bindingCount => IsSpawned ? m_Bindings.Count : m_LocalBindings.Count;

    /// <summary>The share of its sites taken, 0 to 1: how far its colour has shifted toward green.</summary>
    public float coveredFraction => CoveredFraction(bindingCount, siteCount);

    /// <summary>Whether every one of its sites is taken (it is fully green).</summary>
    public bool fullyCovered => siteCount > 0 && bindingCount >= siteCount;

    /// <summary>What kind it is, for the movie's messages: capsid or virion.</summary>
    public string kind => m_Kind;

    /// <summary>The share of the sites taken (0 with no sites).</summary>
    public static float CoveredFraction(int bound, int sites) => sites <= 0 ? 0f : Mathf.Clamp01(bound / (float)sites);

    /// <summary>
    /// A capsid's own colour shifted toward green by the share of its sites taken: unchanged with none, the green
    /// itself (keeping its own opacity) with all of them, in proportion between.
    /// </summary>
    public static Color CoveredColour(Color own, Color green, float fraction)
    {
        var shifted = Color.Lerp(own, green, Mathf.Clamp01(fraction));
        shifted.a = own.a;
        return shifted;
    }

    public string symmetry => m_Symmetry;

    /// <summary>Whether its sites are protrusions (spikes) the antibody binds to the outer end of.</summary>
    public bool protrusions => m_Points.Length == m_Axes.Length && m_Points.Length > 0;

    Binding BindingAt(int i) => IsSpawned ? m_Bindings[i] : m_LocalBindings[i];

    void Awake()
    {
        m_Body = GetComponent<Rigidbody>();
        TryGetComponent(out m_Physics);
        TryGetComponent(out m_Grab);
    }

    void OnEnable() => all.Add(this);

    void OnDisable() => all.Remove(this);

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Bindings.OnListChanged += OnBindingsChanged;
        m_BindingsChanged = true;
    }

    public override void OnNetworkDespawn()
    {
        m_Bindings.OnListChanged -= OnBindingsChanged;
        base.OnNetworkDespawn();
    }

    void OnBindingsChanged(NetworkListEvent<Binding> change) => m_BindingsChanged = true;

    public override void OnDestroy()
    {
        foreach (var (material, _) in m_Colours)
            if (material != null)
                Destroy(material);
        base.OnDestroy();
    }

    /// <summary>Whether site i can take an antibody: it's one of its sites and nothing is bound there.</summary>
    public bool SiteOpen(int i) => i >= 0 && i < m_Axes.Length && IndexOfSite(i) < 0;

    // Which binding is at site i (-1 if none).
    int IndexOfSite(int i)
    {
        for (int b = 0; b < bindingCount; b++)
            if (BindingAt(b).site == i)
                return b;
        return -1;
    }

    /// <summary>Where site i stands on the capsid's surface (world).</summary>
    public Vector3 SiteBase(int i) => transform.TransformPoint(protrusions ? m_Points[i] : m_Axes[i] * m_BaseRadius[i]);

    /// <summary>The direction site i points out of the capsid (world).</summary>
    public Vector3 SiteAxis(int i) => transform.TransformDirection(m_Axes[i]).normalized;

    /// <summary>Site i's length (world).</summary>
    public float SiteLength(int i) => (protrusions && i < m_Lengths.Length ? m_Lengths[i] : m_Radius * m_LengthFraction) * transform.lossyScale.x;

    /// <summary>
    /// Whether an antibody touching it, with its Fab tip here (world), has reached site i: any touch for a capsid site
    /// (it's drawn in to its surface), but only near the outer end for a protrusion, as one touching a spike's side
    /// is still drawn up along it.
    /// </summary>
    public bool Reached(int i, Vector3 tip)
    {
        if (!protrusions)
            return true;
        float across = (i < m_Diameters.Length ? m_Diameters[i] : 2f * m_Radius * m_DiameterFraction) * transform.lossyScale.x;
        return (tip - SiteBase(i)).magnitude < 0.5f * across;
    }

    void Update()
    {
        var container = m_Body != null ? TunnelContainer.At(m_Body.worldCenterOfMass) : null;
        movie = container != null ? container.movie : null;
        bool active = sitesActive;
        bool visible = movie != null && movie.showSites;
        if ((active || visible) && m_Sites == null)
            BuildSites();
        if (m_Sites != null)
        {
            float diameter = movie != null ? movie.siteDiameter : m_DiameterFraction;
            if (diameter != m_DiameterShown && !protrusions)
            {
                m_DiameterShown = diameter;
                float across = 2f * m_Radius * diameter;
                foreach (var site in m_Sites)
                    site.localScale = new Vector3(across, across, site.localScale.z);
            }
            for (int i = 0; i < m_Sites.Length; i++)
            {
                bool open = SiteOpen(i);
                if (m_SiteColliders[i].enabled != (active && open))
                    m_SiteColliders[i].enabled = active && open;
                if (m_SiteRenderers[i].enabled != (visible && open))
                    m_SiteRenderers[i].enabled = visible && open;
            }
        }

        if (!IsSpawned || IsServer)
            CountDownBindings(Time.deltaTime);
        ShowBindings();
    }

    #region Sites

    void BuildSites()
    {
        if (s_Cylinder == null)
            s_Cylinder = CylinderMesh(24);
        int count = m_Axes.Length;
        m_Sites = new Transform[count];
        m_SiteColliders = new Collider[count];
        m_SiteRenderers = new Renderer[count];
        float diameter = 2f * m_Radius * m_DiameterFraction;
        float length = m_Radius * m_LengthFraction;
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"Binding Site {i + 1}");
            go.layer = k_IgnoreRaycastLayer;
            var t = go.transform;
            t.SetParent(transform, false);
            t.localRotation = Quaternion.FromToRotation(Vector3.forward, m_Axes[i]);
            if (protrusions)
            {
                // The spike's cylinder, a little wider and a little past its end.
                float spike = i < m_Lengths.Length ? m_Lengths[i] : length;
                float across = (i < m_Diameters.Length ? m_Diameters[i] : diameter) * m_Margin;
                float past = spike * (m_Margin - 1f);
                t.localPosition = m_Points[i] - m_Axes[i] * spike;
                t.localScale = new Vector3(across, across, spike + past);
            }
            else
            {
                t.localPosition = m_Axes[i] * m_BaseRadius[i];
                t.localScale = new Vector3(diameter, diameter, length);
            }
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = s_Cylinder;
            collider.convex = true;
            collider.isTrigger = true;
            collider.enabled = false;
            go.AddComponent<MeshFilter>().sharedMesh = s_Cylinder;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_SiteMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;
            var site = go.AddComponent<CapsidSite>();
            site.capsid = this;
            site.index = i;
            m_Sites[i] = t;
            m_SiteColliders[i] = collider;
            m_SiteRenderers[i] = renderer;
        }
        Debug.Log(protrusions
            ? $"[Movie] {name}: {count} binding sites, one on each of its {m_Symmetry}"
            : $"[Movie] {name}: {count} binding sites on its {m_Symmetry} axes, {length * transform.lossyScale.x * 100f:0.0} cm long");
    }

    // A cylinder of diameter 1 along +z from 0 to 1, closed at both ends.
    static Mesh CylinderMesh(int sides)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            var p = new Vector3(Mathf.Cos(a) * 0.5f, Mathf.Sin(a) * 0.5f, 0f);
            vertices.Add(p);
            vertices.Add(p + Vector3.forward);
        }
        int bottom = vertices.Count;
        vertices.Add(Vector3.zero);
        int top = vertices.Count;
        vertices.Add(Vector3.forward);
        for (int i = 0; i < sides; i++)
        {
            int a = i * 2, b = (i + 1) % sides * 2;
            triangles.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
            triangles.AddRange(new[] { bottom, b, a });
            triangles.AddRange(new[] { top, a + 1, b + 1 });
        }
        var mesh = new Mesh { name = "Binding Site" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    #endregion

    #region Binding

    /// <summary>
    /// An antibody has reached the capsid at a site: bind it there (in a session the host does, asked by the
    /// antibody's player). Ignored if that site has meanwhile been taken.
    /// </summary>
    public void RequestBind(GameObject antibody, Binding binding)
    {
        if (IsSpawned)
        {
            if (antibody.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned)
                BindRpc(networkObject, binding);
        }
        else
        {
            Bind(antibody, binding);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BindRpc(NetworkObjectReference antibody, Binding binding)
    {
        if (antibody.TryGet(out var networkObject) && networkObject.IsSpawned)
            Bind(networkObject.gameObject, binding);
    }

    void Bind(GameObject antibody, Binding binding)
    {
        if (antibody == null || !SiteOpen(binding.site))
            return;
        if (IsSpawned)
            m_Bindings.Add(binding);
        else
            m_LocalBindings.Add(binding);
        m_BindingsChanged = true;
        float time = movie != null ? movie.bindingTime : TunnelMovie.k_DefaultBindingTime;
        m_TimeLeft[binding.site] = time;
        int count = bindingCount;
        Debug.Log($"[Movie] {antibody.name} bound to {name} at {(protrusions ? "" : m_Symmetry + " ")}site {binding.site + 1} for {time:0} s: " +
            $"{count} of {siteCount} sites taken");
        Remove(antibody);
        if (fullyCovered && !m_AnnouncedCovered && movie != null)
        {
            m_AnnouncedCovered = true;
            movie.Announce($"Every site on a {MoleculeName(gameObject)} {m_Kind} is taken ({count} antibodies): it is fully green.");
        }
    }

    /// <summary>
    /// On the host (or here, not connected): counts down each bound antibody's time while the movie of the container
    /// it's in plays (not while paused, nor outside a container), and lets go of those whose time is up.
    /// </summary>
    public void CountDownBindings(float seconds)
    {
        if (bindingCount == 0 || movie == null || !movie.isPlaying)
            return;
        List<int> due = null;
        for (int b = 0; b < bindingCount; b++)
        {
            int site = BindingAt(b).site;
            if (!m_TimeLeft.TryGetValue(site, out float left))
                left = movie.bindingTime; // bound before this host kept time (e.g. a late change of host)
            left -= seconds;
            m_TimeLeft[site] = left;
            if (left <= 0f)
                (due ??= new List<int>()).Add(site);
        }
        if (due != null)
            foreach (int site in due)
                Release(site, "its binding time is up");
    }

    /// <summary>Seconds the antibody at site i has left before it lets go (-1 if none is bound there).</summary>
    public float TimeLeft(int site) => IndexOfSite(site) >= 0 && m_TimeLeft.TryGetValue(site, out float left) ? left : -1f;

    /// <summary>
    /// On the host (or here, not connected): lets go of every bound antibody at once, each a free antibody again
    /// pushed off its site (the movie's Play again). Returns how many.
    /// </summary>
    public int ReleaseAll(string why)
    {
        int count = 0;
        while (bindingCount > 0)
        {
            if (Release(BindingAt(bindingCount - 1).site, why) == null)
                break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// On the host (or here, not connected): the antibody bound at site i lets go. It becomes a free antibody again
    /// (the same IGG toy and network spawn as the Molecules list's) where it was bound, a little out along the site's
    /// axis and pushed off along it, and rests a few seconds before it can bind again. Returns it (null if the site
    /// was free).
    /// </summary>
    public GameObject Release(int site, string why)
    {
        int index = IndexOfSite(site);
        if (index < 0)
            return null;
        var binding = BindingAt(index);
        var axis = SiteAxis(site);
        var position = transform.TransformPoint(binding.position) + axis * m_ReleaseGap;
        var rotation = transform.rotation * binding.rotation;
        float scale = binding.scale * transform.lossyScale.x;
        var velocity = (m_Body != null && !m_Body.isKinematic ? m_Body.GetPointVelocity(position) : Vector3.zero) + axis * m_ReleaseSpeed;

        if (IsSpawned)
            m_Bindings.RemoveAt(index);
        else
            m_LocalBindings.RemoveAt(index);
        m_TimeLeft.Remove(site);
        m_BindingsChanged = true;
        if (!fullyCovered)
            m_AnnouncedCovered = false;
        Debug.Log($"[Movie] {name} let go of the antibody at site {site + 1}: {why}; {bindingCount} of {siteCount} sites taken");
        return MakeFree(position, rotation, scale, velocity);
    }

    // A free antibody made where a bound one was: placed and sized before it wakes (its network transform takes its
    // pose when it's created), not parked, resting, and spawned for everyone.
    GameObject MakeFree(Vector3 position, Quaternion rotation, float scale, Vector3 velocity)
    {
        if (m_AntibodyPrefab == null)
            return null;
        var holder = new GameObject("Released Antibody Holder");
        holder.SetActive(false);
        var antibody = Instantiate(m_AntibodyPrefab, holder.transform);
        antibody.transform.localScale = Vector3.one * scale;
        antibody.transform.SetPositionAndRotation(position, rotation);
        if (antibody.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        if (antibody.TryGetComponent(out AntibodyBinder binder))
            binder.Rest(m_RestAfterRelease);
        antibody.transform.SetParent(null, true);
        Destroy(holder);

        if (IsSpawned && antibody.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
        if (antibody.TryGetComponent(out Rigidbody body))
        {
            body.constraints = RigidbodyConstraints.None;
            body.isKinematic = false;
            body.linearVelocity = velocity;
        }
        return antibody;
    }

    /// <summary>
    /// Takes a molecule out of the scene for everyone. Whoever holds it lets go first, and it is gone at once
    /// everywhere; it is destroyed a moment later, so no hand is left holding something that no longer exists.
    /// </summary>
    void Remove(GameObject target)
    {
        if (target == null)
            return;
        if (IsSpawned && target.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned)
        {
            HideRpc(networkObject);
            StartCoroutine(DespawnSoon(networkObject));
        }
        else
        {
            Hide(target);
            StartCoroutine(DestroySoon(target));
        }
    }

    [Rpc(SendTo.Everyone)]
    void HideRpc(NetworkObjectReference reference)
    {
        if (reference.TryGet(out var networkObject))
            Hide(networkObject.gameObject);
    }

    static void Hide(GameObject target)
    {
        if (target.TryGetComponent(out XRBaseInteractable grab))
        {
            var manager = grab.interactionManager;
            if (manager != null)
            {
                manager.CancelInteractableSelection((IXRSelectInteractable)grab);
                manager.CancelInteractableFocus((IXRFocusInteractable)grab);
                manager.CancelInteractableHover((IXRHoverInteractable)grab);
            }
            grab.enabled = false;
        }
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        foreach (var collider in target.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        if (target.TryGetComponent(out AntibodyBinder binder))
            binder.enabled = false;
    }

    static IEnumerator DespawnSoon(NetworkObject networkObject)
    {
        yield return new WaitForSeconds(0.2f);
        if (networkObject != null && networkObject.IsSpawned)
            networkObject.Despawn(true);
    }

    static IEnumerator DestroySoon(GameObject target)
    {
        yield return null;
        if (target != null)
            Destroy(target);
    }

    /// <summary>
    /// Shows the antibodies bound now (everyone's copy, late joiners' too): makes those newly bound, removes those
    /// let go, tints the capsid by the share of its sites taken and weighs them with it.
    /// </summary>
    void ShowBindings()
    {
        if (m_BindingsChanged)
        {
            m_BindingsChanged = false;
            int count = bindingCount;
            bool changed = false;
            for (int i = m_Bound.Count - 1; i >= 0; i--)
            {
                bool kept = false;
                for (int b = 0; b < count && !kept; b++)
                    kept = BindingAt(b).Equals(m_Bound[i].binding);
                if (kept)
                    continue;
                var root = m_Bound[i].root;
                if (root != null)
                {
                    root.SetParent(null, false); // out of its colliders now; gone at the end of the frame
                    Destroy(root.gameObject);
                }
                m_Bound.RemoveAt(i);
                changed = true;
            }
            for (int b = 0; b < count; b++)
            {
                var binding = BindingAt(b);
                bool shown = false;
                foreach (var bound in m_Bound)
                    shown |= bound.binding.Equals(binding);
                if (shown)
                    continue;
                m_Bound.Add((MakeAntibody(binding), binding, Time.time));
                changed = true;
            }
            if (changed)
            {
                float volume = 0f;
                for (int b = 0; b < count; b++)
                    volume += BindingAt(b).volume;
                if (m_Physics != null)
                {
                    m_Physics.extraVolume = volume;
                    m_Physics.CollidersChanged();
                }
            }
            Tint(coveredFraction);
        }

        // Each slides into its place.
        for (int i = 0; i < m_Bound.Count; i++)
        {
            var (root, binding, since) = m_Bound[i];
            if (root == null)
                continue;
            float t = m_SettleTime > 0f ? Mathf.Clamp01((Time.time - since) / m_SettleTime) : 1f;
            if (root.localPosition == binding.position && root.localRotation == binding.rotation)
                continue;
            float s = t * t * (3f - 2f * t);
            root.localPosition = Vector3.Lerp(binding.startPosition, binding.position, s);
            root.localRotation = Quaternion.Slerp(binding.startRotation, binding.rotation, s);
        }
    }

    // A bound antibody: the antibody toy's meshes and collider, as part of the capsid.
    Transform MakeAntibody(Binding binding)
    {
        var root = new GameObject($"Bound Antibody {binding.site + 1}").transform;
        root.SetParent(transform, false);
        root.localPosition = binding.startPosition;
        root.localRotation = binding.startRotation;
        root.localScale = Vector3.one * binding.scale;
        if (m_AntibodyPrefab == null)
            return root;

        var prefabRoot = m_AntibodyPrefab.transform;
        var toRoot = prefabRoot.worldToLocalMatrix;
        PhysicsMaterial material = null;
        foreach (var collider in GetComponentsInChildren<Collider>(true))
            if (!collider.isTrigger && collider.sharedMaterial != null)
            {
                material = collider.sharedMaterial;
                break;
            }

        Transform Part(Transform source)
        {
            var part = new GameObject(source.name).transform;
            part.gameObject.layer = gameObject.layer;
            part.SetParent(root, false);
            var m = toRoot * source.localToWorldMatrix;
            part.localPosition = m.GetPosition();
            part.localRotation = m.rotation;
            part.localScale = m.lossyScale;
            return part;
        }

        foreach (var filter in m_AntibodyPrefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter.TryGetComponent(out MeshRenderer sourceRenderer))
                continue;
            var part = Part(filter.transform);
            part.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            part.gameObject.AddComponent<MeshRenderer>().sharedMaterials = sourceRenderer.sharedMaterials;
        }
        foreach (var source in m_AntibodyPrefab.GetComponentsInChildren<Collider>(true))
        {
            if (source.isTrigger)
                continue;
            var part = Part(source.transform).gameObject;
            Collider copy = null;
            switch (source)
            {
                case MeshCollider mesh:
                    var meshCopy = part.AddComponent<MeshCollider>();
                    meshCopy.sharedMesh = mesh.sharedMesh;
                    meshCopy.convex = true;
                    copy = meshCopy;
                    break;
                case BoxCollider box:
                    var boxCopy = part.AddComponent<BoxCollider>();
                    boxCopy.center = box.center;
                    boxCopy.size = box.size;
                    copy = boxCopy;
                    break;
                case SphereCollider sphere:
                    var sphereCopy = part.AddComponent<SphereCollider>();
                    sphereCopy.center = sphere.center;
                    sphereCopy.radius = sphere.radius;
                    copy = sphereCopy;
                    break;
                case CapsuleCollider capsule:
                    var capsuleCopy = part.AddComponent<CapsuleCollider>();
                    capsuleCopy.center = capsule.center;
                    capsuleCopy.radius = capsule.radius;
                    capsuleCopy.height = capsule.height;
                    capsuleCopy.direction = capsule.direction;
                    copy = capsuleCopy;
                    break;
            }
            if (copy != null)
                copy.sharedMaterial = material;
        }
        return root;
    }

    // The capsid's own colours (not its bound antibodies'), shifted toward green by the share of its sites taken.
    void Tint(float fraction)
    {
        if (Mathf.Approximately(fraction, m_FractionShown))
            return;
        m_FractionShown = fraction;
        if (m_Colours.Count == 0)
        {
            foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.GetComponentInParent<CapsidSite>() != null || renderer.transform.parent == null
                    || renderer.transform.parent.name.StartsWith("Bound Antibody"))
                    continue;
                foreach (var material in renderer.materials) // its own copies
                    if (material.HasProperty("_BaseColor"))
                        m_Colours.Add((material, material.GetColor("_BaseColor")));
            }
        }
        foreach (var (material, colour) in m_Colours)
            if (material != null)
                material.SetColor("_BaseColor", CoveredColour(colour, m_CoveredColour, fraction));
    }

    #endregion

    /// <summary>A molecule's name in the TS Menu (its toy's name without the decoration).</summary>
    public static string MoleculeName(GameObject molecule) =>
        molecule.name.Replace("(Clone)", "").Replace("_Networked", "").Replace("Toy_", "").Trim();

#if UNITY_EDITOR
    /// <summary>Set by Tunnel > Movie > Build 3D Movie.</summary>
    public void Configure(string symmetry, Vector3[] axes, float[] baseRadius, float radius, GameObject antibody, Material siteMaterial)
    {
        m_Symmetry = symmetry;
        m_Axes = axes;
        m_BaseRadius = baseRadius;
        m_Radius = radius;
        m_AntibodyPrefab = antibody;
        m_SiteMaterial = siteMaterial;
        m_Points = new Vector3[0];
        m_Lengths = new float[0];
        m_Diameters = new float[0];
        m_Kind = "capsid";
    }

    /// <summary>
    /// Set by Tunnel > Movie > Build 3D Movie for a virion whose sites are its protrusions: where each one's
    /// antibody binds (the middle of its outer end), the direction it points out, its length and its diameter.
    /// </summary>
    public void ConfigureProtrusions(string what, string kind, Vector3[] points, Vector3[] axes, float[] lengths, float[] diameters,
        float radius, GameObject antibody, Material siteMaterial)
    {
        Configure(what, axes, points.Select(p => p.magnitude).ToArray(), radius, antibody, siteMaterial);
        m_Points = points;
        m_Lengths = lengths;
        m_Diameters = diameters;
        m_Kind = kind;
    }
#endif
}
