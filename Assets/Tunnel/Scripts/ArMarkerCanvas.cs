using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Where the printed marker sits on the physical canvas (a white panel), and the panel's size. Read from
/// files/ARMarker/settings.txt on the host (no rebuild needed) and shared with every player.
/// </summary>
[Serializable]
public struct ArMarkerCanvasLayout : INetworkSerializable, IEquatable<ArMarkerCanvasLayout>
{
    /// <summary>The panel's corner the marker sits in: 0 top left, 1 top right, 2 bottom left, 3 bottom right.</summary>
    public byte corner;
    /// <summary>From the panel's left or right edge (whichever is at <see cref="corner"/>) to the marker's black square (m).</summary>
    public float offsetX;
    /// <summary>From the panel's top or bottom edge to the marker's black square (m).</summary>
    public float offsetY;
    public Vector2 size;
    /// <summary>The marker's turn on the panel, counter-clockwise as seen from the front (degrees); 0 is printed upright.</summary>
    public float rotation;
    public float markerWidth;
    /// <summary>The paint area's inset from the panel's edges (m).</summary>
    public float inset;
    /// <summary>The marker's tip counts as touching this far in front of the tracked surface (m), allowing for tracking error.</summary>
    public float touchAllowance;

    static readonly string[] k_Corners = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

    public bool right => corner == 1 || corner == 3;
    public bool bottom => corner >= 2;
    public string cornerName => k_Corners[Mathf.Clamp(corner, 0, 3)];

    /// <summary>The user's panel: 24 x 18 in, the 60 mm marker 2 cm in from the left and top edges, upright.</summary>
    public static ArMarkerCanvasLayout Default => new()
    {
        corner = 0,
        offsetX = 0.02f,
        offsetY = 0.02f,
        size = new Vector2(0.6096f, 0.4572f),
        rotation = 0f,
        markerWidth = 0.06f,
        inset = 0.0254f,
        touchAllowance = 0.01f,
    };

    /// <summary>The paint area (the panel less the inset all round), in meters.</summary>
    public Vector2 paintArea => new(Mathf.Max(0.01f, size.x - 2f * inset), Mathf.Max(0.01f, size.y - 2f * inset));

    /// <summary>
    /// The marker's pose on the canvas: canvas frame at the panel's centre, +X right, +Y up, +Z into the panel (as the
    /// marker's own frame: x right, y toward the top of the printed marker, z into the paper).
    /// </summary>
    public Pose markerOnCanvas
    {
        get
        {
            float half = markerWidth * 0.5f;
            float x = right ? size.x * 0.5f - offsetX - half : -size.x * 0.5f + offsetX + half;
            float y = bottom ? -size.y * 0.5f + offsetY + half : size.y * 0.5f - offsetY - half;
            return new Pose(new Vector3(x, y, 0f), Quaternion.Euler(0f, 0f, rotation));
        }
    }

    /// <summary>The canvas's pose from the marker's.</summary>
    public Pose CanvasFromMarker(Pose marker) => ArMarkerMath.Mul(marker, ArMarkerMath.Inverse(markerOnCanvas));

    /// <summary>
    /// The default with any canvas keys in settings.txt applied: canvasCorner (TopLeft, TopRight, BottomLeft,
    /// BottomRight), canvasOffsetX, canvasOffsetY, canvasWidth, canvasHeight (meters), canvasMarkerRotation (degrees,
    /// counter-clockwise seen from the front), canvasPaintInset (meters), canvasTouchAllowance (meters), and
    /// markerWidth.
    /// </summary>
    public static ArMarkerCanvasLayout Load(string settingsFile, Action<string> log)
    {
        var layout = Default;
        if (settingsFile == null || !File.Exists(settingsFile))
            return layout;
        var culture = CultureInfo.InvariantCulture;
        foreach (var raw in File.ReadAllLines(settingsFile))
        {
            var line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.StartsWith("#") || equals < 0)
                continue;
            string key = line[..equals].Trim(), value = line[(equals + 1)..].Trim();
            bool number = float.TryParse(value, NumberStyles.Float, culture, out float f);
            switch (key)
            {
                case "canvasCorner":
                    int index = Array.FindIndex(k_Corners, c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) layout.corner = (byte)index; else log?.Invoke($"settings.txt: unknown canvasCorner '{value}'");
                    break;
                case "canvasOffsetX" when number: layout.offsetX = f; break;
                case "canvasOffsetY" when number: layout.offsetY = f; break;
                case "canvasWidth" when number && f > 0.05f: layout.size.x = f; break;
                case "canvasHeight" when number && f > 0.05f: layout.size.y = f; break;
                case "canvasMarkerRotation" when number: layout.rotation = f; break;
                case "canvasPaintInset" when number && f >= 0f: layout.inset = f; break;
                case "canvasTouchAllowance" when number && f >= 0f: layout.touchAllowance = f; break;
                case "markerWidth" when number && f > 0.01f: layout.markerWidth = f; break;
            }
        }
        return layout;
    }

    public override string ToString() =>
        $"{size.x * 100f:0.0} x {size.y * 100f:0.0} cm, marker {markerWidth * 1000f:0} mm in the {cornerName} corner, " +
        $"{offsetX * 100f:0.0} cm from the side and {offsetY * 100f:0.0} cm from the {(bottom ? "bottom" : "top")}, turned {rotation:0.#} deg, " +
        $"paint area {paintArea.x * 100f:0.0} x {paintArea.y * 100f:0.0} cm ({inset * 100f:0.0} cm in), touch allowance {touchAllowance * 1000f:0} mm; " +
        $"marker centre on the canvas {markerOnCanvas.position * 100f} cm";

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref corner);
        serializer.SerializeValue(ref offsetX);
        serializer.SerializeValue(ref offsetY);
        serializer.SerializeValue(ref size);
        serializer.SerializeValue(ref rotation);
        serializer.SerializeValue(ref markerWidth);
        serializer.SerializeValue(ref inset);
        serializer.SerializeValue(ref touchAllowance);
    }

    public bool Equals(ArMarkerCanvasLayout other) =>
        corner == other.corner && offsetX == other.offsetX && offsetY == other.offsetY && size == other.size &&
        rotation == other.rotation && markerWidth == other.markerWidth && inset == other.inset && touchAllowance == other.touchAllowance;

    public override bool Equals(object obj) => obj is ArMarkerCanvasLayout other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(corner, offsetX, offsetY, size, rotation, markerWidth, inset);
}

/// <summary>
/// The virtual canvas lying on the physical panel the printed marker is taped to (<see cref="ArMarkerCube"/> in
/// canvas mode moves it to the tracked pose): a grey line at the panel's edges and a dashed line an inch inside them
/// round the paint area, plus a thin square round the marker, green while this headset draws its own sighting and
/// blue while it draws the shared one. The virtual marker paints inside the dashed line
/// (<see cref="IPaintSurface"/>); the marks move with the panel and are shared with everyone.
/// Its transform sits at the panel's centre, +X right, +Y up, +Z into the panel, so its face looks along -Z.
/// </summary>
public class ArMarkerCanvas : MonoBehaviour, IPaintSurface
{
    [SerializeField] Color m_LineColor = new(0.55f, 0.55f, 0.58f, 1f);
    [SerializeField] float m_LineWidth = 0.003f;
    [SerializeField] float m_Dash = 0.012f;
    [SerializeField] float m_Gap = 0.008f;

    ArMarkerCanvasLayout m_Layout = ArMarkerCanvasLayout.Default;
    MeshRenderer m_Lines, m_MarkerSquare;
    Mesh m_LinesMesh, m_SquareMesh;
    Material m_LineMaterial;
    bool m_Built;

    public ArMarkerCanvasLayout layout => m_Layout;

    public event Action rebuilt;

    /// <summary>The paint area, in meters.</summary>
    public Vector2 paintCanvasMeters => m_Layout.paintArea;

    public bool paintable => isActiveAndEnabled;

    public Transform paintParent => transform;

    /// <summary>Sets the panel's size and where the marker is on it.</summary>
    public void Configure(ArMarkerCanvasLayout layout)
    {
        bool changed = !m_Built || !layout.Equals(m_Layout);
        m_Layout = layout;
        if (!changed)
            return;
        Build();
        rebuilt?.Invoke();
    }

    /// <summary>Lays the canvas on the panel, from the marker's pose (its face is local -Z).</summary>
    public void Place(Pose marker)
    {
        var pose = m_Layout.CanvasFromMarker(marker);
        transform.SetPositionAndRotation(pose.position, pose.rotation);
    }

    /// <summary>The square round the marker in this colour (green: own sighting, blue: shared).</summary>
    public void SetMarkerMaterial(Material material)
    {
        if (m_MarkerSquare != null && material != null && m_MarkerSquare.sharedMaterial != material)
            m_MarkerSquare.sharedMaterial = material;
    }

    void Awake()
    {
        if (!m_Built)
            Build();
    }

    void OnDestroy()
    {
        foreach (UnityEngine.Object o in new UnityEngine.Object[] { m_LineMaterial, m_LinesMesh, m_SquareMesh })
            if (o != null)
                Destroy(o);
    }

    #region The surface

    public bool ToPaintCanvas(Vector3 world, out Vector2 uv, out float depth, out Vector3 onSurface)
    {
        var area = m_Layout.paintArea;
        var p = transform.InverseTransformPoint(world);
        uv = new Vector2(p.x / area.x + 0.5f, p.y / area.y + 0.5f);
        // Counted from a little in front of the tracked surface, so the tip still touches when tracking puts the
        // virtual canvas slightly behind the real panel.
        depth = -p.z - m_Layout.touchAllowance;
        onSurface = transform.TransformPoint(p.x, p.y, 0f);
        return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
    }

    /// <summary>The paint area, <paramref name="inset"/> in front of the panel, facing out; UVs are canvas coordinates.</summary>
    public Mesh BuildPaintMesh(float inset)
    {
        var area = m_Layout.paintArea;
        float x = area.x * 0.5f, y = area.y * 0.5f;
        var mesh = new Mesh { name = "AR Canvas Paint" };
        mesh.SetVertices(new[] { new Vector3(-x, -y, -inset), new Vector3(-x, y, -inset), new Vector3(x, y, -inset), new Vector3(x, -y, -inset) });
        mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Whether a point (the marker's tip, radius <paramref name="radius"/>) touches the paint area, and how far it is.</summary>
    public bool Touching(Vector3 tip, float radius, out float distance, out bool over)
    {
        over = ToPaintCanvas(tip, out _, out float depth, out _);
        distance = depth + m_Layout.touchAllowance;
        return over && depth <= radius + 0.004f && depth > -0.15f; // as TunnelMarker judges contact
    }

    #endregion

    #region Lines

    void Build()
    {
        m_Built = true;
        if (m_LineMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            m_LineMaterial = new Material(shader) { name = "AR Canvas Lines" };
            m_LineMaterial.SetColor("_BaseColor", m_LineColor);
            m_LineMaterial.color = m_LineColor;
        }
        m_Lines ??= Child("Edges and Paint Area", m_LineMaterial, out m_LinesMesh);
        m_MarkerSquare ??= Child("Marker Square", m_LineMaterial, out m_SquareMesh);

        const float z = -0.002f;
        var quads = new List<(Vector3 a, Vector3 b)>();
        // The panel's edges, solid.
        var size = m_Layout.size;
        Rectangle(quads, size * 0.5f, z, solid: true);
        // The paint area, dashed.
        Rectangle(quads, m_Layout.paintArea * 0.5f, z, solid: false);
        Fill(m_LinesMesh, quads, m_LineWidth);

        // The marker's square.
        quads.Clear();
        var marker = m_Layout.markerOnCanvas;
        float h = m_Layout.markerWidth * 0.5f + 0.004f;
        Vector3[] corners = { new(-h, -h, 0f), new(-h, h, 0f), new(h, h, 0f), new(h, -h, 0f) };
        for (int i = 0; i < 4; i++)
        {
            var a = ArMarkerMath.Apply(marker, corners[i]) + new Vector3(0f, 0f, z);
            var b = ArMarkerMath.Apply(marker, corners[(i + 1) % 4]) + new Vector3(0f, 0f, z);
            quads.Add((a, b));
        }
        Fill(m_SquareMesh, quads, m_LineWidth * 0.8f);
    }

    void Rectangle(List<(Vector3, Vector3)> quads, Vector2 half, float z, bool solid)
    {
        Vector3[] corners = { new(-half.x, -half.y, z), new(-half.x, half.y, z), new(half.x, half.y, z), new(half.x, -half.y, z) };
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            if (solid)
            {
                quads.Add((a, b));
                continue;
            }
            float length = Vector3.Distance(a, b);
            var along = (b - a) / length;
            for (float d = 0f; d < length; d += m_Dash + m_Gap)
                quads.Add((a + along * d, a + along * Mathf.Min(d + m_Dash, length)));
        }
    }

    // Each segment as a flat strip facing out of the panel (-Z).
    static void Fill(Mesh mesh, List<(Vector3 a, Vector3 b)> segments, float width)
    {
        var vertices = new List<Vector3>(segments.Count * 4);
        var triangles = new List<int>(segments.Count * 6);
        foreach (var (a, b) in segments)
        {
            var along = (b - a).normalized;
            var side = Vector3.Cross(along, Vector3.forward) * (width * 0.5f);
            int v = vertices.Count;
            vertices.Add(a - side - along * width * 0.5f);
            vertices.Add(a + side - along * width * 0.5f);
            vertices.Add(b + side + along * width * 0.5f);
            vertices.Add(b - side + along * width * 0.5f);
            triangles.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3, v, v + 2, v + 1, v, v + 3, v + 2 }); // both faces
        }
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
    }

    MeshRenderer Child(string name, Material material, out Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        mesh = new Mesh { name = name };
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }

    #endregion
}
