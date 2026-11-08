using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// The marker (or brush) that paints on the tunnel's walls and arch: a cylinder a hand's length long, whose diameter
/// is the size of the marks it makes. Two hands on it change its diameter, never its length (see
/// <see cref="MarkerDiameterTransformer"/>). It paints from its tip, the end away from its dark cap.
/// It comes with a palette (<see cref="TunnelPalette"/>) and rests in the palette's holder: let go near the holder, it
/// goes back into it and moves with the palette. Touching one of the palette's colour disks with its tip gives it
/// that colour (with its opacity), and the marker's body takes the colour too. What it does on the wall is chosen on
/// the palette:
///  - Paint: while its tip touches the wall, solid paint follows it;
///  - Airbrush: soft grainy spray where it touches, building up while it stays;
///  - Eraser: takes marks off where it rubs;
///  - Pencil: each touch adds a point; straight lines join the points, or with Curve the points are the control points
///    of one Bezier curve. The line shows as it is drawn and is put on the wall by End line on the palette, by
///    touching the last point again, or by choosing another kind of mark.
/// Only the player holding it paints with it; the marks are shared through each segment's
/// <see cref="TunnelSegmentPaint"/>. Shared over the network like the molecules.
/// </summary>
[DefaultExecutionOrder(-1000)] // builds its collider before the grab interactable collects it
public class TunnelMarker : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("The marker's length (meters); it never changes.")]
    float m_Length = 0.15f;

    [SerializeField]
    Vector2 m_DiameterRange = new(0.002f, 0.1f);

    [SerializeField]
    [Tooltip("The grab zone around it is at least this thick (meters), however thin the marker.")]
    float m_MinGrabRadius = 0.015f;

    [SerializeField]
    [Tooltip("Its tip paints within this distance of the wall beyond its own radius (meters).")]
    float m_ContactSlack = 0.004f;

    [SerializeField]
    [Tooltip("Let go this close to its palette's holder (meters) and it goes back into it.")]
    float m_DockDistance = 0.12f;

    [SerializeField]
    Material m_BodyMaterial;

    [SerializeField]
    Material m_CapMaterial;

    /// <summary>Every marker.</summary>
    public static readonly List<TunnelMarker> all = new();

    readonly NetworkVariable<Color32> m_SharedColor = new(TunnelPalette.k_DefaultDisks[0], NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<NetworkObjectReference> m_SharedPalette = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_SharedDocked = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    Color32 m_LocalColor = TunnelPalette.k_DefaultDisks[0];
    TunnelPalette m_LocalPalette;
    bool m_LocalDocked = true;

    XRGrabInteractable m_Grab;
    Rigidbody m_Body;
    CapsuleCollider m_Collider;
    Material m_BodyInstance;
    Color32 m_ShownColor;
    bool m_ColorShown;
    float m_SizedFor;
    TunnelPalette m_Subscribed;

    // Painting (on the holder's headset).
    bool m_Touching;
    Vector3 m_LastTip;
    float m_AirCarry;
    int m_LastDisk = -1;
    readonly HashSet<TunnelSegmentPaint> m_StrokeSegments = new();
    readonly Dictionary<TunnelSegmentPaint, List<Vector2>> m_Batches = new();
    readonly List<Vector3> m_PencilPoints = new();
    readonly HashSet<TunnelSegmentPaint> m_PreviewSegments = new();
    PaintMode m_LastMode;

    public Color32 color => IsSpawned ? m_SharedColor.Value : m_LocalColor;
    public bool docked => IsSpawned ? m_SharedDocked.Value : m_LocalDocked;
    public float diameter => Mathf.Abs(transform.lossyScale.x);
    public Vector3 tip => transform.TransformPoint(0f, 0f, 0.5f);
    ulong key => IsSpawned ? NetworkObjectId : m_LocalKey;
    ulong m_LocalKey;
    static ulong s_NextLocalKey = 1UL << 40; // apart from network ids

    public TunnelPalette palette
    {
        get
        {
            if (!IsSpawned)
                return m_LocalPalette;
            return m_SharedPalette.Value.TryGet(out NetworkObject networkObject) ? networkObject.GetComponent<TunnelPalette>() : null;
        }
    }

    void Awake()
    {
        m_LocalKey = s_NextLocalKey++;
        m_Grab = GetComponent<XRGrabInteractable>();
        m_Body = GetComponent<Rigidbody>();
        if (TryGetComponent(out NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        if (m_Body != null)
            m_Body.constraints = RigidbodyConstraints.None;
        BuildVisuals();
        FitToScale();
        if (m_Grab != null)
        {
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }
    }

    void OnEnable() => all.Add(this);

    void OnDisable()
    {
        all.Remove(this);
        FinishPencil(false);
        EndStroke();
    }

    public override void OnDestroy()
    {
        if (m_Grab != null)
        {
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }
        if (m_Subscribed != null)
            m_Subscribed.endLineRequested -= OnEndLineRequested;
        if (m_BodyInstance != null)
            Destroy(m_BodyInstance);
        base.OnDestroy();
    }

    /// <summary>Pairs it with its palette, resting in the holder (when it is placed).</summary>
    public void SetPalette(TunnelPalette value)
    {
        if (IsSpawned)
        {
            if (IsServer)
            {
                m_SharedPalette.Value = value != null ? new NetworkObjectReference(value.NetworkObject) : default;
                m_SharedDocked.Value = true;
            }
        }
        else
        {
            m_LocalPalette = value;
            m_LocalDocked = true;
        }
    }

    #region Looks

    static Mesh s_Cylinder;

    // A cylinder along Z, 1 long and 1 across, with flat ends.
    static Mesh Cylinder()
    {
        if (s_Cylinder != null)
            return s_Cylinder;
        const int sides = 16;
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
        s_Cylinder = new Mesh { name = "Marker" };
        s_Cylinder.SetVertices(vertices);
        s_Cylinder.SetNormals(normals);
        s_Cylinder.SetTriangles(triangles, 0);
        s_Cylinder.RecalculateBounds();
        return s_Cylinder;
    }

    // The body in the marker's colour; a dark cap on the back end, so the tip (the front, +Z) is plain to see.
    void BuildVisuals()
    {
        var body = new GameObject("Body");
        body.transform.SetParent(transform, false);
        body.AddComponent<MeshFilter>().sharedMesh = Cylinder();
        var bodyRenderer = body.AddComponent<MeshRenderer>();
        if (m_BodyMaterial != null)
            m_BodyInstance = new Material(m_BodyMaterial) { name = "Marker Body" };
        bodyRenderer.sharedMaterial = m_BodyInstance;

        var cap = new GameObject("Cap");
        cap.transform.SetParent(transform, false);
        cap.transform.localPosition = new Vector3(0f, 0f, -0.44f);
        cap.transform.localScale = new Vector3(1.06f, 1.06f, 0.13f);
        cap.AddComponent<MeshFilter>().sharedMesh = Cylinder();
        cap.AddComponent<MeshRenderer>().sharedMaterial = m_CapMaterial;

        m_Collider = gameObject.AddComponent<CapsuleCollider>();
        m_Collider.direction = 2;
        m_Collider.excludeLayers = ~0; // found by the hands and lasers, pushes nothing
    }

    // Its grab zone at least m_MinGrabRadius thick, however thin it is.
    void FitToScale()
    {
        float d = Mathf.Max(diameter, 1e-4f);
        m_SizedFor = d;
        float length = Mathf.Max(Mathf.Abs(transform.lossyScale.z), 1e-4f);
        m_Collider.radius = Mathf.Max(0.5f, m_MinGrabRadius / d);
        m_Collider.height = 1f + 2f * m_Collider.radius * d / length;
    }

    void ShowColor()
    {
        var current = color;
        if (m_ColorShown && current.Equals(m_ShownColor))
            return;
        m_ShownColor = current;
        m_ColorShown = true;
        if (m_BodyInstance != null)
        {
            Color shown = current;
            shown.a = 1f;
            m_BodyInstance.SetColor("_BaseColor", shown);
        }
    }

    #endregion

    #region Holding and the holder

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode)
        {
            // Its palette goes with it.
            var owner = palette;
            if (owner != null)
                TunnelMenu.Delete(owner.gameObject);
            TunnelMenu.DeleteGrabbed(this, m_Grab);
            return;
        }
        SetDocked(false);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        if (m_Grab.isSelected)
            return;
        EndStroke();
        var holder = palette;
        if (holder != null && holder.dock != null && Vector3.Distance(transform.position, holder.dock.position) <= m_DockDistance)
            SetDocked(true);
    }

    void SetDocked(bool on)
    {
        if (on == docked)
            return;
        if (IsSpawned)
            SetDockedRpc(on);
        else
            m_LocalDocked = on;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetDockedRpc(bool on) => m_SharedDocked.Value = on;

    void SetColor(Color32 value)
    {
        if (IsSpawned)
            SetColorRpc(value);
        else
            m_LocalColor = value;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetColorRpc(Color32 value) => m_SharedColor.Value = value;

    void LateUpdate()
    {
        // Resting in the holder, it moves with the palette (on the headset that moves it).
        var holder = palette;
        if (docked && holder != null && holder.dock != null && !m_Grab.isSelected && (!IsSpawned || IsOwner))
        {
            transform.SetPositionAndRotation(holder.dock.position, holder.dock.rotation);
            if (m_Body != null)
            {
                m_Body.position = holder.dock.position;
                m_Body.rotation = holder.dock.rotation;
            }
        }
    }

    #endregion

    #region Painting

    void Update()
    {
        if (!Mathf.Approximately(diameter, m_SizedFor))
            FitToScale();
        ShowColor();

        var holder = palette;
        if (holder != m_Subscribed)
        {
            if (m_Subscribed != null)
                m_Subscribed.endLineRequested -= OnEndLineRequested;
            m_Subscribed = holder;
            if (holder != null)
                holder.endLineRequested += OnEndLineRequested;
        }

        var mode = holder != null ? holder.mode : PaintMode.Paint;
        if (mode != m_LastMode)
        {
            if (m_LastMode == PaintMode.Pencil)
                FinishPencil(true); // another kind of mark: the pencil line goes on the wall
            EndStroke();
            m_LastMode = mode;
        }
        if (m_PencilPoints.Count > 0 && holder != null && mode == PaintMode.Pencil && m_PencilCurveShown != holder.curve)
            ShowPencil();

        if (m_Grab == null || !m_Grab.isSelected)
        {
            if (m_Touching)
                EndStroke();
            return;
        }

        var tipPoint = tip;
        float radius = diameter * 0.5f;

        // A colour disk on the palette: the marker takes its colour.
        if (holder != null)
        {
            int disk = holder.DiskAt(tipPoint, radius + 0.02f);
            if (disk >= 0 && disk != m_LastDisk)
            {
                SetColor(holder.DiskColor(disk));
                PlayerHudNotification.Instance?.ShowText($"Marker: colour of disk {disk + 1}", 1.5f);
            }
            m_LastDisk = disk;
            if (disk >= 0)
            {
                if (m_Touching)
                    EndStroke();
                return;
            }
        }

        bool touching = TouchesWall(tipPoint, radius);
        switch (mode)
        {
            case PaintMode.Pencil:
                if (touching && !m_Touching)
                    AddPencilPoint(tipPoint);
                m_Touching = touching;
                break;
            default:
                if (touching)
                    Stroke(mode, tipPoint, radius);
                else if (m_Touching)
                    EndStroke();
                break;
        }
    }

    PaintStyle Style(PaintMode mode) => new(mode, color, diameter);

    bool TouchesWall(Vector3 point, float radius)
    {
        foreach (var paint in TunnelSegmentPaint.all)
            if (Contact(paint, point, radius, out _))
                return true;
        return false;
    }

    // Where the tip is on a segment's canvas, if it touches that segment's walls or arch (or is just past its end,
    // within its radius, so a stroke across a join paints both).
    bool Contact(TunnelSegmentPaint paint, Vector3 point, float radius, out Vector2 uv)
    {
        uv = default;
        if (paint == null || paint.geometry == null || !paint.geometry.isActiveAndEnabled)
            return false;
        if (!paint.geometry.ToPaintCanvas(point, out uv, out float depth, out _))
            return false;
        float margin = radius / paint.canvasMeters.x;
        return depth <= radius + m_ContactSlack && depth > -0.15f && uv.x >= -margin && uv.x <= 1f + margin;
    }

    // Freehand: stamps along the way the tip went since last frame, a fraction of its size apart.
    void Stroke(PaintMode mode, Vector3 point, float radius)
    {
        float spacing = Mathf.Max(radius * (mode == PaintMode.Airbrush ? 0.3f : 0.35f), 0.0008f);
        var from = m_Touching ? m_LastTip : point;
        float distance = Vector3.Distance(from, point);
        int steps = Mathf.Clamp(Mathf.CeilToInt(distance / spacing), 1, 400);
        for (int i = m_Touching ? 1 : 0; i <= steps; i++)
            AddStamp(Vector3.Lerp(from, point, i / (float)steps), radius);

        if (mode == PaintMode.Airbrush)
        {
            // The spray keeps building while the tip stays: extra stamps scattered about it, about 60 a second.
            m_AirCarry += Time.deltaTime * 60f;
            for (; m_AirCarry >= 1f; m_AirCarry -= 1f)
            {
                var jitter = Random.insideUnitSphere * radius * 0.35f;
                AddStamp(point + jitter, radius);
            }
        }

        m_Touching = true;
        m_LastTip = point;
        Flush(Style(mode));
    }

    void AddStamp(Vector3 point, float radius)
    {
        foreach (var paint in TunnelSegmentPaint.all)
        {
            if (!Contact(paint, point, radius + 0.05f, out var uv))
                continue;
            if (!m_Batches.TryGetValue(paint, out var batch))
                m_Batches[paint] = batch = new List<Vector2>();
            batch.Add(uv);
        }
    }

    void Flush(PaintStyle style)
    {
        foreach (var (paint, batch) in m_Batches)
        {
            if (batch.Count == 0)
                continue;
            paint.Draw(style, batch);
            m_StrokeSegments.Add(paint);
            batch.Clear();
        }
    }

    void EndStroke()
    {
        foreach (var paint in m_StrokeSegments)
            if (paint != null)
                paint.EndStroke();
        m_StrokeSegments.Clear();
        m_Touching = false;
        m_AirCarry = 0f;
    }

    #endregion

    #region Pencil

    bool m_PencilCurveShown;
    const int k_MaxPencilPoints = 24;

    void AddPencilPoint(Vector3 tipPoint)
    {
        // The touch, on the surface.
        var point = tipPoint;
        float best = float.MaxValue;
        foreach (var paint in TunnelSegmentPaint.all)
        {
            if (paint == null || paint.geometry == null || !paint.geometry.ToPaintCanvas(tipPoint, out var uv, out float depth, out var onSurface))
                continue;
            if (uv.x < 0f || uv.x > 1f || Mathf.Abs(depth) >= best)
                continue;
            best = Mathf.Abs(depth);
            point = onSurface;
        }

        // The last point touched again finishes the line.
        if (m_PencilPoints.Count > 0 && Vector3.Distance(point, m_PencilPoints[^1]) < Mathf.Max(diameter * 1.5f, 0.015f))
        {
            FinishPencil(true);
            return;
        }
        if (m_PencilPoints.Count >= k_MaxPencilPoints)
        {
            PlayerHudNotification.Instance?.ShowText($"A pencil line has at most {k_MaxPencilPoints} points: End line to put it on the wall", 2f);
            return;
        }
        m_PencilPoints.Add(point);
        ShowPencil();
    }

    void OnEndLineRequested()
    {
        if (m_PencilPoints.Count > 0)
            FinishPencil(true);
    }

    // The line through the points as it will be drawn: straight from point to point, or the Bezier curve they control.
    List<Vector3> PencilPath(bool curve)
    {
        var path = new List<Vector3>();
        if (m_PencilPoints.Count == 0)
            return path;
        float spacing = Mathf.Max(diameter * 0.35f, 0.001f);
        if (!curve || m_PencilPoints.Count < 3)
        {
            path.Add(m_PencilPoints[0]);
            for (int i = 1; i < m_PencilPoints.Count; i++)
            {
                var a = m_PencilPoints[i - 1];
                var b = m_PencilPoints[i];
                int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / spacing), 1, 3000);
                for (int s = 1; s <= steps; s++)
                    path.Add(Vector3.Lerp(a, b, s / (float)steps));
            }
            return path;
        }

        float polygon = 0f;
        for (int i = 1; i < m_PencilPoints.Count; i++)
            polygon += Vector3.Distance(m_PencilPoints[i - 1], m_PencilPoints[i]);
        int samples = Mathf.Clamp(Mathf.CeilToInt(polygon / spacing), 2, 6000);
        var work = new Vector3[m_PencilPoints.Count];
        for (int s = 0; s <= samples; s++)
        {
            // De Casteljau: the curve the points control.
            float t = s / (float)samples;
            m_PencilPoints.CopyTo(work);
            for (int level = work.Length - 1; level > 0; level--)
                for (int i = 0; i < level; i++)
                    work[i] = Vector3.LerpUnclamped(work[i], work[i + 1], t);
            path.Add(work[0]);
        }
        return path;
    }

    // Points along a path, on the segments' canvases (each on the segment it lies in, or both near a join).
    Dictionary<TunnelSegmentPaint, List<Vector2>> OnCanvases(List<Vector3> path, float radius)
    {
        var result = new Dictionary<TunnelSegmentPaint, List<Vector2>>();
        foreach (var point in path)
        {
            foreach (var paint in TunnelSegmentPaint.all)
            {
                if (paint == null || paint.geometry == null || !paint.geometry.ToPaintCanvas(point, out var uv, out _, out _))
                    continue;
                float margin = radius / paint.canvasMeters.x;
                if (uv.x < -margin || uv.x > 1f + margin)
                    continue;
                if (!result.TryGetValue(paint, out var list))
                    result[paint] = list = new List<Vector2>();
                list.Add(uv);
            }
        }
        return result;
    }

    // Shows the line being drawn, with its points (and, for a curve, the lines between its control points).
    void ShowPencil()
    {
        var holder = palette;
        bool curve = holder != null && holder.curve;
        m_PencilCurveShown = curve;
        float radius = diameter * 0.5f;
        var line = OnCanvases(PencilPath(curve), radius);
        var polygon = curve && m_PencilPoints.Count >= 3 ? OnCanvases(PolygonPath(), 0.002f) : new Dictionary<TunnelSegmentPaint, List<Vector2>>();
        var points = OnCanvases(m_PencilPoints, Mathf.Max(radius * 1.6f, 0.007f));

        var lineStyle = Style(PaintMode.Pencil);
        var polygonStyle = new PaintStyle(PaintMode.Pencil, new Color32(255, 255, 255, 110), 0.003f);
        var pointStyle = new PaintStyle(PaintMode.Pencil, new Color32(255, 255, 255, 230), Mathf.Max(diameter * 1.6f, 0.014f));
        var shown = new HashSet<TunnelSegmentPaint>();
        foreach (var paint in TunnelSegmentPaint.all)
        {
            if (paint == null)
                continue;
            line.TryGetValue(paint, out var l);
            polygon.TryGetValue(paint, out var g);
            points.TryGetValue(paint, out var p);
            if (l == null && g == null && p == null)
                continue;
            paint.SetPreview(key, new[] { (lineStyle, l), (polygonStyle, g), (pointStyle, p) });
            shown.Add(paint);
        }
        foreach (var paint in m_PreviewSegments)
            if (paint != null && !shown.Contains(paint))
                paint.ClearPreview(key);
        m_PreviewSegments.Clear();
        m_PreviewSegments.UnionWith(shown);
    }

    List<Vector3> PolygonPath()
    {
        var path = new List<Vector3>();
        for (int i = 1; i < m_PencilPoints.Count; i++)
        {
            var a = m_PencilPoints[i - 1];
            var b = m_PencilPoints[i];
            int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / 0.0015f), 1, 2000);
            for (int s = 0; s <= steps; s++)
                path.Add(Vector3.Lerp(a, b, s / (float)steps));
        }
        return path;
    }

    // Puts the pencil line on the wall (or just drops it) and starts a new one.
    void FinishPencil(bool draw)
    {
        if (m_PencilPoints.Count == 0)
            return;
        if (draw && m_PencilPoints.Count >= 2)
        {
            var holder = palette;
            bool curve = holder != null && holder.curve;
            var style = Style(PaintMode.Pencil);
            foreach (var (paint, uvs) in OnCanvases(PencilPath(curve), diameter * 0.5f))
            {
                paint.Draw(style, uvs);
                paint.EndStroke();
            }
            Debug.Log($"[Paint] pencil {(curve ? "curve" : "line")} through {m_PencilPoints.Count} points");
        }
        foreach (var paint in m_PreviewSegments)
            if (paint != null)
                paint.ClearPreview(key);
        m_PreviewSegments.Clear();
        m_PencilPoints.Clear();
    }

    #endregion
}
