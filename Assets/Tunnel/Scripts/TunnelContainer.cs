using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// A Container from the TS Menu: a box or a sphere that encloses a volume, for keeping molecules (or any toy) inside.
/// Its surface is transparent: the box shows its edges as thin solid rods (a quarter inch across), the sphere lines of
/// latitude and longitude every 60° as tubes as thin. Its walls always hold whatever is inside in, also while it is
/// moved, and a molecule held in a hand passes through them, so it is put in by hand.
/// It is grabbed by its edges or lines, like the molecules, and two hands scale it. The box also stretches along its
/// own axes: with both hands on its edges, pulling them apart along an axis stretches it along that axis (see
/// <see cref="ContainerStretchTransformer"/>); with both hands on its corners it scales as a whole, except on a mixed
/// reality box's front corners, which carry small balls (half an inch): two hands on them stretch the front, its
/// width and height, and leave its depth alone.
/// The walls are on the Ignore Raycast layer, which the hands and lasers don't see, so they never get in the way of
/// reaching or pointing at what's inside; the grab zones touch nothing physically, so only the walls ever push.
/// A box can also be a mixed reality box (the TS Menu's Containers list switches it): its walls are opaque inside and
/// out except its front, which is clear glass, so what is inside is seen (and reached) only through the front. Let go of it
/// against one of the tunnel's flat walls and it sets itself into the wall: its front on the wall's inside, facing
/// into the tunnel, the rest of the box beyond the wall. There the tunnel wall opens to show it (the front marks the
/// screen, see Tunnel/Container Window, and the shell leaves that part out), the wall's collider gets a hole there so
/// molecules pass in and out and lasers reach inside, and the real room's depth doesn't hide what's in the box.
/// Molecules inside a mixed reality box never collide with the tunnel's walls, wherever it is. Unsnap from wall in the
/// menu, or grabbing it by its front edges and pulling, brings it back into the tunnel.
/// Shared over the network like the molecules: size and pose by its network transform.
/// </summary>
[DefaultExecutionOrder(-1000)] // builds its colliders before the grab interactable collects them
public class TunnelContainer : NetworkBehaviour
{
    public enum Shape : byte
    {
        Box,
        Sphere,
    }

    [SerializeField]
    Shape m_Shape;

    [SerializeField]
    Material m_EdgeMaterial;

    [SerializeField]
    [Tooltip("Diameter of the rods along its edges, or the sphere's lines (meters): a quarter inch.")]
    float m_EdgeDiameter = 0.00635f;

    [SerializeField]
    [Tooltip("Diameter of the balls on a mixed reality box's front corners (meters): half an inch.")]
    float m_CornerDiameter = 0.0127f;

    [SerializeField]
    [Tooltip("Radius of the grab zone around each front corner ball (meters).")]
    float m_CornerGrabRadius = 0.025f;

    [SerializeField]
    [Tooltip("Thickness of the grab zone around each line (meters).")]
    float m_GrabThickness = 0.03f;

    [SerializeField]
    [Tooltip("Thickness of the walls, outside its faces (meters).")]
    float m_WallThickness = 0.04f;

    [Header("Mixed reality box")]
    [SerializeField]
    [Tooltip("Its walls, opaque (all but the front).")]
    Material m_WallMaterial;

    [SerializeField]
    [Tooltip("Its front: marks where the tunnel wall opens (Tunnel/Container Window).")]
    Material m_WindowMaterial;

    [SerializeField]
    [Tooltip("Its front's glass (Tunnel/Container Glass), so nobody reaches into the wall for it.")]
    Material m_GlassMaterial;

    [SerializeField]
    [Tooltip("Let go with its side this close to a tunnel wall (meters), or through it, and it sets itself into the wall.")]
    float m_SnapDistance = 0.25f;

    [SerializeField]
    [Tooltip("Real surfaces this close around it (meters) don't hide what it shows either (the depth is noisy).")]
    float m_OcclusionMargin = 0.05f;

    /// <summary>The container placed or picked up last, by anyone: the one the TS Menu's Containers list sets.</summary>
    public static TunnelContainer current { get; private set; }

    /// <summary>
    /// How thick the medium inside is, 0 to 1 in steps of 0.1: a molecule inside slows down by this fraction of its
    /// usual drag (1, the default, is as outside any container; 0 is no drag at all, so it keeps drifting and only
    /// bounces slow it). Applies whether or not the container is a collider. Set from the TS Menu's Containers list.
    /// </summary>
    public float viscosity => IsSpawned ? m_Viscosity.Value : m_LocalViscosity;

    readonly NetworkVariable<float> m_Viscosity = new(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    float m_LocalViscosity = 1f;

    /// <summary>How the heat inside moves the molecules there (see <see cref="temperature"/>).</summary>
    public enum Motion : byte
    {
        /// <summary>Brownian: the medium jostles them, so their direction keeps changing at random.</summary>
        Random,
        /// <summary>Like a gas: each flies straight at its thermal speed until a collision turns it.</summary>
        Ballistic,
    }

    /// <summary>
    /// How much kinetic energy the molecules inside have, 0 to <see cref="k_MaxTemperature"/> (continuous): the energy
    /// is proportional to it, as to an absolute temperature, with 10 room temperature (20 °C, 293 K), 0 absolute zero
    /// (no thermal motion, the default) and the top 100 °C (373 K). Each molecule's thermal speed goes as the square
    /// root of temperature over its mass, so heavier (or more magnified) molecules move more slowly. Set from the TS
    /// Menu's Containers list, whose thermometer reads it in °C.
    /// </summary>
    public float temperature => IsSpawned ? m_Temperature.Value : m_LocalTemperature;

    public Motion motion => IsSpawned ? (Motion)m_Motion.Value : m_LocalMotion;

    public const float k_RoomTemperature = 10f;
    public const float k_RoomKelvin = 293.15f;
    public const float k_MaxKelvin = 373.15f; // 100 °C
    public const float k_MaxTemperature = k_RoomTemperature * k_MaxKelvin / k_RoomKelvin;

    /// <summary>The temperature setting in kelvin (10 is 293 K, 20 °C).</summary>
    public static float Kelvin(float setting) => setting / k_RoomTemperature * k_RoomKelvin;

    readonly NetworkVariable<float> m_Temperature = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_Motion = new((byte)Motion.Random, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    float m_LocalTemperature;
    Motion m_LocalMotion = Motion.Random;

    static readonly List<TunnelContainer> s_All = new();

    /// <summary>
    /// Whether this is a mixed reality box: opaque walls, an open front, and it sets itself into a tunnel wall when
    /// let go against one.
    /// </summary>
    public bool isMixedReality => m_Shape == Shape.Box && (IsSpawned ? m_MixedReality.Value : m_LocalMixedReality);

    public Shape shape => m_Shape;

    /// <summary>Its 3D movie: the controls that direct what happens inside it (see <see cref="TunnelMovie"/>).</summary>
    public TunnelMovie movie { get; private set; }

    /// <summary>Which face is the mixed reality box's open front: axis * 2, + 1 on the axis's positive side.</summary>
    public int frontFace => IsSpawned ? m_Front.Value : m_LocalFront;

    readonly NetworkVariable<bool> m_MixedReality = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_Front = new(k_DefaultFront, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    bool m_LocalMixedReality;
    int m_LocalFront = k_DefaultFront;
    const byte k_DefaultFront = 5; // +Z

    Transform m_MixedRealityRoot;
    MeshFilter m_WallFilter, m_WindowFilter;
    Mesh m_WallMesh, m_WindowMesh;
    int m_BuiltFront = -1;
    readonly List<TunnelSegmentGeometry> m_OpenIn = new();
    bool m_Pulling;
    static readonly List<TunnelSegmentGeometry> s_Opened = new();
    Rigidbody m_Body;

    // The real-depth occlusion's exemption: every mixed reality box, for the shaders (see OcclusionComputation.hlsl).
    const int k_MaxOcclusionBoxes = 8;
    static readonly Matrix4x4[] s_BoxMatrices = new Matrix4x4[k_MaxOcclusionBoxes];
    static int s_BoxesSetAt = -1;
    static readonly int s_MixedRealityBoxes = Shader.PropertyToID("_MixedRealityBoxes");
    static readonly int s_MixedRealityBoxCount = Shader.PropertyToID("_MixedRealityBoxCount");

    const int k_IgnoreRaycastLayer = 2;
    const int k_CircleSegments = 64;                // per sphere line
    const int k_TubeSides = 8;                      // around a sphere line
    const int k_GrabSegments = 16;                  // grab zones per sphere line
    const int k_WallBands = 8, k_WallSegments = 16; // sphere wall tiles

    readonly List<Transform> m_Edges = new();
    readonly List<BoxCollider> m_Rods = new();        // box: on m_GrabRoot; sphere: one per child
    readonly List<(BoxCollider collider, float width, float height)> m_Tiles = new(); // sphere walls: angular sizes
    readonly List<BoxCollider> m_Faces = new();       // box walls
    readonly List<(Transform zone, SphereCollider collider, Renderer ball)> m_Corners = new(); // mixed reality front
    Mesh m_SphereLines;                               // sphere: its lines as tubes
    float m_SphereLinesFor;
    Transform m_GrabRoot, m_WallRoot;
    Vector3 m_SizedFor;
    XRGrabInteractable m_Grab;

    void Awake()
    {
        m_Grab = GetComponent<XRGrabInteractable>();
        m_Body = GetComponent<Rigidbody>();
        movie = GetComponent<TunnelMovie>();
        // No spawn lock: the network interactable would freeze a new box on the host (who owns every new one), and a
        // frozen kinematic body turns and scales in the hand but doesn't move. A box is moved only by hand anyway.
        if (TryGetComponent(out NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        if (m_Body != null)
            m_Body.constraints = RigidbodyConstraints.None;
        BuildEdges();
        BuildGrabZones();
        BuildCorners();
        BuildWalls();
        BuildMixedReality();
        FitToScale();
        ShowMixedReality();
        current = this;
        if (m_Grab != null)
        {
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }
    }

    public override void OnDestroy()
    {
        if (m_Grab != null)
        {
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }
        if (current == this)
            current = null;
        if (m_WallMesh != null)
            Destroy(m_WallMesh);
        if (m_WindowMesh != null)
            Destroy(m_WindowMesh);
        if (m_SphereLines != null)
            Destroy(m_SphereLines);
        base.OnDestroy();
    }

    void OnReleased(SelectExitEventArgs args)
    {
        if (isMixedReality && m_Grab != null && !m_Grab.isSelected && !TunnelMenu.deleteMode && isActiveAndEnabled)
            StartCoroutine(SnapAfterRelease());
    }

    void OnGrabbed(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode)
        {
            TunnelMenu.DeleteGrabbed(this, m_Grab);
            return;
        }
        current = this;
        // The network interactable freezes a new object on the player who owns it when it appears (its spawn lock,
        // for the molecules); a container is moved only by hand anyway, so it's free as soon as anyone takes it.
        // Left frozen, the host (who owns every new container) could turn it but not move it.
        Unfreeze();
        if (IsSpawned)
            GrabbedRpc();
    }

    void Unfreeze()
    {
        if (m_Body != null && m_Body.constraints != RigidbodyConstraints.None)
            m_Body.constraints = RigidbodyConstraints.None;
        if (TryGetComponent(out NetworkPhysicsInteractable physics))
            physics.ReleaseSpawnLock();
    }

    /// <summary>
    /// Someone took hold of it: it's the container every player's Containers menu shows, and the molecules inside go
    /// to whoever holds it, so their headset carries them along with it and everyone sees them move together.
    /// </summary>
    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void GrabbedRpc(RpcParams rpcParams = default)
    {
        current = this;
        if (!IsServer)
            return;
        ulong holder = rpcParams.Receive.SenderClientId;
        int moved = 0;
        foreach (var molecule in FindObjectsByType<MoleculeGravity>(FindObjectsSortMode.None))
        {
            if (molecule.isHeld || At(molecule.body.worldCenterOfMass) != this
                || !molecule.TryGetComponent(out NetworkObject networkObject) || !networkObject.IsSpawned || networkObject.OwnerClientId == holder)
                continue;
            if (molecule.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable interactable) && interactable.isInteracting)
                continue; // someone else is holding it
            networkObject.ChangeOwnership(holder);
            moved++;
        }
        if (moved > 0)
            Debug.Log($"[Containers] player {holder} took the {m_Shape}: {moved} molecule(s) inside now move with it");
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_MixedReality.OnValueChanged += OnMixedRealityChanged;
        m_Front.OnValueChanged += OnFrontChanged;
        ShowMixedReality();
    }

    public override void OnNetworkDespawn()
    {
        m_MixedReality.OnValueChanged -= OnMixedRealityChanged;
        m_Front.OnValueChanged -= OnFrontChanged;
        base.OnNetworkDespawn();
    }

    void OnMixedRealityChanged(bool previous, bool current) => ShowMixedReality();

    void OnFrontChanged(byte previous, byte current) => ShowMixedReality();

    /// <summary>Sets the viscosity inside (rounded to 0.1, between 0 and 1) for everyone.</summary>
    public void SetViscosity(float value)
    {
        value = Mathf.Clamp01(Mathf.Round(value * 10f) / 10f);
        if (Mathf.Approximately(value, viscosity))
            return;
        Debug.Log($"[Containers] {m_Shape} viscosity {value:0.0}");
        if (IsSpawned)
            SetViscosityRpc(value);
        else
            m_LocalViscosity = value;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetViscosityRpc(float value) => m_Viscosity.Value = Mathf.Clamp01(value);

    /// <summary>Sets the temperature inside (0 to <see cref="k_MaxTemperature"/>, to the nearest 0.05) for everyone.</summary>
    public void SetTemperature(float value)
    {
        value = Mathf.Clamp(Mathf.Round(value * 20f) / 20f, 0f, k_MaxTemperature);
        if (Mathf.Approximately(value, temperature))
            return;
        if (IsSpawned)
            SetTemperatureRpc(value);
        else
            m_LocalTemperature = value;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetTemperatureRpc(float value) => m_Temperature.Value = Mathf.Clamp(value, 0f, k_MaxTemperature);

    /// <summary>Sets how the heat inside moves molecules, for everyone.</summary>
    public void SetMotion(Motion value)
    {
        if (value == motion)
            return;
        Debug.Log($"[Containers] {m_Shape} motion {value}");
        if (IsSpawned)
            SetMotionRpc((byte)value);
        else
            m_LocalMotion = value;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetMotionRpc(byte value) => m_Motion.Value = value;

    /// <summary>
    /// Makes this box a mixed reality box or a free-standing one again, for everyone. Its open front is first the
    /// face toward this player; set into a wall, the face toward the tunnel.
    /// </summary>
    public void SetMixedReality(bool on)
    {
        if (m_Shape != Shape.Box || on == isMixedReality)
            return;
        int front = frontFace;
        var head = Camera.main;
        if (on && head != null)
            front = NearestFace(head.transform.position - transform.position);
        Debug.Log($"[Containers] box mixed reality {(on ? "on" : "off")}, front face {front}");
        PlayerHudNotification.Instance?.ShowText(on ? "Mixed reality box: let go of it against a tunnel wall to set it into the wall" : "Free-standing box", 3f);
        if (IsSpawned)
            SetMixedRealityRpc(on, (byte)front);
        else
        {
            m_LocalMixedReality = on;
            m_LocalFront = front;
            ShowMixedReality();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetMixedRealityRpc(bool on, byte front)
    {
        m_Front.Value = front;
        m_MixedReality.Value = on;
    }

    void SetFront(int front)
    {
        if (front == frontFace)
            return;
        if (IsSpawned)
            SetFrontRpc((byte)front);
        else
        {
            m_LocalFront = front;
            ShowMixedReality();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetFrontRpc(byte front) => m_Front.Value = front;

    void OnEnable() => s_All.Add(this);

    void OnDisable()
    {
        s_All.Remove(this);
        foreach (var segment in m_OpenIn)
            if (segment != null)
                segment.ClearWallOpening(this);
        m_OpenIn.Clear();
        SetOcclusionBoxes();
    }

    /// <summary>Whether a point is inside the container's volume.</summary>
    public bool Contains(Vector3 world)
    {
        // In its own coordinates the box spans ±0.5 and the sphere has radius 0.5.
        var p = transform.InverseTransformPoint(world);
        return m_Shape == Shape.Box
            ? Mathf.Abs(p.x) <= 0.5f && Mathf.Abs(p.y) <= 0.5f && Mathf.Abs(p.z) <= 0.5f
            : p.sqrMagnitude <= 0.25f;
    }

    /// <summary>
    /// The container whose medium a point is in: the smallest one it is inside (so a container inside another has its
    /// own), or null outside them all.
    /// </summary>
    public static TunnelContainer At(Vector3 world)
    {
        TunnelContainer result = null;
        float smallest = float.MaxValue;
        foreach (var container in s_All)
        {
            if (container == null || !container.Contains(world))
                continue;
            var s = container.transform.lossyScale;
            float size = Mathf.Abs(s.x * s.y * s.z);
            if (size < smallest)
            {
                smallest = size;
                result = container;
            }
        }
        return result;
    }

    /// <summary>The viscosity at a point: that of the container it is in (see <see cref="At"/>), or 1 outside.</summary>
    public static float ViscosityAt(Vector3 world)
    {
        var container = At(world);
        return container != null ? container.viscosity : 1f;
    }

    void Update()
    {
        if (transform.lossyScale != m_SizedFor)
            FitToScale();
        UpdateWallOpening();
        LogHold();
    }

    float m_NextHoldLog;
    Vector3 m_HoldLogFrom;

    // While it is held here, once a second: who moves it and how far it went, to see why a hold doesn't move it.
    void LogHold()
    {
        if (m_Grab == null || !m_Grab.isSelected)
        {
            m_NextHoldLog = 0f;
            return;
        }
        if (m_NextHoldLog == 0f)
        {
            m_NextHoldLog = Time.time + 1f;
            m_HoldLogFrom = transform.position;
            return;
        }
        if (Time.time < m_NextHoldLog)
            return;
        TryGetComponent(out NetworkPhysicsInteractable physics);
        TryGetComponent(out ClientNetworkTransform networkTransform);
        var holder = m_Grab.interactorsSelecting[0];
        string how = holder is NearFarInteractor nearFar ? $"{nearFar.handedness} {nearFar.selectionRegion.Value}" : holder.transform.name;
        Debug.Log($"[Containers] held by {m_Grab.interactorsSelecting.Count} hand(s) ({how}): moved {Vector3.Distance(m_HoldLogFrom, transform.position):F3} m in 1 s; " +
            $"spawned {IsSpawned}, server {IsServer}, owner {OwnerClientId} (this player {(NetworkManager != null ? NetworkManager.LocalClientId : 0)}, owns it {IsOwner}), " +
            $"interacting {(physics != null && physics.isInteracting)}, network transform {(networkTransform != null && networkTransform.enabled)} can commit {(networkTransform != null && networkTransform.CanCommitToTransform)}, " +
            $"kinematic {(m_Body != null && m_Body.isKinematic)}, constraints {(m_Body != null ? m_Body.constraints : RigidbodyConstraints.None)}, pulling {m_Pulling}");
        m_NextHoldLog = Time.time + 1f;
        m_HoldLogFrom = transform.position;
    }

    void LateUpdate()
    {
        // Once a frame, after everything has moved: where the boxes are, for the occlusion shaders.
        if (s_BoxesSetAt != Time.frameCount)
            SetOcclusionBoxes();
    }

    #region Mixed reality box

    /// <summary>A face's outward normal in the box's own coordinates (face = axis * 2, + 1 on the positive side).</summary>
    static Vector3 FaceNormal(int face)
    {
        var normal = Vector3.zero;
        normal[face / 2] = face % 2 == 0 ? -1f : 1f;
        return normal;
    }

    /// <summary>The face whose outward normal is nearest a world direction (only faces on other axes, if one is given).</summary>
    int NearestFace(Vector3 direction, int otherThanAxis = -1)
    {
        int best = k_DefaultFront;
        float bestDot = float.MinValue;
        for (int face = 0; face < 6; face++)
        {
            if (face / 2 == otherThanAxis)
                continue;
            float dot = Vector3.Dot(transform.TransformDirection(FaceNormal(face)), direction);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = face;
            }
        }
        return best;
    }

    void BuildMixedReality()
    {
        m_MixedRealityRoot = new GameObject("Mixed Reality").transform;
        m_MixedRealityRoot.SetParent(transform, false);
        m_WallFilter = AddRenderer("Walls", m_WallMaterial);
        m_WindowFilter = AddRenderer("Window", m_WindowMaterial);
        AddRenderer("Glass", m_GlassMaterial).sharedMesh = m_WindowMesh = new Mesh { name = "Mixed Reality Box Window" };
        m_WallMesh = new Mesh { name = "Mixed Reality Box Walls" };
        m_WallFilter.sharedMesh = m_WallMesh;
        m_WindowFilter.sharedMesh = m_WindowMesh;
        m_MixedRealityRoot.gameObject.SetActive(false);
    }

    MeshFilter AddRenderer(string name, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(m_MixedRealityRoot, false);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.AddComponent<MeshFilter>();
    }

    void ShowMixedReality()
    {
        if (m_MixedRealityRoot == null)
            return;
        bool on = isMixedReality;
        m_MixedRealityRoot.gameObject.SetActive(on);
        if (on && m_BuiltFront != frontFace)
            BuildMixedRealityMeshes(frontFace);
        PlaceCorners();
    }

    /// <summary>
    /// The five walls other than the front, each facing both in and out (so the box is opaque from outside too and
    /// lit from inside), and the front's window.
    /// </summary>
    void BuildMixedRealityMeshes(int front)
    {
        m_BuiltFront = front;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        for (int face = 0; face < 6; face++)
        {
            if (face == front)
                continue;
            AddFace(vertices, normals, triangles, face, true);
            AddFace(vertices, normals, triangles, face, false);
        }
        m_WallMesh.Clear();
        m_WallMesh.SetVertices(vertices);
        m_WallMesh.SetNormals(normals);
        m_WallMesh.SetTriangles(triangles, 0);
        m_WallMesh.RecalculateBounds();

        vertices.Clear();
        normals.Clear();
        triangles.Clear();
        AddFace(vertices, normals, triangles, front, true);
        m_WindowMesh.Clear();
        m_WindowMesh.SetVertices(vertices);
        m_WindowMesh.SetNormals(normals);
        m_WindowMesh.SetTriangles(triangles, 0);
        m_WindowMesh.RecalculateBounds();
    }

    static void AddFace(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, int face, bool outward)
    {
        int axis = face / 2, u = (axis + 1) % 3, v = (axis + 2) % 3;
        var normal = FaceNormal(face);
        int start = vertices.Count;
        foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            var corner = normal * 0.5f;
            corner[u] = a * 0.5f;
            corner[v] = b * 0.5f;
            vertices.Add(corner);
            normals.Add(outward ? normal : -normal);
        }
        // Wound in this order the quad faces along +axis; flipped to face the other way.
        bool alongAxis = (face % 2 == 1) == outward;
        triangles.AddRange(alongAxis
            ? new[] { start, start + 1, start + 2, start, start + 2, start + 3 }
            : new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
    }

    IEnumerator SnapAfterRelease()
    {
        // After the interaction toolkit and the network interactable have finished letting go.
        yield return null;
        if (m_Grab.isSelected || !isMixedReality)
            yield break;
        if (IsSpawned && !IsOwner)
        {
            Debug.Log("[Containers] mixed reality box let go before this player owned it; not set into the wall");
            yield break;
        }
        TrySnapToWall();
    }

    /// <summary>
    /// If the box was let go near one of the tunnel's flat walls (or through it), sets it into that wall: turned
    /// square to the wall with its face toward the tunnel as the open front, the front on the wall's inside
    /// surface (within the wall where it fits), the rest of it beyond the wall.
    /// </summary>
    bool TrySnapToWall()
    {
        var center = transform.position;
        var half = Abs(transform.lossyScale) * 0.5f;
        TunnelSegmentGeometry wall = null;
        int wallSide = 0;
        float nearest = float.MaxValue;
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var p = segment.transform.InverseTransformPoint(center);
            if (Mathf.Abs(p.z) > segment.length * 0.5f + m_SnapDistance || p.y < -m_SnapDistance || p.y > segment.wallHeight + m_SnapDistance)
                continue;
            foreach (int side in new[] { -1, 1 })
            {
                var inward = segment.transform.TransformDirection(Vector3.right * -side);
                float reach = 0f; // half the box's depth toward the wall
                for (int axis = 0; axis < 3; axis++)
                {
                    var direction = Vector3.zero;
                    direction[axis] = 1f;
                    reach += Mathf.Abs(Vector3.Dot(transform.TransformDirection(direction), inward)) * half[axis];
                }
                float inside = segment.width * 0.5f - side * p.x; // the center's distance in from the wall
                float gap = inside - reach;                       // its nearest side's (negative: through the wall)
                if (gap > m_SnapDistance || inside < -reach - m_SnapDistance)
                    continue;
                if (Mathf.Abs(gap) < nearest)
                {
                    nearest = Mathf.Abs(gap);
                    wall = segment;
                    wallSide = side;
                }
            }
        }
        if (wall == null)
            return false;

        var wallTransform = wall.transform;
        var into = wallTransform.TransformDirection(Vector3.right * -wallSide);
        var up = wallTransform.up;
        int front = NearestFace(into);
        int top = NearestFace(up, front / 2);
        var rotation = Quaternion.LookRotation(into, up) * Quaternion.Inverse(Quaternion.LookRotation(FaceNormal(front), FaceNormal(top)));

        var size = Abs(transform.lossyScale);
        float depth = size[front / 2], height = size[top / 2], width = size[3 - front / 2 - top / 2];
        var local = wallTransform.InverseTransformPoint(center);
        float halfLength = wall.length * 0.5f;
        float z = width <= wall.length ? Mathf.Clamp(local.z, -halfLength + width * 0.5f, halfLength - width * 0.5f) : local.z;
        float y = height <= wall.wallHeight ? Mathf.Clamp(local.y, height * 0.5f, wall.wallHeight - height * 0.5f) : Mathf.Max(local.y, height * 0.5f);
        var onWall = wallTransform.TransformPoint(new Vector3(wallSide * wall.width * 0.5f, y, z));
        var position = onWall - into * (depth * 0.5f);

        transform.SetPositionAndRotation(position, rotation);
        if (m_Body != null)
        {
            m_Body.position = position;
            m_Body.rotation = rotation;
        }
        SetFront(front);
        m_BuiltFront = -1;
        ShowMixedReality();
        Debug.Log($"[Containers] mixed reality box set into the {(wallSide < 0 ? "-X" : "+X")} wall of {wall.name} at {position:F2}, front face {front}");
        PlayerHudNotification.Instance?.ShowText("Mixed reality box set into the wall: Unsnap from wall in the Containers menu, or grab its front edges, to pull it out", 3f);
        return true;
    }

    static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    /// <summary>Whether this mixed reality box is set into a tunnel wall (its front opens one).</summary>
    public bool isSetInWall => m_OpenIn.Count > 0;

    /// <summary>
    /// Frees a mixed reality box set into a wall, for everyone: it glides straight out into the tunnel, far enough
    /// that letting go of it there doesn't set it back, carrying the molecules inside with it. It stays a mixed
    /// reality box.
    /// </summary>
    public void Unsnap()
    {
        if (!isSetInWall || m_Pulling)
            return;
        var outOfWall = transform.TransformDirection(FaceNormal(frontFace));
        float depth = Abs(transform.lossyScale)[frontFace / 2];
        var distance = outOfWall * (depth + m_SnapDistance + 0.05f);
        if (IsSpawned)
            UnsnapRpc(distance);
        else
            StartCoroutine(PullOut(distance));
    }

    // Every player moves what their headset moves (the box, if theirs, and the molecules inside that are theirs) by
    // the same distance, so all of it comes out together for everyone.
    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void UnsnapRpc(Vector3 distance)
    {
        if (!m_Pulling)
            StartCoroutine(PullOut(distance));
    }

    IEnumerator PullOut(Vector3 distance)
    {
        m_Pulling = true;
        bool moveBox = !IsSpawned || IsOwner;
        var start = m_Body != null ? m_Body.position : transform.position;

        // The molecules inside that this headset moves.
        var carried = new List<MoleculeGravity>();
        foreach (var molecule in FindObjectsByType<MoleculeGravity>(FindObjectsSortMode.None))
            if (molecule.simulatedHere && !molecule.isHeld && At(molecule.body.worldCenterOfMass) == this)
                carried.Add(molecule);
        Debug.Log($"[Containers] mixed reality box unsnapped from the wall{(moveBox ? "" : " (moved by its owner)")}, carrying {carried.Count} molecule(s) moved here");
        PlayerHudNotification.Instance?.ShowText("Mixed reality box out of the wall", 2f);

        const float duration = 0.6f;
        float t = 0f;
        var moved = Vector3.zero;
        while (t < 1f && this != null)
        {
            yield return new WaitForFixedUpdate();
            if (m_Grab != null && m_Grab.isSelected)
                break;
            t = Mathf.Min(1f, t + Time.fixedDeltaTime / duration);
            var target = Vector3.Lerp(Vector3.zero, distance, Mathf.SmoothStep(0f, 1f, t));
            var step = target - moved;
            moved = target;
            if (moveBox && m_Body != null)
                m_Body.MovePosition(start + target);
            else if (moveBox)
                transform.position = start + target;
            foreach (var molecule in carried)
                if (molecule != null)
                    molecule.body.position += step; // a parked one stays parked, just further out
        }
        m_Pulling = false;
    }

    /// <summary>
    /// Opens the walls the mixed reality box's front lies in: square to a flat wall, on its inside surface. Worked out
    /// from where it is, so every player's walls open alike.
    /// </summary>
    void UpdateWallOpening()
    {
        s_Opened.Clear();
        if (isMixedReality)
        {
            var normal = FaceNormal(frontFace);
            var facing = transform.TransformDirection(normal);
            foreach (var segment in TunnelSegmentGeometry.all)
            {
                if (segment == null || !segment.isActiveAndEnabled)
                    continue;
                var wallTransform = segment.transform;
                foreach (int side in new[] { -1, 1 })
                {
                    if (Vector3.Dot(facing, wallTransform.TransformDirection(Vector3.right * -side)) < 0.999f)
                        continue;
                    var frontCenter = wallTransform.InverseTransformPoint(transform.TransformPoint(normal * 0.5f));
                    if (Mathf.Abs(frontCenter.x - side * segment.width * 0.5f) > 0.01f)
                        continue;
                    // The front's corners, in the wall's own coordinates (along the tunnel, up).
                    int axis = frontFace / 2, u = (axis + 1) % 3, v = (axis + 2) % 3;
                    float zMin = float.MaxValue, zMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
                    foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                    {
                        var corner = normal * 0.5f;
                        corner[u] = a * 0.5f;
                        corner[v] = b * 0.5f;
                        var p = wallTransform.InverseTransformPoint(transform.TransformPoint(corner));
                        zMin = Mathf.Min(zMin, p.z);
                        zMax = Mathf.Max(zMax, p.z);
                        yMin = Mathf.Min(yMin, p.y);
                        yMax = Mathf.Max(yMax, p.y);
                    }
                    float halfLength = segment.length * 0.5f;
                    if (zMax <= -halfLength || zMin >= halfLength || yMax <= 0f || yMin >= segment.wallHeight)
                        continue;
                    segment.SetWallOpening(this, side, Rect.MinMaxRect(zMin, yMin, zMax, yMax));
                    s_Opened.Add(segment);
                    break;
                }
            }
        }

        foreach (var segment in m_OpenIn)
            if (segment != null && !s_Opened.Contains(segment))
                segment.ClearWallOpening(this);
        m_OpenIn.Clear();
        m_OpenIn.AddRange(s_Opened);
    }

    /// <summary>Gives the occlusion shaders every mixed reality box, a little larger (see OcclusionComputation.hlsl).</summary>
    static void SetOcclusionBoxes()
    {
        s_BoxesSetAt = Time.frameCount;
        int count = 0;
        foreach (var container in s_All)
        {
            if (count == k_MaxOcclusionBoxes)
                break;
            if (container == null || !container.isMixedReality)
                continue;
            var t = container.transform;
            var size = Abs(t.lossyScale) + Vector3.one * (2f * container.m_OcclusionMargin);
            s_BoxMatrices[count++] = Matrix4x4.TRS(t.position, t.rotation, size).inverse;
        }
        for (int i = count; i < k_MaxOcclusionBoxes; i++)
            s_BoxMatrices[i] = Matrix4x4.identity;
        Shader.SetGlobalMatrixArray(s_MixedRealityBoxes, s_BoxMatrices);
        Shader.SetGlobalFloat(s_MixedRealityBoxCount, count);
    }

    #endregion

    #region Edges and lines

    // The box's twelve edges, four along each axis: (axis, the corner offsets on the other two axes).
    static readonly (int axis, int a, int b)[] s_EdgeLayout = BuildEdgeLayout();

    static (int, int, int)[] BuildEdgeLayout()
    {
        var edges = new (int, int, int)[12];
        int e = 0;
        for (int axis = 0; axis < 3; axis++)
            foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                edges[e++] = (axis, a, b);
        return edges;
    }

    static Mesh s_CylinderMesh, s_BallMesh;

    /// <summary>A cylinder along Z, 1 long and 1 across, centred on its origin, with flat ends.</summary>
    static Mesh CylinderMesh()
    {
        if (s_CylinderMesh != null)
            return s_CylinderMesh;
        const int sides = 12;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i <= sides; i++)
        {
            float angle = i * 2f * Mathf.PI / sides;
            var around = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            vertices.Add(around * 0.5f + Vector3.back * 0.5f);
            vertices.Add(around * 0.5f + Vector3.forward * 0.5f);
            normals.Add(around);
            normals.Add(around);
        }
        for (int i = 0; i < sides; i++)
        {
            int a = i * 2;
            triangles.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
        }
        foreach (int end in new[] { -1, 1 })
        {
            int center = vertices.Count;
            vertices.Add(Vector3.forward * (0.5f * end));
            normals.Add(Vector3.forward * end);
            for (int i = 0; i <= sides; i++)
            {
                float angle = i * 2f * Mathf.PI / sides;
                vertices.Add(new Vector3(Mathf.Cos(angle) * 0.5f, Mathf.Sin(angle) * 0.5f, 0.5f * end));
                normals.Add(Vector3.forward * end);
            }
            for (int i = 0; i < sides; i++)
                triangles.AddRange(end > 0
                    ? new[] { center, center + 1 + i, center + 2 + i }
                    : new[] { center, center + 2 + i, center + 1 + i });
        }
        s_CylinderMesh = new Mesh { name = "Container Edge" };
        s_CylinderMesh.SetVertices(vertices);
        s_CylinderMesh.SetNormals(normals);
        s_CylinderMesh.SetTriangles(triangles, 0);
        s_CylinderMesh.RecalculateBounds();
        return s_CylinderMesh;
    }

    /// <summary>A ball 1 across, centred on its origin.</summary>
    static Mesh BallMesh()
    {
        if (s_BallMesh != null)
            return s_BallMesh;
        const int bands = 8, segments = 12;
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int b = 0; b <= bands; b++)
        {
            float latitude = Mathf.PI * (b / (float)bands - 0.5f);
            for (int s = 0; s <= segments; s++)
            {
                float longitude = 2f * Mathf.PI * s / segments;
                vertices.Add(0.5f * new Vector3(Mathf.Cos(latitude) * Mathf.Cos(longitude), Mathf.Sin(latitude), Mathf.Cos(latitude) * Mathf.Sin(longitude)));
            }
        }
        for (int b = 0; b < bands; b++)
        {
            for (int s = 0; s < segments; s++)
            {
                int a = b * (segments + 1) + s, up = a + segments + 1;
                triangles.AddRange(new[] { a, up, a + 1, a + 1, up, up + 1 });
            }
        }
        s_BallMesh = new Mesh { name = "Container Corner" };
        s_BallMesh.SetVertices(vertices);
        s_BallMesh.SetNormals(vertices.ConvertAll(v => v.normalized));
        s_BallMesh.SetTriangles(triangles, 0);
        s_BallMesh.RecalculateBounds();
        return s_BallMesh;
    }

    // The box: thin solid cylinders along the edges (sized in FitToScale). The sphere: its lines of latitude and
    // longitude as tubes of the same thickness, one mesh (made in FitToScale, the tubes' thickness depending on size).
    void BuildEdges()
    {
        var root = new GameObject(m_Shape == Shape.Box ? "Edges" : "Lines").transform;
        root.SetParent(transform, false);
        if (m_Shape == Shape.Sphere)
        {
            m_SphereLines = new Mesh { name = "Container Sphere Lines" };
            root.gameObject.AddComponent<MeshFilter>().sharedMesh = m_SphereLines;
            var lines = root.gameObject.AddComponent<MeshRenderer>();
            lines.sharedMaterial = m_EdgeMaterial;
            lines.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return;
        }
        foreach (var (axis, _, _) in s_EdgeLayout)
        {
            var go = new GameObject("Edge");
            go.transform.SetParent(root, false);
            // Its length (Z) along the edge's axis.
            go.transform.localRotation = Quaternion.LookRotation(Axis(axis), Axis((axis + 1) % 3));
            go.AddComponent<MeshFilter>().sharedMesh = CylinderMesh();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_EdgeMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Edges.Add(go.transform);
        }
    }

    static Vector3 Axis(int axis)
    {
        var v = Vector3.zero;
        v[axis] = 1f;
        return v;
    }

    // A point on the unit-diameter sphere: latitude and longitude in degrees.
    static Vector3 OnSphere(float latitude, float longitude)
    {
        float lat = latitude * Mathf.Deg2Rad, lon = longitude * Mathf.Deg2Rad;
        return 0.5f * new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
    }

    static Vector3 Latitude(float latitude, float t) => OnSphere(latitude, t * 360f);

    // Around the great circle through the poles at this longitude (and the opposite one).
    static Vector3 Meridian(float longitude, float t)
    {
        float angle = t * 2f * Mathf.PI, lon = longitude * Mathf.Deg2Rad;
        return 0.5f * (Mathf.Cos(angle) * new Vector3(Mathf.Cos(lon), 0f, Mathf.Sin(lon)) + Mathf.Sin(angle) * Vector3.up);
    }

    // The sphere's lines: latitudes -60°, 0°, 60°; longitudes every 60° (three great circles through the poles).
    static IEnumerable<System.Func<float, Vector3>> SphereLines()
    {
        foreach (float latitude in new[] { -60f, 0f, 60f })
            yield return t => Latitude(latitude, t);
        foreach (float longitude in new[] { 0f, 60f, 120f })
            yield return t => Meridian(longitude, t);
    }

    /// <summary>The sphere's lines as closed tubes of this radius (in its own coordinates).</summary>
    void BuildSphereLines(float radius)
    {
        m_SphereLinesFor = radius;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var at in SphereLines())
        {
            int start = vertices.Count;
            for (int i = 0; i < k_CircleSegments; i++)
            {
                float t = i / (float)k_CircleSegments;
                var p = at(t);
                var tangent = (at(t + 0.5f / k_CircleSegments) - at(t - 0.5f / k_CircleSegments)).normalized;
                var outward = Vector3.ProjectOnPlane(p, tangent).normalized;
                var side = Vector3.Cross(tangent, outward);
                for (int k = 0; k < k_TubeSides; k++)
                {
                    float angle = k * 2f * Mathf.PI / k_TubeSides;
                    var normal = Mathf.Cos(angle) * outward + Mathf.Sin(angle) * side;
                    vertices.Add(p + normal * radius);
                    normals.Add(normal);
                }
            }
            for (int i = 0; i < k_CircleSegments; i++)
            {
                int ring = start + i * k_TubeSides, next = start + (i + 1) % k_CircleSegments * k_TubeSides;
                for (int k = 0; k < k_TubeSides; k++)
                {
                    int k1 = (k + 1) % k_TubeSides;
                    triangles.AddRange(new[] { ring + k, ring + k1, next + k, ring + k1, next + k1, next + k }); // facing out
                }
            }
        }
        m_SphereLines.Clear();
        m_SphereLines.SetVertices(vertices);
        m_SphereLines.SetNormals(normals);
        m_SphereLines.SetTriangles(triangles, 0);
        m_SphereLines.RecalculateBounds();
    }

    #endregion

    #region Front corners

    // A mixed reality box's front corners: a small ball on each, with a grab zone around it larger than the ball, so
    // two hands on two of them stretch the front (see ContainerStretchTransformer). Each zone is kept at the box's
    // size in meters whatever its scale (its transform undoes the box's), shown only on a mixed reality box.
    void BuildCorners()
    {
        if (m_Shape != Shape.Box)
            return;
        var root = new GameObject("Front Corners").transform;
        root.SetParent(transform, false);
        for (int i = 0; i < 4; i++)
        {
            var zone = new GameObject("Corner").transform;
            zone.SetParent(root, false);
            var collider = (SphereCollider)Touchless(zone.gameObject.AddComponent<SphereCollider>());
            var ball = new GameObject("Ball");
            ball.transform.SetParent(zone, false);
            ball.AddComponent<MeshFilter>().sharedMesh = BallMesh();
            var renderer = ball.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_EdgeMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Corners.Add((zone, collider, renderer));
        }
    }

    /// <summary>The front face's four corners, in its own coordinates.</summary>
    Vector3 FrontCorner(int index)
    {
        int front = frontFace, axis = front / 2, u = (axis + 1) % 3, v = (axis + 2) % 3;
        var corner = FaceNormal(front) * 0.5f;
        corner[u] = (index == 1 || index == 2 ? 1 : -1) * 0.5f;
        corner[v] = (index >= 2 ? 1 : -1) * 0.5f;
        return corner;
    }

    // On the front's corners and shown on a mixed reality box; off otherwise (left active, so the grab interactable
    // has their colliders from the start).
    void PlaceCorners()
    {
        if (m_Corners.Count == 0)
            return;
        bool on = isMixedReality;
        var s = SafeScale();
        var undo = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
        for (int i = 0; i < m_Corners.Count; i++)
        {
            var (zone, collider, ball) = m_Corners[i];
            collider.enabled = on;
            ball.enabled = on;
            zone.localPosition = FrontCorner(i);
            zone.localScale = undo;
            collider.radius = m_CornerGrabRadius;
            ball.transform.localScale = Vector3.one * m_CornerDiameter;
        }
    }

    /// <summary>
    /// Whether a point (a hand holding it) is on one of a mixed reality box's front corner balls: two hands on them
    /// stretch its front, its depth unchanged.
    /// </summary>
    public bool OnFrontCorner(Vector3 world)
    {
        if (!isMixedReality)
            return false;
        foreach (var (zone, _, _) in m_Corners)
            if (Vector3.Distance(zone.position, world) <= m_CornerGrabRadius * 1.5f)
                return true;
        return false;
    }

    #endregion

    #region Grab zones and walls

    // Grab zones: thin boxes along the edges or lines, on the Default layer so the hands and lasers find them,
    // touching nothing.
    void BuildGrabZones()
    {
        m_GrabRoot = new GameObject("Grab").transform;
        m_GrabRoot.SetParent(transform, false);
        if (m_Shape == Shape.Box)
        {
            for (int i = 0; i < 12; i++)
                m_Rods.Add((BoxCollider)Touchless(m_GrabRoot.gameObject.AddComponent<BoxCollider>()));
            return;
        }
        foreach (var at in SphereLines())
        {
            for (int i = 0; i < k_GrabSegments; i++)
            {
                var a = at(i / (float)k_GrabSegments);
                var b = at((i + 1) / (float)k_GrabSegments);
                var go = new GameObject("Grab Segment");
                go.transform.SetParent(m_GrabRoot, false);
                go.transform.localPosition = (a + b) * 0.5f;
                go.transform.localRotation = Quaternion.LookRotation(b - a, (a + b).normalized);
                var rod = (BoxCollider)Touchless(go.AddComponent<BoxCollider>());
                rod.size = new Vector3(0f, 0f, (b - a).magnitude * 1.05f);
                m_Rods.Add(rod);
            }
        }
    }

    static Collider Touchless(Collider collider)
    {
        collider.excludeLayers = ~0; // found by the hands' and lasers' queries, but pushes nothing
        return collider;
    }

    // Walls: on Ignore Raycast, just outside its surface; they always hold what is inside in.
    void BuildWalls()
    {
        m_WallRoot = new GameObject("Walls").transform;
        m_WallRoot.SetParent(transform, false);
        m_WallRoot.gameObject.layer = k_IgnoreRaycastLayer;
        if (m_Shape == Shape.Box)
        {
            for (int i = 0; i < 6; i++)
                m_Faces.Add(m_WallRoot.gameObject.AddComponent<BoxCollider>());
            return;
        }

        // Tiles over the sphere, a little wider than their share so they overlap.
        for (int b = 0; b < k_WallBands; b++)
        {
            float latitude = -90f + (b + 0.5f) * 180f / k_WallBands;
            // Angular size (radians); near the poles no narrower than half a band, so they close over the poles.
            float height = Mathf.PI / k_WallBands * 1.15f;
            float width = Mathf.Max(2f * Mathf.PI / k_WallSegments * Mathf.Cos(latitude * Mathf.Deg2Rad), Mathf.PI / k_WallBands * 0.5f) * 1.15f;
            for (int s = 0; s < k_WallSegments; s++)
            {
                float longitude = (s + 0.5f) * 360f / k_WallSegments;
                var normal = OnSphere(latitude, longitude).normalized;
                var go = new GameObject("Wall Tile") { layer = k_IgnoreRaycastLayer };
                go.transform.SetParent(m_WallRoot, false);
                var east = Vector3.Cross(Vector3.up, normal).sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, normal).normalized : Vector3.right;
                go.transform.localRotation = Quaternion.LookRotation(normal, Vector3.Cross(normal, east));
                m_Tiles.Add((go.AddComponent<BoxCollider>(), width, height));
            }
        }
    }

    Vector3 SafeScale()
    {
        var s = transform.lossyScale;
        return new Vector3(Mathf.Max(Mathf.Abs(s.x), 1e-4f), Mathf.Max(Mathf.Abs(s.y), 1e-4f), Mathf.Max(Mathf.Abs(s.z), 1e-4f));
    }

    /// <summary>
    /// Keeps the edges or lines, their grab zones, the corner balls and the walls their thickness in meters whatever
    /// the container's size (the box can be scaled differently along each axis; the sphere scales evenly).
    /// </summary>
    void FitToScale()
    {
        m_SizedFor = transform.lossyScale;
        var s = SafeScale();
        PlaceCorners();

        if (m_Shape == Shape.Sphere)
        {
            float scale = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            float grab = m_GrabThickness / scale, wall = m_WallThickness / scale;
            float tube = m_EdgeDiameter * 0.5f / scale;
            if (m_SphereLines != null && !Mathf.Approximately(tube, m_SphereLinesFor))
                BuildSphereLines(tube);
            foreach (var rod in m_Rods)
                rod.size = new Vector3(grab, grab, rod.size.z);
            foreach (var (tile, width, height) in m_Tiles)
            {
                float radius = 0.5f + 0.5f * wall;
                tile.center = new Vector3(0f, 0f, radius);
                tile.size = new Vector3(width * radius, height * radius, wall);
            }
            return;
        }

        for (int e = 0; e < s_EdgeLayout.Length; e++)
        {
            var (axis, a, b) = s_EdgeLayout[e];
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            var center = Vector3.zero;
            center[u] = a * 0.5f;
            center[v] = b * 0.5f;

            var size = Vector3.zero;
            size[axis] = 1f + m_GrabThickness / s[axis];
            size[u] = m_GrabThickness / s[u];
            size[v] = m_GrabThickness / s[v];
            m_Rods[e].center = center;
            m_Rods[e].size = size;

            // The cylinder: its Z along the axis, its Y along u, its X along v (see BuildEdges); a little longer
            // than the edge so neighbouring edges meet at the corners.
            if (e < m_Edges.Count)
            {
                var edge = m_Edges[e];
                edge.localPosition = center;
                edge.localScale = new Vector3(m_EdgeDiameter / s[v], m_EdgeDiameter / s[u], 1f + m_EdgeDiameter / s[axis]);
            }
        }

        // Six walls, outside the faces, overlapping at the edges.
        int f = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            foreach (int side in new[] { -1, 1 })
            {
                var center = Vector3.zero;
                center[axis] = side * (0.5f + 0.5f * m_WallThickness / s[axis]);
                var size = Vector3.one + 2f * new Vector3(m_WallThickness / s.x, m_WallThickness / s.y, m_WallThickness / s.z);
                size[axis] = m_WallThickness / s[axis];
                m_Faces[f].center = center;
                m_Faces[f].size = size;
                f++;
            }
        }
    }

    #endregion
}
