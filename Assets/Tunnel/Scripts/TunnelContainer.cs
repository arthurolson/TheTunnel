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
/// Its surface is transparent: the box shows its edges, the sphere lines of latitude and longitude every 60°. The
/// lines are green while it lets things through and red while it is a collider: then its walls hold whatever is
/// inside in, also while it is moved. Put a molecule in while it is green, then make it red.
/// To switch: hold it with one hand, point the other hand straight up (so its laser touches nothing) and double-click
/// that hand's grip or trigger (or pinch twice).
/// It is grabbed by its lines, like the molecules, and two hands scale it. The box also stretches along its own axes:
/// with both hands on its edges, pulling them apart along an axis stretches it along that axis (see
/// <see cref="ContainerStretchTransformer"/>); with both hands on its corners it scales as a whole.
/// The walls are on the Ignore Raycast layer, which the hands and lasers don't see, so they never get in the way of
/// reaching or pointing at what's inside; the grab lines touch nothing physically, so only the walls ever push.
/// A box can also be a mixed reality box (the TS Menu's Containers list switches it): its walls are opaque inside and
/// out except its front, which is open, so what is inside is seen (and reached) only through the front. Let go of it
/// against one of the tunnel's flat walls and it sets itself into the wall: its front on the wall's inside, facing
/// into the tunnel, the rest of the box beyond the wall. There the tunnel wall opens to show it (the front marks the
/// screen, see Tunnel/Container Window, and the shell leaves that part out), the wall's collider gets a hole there so
/// molecules pass in and out and lasers reach inside, and the real room's depth doesn't hide what's in the box.
/// Molecules inside a mixed reality box never collide with the tunnel's walls, wherever it is. Grab it by its front
/// edges and pull it out to bring it back into the tunnel.
/// Shared over the network like the molecules: size and pose by its network transform, collider or not here.
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
    Material m_LineMaterial;

    [SerializeField]
    [Tooltip("Width of the surface lines (meters).")]
    float m_LineWidth = 0.004f;

    [SerializeField]
    [Tooltip("Thickness of the grab zone around each line (meters).")]
    float m_GrabThickness = 0.03f;

    [SerializeField]
    [Tooltip("Thickness of the walls while it is a collider, outside its surface (meters).")]
    float m_WallThickness = 0.04f;

    [SerializeField]
    Color m_ColliderColor = new(1f, 0.15f, 0.15f);

    [SerializeField]
    Color m_OpenColor = new(0.15f, 1f, 0.3f);

    [SerializeField]
    [Tooltip("Longest time between the two clicks of a double-click (seconds).")]
    float m_DoubleClickTime = 0.5f;

    [SerializeField]
    [Tooltip("How straight up the other hand must point for its double-click to count (cosine of its angle from up).")]
    float m_PointingUp = 0.7f;

    [Header("Mixed reality box")]
    [SerializeField]
    [Tooltip("Its walls, opaque (all but the front).")]
    Material m_WallMaterial;

    [SerializeField]
    [Tooltip("Its front: marks where the tunnel wall opens (Tunnel/Container Window).")]
    Material m_WindowMaterial;

    [SerializeField]
    [Tooltip("Let go with its side this close to a tunnel wall (meters), or through it, and it sets itself into the wall.")]
    float m_SnapDistance = 0.25f;

    [SerializeField]
    [Tooltip("Real surfaces this close around it (meters) don't hide what it shows either (the depth is noisy).")]
    float m_OcclusionMargin = 0.05f;

    public Shape shape => m_Shape;
    public bool isCollider => IsSpawned ? m_IsCollider.Value : m_LocalIsCollider;

    /// <summary>The container placed or picked up last: the one the TS Menu's Collider check box switches.</summary>
    public static TunnelContainer current { get; private set; }

    readonly NetworkVariable<bool> m_IsCollider = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    bool m_LocalIsCollider;

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
    /// How much kinetic energy the molecules inside have, 0 to 10 (continuous): the energy is proportional to it, as
    /// to an absolute temperature, with 10 room temperature (20 °C, 293 K) and 0 absolute zero (no thermal motion,
    /// the default). Each molecule's thermal speed goes as the square root of temperature over its mass, so heavier
    /// (or more magnified) molecules move more slowly. Set from the TS Menu's Containers list.
    /// </summary>
    public float temperature => IsSpawned ? m_Temperature.Value : m_LocalTemperature;

    public Motion motion => IsSpawned ? (Motion)m_Motion.Value : m_LocalMotion;

    public const float k_MaxTemperature = 10f;
    public const float k_RoomKelvin = 293.15f;

    /// <summary>The temperature setting in kelvin (10 is 293 K, 20 °C).</summary>
    public static float Kelvin(float setting) => setting / k_MaxTemperature * k_RoomKelvin;

    readonly NetworkVariable<float> m_Temperature = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_Motion = new((byte)Motion.Random, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    float m_LocalTemperature;
    Motion m_LocalMotion = Motion.Random;

    static readonly List<TunnelContainer> s_All = new();

    /// <summary>
    /// Whether this is a mixed reality box: opaque walls, an open front, and it sets itself into a tunnel wall when
    /// let go against one (boxes only).
    /// </summary>
    public bool isMixedReality => m_Shape == Shape.Box && (IsSpawned ? m_MixedReality.Value : m_LocalMixedReality);

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
    const int k_CircleSegments = 64;
    const int k_GrabSegments = 16;          // per sphere line
    const int k_WallBands = 8, k_WallSegments = 16; // sphere wall tiles
    static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int s_Color = Shader.PropertyToID("_Color");

    readonly List<LineRenderer> m_Lines = new();
    readonly List<BoxCollider> m_Rods = new();        // box: on m_GrabRoot; sphere: one per child
    readonly List<(BoxCollider collider, float width, float height)> m_Tiles = new(); // sphere walls: angular sizes
    readonly List<BoxCollider> m_Faces = new();       // box walls
    Transform m_GrabRoot, m_WallRoot;
    Vector3 m_SizedFor;
    MaterialPropertyBlock m_Block;
    XRGrabInteractable m_Grab;

    // Double-click on the free hand.
    static readonly List<NearFarInteractor> s_Hands = new();
    static float s_HandsFoundAt = -10f;
    float m_LastClick = -10f;
    NearFarInteractor m_LastClickHand;

    void Awake()
    {
        m_Grab = GetComponent<XRGrabInteractable>();
        m_Body = GetComponent<Rigidbody>();
        m_Block = new MaterialPropertyBlock();
        BuildLines();
        BuildGrabZones();
        BuildWalls();
        BuildMixedReality();
        FitToScale();
        Apply(isCollider);
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
            TunnelMenu.DeleteGrabbed(this, m_Grab);
        else
            current = this;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_IsCollider.OnValueChanged += OnColliderChanged;
        m_MixedReality.OnValueChanged += OnMixedRealityChanged;
        m_Front.OnValueChanged += OnFrontChanged;
        Apply(m_IsCollider.Value);
        ShowMixedReality();
    }

    public override void OnNetworkDespawn()
    {
        m_IsCollider.OnValueChanged -= OnColliderChanged;
        m_MixedReality.OnValueChanged -= OnMixedRealityChanged;
        m_Front.OnValueChanged -= OnFrontChanged;
        base.OnNetworkDespawn();
    }

    void OnMixedRealityChanged(bool previous, bool current) => ShowMixedReality();

    void OnFrontChanged(byte previous, byte current) => ShowMixedReality();

    void OnColliderChanged(bool previous, bool current) => Apply(current);

    /// <summary>Switches between letting things through (green) and holding them in (red), for everyone.</summary>
    public void ToggleCollider()
    {
        bool next = !isCollider;
        PlayerHudNotification.Instance?.ShowText(next ? "Container: collider (red), holds things in" : "Container: no collider (green), lets things through", 2.5f);
        if (IsSpawned)
            SetColliderRpc(next);
        else
        {
            m_LocalIsCollider = next;
            Apply(next);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetColliderRpc(bool on) => m_IsCollider.Value = on;

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

    /// <summary>Sets the temperature inside (0 to 10, to the nearest 0.05) for everyone.</summary>
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

    void Apply(bool collider)
    {
        if (m_WallRoot != null)
            foreach (var wall in m_WallRoot.GetComponentsInChildren<Collider>(true))
                wall.enabled = collider;
        var color = collider ? m_ColliderColor : m_OpenColor;
        m_Block.SetColor(s_BaseColor, color);
        m_Block.SetColor(s_Color, color);
        foreach (var line in m_Lines)
        {
            line.startColor = line.endColor = color;
            line.SetPropertyBlock(m_Block);
        }
    }

    void Update()
    {
        if (transform.lossyScale != m_SizedFor)
            FitToScale();
        WatchForDoubleClick();
        UpdateWallOpening();
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
        if (m_Shape != Shape.Box)
            return;
        m_MixedRealityRoot = new GameObject("Mixed Reality").transform;
        m_MixedRealityRoot.SetParent(transform, false);
        m_WallFilter = AddRenderer("Walls", m_WallMaterial);
        m_WindowFilter = AddRenderer("Window", m_WindowMaterial);
        m_WallMesh = new Mesh { name = "Mixed Reality Box Walls" };
        m_WindowMesh = new Mesh { name = "Mixed Reality Box Window" };
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
        if (IsSpawned && !IsOwner)
            UnsnapRpc();
        else
            StartCoroutine(PullOut());
    }

    // Run by whoever moves the box.
    [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Everyone)]
    void UnsnapRpc()
    {
        if (isSetInWall && !m_Pulling)
            StartCoroutine(PullOut());
    }

    IEnumerator PullOut()
    {
        m_Pulling = true;
        var normal = FaceNormal(frontFace);
        var outOfWall = transform.TransformDirection(normal);
        float depth = Abs(transform.lossyScale)[frontFace / 2];
        var distance = outOfWall * (depth + m_SnapDistance + 0.05f);
        var start = m_Body != null ? m_Body.position : transform.position;

        // The molecules inside that this headset moves (the others' owners see them pushed by the box, if a collider).
        var carried = new List<MoleculeGravity>();
        foreach (var molecule in FindObjectsByType<MoleculeGravity>(FindObjectsSortMode.None))
            if (molecule.simulatedHere && At(molecule.body.worldCenterOfMass) == this)
                carried.Add(molecule);
        Debug.Log($"[Containers] mixed reality box unsnapped from the wall, carrying {carried.Count} molecule(s)");
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
            if (m_Body != null)
                m_Body.MovePosition(start + target);
            else
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

    #region Double-click

    void WatchForDoubleClick()
    {
        if (m_Grab == null || m_Grab.interactorsSelecting.Count != 1)
            return;
        if (m_Grab.interactorsSelecting[0] is not XRBaseInteractor holder)
            return;

        if (Time.unscaledTime - s_HandsFoundAt > 1f)
        {
            s_HandsFoundAt = Time.unscaledTime;
            s_Hands.Clear();
            s_Hands.AddRange(FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Exclude));
        }

        foreach (var hand in s_Hands)
        {
            if (hand == null || !hand.isActiveAndEnabled || ReferenceEquals(hand, holder) || hand.handedness == holder.handedness)
                continue;
            bool clicked = hand.selectInput.ReadWasPerformedThisFrame() || hand.activateInput.ReadWasPerformedThisFrame();
            if (!clicked)
                continue;
            float up = Vector3.Dot(hand.transform.forward, Vector3.up);
            Debug.Log($"[Containers] {hand.name} ({hand.handedness}) clicked while {holder.name} ({holder.handedness}) holds {name}: pointing up {up:F2} (needs {m_PointingUp:F2}), {Time.unscaledTime - m_LastClick:F2} s since the last click");
            if (up < m_PointingUp)
            {
                m_LastClick = -10f;
                continue;
            }

            if (ReferenceEquals(hand, m_LastClickHand) && Time.unscaledTime - m_LastClick <= m_DoubleClickTime)
            {
                m_LastClick = -10f;
                ToggleCollider();
            }
            else
            {
                m_LastClick = Time.unscaledTime;
                m_LastClickHand = hand;
            }
        }
    }

    #endregion

    #region Lines

    void BuildLines()
    {
        var root = new GameObject("Lines").transform;
        root.SetParent(transform, false);
        if (m_Shape == Shape.Box)
        {
            // Bottom and top squares, and the four upright edges.
            AddLine(root, true, Corner(-1, -1, -1), Corner(1, -1, -1), Corner(1, -1, 1), Corner(-1, -1, 1));
            AddLine(root, true, Corner(-1, 1, -1), Corner(1, 1, -1), Corner(1, 1, 1), Corner(-1, 1, 1));
            foreach (var (x, z) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                AddLine(root, false, Corner(x, -1, z), Corner(x, 1, z));
        }
        else
        {
            // Latitudes -60°, 0°, 60°; longitudes every 60° (three great circles through the poles).
            foreach (float latitude in new[] { -60f, 0f, 60f })
                AddLine(root, true, Circle(p => Latitude(latitude, p)));
            foreach (float longitude in new[] { 0f, 60f, 120f })
                AddLine(root, true, Circle(p => Meridian(longitude, p)));
        }
    }

    static Vector3 Corner(int x, int y, int z) => new Vector3(x, y, z) * 0.5f;

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

    static Vector3[] Circle(System.Func<float, Vector3> at)
    {
        var points = new Vector3[k_CircleSegments];
        for (int i = 0; i < k_CircleSegments; i++)
            points[i] = at(i / (float)k_CircleSegments);
        return points;
    }

    void AddLine(Transform root, bool loop, params Vector3[] points)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(root, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = loop;
        line.positionCount = points.Length;
        line.SetPositions(points);
        line.widthMultiplier = m_LineWidth;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.sharedMaterial = m_LineMaterial;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.alignment = LineAlignment.View;
        m_Lines.Add(line);
    }

    #endregion

    #region Grab zones and walls

    // Grab zones: thin boxes along the lines, on the Default layer so the hands and lasers find them, touching nothing.
    void BuildGrabZones()
    {
        m_GrabRoot = new GameObject("Grab").transform;
        m_GrabRoot.SetParent(transform, false);
        if (m_Shape == Shape.Box)
        {
            for (int i = 0; i < 12; i++)
                m_Rods.Add(Touchless(m_GrabRoot.gameObject.AddComponent<BoxCollider>()));
            return;
        }

        foreach (float latitude in new[] { -60f, 0f, 60f })
            AddSphereRods(t => Latitude(latitude, t));
        foreach (float longitude in new[] { 0f, 60f, 120f })
            AddSphereRods(t => Meridian(longitude, t));
    }

    void AddSphereRods(System.Func<float, Vector3> at)
    {
        for (int i = 0; i < k_GrabSegments; i++)
        {
            var a = at(i / (float)k_GrabSegments);
            var b = at((i + 1) / (float)k_GrabSegments);
            var go = new GameObject("Grab Segment");
            go.transform.SetParent(m_GrabRoot, false);
            go.transform.localPosition = (a + b) * 0.5f;
            go.transform.localRotation = Quaternion.LookRotation(b - a, (a + b).normalized);
            var rod = Touchless(go.AddComponent<BoxCollider>());
            rod.size = new Vector3(0f, 0f, (b - a).magnitude * 1.05f);
            m_Rods.Add(rod);
        }
    }

    static BoxCollider Touchless(BoxCollider collider)
    {
        collider.excludeLayers = ~0; // found by the hands' and lasers' queries, but pushes nothing
        return collider;
    }

    // Walls: on Ignore Raycast, enabled only while it is a collider, just outside its surface.
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

    /// <summary>
    /// Keeps the lines' grab zones and the walls their thickness in meters whatever the container's size (the box
    /// can be scaled differently along each axis).
    /// </summary>
    void FitToScale()
    {
        m_SizedFor = transform.lossyScale;
        var s = new Vector3(Mathf.Max(m_SizedFor.x, 1e-4f), Mathf.Max(m_SizedFor.y, 1e-4f), Mathf.Max(m_SizedFor.z, 1e-4f));

        if (m_Shape == Shape.Box)
        {
            // Twelve edges: four along each axis.
            int r = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    var center = Vector3.zero;
                    center[u] = a * 0.5f;
                    center[v] = b * 0.5f;
                    var size = Vector3.zero;
                    size[axis] = 1f + m_GrabThickness / s[axis];
                    size[u] = m_GrabThickness / s[u];
                    size[v] = m_GrabThickness / s[v];
                    m_Rods[r].center = center;
                    m_Rods[r].size = size;
                    r++;
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
            return;
        }

        // The sphere scales evenly.
        float scale = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
        float grab = m_GrabThickness / scale, wall = m_WallThickness / scale;
        foreach (var rod in m_Rods)
            rod.size = new Vector3(grab, grab, rod.size.z);
        foreach (var (tile, width, height) in m_Tiles)
        {
            float radius = 0.5f + 0.5f * wall;
            tile.center = new Vector3(0f, 0f, radius);
            tile.size = new Vector3(width * radius, height * radius, wall);
        }
    }

    #endregion
}
