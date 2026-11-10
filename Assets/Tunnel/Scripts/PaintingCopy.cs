using System;
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
/// A copy of a painting made on a real panel (<see cref="RealMarkerTracker"/>), put on a tunnel wall for everyone,
/// see-through where the panel wasn't painted. Made by the palette's Copy to wall button
/// (<see cref="TunnelPaintMenu.CopyPainting"/>): it starts on the tunnel wall the painter looks at, the size of the
/// panel's canvas, with the canvas's dark outline and a small ball on each corner.
///  - Grabbed by a corner, that corner follows the hand while the opposite corner stays put: the copy grows or
///    shrinks and turns, flat on its wall.
///  - Grabbed anywhere else, it moves; let go near a flat tunnel wall, it lies flat on the wall, keeping its turn.
///  - Grabbed with the TS Menu's Delete on, it goes.
/// Its marks are shared like a tunnel segment's (its own <see cref="TunnelSegmentPaint"/>), so players who join later
/// see it too, and the virtual marker can paint on it. Works locally when not connected.
/// Its transform sits at its centre, +X right, +Y up, +Z into the wall; its scale is how much it has been enlarged.
/// </summary>
[RequireComponent(typeof(TunnelSegmentPaint))]
public class PaintingCopy : NetworkBehaviour, IPaintSurface
{
    [SerializeField]
    [Tooltip("A grab within this fraction of the copy's shorter side from a corner (or 5 cm) takes the corner.")]
    float m_CornerZone = 0.2f;

    [SerializeField]
    [Tooltip("The corner balls' diameter (meters), whatever the copy's size.")]
    float m_CornerBall = 0.02f;

    [SerializeField]
    [Tooltip("Let go this close to a flat tunnel wall (meters) and it lies flat on it.")]
    float m_SnapDistance = 0.3f;

    [SerializeField]
    [Tooltip("How much it can be shrunk or enlarged.")]
    Vector2 m_ScaleRange = new(0.1f, 6f);

    [SerializeField]
    Color m_OutlineColor = new(0.05f, 0.05f, 0.06f, 1f);

    [SerializeField]
    [Tooltip("Tunnel/Paint Overlay.")]
    Material m_Overlay;

    [SerializeField]
    [Tooltip("Tunnel/Paint Brush.")]
    Shader m_Brush;

    [SerializeField]
    [Tooltip("Size of a canvas texel (meters, before enlarging).")]
    float m_Texel = 0.0015f;

    /// <summary>Every copy.</summary>
    public static readonly List<PaintingCopy> all = new();

    // Copies asked for on this headset, waiting to be filled: request number, the paint to copy.
    static readonly Dictionary<int, TunnelSegmentPaint> s_Pending = new();
    static int s_NextRequest = 1;

    readonly NetworkVariable<Vector2> m_SharedMeters = new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<int> m_SharedRequest = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<ulong> m_SharedRequester = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    Vector2 m_LocalMeters;
    int m_LocalRequest;
    TunnelSegmentPaint m_Paint;
    XRGrabInteractable m_Grab;
    Rigidbody m_Body;
    BoxCollider m_Collider;
    LineRenderer m_Outline;
    Material m_OutlineMaterial, m_BallMaterial;
    readonly Transform[] m_Balls = new Transform[4];
    Vector2 m_Shown;
    bool m_Filled;

    // A corner being dragged.
    int m_DragCorner = -1;
    IXRSelectInteractor m_Dragger;
    Vector3 m_FixedCorner, m_StartDiagonal, m_PlaneNormal;
    Quaternion m_StartRotation;
    float m_StartScale;

    public event Action rebuilt;

    /// <summary>The panel canvas's size (meters): the marks keep their size on it, however the copy is enlarged.</summary>
    public Vector2 paintCanvasMeters => IsSpawned ? m_SharedMeters.Value : m_LocalMeters;

    public bool paintable => isActiveAndEnabled && paintCanvasMeters.x > 0f && paintCanvasMeters.y > 0f;

    public Transform paintParent => transform;

    float scale => Mathf.Abs(transform.lossyScale.x);

    /// <summary>
    /// Remembers which paint a copy asked for on this headset is to be filled from; the number goes with the request.
    /// </summary>
    public static int Remember(TunnelSegmentPaint source)
    {
        int request = s_NextRequest++;
        s_Pending[request] = source;
        return request;
    }

    /// <summary>Set before it is spawned (or, not connected, before it wakes): its canvas size and who asked.</summary>
    public void Setup(Vector2 meters, int request, ulong requester)
    {
        m_LocalMeters = meters;
        m_LocalRequest = request;
        m_SharedMeters.Value = meters;
        m_SharedRequest.Value = request;
        m_SharedRequester.Value = requester;
    }

    void Awake()
    {
        m_Paint = GetComponent<TunnelSegmentPaint>();
        m_Paint.UseSurface(this, m_Overlay, m_Brush, m_Texel);
        m_Grab = GetComponent<XRGrabInteractable>();
        m_Body = GetComponent<Rigidbody>();
        m_Collider = GetComponent<BoxCollider>();
        if (TryGetComponent(out NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        if (m_Grab != null)
        {
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }
    }

    void OnEnable() => all.Add(this);

    void OnDisable() => all.Remove(this);

    void Start()
    {
        // Not connected: fill it now.
        if (!IsSpawned && m_LocalRequest != 0)
            Fill(m_LocalRequest);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer && m_LocalMeters.x > 0f)
            m_SharedMeters.Value = m_LocalMeters;
        if (NetworkManager.LocalClientId == m_SharedRequester.Value)
            Fill(m_SharedRequest.Value);
    }

    public override void OnDestroy()
    {
        if (m_Grab != null)
        {
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }
        foreach (var material in new[] { m_OutlineMaterial, m_BallMaterial })
            if (material != null)
                Destroy(material);
        base.OnDestroy();
    }

    // On the headset that asked for it: the panel's marks go onto it, and from it to everyone.
    void Fill(int request)
    {
        if (m_Filled || !s_Pending.TryGetValue(request, out var source))
            return;
        m_Filled = true;
        s_Pending.Remove(request);
        if (source != null)
            StartCoroutine(source.ReplayInto(m_Paint));
    }

    void Update()
    {
        var meters = paintCanvasMeters;
        if (meters.x > 0f && meters != m_Shown)
        {
            m_Shown = meters;
            Build(meters);
            rebuilt?.Invoke();
        }
        if (m_Shown.x > 0f)
            PlaceBalls();
        if (m_DragCorner >= 0)
            DragCorner();
    }

    #region The surface

    public bool ToPaintCanvas(Vector3 world, out Vector2 uv, out float depth, out Vector3 onSurface)
    {
        var meters = paintCanvasMeters;
        var p = transform.InverseTransformPoint(world);
        uv = new Vector2(p.x / Mathf.Max(meters.x, 1e-4f) + 0.5f, p.y / Mathf.Max(meters.y, 1e-4f) + 0.5f);
        depth = -p.z * scale;
        onSurface = transform.TransformPoint(p.x, p.y, 0f);
        return meters.x > 0f && uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
    }

    public Mesh BuildPaintMesh(float inset)
    {
        var meters = paintCanvasMeters;
        float x = meters.x * 0.5f, y = meters.y * 0.5f, z = -inset / Mathf.Max(scale, 1e-3f);
        var mesh = new Mesh { name = "Painting Copy" };
        mesh.SetVertices(new[] { new Vector3(-x, -y, z), new Vector3(-x, y, z), new Vector3(x, y, z), new Vector3(x, -y, z) });
        mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // The outline, the grab box and the corner balls, for a canvas this size.
    void Build(Vector2 meters)
    {
        float x = meters.x * 0.5f, y = meters.y * 0.5f;
        if (m_Collider != null)
        {
            m_Collider.size = new Vector3(meters.x, meters.y, 0.02f);
            m_Collider.center = new Vector3(0f, 0f, -0.006f);
        }

        if (m_Outline == null)
        {
            var go = new GameObject("Outline");
            go.transform.SetParent(transform, false);
            m_Outline = go.AddComponent<LineRenderer>();
            m_Outline.useWorldSpace = false;
            m_Outline.loop = true;
            m_Outline.widthMultiplier = 0.005f;
            m_Outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Outline.receiveShadows = false;
            m_OutlineMaterial = Unlit("Painting Copy Outline", m_OutlineColor);
            m_Outline.sharedMaterial = m_OutlineMaterial;
            m_BallMaterial = Unlit("Painting Copy Corner", m_OutlineColor);
            for (int i = 0; i < 4; i++)
            {
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = $"Corner {i + 1}";
                Destroy(ball.GetComponent<Collider>());
                ball.GetComponent<MeshRenderer>().sharedMaterial = m_BallMaterial;
                ball.transform.SetParent(transform, false);
                m_Balls[i] = ball.transform;
            }
        }

        m_Outline.positionCount = 4;
        m_Outline.SetPositions(new[] { new Vector3(-x, -y, -0.004f), new Vector3(-x, y, -0.004f), new Vector3(x, y, -0.004f), new Vector3(x, -y, -0.004f) });
    }

    // Corners in its own coordinates: bottom-left, top-left, top-right, bottom-right.
    Vector3 Corner(int index)
    {
        var meters = m_Shown;
        return new Vector3((index == 2 || index == 3 ? 0.5f : -0.5f) * meters.x, (index == 1 || index == 2 ? 0.5f : -0.5f) * meters.y, 0f);
    }

    void PlaceBalls()
    {
        float size = m_CornerBall / Mathf.Max(scale, 1e-3f);
        for (int i = 0; i < 4; i++)
        {
            if (m_Balls[i] == null)
                continue;
            m_Balls[i].localPosition = Corner(i) + new Vector3(0f, 0f, -0.004f);
            m_Balls[i].localScale = Vector3.one * size;
        }
    }

    static Material Unlit(string name, Color colour)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var material = new Material(shader != null ? shader : Shader.Find("Sprites/Default")) { name = name };
        material.SetColor("_BaseColor", colour);
        material.color = colour;
        return material;
    }

    #endregion

    #region Grabbing: corners, moving, the wall

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode)
        {
            TunnelMenu.DeleteGrabbed(this, m_Grab);
            return;
        }

        var point = args.interactorObject.GetAttachTransform(m_Grab).position;
        if (args.interactorObject is NearFarInteractor nearFar && nearFar.selectionRegion.Value == NearFarInteractor.Region.Far &&
            nearFar.TryGetCurveEndPoint(out var end) != UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals.EndPointType.None)
            point = end;

        // The nearest corner, if the grab is on it.
        float zone = Mathf.Max(0.05f, m_CornerZone * Mathf.Min(m_Shown.x, m_Shown.y) * scale);
        int corner = -1;
        for (int i = 0; i < 4; i++)
        {
            float d = Vector3.Distance(transform.TransformPoint(Corner(i)), point);
            if (d < zone)
            {
                zone = d;
                corner = i;
            }
        }

        if (corner < 0)
            return;
        m_DragCorner = corner;
        m_Dragger = args.interactorObject;
        m_FixedCorner = transform.TransformPoint(Corner((corner + 2) % 4));
        m_StartDiagonal = transform.TransformPoint(Corner(corner)) - m_FixedCorner;
        m_PlaneNormal = -transform.forward;
        m_StartRotation = transform.rotation;
        m_StartScale = scale;
        m_Grab.trackPosition = m_Grab.trackRotation = m_Grab.trackScale = false;
        PlayerHudNotification.Instance?.ShowText("Corner: move it to size and turn the painting", 1.5f);
    }

    // The dragged corner follows the hand, on the copy's plane; the opposite corner stays where it was.
    void DragCorner()
    {
        if (m_Dragger == null || (IsSpawned && !IsOwner))
            return;
        var point = m_Dragger.GetAttachTransform(m_Grab).position;
        var onPlane = point - m_PlaneNormal * Vector3.Dot(point - m_FixedCorner, m_PlaneNormal);
        var diagonal = onPlane - m_FixedCorner;
        if (diagonal.magnitude < 0.01f)
            return;
        float s = Mathf.Clamp(m_StartScale * diagonal.magnitude / m_StartDiagonal.magnitude, m_ScaleRange.x, m_ScaleRange.y);
        float turn = Vector3.SignedAngle(m_StartDiagonal, diagonal, m_PlaneNormal);
        var rotation = Quaternion.AngleAxis(turn, m_PlaneNormal) * m_StartRotation;
        var actual = Quaternion.AngleAxis(turn, m_PlaneNormal) * m_StartDiagonal * (s / m_StartScale);
        var center = m_FixedCorner + actual * 0.5f;
        SetPose(center, rotation, s);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        if (m_DragCorner >= 0 && args.interactorObject == m_Dragger)
        {
            m_DragCorner = -1;
            m_Dragger = null;
            if (m_Grab != null)
                m_Grab.trackPosition = m_Grab.trackRotation = m_Grab.trackScale = true;
            Debug.Log($"[Paint] painting copy sized to {m_Shown.x * scale * 100f:0} x {m_Shown.y * scale * 100f:0} cm");
            return;
        }

        if (m_Grab != null && !m_Grab.isSelected && !TunnelMenu.deleteMode && isActiveAndEnabled)
            StartCoroutine(SnapAfterRelease());
    }

    IEnumerator SnapAfterRelease()
    {
        yield return null; // after the interaction toolkit and the network interactable have let go
        if (m_Grab.isSelected || (IsSpawned && !IsOwner))
            yield break;
        if (WallNear(transform.position, transform.up, m_SnapDistance, out var pose))
        {
            SetPose(pose.position, pose.rotation, scale);
            PlayerHudNotification.Instance?.ShowText("Painting on the wall", 1.5f);
        }
    }

    void SetPose(Vector3 position, Quaternion rotation, float s)
    {
        transform.SetPositionAndRotation(position, rotation);
        transform.localScale = Vector3.one * s;
        if (m_Body != null)
        {
            m_Body.position = position;
            m_Body.rotation = rotation;
        }
    }

    /// <summary>
    /// The nearest flat tunnel wall within <paramref name="reach"/> of a point: a pose flat on it, facing into the
    /// tunnel, turned so its up is as near <paramref name="up"/> as the wall allows.
    /// </summary>
    public static bool WallNear(Vector3 point, Vector3 up, float reach, out Pose pose)
    {
        pose = default;
        float nearest = reach;
        bool found = false;
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var p = segment.transform.InverseTransformPoint(point);
            if (Mathf.Abs(p.z) > segment.length * 0.5f || p.y < 0f || p.y > segment.wallHeight)
                continue;
            int side = p.x < 0f ? -1 : 1;
            float gap = Mathf.Abs(segment.width * 0.5f - side * p.x);
            if (gap >= nearest)
                continue;
            nearest = gap;
            found = true;
            pose = OnWall(segment, side, p.y, p.z, up);
        }
        return found;
    }

    /// <summary>The tunnel wall the head looks at (within 6 m): a pose flat on it, upright, where the gaze meets it.</summary>
    public static bool WallInView(out Pose pose)
    {
        pose = default;
        var head = Camera.main != null ? Camera.main.transform : null;
        if (head == null)
            return false;
        float nearest = 6f;
        bool found = false;
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var frame = segment.transform;
            var origin = frame.InverseTransformPoint(head.position);
            var direction = frame.InverseTransformDirection(head.forward);
            if (Mathf.Abs(direction.x) < 1e-3f)
                continue;
            int side = direction.x < 0f ? -1 : 1;
            float t = (side * segment.width * 0.5f - origin.x) / direction.x;
            var hit = origin + direction * t;
            if (t <= 0.2f || t >= nearest || Mathf.Abs(hit.z) > segment.length * 0.5f || hit.y < 0f || hit.y > segment.wallHeight)
                continue;
            nearest = t;
            found = true;
            pose = OnWall(segment, side, hit.y, hit.z, Vector3.up);
        }
        return found;
    }

    static Pose OnWall(TunnelSegmentGeometry segment, int side, float y, float z, Vector3 up)
    {
        var frame = segment.transform;
        var into = frame.TransformDirection(Vector3.right * side);
        var onWallUp = Vector3.ProjectOnPlane(up, into);
        if (onWallUp.sqrMagnitude < 1e-4f)
            onWallUp = frame.up;
        var position = frame.TransformPoint(new Vector3(side * (segment.width * 0.5f - 0.008f), y, z));
        return new Pose(position, Quaternion.LookRotation(into, onWallUp.normalized));
    }

    #endregion
}
