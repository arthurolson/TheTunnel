using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
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

    public Shape shape => m_Shape;
    public bool isCollider => IsSpawned ? m_IsCollider.Value : m_LocalIsCollider;

    readonly NetworkVariable<bool> m_IsCollider = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    bool m_LocalIsCollider;

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
        m_Block = new MaterialPropertyBlock();
        BuildLines();
        BuildGrabZones();
        BuildWalls();
        FitToScale();
        Apply(isCollider);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_IsCollider.OnValueChanged += OnColliderChanged;
        Apply(m_IsCollider.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_IsCollider.OnValueChanged -= OnColliderChanged;
        base.OnNetworkDespawn();
    }

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
    }

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
            if (Vector3.Dot(hand.transform.forward, Vector3.up) < m_PointingUp)
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
