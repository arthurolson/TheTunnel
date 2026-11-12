using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// A tracking test: a wireframe cube, 60 mm on a side, standing on a printed 60 mm artoolkitX marker and following
/// it as it moves and turns, for every player. Started and removed with the palette's "AR marker cube" row.
///
/// Every headset tracks the marker with both of its passthrough cameras (<see cref="ArMarkerTracker"/>) and sends its
/// fresh sightings, stamped with when the images were taken and how good they are, in the shared tabletop frame. The
/// host publishes the best fresh sighting as the shared pose, switching to another headset only after it has been
/// clearly better for a while (<see cref="ArMarkerArbiter"/>). Each headset draws the cube at its own sighting when
/// that is fresh, steady and close to the shared pose, and at the shared pose otherwise (<see cref="ArMarkerRenderChooser"/>),
/// blending across every switch so the cube doesn't jump; the cube hides when every sighting is stale. Gameplay uses
/// the shared pose (<see cref="sharedPose"/>).
/// The edges are green while this headset draws its own sighting and blue while it draws the shared pose. A card
/// below the view says which cameras see the marker, which player supplies the shared pose, and which pose is drawn.
/// Works on its own (one headset) when not connected.
///
/// Canvas mode (the palette's "AR canvas"): the same tracked object shows, instead of the cube, the virtual canvas
/// (<see cref="ArMarkerCanvas"/>) lying on the physical panel the marker is taped to, at the panel's real size, with
/// its own shared paint (<see cref="TunnelSegmentPaint"/>) that the virtual marker paints on inside the paint area and
/// that moves with the panel. Where the marker sits on the panel comes from the host's files/ARMarker/settings.txt
/// (<see cref="ArMarkerCanvasLayout"/>) and is shared with everyone. Copy to wall copies the paint area.
/// </summary>
public class ArMarkerCube : NetworkBehaviour
{
    /// <summary>What the tracked marker carries.</summary>
    public enum Shows : byte { Cube = 0, Canvas = 1 }

    [SerializeField]
    [Tooltip("Moved to the drawn pose: the cube's base is centred on the marker, its up out of the marker.")]
    Transform m_Visual;

    [SerializeField] Renderer[] m_Edges;
    [SerializeField] Material m_LocalMaterial;
    [SerializeField] Material m_SharedMaterial;

    [SerializeField]
    [Tooltip("Show the status card below the view.")]
    bool m_ShowCard = true;

    [Header("Canvas")]
    [SerializeField] ArMarkerCanvas m_Canvas;
    [SerializeField] TunnelSegmentPaint m_Paint;
    [SerializeField]
    [Tooltip("Tunnel/Paint Overlay.")]
    Material m_PaintOverlay;
    [SerializeField]
    [Tooltip("Tunnel/Paint Brush.")]
    Shader m_PaintBrush;
    [SerializeField]
    [Tooltip("Size of a canvas texel (meters).")]
    float m_PaintTexel = 0.0012f;

    readonly NetworkVariable<ArMarkerSample> m_Shared = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_SharedShows = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<ArMarkerCanvasLayout> m_SharedLayout = new(ArMarkerCanvasLayout.Default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    Shows m_LocalShows;
    ArMarkerCanvasLayout m_LocalLayout = ArMarkerCanvasLayout.Default;

    readonly ArMarkerArbiter m_Arbiter = new();
    readonly ArMarkerRenderChooser m_Chooser = new();
    readonly ArMarkerDisplay m_Display = new();
    ArMarkerSample m_LocalShared; // the shared pose when not connected
    ArMarkerRenderChooser.Mode m_LastMode = ArMarkerRenderChooser.Mode.Hidden;
    ulong m_LastSource = ulong.MaxValue;
    bool m_LastValid;
    bool m_Started;
    Transform m_Table;
    TextMeshPro m_Card;
    float m_NextCard, m_NextLog;
    int m_SightingsSent;

    public static ArMarkerCube instance { get; private set; }

    /// <summary>The shared pose for gameplay (the cube's base centre and turn in the world), and whether it's fresh.</summary>
    public Pose sharedPose => ToWorld(shared.pose);
    public bool sharedValid => shared.valid && Now - shared.time <= m_Chooser.sharedFreshFor;
    public ArMarkerSample shared => IsSpawned ? m_Shared.Value : m_LocalShared;

    /// <summary>Whether it shows the cube or the canvas.</summary>
    public Shows shows => IsSpawned ? (Shows)m_SharedShows.Value : m_LocalShows;

    /// <summary>The canvas's shared paint (painted inside the paint area), or null in cube mode.</summary>
    public TunnelSegmentPaint canvasPaint => shows == Shows.Canvas ? m_Paint : null;

    /// <summary>The canvas's paint area in meters (the size a copy of its painting should have).</summary>
    public Vector2 canvasPaintMeters => layout.paintArea;

    public ArMarkerCanvasLayout layout => IsSpawned ? m_SharedLayout.Value : m_LocalLayout;

    /// <summary>A short status for the palette.</summary>
    public string shortStatus { get; private set; } = "AR marker cube: starting...";

    ulong self => IsSpawned ? NetworkManager.LocalClientId : 0;
    double Now => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.realtimeSinceStartupAsDouble;

    /// <summary>
    /// The palette's AR rows (the host does this when connected): places the tracked marker showing
    /// <paramref name="what"/> if there's none, switches it to <paramref name="what"/> if it shows the other, or
    /// removes it if it already shows <paramref name="what"/>.
    /// </summary>
    public static void Toggle(GameObject prefab, bool networked, Shows what = Shows.Cube)
    {
        if (instance != null)
        {
            if (instance.shows != what)
            {
                instance.SetShows(what);
                Debug.Log($"[ARMarker] now showing the {what}");
                return;
            }
            if (networked && instance.IsSpawned)
                instance.NetworkObject.Despawn(true);
            else
                Destroy(instance.gameObject);
            Debug.Log($"[ARMarker] {what} removed");
            return;
        }
        if (prefab == null)
        {
            Debug.LogWarning("[ARMarker] can't place the cube: its prefab is missing");
            return;
        }
        // Set up before it wakes, as the palette is.
        var holder = new GameObject("AR Marker Holder");
        holder.SetActive(false);
        var placed = Instantiate(prefab, holder.transform);
        var component = placed.GetComponent<ArMarkerCube>();
        var layout = ArMarkerCanvasLayout.Load(SettingsFile, line => Debug.Log("[ARMarker] " + line));
        component.m_LocalShows = what;
        component.m_LocalLayout = layout;
        component.m_SharedShows.Value = (byte)what;
        component.m_SharedLayout.Value = layout;
        placed.transform.SetParent(null, false);
        Destroy(holder);
        if (networked && placed.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
        Debug.Log($"[ARMarker] {what} placed; canvas layout: {layout}");
    }

    static string SettingsFile => System.IO.Path.Combine(Application.persistentDataPath, "ARMarker", "settings.txt");

    void SetShows(Shows what)
    {
        if (IsSpawned)
            m_SharedShows.Value = (byte)what;
        else
            m_LocalShows = what;
    }

    void Awake()
    {
        instance = this;
        if (m_Visual != null)
            m_Visual.gameObject.SetActive(false);
        if (m_Canvas != null)
        {
            m_Canvas.gameObject.SetActive(false);
            if (m_Paint != null)
                m_Paint.UseSurface(m_Canvas, m_PaintOverlay, m_PaintBrush, m_PaintTexel);
        }
    }

    void Start()
    {
        // Not connected: run on this headset alone.
        if (!IsSpawned)
            Begin();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
            NetworkManager.OnClientDisconnectCallback += OnClientLeft;
        Begin();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= OnClientLeft;
        Finish();
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        Finish();
        if (instance == this)
            instance = null;
        base.OnDestroy();
    }

    void Begin()
    {
        if (m_Started)
            return;
        m_Started = true;
        var table = FindAnyObjectByType<TableTop>();
        m_Table = table != null ? table.transform : null;
        var tracker = ArMarkerTracker.Get();
        tracker.sighted += OnSighted;
        tracker.Begin();
        tracker.Log($"{shows} started on player {self} ({(IsSpawned ? (IsServer ? "host" : "client") : "not connected")})");
        tracker.Log($"canvas layout (from the host's settings.txt): {layout}");
        if (m_Canvas != null)
            m_Canvas.Configure(layout);
        if (m_ShowCard)
            MakeCard();
    }

    void Finish()
    {
        if (!m_Started)
            return;
        m_Started = false;
        var tracker = ArMarkerTracker.instance;
        if (tracker != null)
        {
            tracker.sighted -= OnSighted;
            tracker.End();
        }
        if (m_Card != null)
            Destroy(m_Card.gameObject);
    }

    void OnClientLeft(ulong client) => m_Arbiter.Forget(client);

    #region Sightings

    void OnSighted(ArMarkerTracker.Sighting sighting)
    {
        var sample = new ArMarkerSample
        {
            position = sighting.cubeInTable.position,
            rotation = sighting.cubeInTable.rotation,
            time = Now - sighting.age,
            quality = sighting.quality,
            cameras = sighting.cameras,
            source = self,
            valid = true,
        };
        m_Chooser.AddLocal(sample);
        if (IsSpawned && !IsServer)
        {
            SightingRpc(sample);
            m_SightingsSent++;
        }
        else
        {
            m_Arbiter.Submit(sample);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void SightingRpc(ArMarkerSample sample, RpcParams rpcParams = default)
    {
        sample.source = rpcParams.Receive.SenderClientId;
        m_Arbiter.Submit(sample);
    }

    #endregion

    #region Every frame

    void Update()
    {
        if (!m_Started)
            return;
        double now = Now;

        // The host (or this headset alone) picks the shared pose.
        if (!IsSpawned || IsServer)
        {
            var published = m_Arbiter.Choose(now, out string change);
            if (change != null)
                ArMarkerTracker.Get().Log($"shared pose: {change} (handoffs {m_Arbiter.handoffs})");
            if (IsSpawned)
            {
                if (!published.Equals(m_Shared.Value))
                    m_Shared.Value = published;
            }
            else
            {
                m_LocalShared = published;
            }
        }

        var sharedNow = shared;
        if (sharedNow.valid != m_LastValid || (sharedNow.valid && sharedNow.source != m_LastSource))
        {
            ArMarkerTracker.Get().Log(sharedNow.valid
                ? $"shared pose now from {PlayerName(sharedNow.source)} (quality {sharedNow.quality:0.00}, {sharedNow.CamerasText()}, handoffs {sharedNow.handoffs})"
                : "shared pose: none fresh");
            m_LastValid = sharedNow.valid;
            m_LastSource = sharedNow.source;
        }
        m_Chooser.SetShared(sharedNow);
        var mode = m_Chooser.Update(now, self, out var target);
        if (mode != m_LastMode)
        {
            ArMarkerTracker.Get().Log($"drawing: {m_LastMode} -> {mode} (local fresh {m_Chooser.localFresh}, shared fresh {m_Chooser.sharedFresh}, " +
                $"agree {m_Chooser.agree}, {m_Chooser.disagreement * 1000f:0} mm apart)");
            m_LastMode = mode;
        }

        bool canvasMode = shows == Shows.Canvas && m_Canvas != null;
        if (mode == ArMarkerRenderChooser.Mode.Hidden)
        {
            SetActive(m_Visual, false);
            SetActive(m_Canvas != null ? m_Canvas.transform : null, false);
            m_Display.Reset();
        }
        else
        {
            string key = mode == ArMarkerRenderChooser.Mode.Local ? "local" : "shared " + sharedNow.source;
            // The canvas reaches half a metre from the marker, so a small turn moves its far side a lot: steadier.
            m_Display.minCutoff = canvasMode ? 1f : 2f;
            var drawn = m_Display.Step(target, key, Time.deltaTime);
            var world = ToWorld(drawn);
            var material = mode == ArMarkerRenderChooser.Mode.Local ? m_LocalMaterial : m_SharedMaterial;
            if (m_Visual != null)
            {
                m_Visual.SetPositionAndRotation(world.position, world.rotation);
                SetActive(m_Visual, !canvasMode);
                foreach (var edge in m_Edges)
                    if (edge != null && edge.sharedMaterial != material)
                        edge.sharedMaterial = material;
            }
            if (m_Canvas != null)
            {
                if (canvasMode)
                {
                    m_Canvas.Place(ArMarkerMath.MarkerFromCube(world));
                    m_Canvas.SetMarkerMaterial(material);
                }
                SetActive(m_Canvas.transform, canvasMode);
            }
        }

        if (Time.unscaledTime >= m_NextLog)
        {
            m_NextLog = Time.unscaledTime + 1f;
            var tracker = ArMarkerTracker.Get();
            var local = m_Chooser.local;
            tracker.Log($"cube: drawing {mode}, local {(m_Chooser.localFresh ? $"{local.CamerasText()} q {local.quality:0.00} {(now - local.time) * 1000:0} ms old at {local.position:F3}" : "stale")}, " +
                $"shared {(sharedNow.valid ? $"from {PlayerName(sharedNow.source)} q {sharedNow.quality:0.00} {(now - sharedNow.time) * 1000:0} ms old at {sharedNow.position:F3}" : "none")}, " +
                $"apart {m_Chooser.disagreement * 1000f:0} mm, sent {m_SightingsSent}" +
                (m_Visual != null && m_Visual.gameObject.activeSelf ? $", drawn at world {m_Visual.position:F3}" : "") + CanvasLog());
            m_SightingsSent = 0;
        }
        UpdateCard(mode, sharedNow, now);
    }

    static void SetActive(Transform t, bool on)
    {
        if (t != null && t.gameObject.activeSelf != on)
            t.gameObject.SetActive(on);
    }

    // The canvas's pose, and whether each virtual marker's tip is touching it, for the log.
    string CanvasLog()
    {
        if (shows != Shows.Canvas || m_Canvas == null)
            return "";
        if (!m_Canvas.gameObject.activeInHierarchy)
            return "; canvas hidden";
        var t = m_Canvas.transform;
        var text = new StringBuilder($"; canvas centre {t.position:F3}, facing {(-t.forward).ToString("F2")}, up {t.up.ToString("F2")}");
        foreach (var marker in TunnelMarker.all)
        {
            if (marker == null || marker.docked)
                continue;
            bool touching = m_Canvas.Touching(marker.tip, marker.diameter * 0.5f, out float distance, out bool over);
            text.Append($"; marker tip {distance * 1000f:0} mm from the canvas, {(over ? "over the paint area" : "outside the paint area")}, touching {(touching ? "YES" : "no")}");
        }
        return text.ToString();
    }

    Pose ToWorld(Pose inTable) =>
        m_Table != null ? ArMarkerMath.Mul(new Pose(m_Table.position, m_Table.rotation), inTable) : inTable;

    #endregion

    #region Status card

    void MakeCard()
    {
        var go = new GameObject("AR Marker Status");
        m_Card = go.AddComponent<TextMeshPro>();
        m_Card.fontSize = 0.22f;
        m_Card.rectTransform.sizeDelta = new Vector2(0.62f, 0.2f);
        m_Card.alignment = TextAlignmentOptions.TopLeft;
        m_Card.textWrappingMode = TextWrappingModes.Normal;
        m_Card.color = Color.white;
        m_Card.richText = true;
        m_Card.text = shortStatus;
    }

    void UpdateCard(ArMarkerRenderChooser.Mode mode, ArMarkerSample sharedNow, double now)
    {
        var head = Camera.main != null ? Camera.main.transform : null;
        if (m_Card != null && head != null)
        {
            // Below the view, following the head lazily.
            var flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f)
                flatForward = head.up;
            flatForward.Normalize();
            var want = head.position + flatForward * 0.7f + Vector3.down * 0.3f;
            var card = m_Card.transform;
            card.position = Vector3.Lerp(card.position, want, (card.position - want).sqrMagnitude > 1f ? 1f : Time.deltaTime * 4f);
            card.rotation = Quaternion.LookRotation(card.position - head.position, Vector3.up);
        }

        if (Time.unscaledTime < m_NextCard)
            return;
        m_NextCard = Time.unscaledTime + 0.2f;

        var tracker = ArMarkerTracker.instance;
        var text = new StringBuilder();
        text.Append(shows == Shows.Canvas ? "<b>AR canvas</b>" : "<b>AR marker cube</b>").Append("  <size=80%>(60 mm marker, barcode 1)</size>\n");
        string cameras = m_Chooser.localFresh ? m_Chooser.local.CamerasText() : "none";
        text.Append("This headset sees it with: <b>").Append(cameras).Append("</b>");
        if (m_Chooser.localFresh)
            text.Append($"  quality {m_Chooser.local.quality:0.00}");
        text.Append('\n');
        if (sharedNow.valid)
        {
            text.Append("Shared pose from: <b>").Append(PlayerName(sharedNow.source)).Append("</b>")
                .Append($" ({sharedNow.CamerasText()}, q {sharedNow.quality:0.00}, {(now - sharedNow.time) * 1000:0} ms old, {sharedNow.handoffs} handoffs)\n");
        }
        else
        {
            text.Append("Shared pose: <b>none fresh</b>\n");
        }
        text.Append("Drawing: ").Append(mode switch
        {
            ArMarkerRenderChooser.Mode.Local => "<color=#4F4><b>LOCAL pose</b></color> (green)",
            ArMarkerRenderChooser.Mode.Shared => "<color=#5CF><b>SHARED pose</b></color> (blue)",
            _ => "<color=#AAA><b>hidden</b></color> (all stale)",
        });
        if (mode != ArMarkerRenderChooser.Mode.Hidden && !m_Chooser.agree)
            text.Append($"  <color=#FA4>local is {m_Chooser.disagreement * 100f:0.0} cm off</color>");
        if (tracker != null && (!tracker.active || !tracker.status.StartsWith("Tracking:")))
            text.Append("\n<size=80%>").Append(tracker.status).Append("</size>");
        if (m_Card != null)
            m_Card.text = text.ToString();

        shortStatus = $"{(shows == Shows.Canvas ? "AR canvas" : "AR cube")}: sees {cameras}; shared from {(sharedNow.valid ? PlayerName(sharedNow.source) : "nobody")}; drawing {mode}." +
            (tracker != null && (!tracker.active || !tracker.status.StartsWith("Tracking:")) ? " " + tracker.status : "");
    }

    string PlayerName(ulong client)
    {
        if (!IsSpawned)
            return "this headset";
        string who = client == NetworkManager.LocalClientId ? "you" : null;
        foreach (var player in FindObjectsByType<XRINetworkPlayer>())
        {
            if (player.OwnerClientId == client && !string.IsNullOrEmpty(player.playerName))
                return who != null ? $"you ({player.playerName})" : player.playerName;
        }
        return who ?? $"player {client}";
    }

    #endregion
}
