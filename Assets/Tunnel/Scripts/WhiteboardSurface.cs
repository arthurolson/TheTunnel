using System;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// A real whiteboard, found by the headset's camera (<see cref="RealMarkerTracker"/>), as a surface markers paint on
/// in place of the tunnel's walls. Its transform sits at the board's centre with +X to the right, +Y up and +Z into
/// the board, so the board's face looks along -Z. A thin outline shows where the headset thinks the board is.
/// Its paint (<see cref="TunnelSegmentPaint"/>) stays on this headset.
/// </summary>
public class WhiteboardSurface : MonoBehaviour, IPaintSurface
{
    [SerializeField]
    Vector2 m_Size = new(0.9144f, 0.6096f); // 3 x 2 ft

    [SerializeField]
    [Tooltip("Points this far beyond the board's edges (meters) still count as over it, so strokes reach its edges.")]
    float m_EdgeMargin = 0.03f;

    [SerializeField]
    Color m_OutlineColor = new(0.3f, 0.85f, 1f, 0.9f);

    LineRenderer m_Outline;
    Material m_OutlineMaterial;

    public Vector2 size => m_Size;

    public event Action rebuilt;

    public Vector2 paintCanvasMeters => m_Size;

    public bool paintable => isActiveAndEnabled;

    public Transform paintParent => transform;

    /// <summary>The board's face, looking out toward whoever stands at it.</summary>
    public Vector3 normal => -transform.forward;

    /// <summary>Puts the board here: <paramref name="center"/> on its face, facing along <paramref name="facing"/>.</summary>
    public void Place(Vector3 center, Vector3 facing, Vector3 up, Vector2 size)
    {
        transform.SetPositionAndRotation(center, Quaternion.LookRotation(-facing, up));
        transform.localScale = Vector3.one;
        bool resized = size != m_Size;
        m_Size = size;
        ShowOutline();
        if (resized)
            rebuilt?.Invoke();
    }

    public bool ToPaintCanvas(Vector3 world, out Vector2 uv, out float depth, out Vector3 onSurface)
    {
        var p = transform.InverseTransformPoint(world);
        uv = new Vector2(p.x / m_Size.x + 0.5f, p.y / m_Size.y + 0.5f);
        depth = -p.z;
        onSurface = transform.TransformPoint(p.x, p.y, 0f);
        return Mathf.Abs(p.x) <= m_Size.x * 0.5f + m_EdgeMargin && Mathf.Abs(p.y) <= m_Size.y * 0.5f + m_EdgeMargin;
    }

    /// <summary>The board's face, <paramref name="inset"/> in front of it, facing out; UVs are canvas coordinates.</summary>
    public Mesh BuildPaintMesh(float inset)
    {
        float x = m_Size.x * 0.5f, y = m_Size.y * 0.5f;
        var mesh = new Mesh { name = "Whiteboard Paint" };
        mesh.SetVertices(new[] { new Vector3(-x, -y, -inset), new Vector3(-x, y, -inset), new Vector3(x, y, -inset), new Vector3(x, -y, -inset) });
        mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0); // clockwise seen from the front (-Z)
        mesh.RecalculateBounds();
        return mesh;
    }

    void ShowOutline()
    {
        if (m_Outline == null)
        {
            var go = new GameObject("Outline");
            go.transform.SetParent(transform, false);
            m_Outline = go.AddComponent<LineRenderer>();
            m_Outline.useWorldSpace = false;
            m_Outline.loop = true;
            m_Outline.widthMultiplier = 0.004f;
            m_Outline.numCornerVertices = 2;
            m_Outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Outline.receiveShadows = false;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            m_OutlineMaterial = new Material(shader) { name = "Whiteboard Outline" };
            m_OutlineMaterial.SetColor("_BaseColor", m_OutlineColor);
            m_OutlineMaterial.color = m_OutlineColor;
            m_Outline.sharedMaterial = m_OutlineMaterial;
        }

        float x = m_Size.x * 0.5f, y = m_Size.y * 0.5f, z = -0.004f;
        m_Outline.positionCount = 4;
        m_Outline.SetPositions(new[] { new Vector3(-x, -y, z), new Vector3(-x, y, z), new Vector3(x, y, z), new Vector3(x, -y, z) });
    }

    void OnDestroy()
    {
        if (m_OutlineMaterial != null)
            Destroy(m_OutlineMaterial);
    }
}
