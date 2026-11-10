using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The marks painted on a Tunnel Segment's walls and arch. They are kept in a canvas texture laid over the inside of
/// the shell (a thin surface just in front of it, see <see cref="TunnelSegmentGeometry.BuildPaintMesh"/>), so they sit
/// on top of the wall's colour and texture. A marker (<see cref="TunnelMarker"/>) adds brush stamps in the canvas's
/// coordinates (<see cref="TunnelSegmentGeometry.ToPaintCanvas"/>):
///  - solid paint and pencil lines: hard round stamps, each pixel painted once per stroke (a stencil), so a
///    see-through colour stays even along the stroke;
///  - airbrush: soft grainy stamps that build up;
///  - eraser: takes paint off.
/// A pencil line still being drawn is a preview, on a second texture, until it is finished.
/// Shared: every stamp goes to everyone, and the host keeps them all, so a player who joins later gets the marks
/// already made. Clear takes every mark off for everyone. Works locally when not connected.
/// The canvas is made the first time something is painted, and the surface is drawn only while there is paint.
/// The same paint also goes on other surfaces (<see cref="IPaintSurface"/>): a real whiteboard found by the camera
/// (<see cref="WhiteboardSurface"/>) is given its own, local to the headset that found it (see <see cref="UseSurface"/>).
/// </summary>
public class TunnelSegmentPaint : NetworkBehaviour
{
    [SerializeField]
    TunnelSegmentGeometry m_Geometry;

    [SerializeField]
    [Tooltip("Tunnel/Paint Overlay: draws the canvas over the inside of the shell.")]
    Material m_OverlayMaterial;

    [SerializeField]
    [Tooltip("Tunnel/Paint Brush: draws stamps into the canvas.")]
    Shader m_BrushShader;

    [SerializeField]
    [Tooltip("Size of a canvas texel on the wall (meters).")]
    float m_Texel = 0.003f;

    [SerializeField]
    int m_MaxCanvasSize = 4096;

    [SerializeField]
    [Tooltip("How far in front of the shell the paint is drawn (meters).")]
    float m_Inset = 0.003f;

    [SerializeField]
    [Tooltip("The host keeps at most this many stamps for players who join later (the oldest go first).")]
    int m_MaxHistoryStamps = 500000;

    /// <summary>Every segment's paint.</summary>
    public static readonly List<TunnelSegmentPaint> all = new();

    public TunnelSegmentGeometry geometry => m_Geometry;

    // A surface other than a tunnel segment (a whiteboard), set at runtime.
    IPaintSurface m_Other;

    /// <summary>What this paints on: the segment's walls and arch, or another surface.</summary>
    public IPaintSurface surface => m_Other ?? (m_Geometry != null ? m_Geometry : null);

    /// <summary>Whether it can be painted on now.</summary>
    public bool paintable => surface is { paintable: true };

    const int k_HardPass = 0, k_AirPass = 1, k_ErasePass = 2;
    const int k_MaxStampsPerMessage = 1000;
    const float k_PackMin = -0.25f, k_PackRange = 1.5f; // u, v packed as 0..65535 over -0.25..1.25

    static readonly int s_Canvas = Shader.PropertyToID("_Canvas");
    static readonly int s_Preview = Shader.PropertyToID("_Preview");
    static readonly int s_CanvasSize = Shader.PropertyToID("_CanvasSize");

    RenderTexture m_CanvasTexture, m_PreviewTexture;
    Material m_Brush, m_Overlay;
    MeshRenderer m_Surface;
    Mesh m_SurfaceMesh;
    bool m_HasPaint;

    // To draw, in order, at the end of the frame: stamps, ends of strokes (the stencil cleared), a clear.
    enum OpKind : byte { Stamps, EndStroke, Clear }
    readonly List<(OpKind kind, PaintStyle style, Vector2[] uvs)> m_Pending = new();

    // Pencil lines being drawn, by marker: line, control polygon and control points.
    readonly Dictionary<ulong, (PaintStyle style, Vector2[] uvs)[]> m_Previews = new();
    bool m_PreviewDirty;

    // The host's record of everything drawn, for players who join later.
    readonly List<(OpKind kind, PaintStyle style, ushort[] packed)> m_History = new();
    int m_HistoryStamps;

    readonly List<Mesh> m_MeshPool = new();
    CommandBuffer m_Commands;

    void Awake()
    {
        if (m_Geometry == null)
            m_Geometry = GetComponentInChildren<TunnelSegmentGeometry>(true);
    }

    void OnEnable()
    {
        all.Add(this);
        if (surface != null)
            surface.rebuilt += OnGeometryRebuilt;
    }

    void OnDisable()
    {
        all.Remove(this);
        if (surface != null)
            surface.rebuilt -= OnGeometryRebuilt;
    }

    /// <summary>
    /// Paints on another surface (a whiteboard) instead of a segment: drawn with these materials, its canvas texels
    /// <paramref name="texel"/> meters across. Not shared over the network.
    /// </summary>
    public void UseSurface(IPaintSurface other, Material overlay, Shader brush, float texel)
    {
        if (isActiveAndEnabled && surface != null)
            surface.rebuilt -= OnGeometryRebuilt;
        m_Other = other;
        m_OverlayMaterial = overlay;
        m_BrushShader = brush;
        m_Texel = texel;
        if (isActiveAndEnabled && surface != null)
            surface.rebuilt += OnGeometryRebuilt;
    }

    public override void OnDestroy()
    {
        foreach (var texture in new[] { m_CanvasTexture, m_PreviewTexture })
            if (texture != null)
                texture.Release();
        foreach (Object o in new Object[] { m_CanvasTexture, m_PreviewTexture, m_Brush, m_Overlay, m_SurfaceMesh })
            if (o != null)
                Destroy(o);
        foreach (var mesh in m_MeshPool)
            Destroy(mesh);
        m_Commands?.Release();
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsServer)
            RequestHistoryRpc();
    }

    void OnGeometryRebuilt()
    {
        if (m_SurfaceMesh == null)
            return;
        Destroy(m_SurfaceMesh);
        m_SurfaceMesh = surface.BuildPaintMesh(m_Inset);
        m_Surface.GetComponent<MeshFilter>().sharedMesh = m_SurfaceMesh;
    }

    #region Drawing (called on the painter's headset)

    /// <summary>The canvas's scale: meters per unit of u (along the tunnel) and v (around it).</summary>
    public Vector2 canvasMeters => surface.paintCanvasMeters;

    /// <summary>Draws stamps (canvas coordinates) for everyone.</summary>
    public void Draw(PaintStyle style, List<Vector2> uvs)
    {
        if (uvs.Count == 0)
            return;
        for (int start = 0; start < uvs.Count; start += k_MaxStampsPerMessage)
        {
            int count = Mathf.Min(k_MaxStampsPerMessage, uvs.Count - start);
            var part = uvs.GetRange(start, count).ToArray();
            var packed = Pack(part);
            AddOp(OpKind.Stamps, style, Unpack(packed)); // as everyone else gets them
            if (IsSpawned)
                DrawRpc(style, packed);
        }
    }

    /// <summary>A solid stroke ended here: the next one may paint over it.</summary>
    public void EndStroke()
    {
        AddOp(OpKind.EndStroke, default, null);
        if (IsSpawned)
            EndStrokeRpc();
    }

    /// <summary>Takes every mark off, for everyone.</summary>
    public void Clear()
    {
        if (IsSpawned)
            ClearRpc();
        else
            AddOp(OpKind.Clear, default, null);
    }

    /// <summary>Takes every mark off every segment, for everyone.</summary>
    public static void ClearAll()
    {
        foreach (var paint in all.ToArray())
            if (paint != null)
                paint.Clear();
    }

    /// <summary>
    /// Shows a pencil line still being drawn, for everyone: its layers (line, control polygon, control points) are
    /// drawn over the marks until it is finished or cleared.
    /// </summary>
    public void SetPreview(ulong marker, (PaintStyle style, List<Vector2> uvs)[] layers)
    {
        var line = layers.Length > 0 ? layers[0] : default;
        var polygon = layers.Length > 1 ? layers[1] : default;
        var points = layers.Length > 2 ? layers[2] : default;
        // All three in one message: the points as they are, the rest thinned out to fit if they must be.
        var packedPoints = Pack(Thin(points.uvs, 64));
        var packedPolygon = Pack(Thin(polygon.uvs, 250));
        var packedLine = Pack(Thin(line.uvs, k_MaxStampsPerMessage - packedPoints.Length / 2 - packedPolygon.Length / 2));
        ApplyPreview(marker, line.style, packedLine, polygon.style, packedPolygon, points.style, packedPoints);
        if (IsSpawned)
            PreviewRpc(marker, line.style, packedLine, polygon.style, packedPolygon, points.style, packedPoints);
    }

    public void ClearPreview(ulong marker)
    {
        if (!m_Previews.ContainsKey(marker))
            return;
        RemovePreview(marker);
        if (IsSpawned)
            ClearPreviewRpc(marker);
    }

    /// <summary>Whether anything has been painted here (and not cleared). Known on the host, or when not shared.</summary>
    public bool hasMarks => m_History.Exists(op => op.kind == OpKind.Stamps && op.style.mode != PaintMode.Eraser);

    /// <summary>
    /// Paints everything painted here, in order, onto <paramref name="target"/> (which shares it as its own marks), a
    /// few thousand stamps a frame so the network keeps up. The marks keep their size in meters of this canvas, so
    /// the target should have the same canvas size.
    /// </summary>
    public System.Collections.IEnumerator ReplayInto(TunnelSegmentPaint target)
    {
        var history = m_History.ToArray();
        int stamps = 0, thisFrame = 0;
        var uvs = new List<Vector2>();
        foreach (var (kind, style, packed) in history)
        {
            if (target == null)
                yield break;
            if (kind == OpKind.EndStroke)
            {
                target.EndStroke();
                continue;
            }
            if (kind != OpKind.Stamps || packed == null)
                continue;
            uvs.Clear();
            uvs.AddRange(Unpack(packed));
            target.Draw(style, uvs);
            stamps += uvs.Count;
            thisFrame += uvs.Count;
            if (thisFrame >= 2000)
            {
                thisFrame = 0;
                yield return null;
            }
        }
        if (target != null)
            target.EndStroke();
        Debug.Log($"[Paint] copied {stamps} stamps from {name} to {(target != null ? target.name : "nothing")}");
    }

    // At most this many stamps, evenly picked along the way (the first and last kept).
    static Vector2[] Thin(List<Vector2> uvs, int most)
    {
        if (uvs == null || uvs.Count == 0 || most <= 0)
            return System.Array.Empty<Vector2>();
        if (uvs.Count <= most)
            return uvs.ToArray();
        var thinned = new Vector2[most];
        for (int i = 0; i < most; i++)
            thinned[i] = uvs[Mathf.RoundToInt(i * (uvs.Count - 1) / (float)(most - 1))];
        return thinned;
    }

    #endregion

    #region Network

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void DrawRpc(PaintStyle style, ushort[] packed) => AddOp(OpKind.Stamps, style, Unpack(packed));

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void EndStrokeRpc() => AddOp(OpKind.EndStroke, default, null);

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void ClearRpc() => AddOp(OpKind.Clear, default, null);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void PreviewRpc(ulong marker, PaintStyle lineStyle, ushort[] line, PaintStyle polygonStyle, ushort[] polygon, PaintStyle pointStyle, ushort[] points)
        => ApplyPreview(marker, lineStyle, line, polygonStyle, polygon, pointStyle, points);

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void ClearPreviewRpc(ulong marker) => RemovePreview(marker);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestHistoryRpc(RpcParams rpcParams = default)
    {
        if (m_History.Count > 0)
            StartCoroutine(SendHistory(rpcParams.Receive.SenderClientId, m_History.ToArray()));
    }

    // A few kilobytes a frame, so the reliable channel's queue never overflows.
    System.Collections.IEnumerator SendHistory(ulong client, (OpKind kind, PaintStyle style, ushort[] packed)[] history)
    {
        int stamps = 0, bytesThisFrame = 0;
        foreach (var (kind, style, packed) in history)
        {
            if (!IsSpawned || NetworkManager == null || !NetworkManager.ConnectedClients.ContainsKey(client))
                yield break;
            HistoryRpc((byte)kind, style, packed ?? System.Array.Empty<ushort>(), RpcTarget.Single(client, RpcTargetUse.Temp));
            int count = packed != null ? packed.Length / 2 : 0;
            stamps += count;
            bytesThisFrame += 32 + count * 4;
            if (bytesThisFrame >= 8000)
            {
                bytesThisFrame = 0;
                yield return null;
            }
        }
        Debug.Log($"[Paint] sent {name}'s marks ({stamps} stamps) to player {client}");
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void HistoryRpc(byte kind, PaintStyle style, ushort[] packed, RpcParams rpcParams = default)
        => AddOp((OpKind)kind, style, kind == (byte)OpKind.Stamps ? Unpack(packed) : null);

    static ushort[] Pack(Vector2[] uvs)
    {
        var packed = new ushort[uvs.Length * 2];
        for (int i = 0; i < uvs.Length; i++)
        {
            packed[i * 2] = (ushort)Mathf.RoundToInt(Mathf.Clamp01((uvs[i].x - k_PackMin) / k_PackRange) * 65535f);
            packed[i * 2 + 1] = (ushort)Mathf.RoundToInt(Mathf.Clamp01((uvs[i].y - k_PackMin) / k_PackRange) * 65535f);
        }
        return packed;
    }

    static Vector2[] Unpack(ushort[] packed)
    {
        var uvs = new Vector2[packed.Length / 2];
        for (int i = 0; i < uvs.Length; i++)
            uvs[i] = new Vector2(packed[i * 2] / 65535f * k_PackRange + k_PackMin, packed[i * 2 + 1] / 65535f * k_PackRange + k_PackMin);
        return uvs;
    }

    void AddOp(OpKind kind, PaintStyle style, Vector2[] uvs)
    {
        if (kind == OpKind.Clear)
        {
            m_Pending.Clear();
            m_History.Clear();
            m_HistoryStamps = 0;
        }
        m_Pending.Add((kind, style, uvs));
        if (!IsSpawned || IsServer)
            Remember(kind, style, uvs);
    }

    void Remember(OpKind kind, PaintStyle style, Vector2[] uvs)
    {
        if (kind == OpKind.Clear)
            return;
        var packed = uvs != null ? Pack(uvs) : null;
        m_History.Add((kind, style, packed));
        m_HistoryStamps += uvs?.Length ?? 0;
        while (m_HistoryStamps > m_MaxHistoryStamps && m_History.Count > 1)
        {
            m_HistoryStamps -= m_History[0].packed != null ? m_History[0].packed.Length / 2 : 0;
            m_History.RemoveAt(0);
        }
    }

    void ApplyPreview(ulong marker, PaintStyle lineStyle, ushort[] line, PaintStyle polygonStyle, ushort[] polygon, PaintStyle pointStyle, ushort[] points)
    {
        m_Previews[marker] = new[] { (lineStyle, Unpack(line)), (polygonStyle, Unpack(polygon)), (pointStyle, Unpack(points)) };
        m_PreviewDirty = true;
    }

    void RemovePreview(ulong marker)
    {
        if (m_Previews.Remove(marker))
            m_PreviewDirty = true;
    }

    #endregion

    #region Rendering

    void LateUpdate()
    {
        if (m_Pending.Count == 0 && !m_PreviewDirty)
            return;
        if (surface != null && (surface.paintCanvasMeters.x <= 0f || surface.paintCanvasMeters.y <= 0f))
            return; // its size isn't known yet (a painting copy just arrived): the marks wait
        if (!EnsureCanvas())
        {
            m_Pending.Clear();
            m_PreviewDirty = false;
            return;
        }
        m_Commands ??= new CommandBuffer { name = "Tunnel Paint" };
        m_Commands.Clear();
        int meshes = 0;

        if (m_Pending.Count > 0)
        {
            BeginTarget(m_CanvasTexture);
            foreach (var (kind, style, uvs) in m_Pending)
            {
                switch (kind)
                {
                    case OpKind.Clear:
                        m_Commands.ClearRenderTarget(true, true, Color.clear);
                        m_HasPaint = false;
                        break;
                    case OpKind.EndStroke:
                        m_Commands.ClearRenderTarget(true, false, Color.clear); // the stencil
                        break;
                    default:
                        DrawStamps(style, uvs, ref meshes);
                        if (style.mode != PaintMode.Eraser)
                            m_HasPaint = true;
                        break;
                }
            }
            m_Pending.Clear();
        }

        if (m_PreviewDirty)
        {
            m_PreviewDirty = false;
            BeginTarget(m_PreviewTexture);
            m_Commands.ClearRenderTarget(true, true, Color.clear);
            foreach (var layers in m_Previews.Values)
            {
                foreach (var (style, uvs) in layers)
                {
                    if (uvs == null || uvs.Length == 0)
                        continue;
                    DrawStamps(style, uvs, ref meshes);
                    m_Commands.ClearRenderTarget(true, false, Color.clear);
                }
            }
        }

        Graphics.ExecuteCommandBuffer(m_Commands);
        m_Surface.enabled = m_HasPaint || m_Previews.Count > 0;
    }

    void BeginTarget(RenderTexture target)
    {
        m_Commands.SetRenderTarget(target.colorBuffer, target.depthBuffer);
        // Canvas coordinates (0..1) fill the target, upright as the surface samples it (the command buffer adjusts
        // the projection for the graphics API and for drawing into a texture itself).
        m_Commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 1f));
    }

    // One quad per stamp: corners in canvas space, the stamp's corner (-1..1) and centre for the brush shader.
    void DrawStamps(PaintStyle style, Vector2[] uvs, ref int meshIndex)
    {
        if (uvs == null || uvs.Length == 0)
            return;
        var meters = canvasMeters;
        var half = new Vector2(style.diameter * 0.5f / meters.x, style.diameter * 0.5f / meters.y);
        var vertices = new Vector3[uvs.Length * 4];
        var corners = new Vector2[uvs.Length * 4];
        var centers = new Vector2[uvs.Length * 4];
        var colors = new Color32[uvs.Length * 4];
        var triangles = new int[uvs.Length * 6];
        for (int i = 0; i < uvs.Length; i++)
        {
            var c = uvs[i];
            for (int k = 0; k < 4; k++)
            {
                var corner = new Vector2(k == 1 || k == 2 ? 1f : -1f, k >= 2 ? 1f : -1f);
                vertices[i * 4 + k] = new Vector3(c.x + corner.x * half.x, c.y + corner.y * half.y, 0f);
                corners[i * 4 + k] = corner;
                centers[i * 4 + k] = c;
                colors[i * 4 + k] = style.color;
            }
            int v = i * 4, t = i * 6;
            triangles[t] = v; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
            triangles[t + 3] = v; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
        }
        if (meshIndex == m_MeshPool.Count)
            m_MeshPool.Add(new Mesh { name = "Paint Stamps", indexFormat = IndexFormat.UInt32 });
        var mesh = m_MeshPool[meshIndex++];
        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = corners;
        mesh.uv2 = centers;
        mesh.colors32 = colors;
        mesh.triangles = triangles;
        int pass = style.mode switch
        {
            PaintMode.Airbrush => k_AirPass,
            PaintMode.Eraser => k_ErasePass,
            _ => k_HardPass,
        };
        m_Commands.DrawMesh(mesh, Matrix4x4.identity, m_Brush, 0, pass);
    }

    bool EnsureCanvas()
    {
        if (m_CanvasTexture != null && m_CanvasTexture.IsCreated())
            return true;
        if (surface == null || m_BrushShader == null || m_OverlayMaterial == null)
        {
            Debug.LogWarning($"[Paint] {name} can't paint: missing its surface, brush shader or overlay material");
            return false;
        }

        var meters = canvasMeters;
        float width = meters.x / m_Texel, height = meters.y / m_Texel;
        float fit = Mathf.Min(1f, m_MaxCanvasSize / Mathf.Max(width, height));
        int w = Mathf.Max(64, Mathf.RoundToInt(width * fit)), h = Mathf.Max(64, Mathf.RoundToInt(height * fit));
        var depthFormat = SystemInfo.IsFormatSupported(GraphicsFormat.D24_UNorm_S8_UInt, GraphicsFormatUsage.Render)
            ? GraphicsFormat.D24_UNorm_S8_UInt : GraphicsFormat.D32_SFloat_S8_UInt;
        m_CanvasTexture = MakeTexture($"{name} Paint", w, h, depthFormat);
        m_PreviewTexture = MakeTexture($"{name} Pencil Preview", w, h, depthFormat);

        // Both start empty.
        var clear = new CommandBuffer { name = "Tunnel Paint Clear" };
        foreach (var texture in new[] { m_CanvasTexture, m_PreviewTexture })
        {
            clear.SetRenderTarget(texture.colorBuffer, texture.depthBuffer);
            clear.ClearRenderTarget(true, true, Color.clear);
        }
        Graphics.ExecuteCommandBuffer(clear);
        clear.Release();

        if (m_Brush == null)
            m_Brush = new Material(m_BrushShader) { name = "Paint Brush" };
        if (m_Surface == null)
        {
            m_Overlay = new Material(m_OverlayMaterial) { name = "Paint Overlay" };
            m_SurfaceMesh = surface.BuildPaintMesh(m_Inset);
            var go = new GameObject("Paint");
            go.transform.SetParent(surface.paintParent, false);
            go.AddComponent<MeshFilter>().sharedMesh = m_SurfaceMesh;
            m_Surface = go.AddComponent<MeshRenderer>();
            m_Surface.sharedMaterial = m_Overlay;
            m_Surface.shadowCastingMode = ShadowCastingMode.Off;
            m_Surface.receiveShadows = false;
        }
        m_Overlay.SetTexture(s_Canvas, m_CanvasTexture);
        m_Overlay.SetTexture(s_Preview, m_PreviewTexture);
        m_Overlay.SetVector(s_CanvasSize, new Vector4(w, h, 0f, 0f));
        Debug.Log($"[Paint] {name}: canvas {w} x {h} ({meters.x / w * 1000f:0.0} mm texels)");
        return true;
    }

    static RenderTexture MakeTexture(string name, int width, int height, GraphicsFormat depthFormat)
    {
        var texture = new RenderTexture(width, height, GraphicsFormat.R8G8B8A8_UNorm, depthFormat)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
        };
        texture.Create();
        return texture;
    }

    #endregion
}
