using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using XRMultiplayer;

/// <summary>
/// The spawn nozzle: a small spray gun for molecules, called up (or removed) with the Spawn nozzle check box on the
/// TS Menu's Molecules list (see <see cref="TunnelMoleculeMenu"/>). There is one at a time, shared by everyone, and it
/// can be picked up and aimed by its grip.
/// A molecule let go of against the cup at its back is attached there (it then rides on the nozzle); pulling it
/// off detaches it, and another molecule can be attached in its place. A molecule let go of at a nozzle that already
/// holds one is refused.
/// Its menu, beside it (see <see cref="SpawnNozzlePanel"/>), has a Copies slider (1 to 100) and a Spawn button: the
/// host then makes that many copies of the attached molecule, at its size, one after another out of the nozzle's
/// mouth in a narrow spray, each a normal molecule of that kind for everyone (the same prefab and network spawn as
/// the Molecules list). A copy waits while the last one still fills the mouth, so they never start inside each
/// other. Large models are capped so one spray stays within a triangle budget the headsets can draw (the menu says
/// "at most N of these"). Pressing the button again while it sprays stops it.
/// In a session everyone sees the same attached molecule, count, progress and messages. Works locally when not
/// connected.
/// </summary>
public class SpawnNozzle : NetworkBehaviour
{
    public const int k_MinCount = 1;
    public const int k_MaxCount = 100;
    public const int k_DefaultCount = 10;

    [SerializeField]
    [Tooltip("The molecules the TS Menu lists: a copy is made from the attached molecule's own prefab, found here.")]
    TunnelPrefabRegistry m_Prefabs;

    [SerializeField]
    [Tooltip("The cup at the back that holds the attached molecule; its +Z points out of the cup.")]
    Transform m_Cradle;

    [SerializeField]
    [Tooltip("The mouth; copies come out along its +Z.")]
    Transform m_Mouth;

    [SerializeField]
    [Tooltip("The cup's ring, coloured to show whether it's empty, holding a molecule, or ready to take the one in hand.")]
    Renderer m_CradleRing;

    [SerializeField]
    [Tooltip("The menu shown beside the nozzle (made by Tunnel > Molecules > Build Spawn Nozzle).")]
    SpawnNozzlePanel m_PanelPrefab;

    [SerializeField]
    [Tooltip("A molecule let go of with its surface this close to the cup (meters) is attached.")]
    float m_DockReach = 0.06f;

    [SerializeField]
    [Tooltip("How fast copies leave the mouth (m/s).")]
    float m_Speed = 0.6f;

    [SerializeField]
    [Tooltip("Half-angle of the spray (degrees).")]
    float m_SprayAngle = 8f;

    [SerializeField]
    [Tooltip("Shortest time between two copies (seconds).")]
    float m_Interval = 0.05f;

    [SerializeField]
    [Tooltip("Most triangles one spray may add (all its copies together), so the headsets keep their frame rate.")]
    int m_TriangleBudget = 3000000;

    [SerializeField]
    [Tooltip("Gap between the nozzle and its menu (meters).")]
    float m_PanelGap = 0.05f;

    [SerializeField]
    Color m_EmptyColor = new(0.55f, 0.58f, 0.62f);

    [SerializeField]
    Color m_ReadyColor = new(0.25f, 0.9f, 0.35f);

    [SerializeField]
    Color m_LoadedColor = new(0.3f, 0.65f, 1f);

    /// <summary>The nozzle in the tunnel, if there is one.</summary>
    public static SpawnNozzle current { get; private set; }

    readonly NetworkVariable<NetworkObjectReference> m_SharedLoaded = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_SharedCount = new(k_DefaultCount, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<ushort> m_SharedMade = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<ushort> m_SharedTarget = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    MoleculeGravity m_LocalLoaded;
    int m_LocalCount = k_DefaultCount;
    int m_LocalMade, m_LocalTarget;

    // This headset's view of the attached molecule: the one it pins to the cup, and how it sits there.
    MoleculeGravity m_Pinned;
    XRGrabInteractable m_PinnedGrab;
    Quaternion m_PinnedRotation; // relative to the cup
    float m_PinnedRadius;
    Vector3 m_PinnedRadiusFor;
    // One this headset just pulled off or attached, until the host agrees.
    MoleculeGravity m_PulledOff, m_Attaching;
    float m_PulledOffAt, m_AttachingAt;

    Coroutine m_Spraying;
    SpawnNozzlePanel m_Panel;
    Vector3 m_Facing;
    MaterialPropertyBlock m_RingBlock;
    Color m_RingShown = Color.clear;
    XRGrabInteractable m_Grab;

    static readonly Dictionary<GameObject, int> s_Triangles = new();
    static readonly Collider[] s_Overlaps = new Collider[32];

    /// <summary>How many copies Spawn makes (1 to 100).</summary>
    public int count => IsSpawned ? Mathf.Clamp(m_SharedCount.Value, k_MinCount, k_MaxCount) : m_LocalCount;

    /// <summary>Copies made so far by the spray under way, and how many it makes (0 when none is).</summary>
    public int made => IsSpawned ? m_SharedMade.Value : m_LocalMade;

    public int target => IsSpawned ? m_SharedTarget.Value : m_LocalTarget;

    public bool spraying => target > 0;

    /// <summary>The molecule attached to the cup, or null.</summary>
    public MoleculeGravity loaded
    {
        get
        {
            if (!IsSpawned)
                return m_LocalLoaded != null && m_LocalLoaded.gameObject.activeInHierarchy ? m_LocalLoaded : null;
            return m_SharedLoaded.Value.TryGet(out NetworkObject networkObject) && networkObject != null && networkObject.gameObject.activeInHierarchy
                && networkObject.TryGetComponent(out MoleculeGravity molecule) ? molecule : null;
        }
    }

    /// <summary>The attached molecule's name as the Molecules list shows it ("" if none).</summary>
    public string loadedName => NameOf(loaded);

    /// <summary>The attached molecule's size, as the Molecules list's magnification (1 if none).</summary>
    public float loadedMagnification
    {
        get
        {
            var molecule = loaded;
            var prefab = PrefabOf(molecule);
            if (molecule == null || prefab == null || Mathf.Approximately(prefab.transform.localScale.x, 0f))
                return 1f;
            return molecule.transform.localScale.x / prefab.transform.localScale.x;
        }
    }

    /// <summary>The most copies of the attached molecule one spray makes (the triangle budget's limit, at most 100).</summary>
    public int maxCopies => MaxCopies(PrefabOf(loaded));

    /// <summary>Its latest message, for its menu.</summary>
    public string message { get; private set; } = "";

    void Awake()
    {
        m_RingBlock = new MaterialPropertyBlock();
        TryGetComponent(out m_Grab);
        if (m_Grab != null)
            m_Grab.selectEntered.AddListener(OnGrabbed);
    }

    void OnEnable()
    {
        current = this;
        MoleculeGravity.released += OnMoleculeReleased;
        if (m_Panel != null)
            m_Panel.gameObject.SetActive(true);
    }

    void OnDisable()
    {
        if (current == this)
            current = null;
        MoleculeGravity.released -= OnMoleculeReleased;
        Unpin();
        if (m_Panel != null)
            m_Panel.gameObject.SetActive(false);
    }

    void Start()
    {
        if (m_PanelPrefab != null && m_Panel == null)
        {
            m_Panel = Instantiate(m_PanelPrefab);
            m_Panel.name = "Spawn Nozzle Menu";
            m_Panel.Bind(this);
            PlacePanel(true);
        }
        Debug.Log($"[Nozzle] appeared at {transform.position:F2} ({(IsSpawned ? "for everyone" : "not connected, here only")})");
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_SharedCount.OnValueChanged += OnCountChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_SharedCount.OnValueChanged -= OnCountChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        Unpin();
        if (m_Grab != null)
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
        if (m_Panel != null)
            Destroy(m_Panel.gameObject);
        base.OnDestroy();
    }

    void OnCountChanged(byte previous, byte current)
    {
        if (m_Panel != null)
            m_Panel.Refresh(true);
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode && !(args.interactorObject is XRSocketInteractor))
            TunnelMenu.DeleteGrabbed(this, m_Grab);
    }

    #region Attaching

    /// <summary>
    /// Whether a molecule here is close enough to the cup to be attached: its surface within reach of the cup.
    /// </summary>
    public bool InReach(MoleculeGravity molecule)
    {
        if (molecule == null || m_Cradle == null)
            return false;
        float radius = TunnelMoleculeMenu.WorldRadius(molecule.gameObject); // from its middle (its transform's origin)
        return Vector3.Distance(molecule.transform.position, m_Cradle.position) - radius <= m_DockReach;
    }

    // A molecule let go of by a hand on this headset: attached if it's at the cup.
    void OnMoleculeReleased(MoleculeGravity molecule)
    {
        if (molecule == null || !InReach(molecule))
            return;
        var holding = loaded;
        if (holding == molecule)
            return;
        if (holding != null)
        {
            ShareMessage($"The nozzle already holds {NameOf(holding)}: pull it off first.");
            Debug.Log($"[Nozzle] {molecule.name} let go at the cup, refused: it already holds {holding.name}");
            return;
        }
        Attach(molecule);
    }

    /// <summary>Attaches a molecule to the cup, for everyone (refused if one is already there).</summary>
    public bool Attach(MoleculeGravity molecule)
    {
        if (molecule == null || (m_Prefabs != null && PrefabOf(molecule) == null))
        {
            ShareMessage(molecule == null ? "Nothing to attach." : $"{molecule.name} can't be sprayed: it isn't in the Molecules list.");
            return false;
        }
        if (loaded != null && loaded != molecule)
        {
            ShareMessage($"The nozzle already holds {NameOf(loaded)}: pull it off first.");
            return false;
        }
        Debug.Log($"[Nozzle] attaching {molecule.name} ({(IsSpawned ? "asking the host" : "here only")})");
        if (IsSpawned)
        {
            if (!molecule.TryGetComponent(out NetworkObject networkObject) || !networkObject.IsSpawned)
                return false;
            m_Attaching = molecule;
            m_AttachingAt = Time.time;
            AttachRpc(networkObject);
        }
        else
        {
            m_LocalLoaded = molecule;
            ShowAttached(molecule);
        }
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void AttachRpc(NetworkObjectReference reference)
    {
        if (!reference.TryGet(out NetworkObject networkObject) || !networkObject.TryGetComponent(out MoleculeGravity molecule))
            return;
        var holding = loaded;
        if (holding != null && holding != molecule)
        {
            StatusRpc($"The nozzle already holds {NameOf(holding)}: pull it off first.");
            return;
        }
        m_SharedLoaded.Value = reference;
        ShowAttached(molecule);
    }

    void ShowAttached(MoleculeGravity molecule)
    {
        var prefab = PrefabOf(molecule);
        int most = MaxCopies(prefab);
        string limit = most < k_MaxCount ? $" (at most {most} of these: {Triangles(prefab) / 1000}k triangles each)" : "";
        ShareMessage($"{NameOf(molecule)} attached{limit}. Set the copies, then Spawn.");
        Debug.Log($"[Nozzle] {molecule.name} attached at ×{MagnificationOf(molecule):0.#}, {Triangles(prefab)} triangles, at most {most} copies per spray");
    }

    float MagnificationOf(MoleculeGravity molecule)
    {
        var prefab = PrefabOf(molecule);
        return prefab != null && !Mathf.Approximately(prefab.transform.localScale.x, 0f) ? molecule.transform.localScale.x / prefab.transform.localScale.x : 1f;
    }

    /// <summary>Takes the attached molecule off the cup, for everyone; it stays where it is.</summary>
    public void Detach()
    {
        var molecule = loaded;
        if (molecule == null)
            return;
        Debug.Log($"[Nozzle] {molecule.name} pulled off");
        if (molecule == m_Pinned)
            Unpin();
        if (IsSpawned)
        {
            m_PulledOff = molecule;
            m_PulledOffAt = Time.time;
            if (molecule.TryGetComponent(out NetworkObject networkObject))
                DetachRpc(networkObject);
        }
        else
        {
            m_LocalLoaded = null;
            SetMessage($"{NameOf(molecule)} pulled off. Let go of a molecule at the cup to attach it.");
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DetachRpc(NetworkObjectReference reference)
    {
        if (!m_SharedLoaded.Value.Equals(reference))
            return;
        string name = loadedName;
        m_SharedLoaded.Value = default;
        StatusRpc($"{name} pulled off. Let go of a molecule at the cup to attach it.");
    }

    // The molecule pinned here was taken in hand: it's off the cup.
    void OnPinnedGrabbed(SelectEnterEventArgs args)
    {
        if (args.interactorObject is XRSocketInteractor)
            return;
        Detach();
    }

    /// <summary>The molecule this headset should hold on the cup (its own requests first, until the host agrees).</summary>
    MoleculeGravity Wanted()
    {
        var shared = loaded;
        if (m_PulledOff != null)
        {
            if (shared != m_PulledOff || Time.time - m_PulledOffAt > 2f)
                m_PulledOff = null;
            else
                shared = null;
        }
        if (m_Attaching != null)
        {
            if (shared == m_Attaching || Time.time - m_AttachingAt > 2f || m_Attaching == null)
                m_Attaching = null;
            else if (shared == null)
                shared = m_Attaching;
        }
        return shared;
    }

    void Pin(MoleculeGravity molecule)
    {
        if (molecule == m_Pinned)
            return;
        Unpin();
        if (molecule == null)
            return;
        m_Pinned = molecule;
        m_Pinned.SetDock(this);
        m_PinnedRotation = Quaternion.Inverse(m_Cradle.rotation) * molecule.transform.rotation;
        m_PinnedRadiusFor = Vector3.zero;
        if (molecule.TryGetComponent(out m_PinnedGrab))
            m_PinnedGrab.selectEntered.AddListener(OnPinnedGrabbed);
    }

    void Unpin()
    {
        if (m_Pinned != null && m_Pinned.dock == this)
            m_Pinned.SetDock(null);
        if (m_PinnedGrab != null)
            m_PinnedGrab.selectEntered.RemoveListener(OnPinnedGrabbed);
        m_Pinned = null;
        m_PinnedGrab = null;
    }

    // In this player's hand, or (in a session) another's.
    static bool HeldByAnyone(MoleculeGravity molecule) =>
        molecule.isHeld || (molecule.TryGetComponent(out NetworkPhysicsInteractable interactable) && interactable.IsSpawned && interactable.isInteracting);

    // Sits the pinned molecule in the cup: its surface on the cup, out along the cup's axis, turned as it was attached.
    void HoldPinned()
    {
        if (m_Pinned == null || HeldByAnyone(m_Pinned) || m_Cradle == null)
            return;
        var t = m_Pinned.transform;
        if (t.lossyScale != m_PinnedRadiusFor)
        {
            m_PinnedRadiusFor = t.lossyScale;
            m_PinnedRadius = TunnelMoleculeMenu.WorldRadius(m_Pinned.gameObject);
        }
        // Its middle (the point its size is measured from) on the cup's axis, its surface on the cup.
        var rotation = m_Cradle.rotation * m_PinnedRotation;
        var position = m_Cradle.position + m_Cradle.forward * m_PinnedRadius;
        t.SetPositionAndRotation(position, rotation);
    }

    #endregion

    #region Spraying

    /// <summary>Sets how many copies Spawn makes (1 to 100), for everyone.</summary>
    public void SetCount(int value)
    {
        value = Mathf.Clamp(value, k_MinCount, k_MaxCount);
        if (IsSpawned)
            SetCountRpc((byte)value);
        else
            m_LocalCount = value;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetCountRpc(byte value) => m_SharedCount.Value = (byte)Mathf.Clamp(value, k_MinCount, k_MaxCount);

    /// <summary>The Spawn button: sprays the copies, or stops the spray under way.</summary>
    public void SpawnOrStop()
    {
        if (spraying)
        {
            Debug.Log("[Nozzle] stop pressed");
            if (IsSpawned)
                StopRpc();
            else
                StopSpraying("Stopped");
            return;
        }
        int n = count;
        Debug.Log($"[Nozzle] spawn {n} pressed: {(IsSpawned ? (IsServer ? "spraying for everyone" : "asking the host") : "not connected, here only")}");
        if (IsSpawned)
            SpawnRpc((byte)n);
        else
            StartSpraying(n);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnRpc(byte n) => StartSpraying(n);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void StopRpc() => StopSpraying("Stopped");

    /// <summary>
    /// How many copies a spray of <paramref name="requested"/> makes of this prefab: 1 to 100, and no more than the
    /// triangle budget allows.
    /// </summary>
    public static int CopiesFor(int requested, int trianglesEach, int budget) =>
        Mathf.Clamp(Mathf.Min(requested, trianglesEach > 0 ? Mathf.Max(1, budget / trianglesEach) : k_MaxCount), k_MinCount, k_MaxCount);

    int MaxCopies(GameObject prefab) => prefab == null ? k_MaxCount : CopiesFor(k_MaxCount, Triangles(prefab), m_TriangleBudget);

    // On the host (or here when not connected).
    void StartSpraying(int requested)
    {
        if (m_Spraying != null)
            return;
        var molecule = loaded;
        var prefab = PrefabOf(molecule);
        if (molecule == null || prefab == null)
        {
            ShareMessage(molecule == null ? "Attach a molecule first: let go of one at the cup at the nozzle's back." : $"{molecule.name} isn't in the Molecules list, so it can't be copied.");
            return;
        }
        int n = CopiesFor(requested, Triangles(prefab), m_TriangleBudget);
        string capped = n < Mathf.Clamp(requested, k_MinCount, k_MaxCount) ? $" (at most {n} of these: {Triangles(prefab) / 1000}k triangles each)" : "";
        Debug.Log($"[Nozzle] spraying {n} of {NameOf(molecule)} (asked for {requested}; {Triangles(prefab)} triangles each, budget {m_TriangleBudget})");
        ShareMessage($"Spawning {n} {NameOf(molecule)}{capped}...");
        SetProgress(0, n);
        m_Spraying = StartCoroutine(Spray(prefab, molecule.transform.localScale, TunnelMoleculeMenu.WorldRadius(molecule.gameObject), NameOf(molecule), n));
    }

    void StopSpraying(string why)
    {
        if (m_Spraying != null)
            StopCoroutine(m_Spraying);
        m_Spraying = null;
        int done = made;
        if (target > 0)
        {
            ShareMessage($"{why}: {done} of {target} made.");
            Debug.Log($"[Nozzle] {why.ToLowerInvariant()} after {done} of {target}");
        }
        SetProgress(0, 0);
    }

    void SetProgress(int doneSoFar, int of)
    {
        if (IsSpawned)
        {
            m_SharedMade.Value = (ushort)doneSoFar;
            m_SharedTarget.Value = (ushort)of;
        }
        else
        {
            m_LocalMade = doneSoFar;
            m_LocalTarget = of;
        }
    }

    IEnumerator Spray(GameObject prefab, Vector3 scale, float radius, string moleculeName, int n)
    {
        float next = 0f;
        float blockedSince = -1f;
        int doneSoFar = 0;
        while (doneSoFar < n)
        {
            yield return null;
            if (Time.time < next || m_Mouth == null)
                continue;
            var position = m_Mouth.position + m_Mouth.forward * (radius + 0.01f);
            if (MouthBlocked(position, radius))
            {
                if (blockedSince < 0f)
                    blockedSince = Time.time;
                else if (Time.time - blockedSince > 2f)
                {
                    ShareMessage($"Waiting: something is in front of the mouth ({doneSoFar} of {n} made).");
                    blockedSince = Time.time + 1000f; // said once
                }
                continue;
            }
            blockedSince = -1f;
            MakeCopy(prefab, scale, position, SprayDirection());
            doneSoFar++;
            SetProgress(doneSoFar, n);
            next = Time.time + m_Interval;
        }
        m_Spraying = null;
        SetProgress(0, 0);
        ShareMessage($"Done: {n} {moleculeName} made.");
        Debug.Log($"[Nozzle] done: {n} {moleculeName}");
    }

    Vector3 SprayDirection()
    {
        // Evenly over the cone's disc.
        float angle = Mathf.Sqrt(Random.value) * m_SprayAngle;
        float around = Random.value * 360f;
        return m_Mouth.rotation * (Quaternion.Euler(0f, 0f, around) * Quaternion.Euler(angle, 0f, 0f) * Vector3.forward);
    }

    // Another molecule or a container (not the one on the cup) still takes up the spot just past the mouth.
    bool MouthBlocked(Vector3 position, float radius)
    {
        Physics.SyncTransforms();
        int hits = Physics.OverlapSphereNonAlloc(position, radius, s_Overlaps, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            var body = s_Overlaps[i].attachedRigidbody;
            if (body == null || body.gameObject == gameObject || (m_Pinned != null && body == m_Pinned.body))
                continue;
            if (body.TryGetComponent(out MoleculeGravity _))
                return true;
        }
        return false;
    }

    /// <summary>
    /// One copy, made as the Molecules list makes a molecule: sized and placed before it wakes (its network transform
    /// takes its pose when it's created), then spawned for everyone; it isn't parked, so it flies off.
    /// </summary>
    GameObject MakeCopy(GameObject prefab, Vector3 scale, Vector3 position, Vector3 direction)
    {
        var holder = new GameObject("Nozzle Copy Holder");
        holder.SetActive(false);
        var copy = Instantiate(prefab, holder.transform);
        copy.transform.localScale = scale;
        copy.transform.SetPositionAndRotation(position, Random.rotation);
        if (copy.TryGetComponent(out NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        copy.transform.SetParent(null, true);
        Destroy(holder);

        if (IsSpawned && copy.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
        if (copy.TryGetComponent(out Rigidbody body))
        {
            body.constraints = RigidbodyConstraints.None;
            body.isKinematic = false;
            body.linearVelocity = direction * m_Speed;
        }
        return copy;
    }

    #endregion

    #region Molecules

    /// <summary>The prefab a molecule was made from (found in the Molecules list by its network prefab id), or null.</summary>
    public GameObject PrefabOf(MoleculeGravity molecule)
    {
        if (molecule == null || m_Prefabs == null || !molecule.TryGetComponent(out NetworkObject networkObject))
            return null;
        uint id = networkObject.PrefabIdHash;
        foreach (var entry in m_Prefabs.molecules)
            if (entry != null && entry.prefab != null && entry.prefab.TryGetComponent(out NetworkObject prefabObject) && prefabObject.PrefabIdHash == id)
                return entry.prefab;
        return null;
    }

    string NameOf(MoleculeGravity molecule)
    {
        if (molecule == null)
            return "";
        var prefab = PrefabOf(molecule);
        if (prefab != null)
            foreach (var entry in m_Prefabs.molecules)
                if (entry.prefab == prefab)
                    return entry.name;
        return molecule.name.Replace("(Clone)", "").Replace("_Networked", "").Replace("Toy_", "").Trim();
    }

    /// <summary>Triangles drawn for one copy of a prefab (all its meshes).</summary>
    public static int Triangles(GameObject prefab)
    {
        if (prefab == null)
            return 0;
        if (s_Triangles.TryGetValue(prefab, out int known))
            return known;
        long triangles = 0;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            if (mesh == null || !filter.TryGetComponent(out MeshRenderer _))
                continue;
            for (int i = 0; i < mesh.subMeshCount; i++)
                triangles += mesh.GetIndexCount(i) / 3;
        }
        int total = (int)Mathf.Min(triangles, int.MaxValue);
        s_Triangles[prefab] = total;
        return total;
    }

    #endregion

    void LateUpdate()
    {
        // The host forgets a molecule that was deleted.
        if (IsSpawned && IsServer && loaded == null && !m_SharedLoaded.Value.Equals(default(NetworkObjectReference)))
            m_SharedLoaded.Value = default;

        Pin(Wanted());
        HoldPinned();
        ShowRing();
        if (m_Panel != null && m_Panel.gameObject.activeSelf)
            PlacePanel(false);
    }

    // Grey when empty, blue holding a molecule, green while a molecule in this player's hand would attach if let go.
    void ShowRing()
    {
        if (m_CradleRing == null)
            return;
        var colour = m_Pinned != null ? m_LoadedColor : m_EmptyColor;
        if (m_Pinned == null)
            foreach (var molecule in MoleculeGravity.all)
                if (molecule != null && molecule.isHeld && InReach(molecule))
                {
                    colour = m_ReadyColor;
                    break;
                }
        if (colour == m_RingShown)
            return;
        m_RingShown = colour;
        m_CradleRing.GetPropertyBlock(m_RingBlock);
        m_RingBlock.SetColor("_BaseColor", colour);
        m_CradleRing.SetPropertyBlock(m_RingBlock);
    }

    /// <summary>
    /// Its menu beside it, on its right as whoever looks at it sees it, level with it, upright and facing them
    /// (turning to face them smoothly), so it reads well however the nozzle is aimed.
    /// </summary>
    void PlacePanel(bool snap)
    {
        var t = transform;
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
        float reach = 0.1f; // the nozzle's half-length, so the menu clears it however it's turned
        m_Panel.transform.SetPositionAndRotation(t.position + side * (reach + m_PanelGap + m_Panel.width * 0.5f),
            Quaternion.LookRotation(-facing, Vector3.up));
    }

    /// <summary>A message on everyone's copy of its menu.</summary>
    void ShareMessage(string text)
    {
        if (IsSpawned)
            StatusRpc(text);
        else
            SetMessage(text);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void StatusRpc(string text) => SetMessage(text);

    void SetMessage(string text)
    {
        message = text;
        if (m_Panel != null)
            m_Panel.Refresh();
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Molecules > Build Spawn Nozzle).</summary>
    public void Configure(TunnelPrefabRegistry prefabs, Transform cradle, Transform mouth, Renderer ring, SpawnNozzlePanel panel)
    {
        m_Prefabs = prefabs;
        m_Cradle = cradle;
        m_Mouth = mouth;
        m_CradleRing = ring;
        m_PanelPrefab = panel;
    }
#endif
}
