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
/// The canvas doesn't keep following the marker (that made it jitter): the host settles it on a robust average of the
/// first good sightings (<see cref="ArMarkerCanvasLocker"/>), locks it there for everyone in the shared tabletop frame,
/// and every headset stops tracking and gives its cameras back. Pressing AR canvas again (it reads "Re-find canvas")
/// finds the panel again, for instance after it was moved; the paint stays on the canvas.
/// Every headset's good sightings count toward settling it (<see cref="ArMarkerCanvasFinder"/>), the host's first, and
/// a canvas within a few degrees of level or upright is made exactly so (canvasLevelWithin). When other players are in
/// the session, tracking stays on for a little while after the canvas settles (up to <see cref="k_AlignAfterLock"/>
/// seconds), until each joining player's room has been lined up with the host's by the marker
/// (<see cref="CoLocation"/>): without that, a joining player's canvas stays off by its floor QR alignment's error.
/// Sighting times are on the app's own clock when not connected and the shared network clock in a session; joining or
/// starting a session drops everything gathered on the old clock (<see cref="ArMarkerClock"/>).
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
    // The host's own latest sighting (not the shared pose), for the joining players' fine alignment (CoLocation).
    readonly NetworkVariable<ArMarkerSample> m_HostSighting = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<ArMarkerCanvasLock> m_SharedLock = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // Until when (shared clock) tracking stays on after the canvas settles, so joining players can line up their rooms.
    readonly NetworkVariable<double> m_AlignUntil = new(0.0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    ArMarkerCanvasLock m_LocalLock;
    readonly ArMarkerCanvasFinder m_Finder = new();
    double m_FindStart;
    readonly System.Collections.Generic.HashSet<ulong> m_LinedUp = new(); // on the host: players done lining up since the lock
    float m_LockedAt = float.NegativeInfinity; // unscaled time this headset saw the canvas lock
    bool m_LinedUpSent, m_AlignWaitLogged;
    int m_FromAnotherClockLogged;
    bool m_Tracking;

    /// <summary>After the canvas settles, tracking stays on at most this long (seconds) for joining players' room alignment.</summary>
    const float k_AlignAfterLock = 15f;
    ushort m_ShownGeneration = ushort.MaxValue;
    bool m_ShownLocked;
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

    /// <summary>This headset's own sightings of the marker (shared tabletop frame, shared clock), as they come.</summary>
    public static event System.Action<ArMarkerSample> ownSighting;

    /// <summary>On players other than the host: the host's own sightings, as they arrive.</summary>
    public static event System.Action<ArMarkerSample> hostSighting;

    /// <summary>A line about this headset's room alignment from the marker, for the status card (set by CoLocation).</summary>
    public static string roomNote;

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

    /// <summary>Where the canvas has settled, once it has.</summary>
    public ArMarkerCanvasLock canvasLock => IsSpawned ? m_SharedLock.Value : m_LocalLock;

    /// <summary>The canvas is shown and settled (marker tracking is off).</summary>
    public bool canvasLocked => shows == Shows.Canvas && canvasLock.locked;

    /// <summary>Sightings gathered toward settling the canvas (known on the host).</summary>
    public int lockProgress => m_Finder.count;

    /// <summary>A short status for the palette.</summary>
    public string shortStatus { get; private set; } = "AR marker cube: starting...";

    ulong self => IsSpawned ? NetworkManager.LocalClientId : 0;
    double Now => IsSpawned && NetworkManager != null ? ArMarkerClock.SharedNow(NetworkManager) : Time.realtimeSinceStartupAsDouble;

    /// <summary>
    /// The palette's AR rows (the host does this when connected): places the tracked marker showing
    /// <paramref name="what"/> if there's none, switches it to <paramref name="what"/> if it shows the other, finds a
    /// settled canvas again (Re-find canvas), or else removes it.
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
            if (what == Shows.Canvas && instance.canvasLock.locked)
            {
                instance.Refind();
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
        if (what == Shows.Canvas)
            Refind();
    }

    /// <summary>Unlocks the canvas and finds the panel again (on the host, or this headset alone).</summary>
    void Refind()
    {
        var unlocked = new ArMarkerCanvasLock { locked = false, generation = (ushort)(canvasLock.generation + 1) };
        if (IsSpawned)
            m_SharedLock.Value = unlocked;
        else
            m_LocalLock = unlocked;
        m_FindStart = Now;
        m_Finder.Begin(m_FindStart);
        m_LinedUp.Clear();
        if (IsSpawned)
            m_AlignUntil.Value = 0.0;
        ArMarkerTracker.Get().Log($"finding the canvas (search {unlocked.generation})");
    }

    // The clock sightings are stamped on changes (this headset joined, started or left a session): what was gathered
    // on the old one is dropped, or a sighting from the app's own clock, far ahead of the network clock, would stay
    // "fresh" for ever and block every newer one (as in the two-player test of 13 Nov 2026: the shared pose stayed on a
    // 20-minute-old sighting for 6 minutes).
    void ClockChanged(string why)
    {
        m_Arbiter.Reset();
        m_Chooser.Reset();
        m_Display.Reset();
        m_LocalShared = default;
        m_LastMode = ArMarkerRenderChooser.Mode.Hidden;
        m_LastSource = ulong.MaxValue;
        m_LastValid = false;
        m_FromAnotherClockLogged = 0;
        m_FindStart = Now;
        m_Finder.Begin(m_FindStart);
        m_LinedUp.Clear();
        m_LockedAt = float.NegativeInfinity;
        m_LinedUpSent = m_AlignWaitLogged = false;
        m_ShownGeneration = ushort.MaxValue;
        ArMarkerTracker.Get().Log($"clock changed ({why}): earlier sightings dropped; the shared clock is now {Now:0.000} s" +
            (shows == Shows.Canvas && !canvasLock.locked ? "; finding the canvas afresh" : ""));
    }

    // Marker tracking runs except while the canvas is locked.
    void SetTracking(bool on)
    {
        if (on == m_Tracking)
            return;
        m_Tracking = on;
        var tracker = ArMarkerTracker.Get();
        if (on)
        {
            m_Display.Reset();
            tracker.Begin();
        }
        else
        {
            tracker.Log("canvas locked: tracking off, cameras given back" + AlignmentNote());
            tracker.End();
        }
    }

    // How this headset's room stands against the host's, for the log.
    string AlignmentNote()
    {
        if (!IsSpawned)
            return "";
        if (IsServer)
            return "; this headset is the host: the shared room is its own";
        return "; " + (CoLocation.markerAlignmentSummary ?? "marker alignment: not run this session (this headset and the host never saw the marker at the same time)");
    }

    // Tracking runs while the canvas is found, and after it settles while joining players line up their rooms.
    bool WantTracking(double now)
    {
        if (!canvasLocked)
            return true;
        if (!IsSpawned || now >= m_AlignUntil.Value)
            return false;
        // A joining player stops once its own room is lined up; the host keeps going until every player is.
        return IsServer || !m_LinedUpSent;
    }

    // After the canvas settles: a joining player whose room the marker shows lined up tells the host; the host stops
    // tracking for everyone once every player has.
    void FollowAlignmentAfterLock(double now)
    {
        if (!IsSpawned || !canvasLocked || now >= m_AlignUntil.Value)
            return;
        if (!IsServer)
        {
            if (!m_LinedUpSent && CoLocation.markerLinedUpAt > m_LockedAt)
            {
                m_LinedUpSent = true;
                ArMarkerTracker.Get().Log("after the lock: this headset's room is lined up with the host's by the marker; telling the host" + AlignmentNote());
                LinedUpRpc();
            }
            else if (!m_AlignWaitLogged)
            {
                m_AlignWaitLogged = true;
                ArMarkerTracker.Get().Log($"after the lock: tracking stays on (up to {m_AlignUntil.Value - now:0} s) until the marker shows this headset's room lined up with the host's");
            }
            return;
        }
        int others = 0, done = 0;
        foreach (var client in NetworkManager.ConnectedClientsIds)
        {
            if (client == NetworkManager.LocalClientId)
                continue;
            others++;
            if (m_LinedUp.Contains(client))
                done++;
        }
        if (others == 0 || done == others)
        {
            m_AlignUntil.Value = now;
            ArMarkerTracker.Get().Log(others == 0 ? "after the lock: no other players now; tracking off" : $"after the lock: all {others} other player(s) lined up by the marker; tracking off");
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LinedUpRpc(RpcParams rpcParams = default)
    {
        ulong client = rpcParams.Receive.SenderClientId;
        if (m_LinedUp.Add(client))
            ArMarkerTracker.Get().Log($"after the lock: {PlayerName(client)} has lined up its room by the marker");
    }

    // On the host (or alone): a sighting toward settling the canvas, while it is being found.
    void GatherForLock(ArMarkerSample sample)
    {
        if (shows == Shows.Canvas && !canvasLock.locked)
            m_Finder.Add(sample);
    }

    // On the host (or alone): settle the canvas once some headset's sightings agree (the host's first).
    void TryLockCanvas(double now)
    {
        if (!m_Finder.TryLock(now, self, out ulong source, out var result))
            return;

        var measured = layout.CanvasFromMarker(ArMarkerMath.MarkerFromCube(result.pose));
        var panel = layout.Levelled(measured, result.pose.position, out float tilt, out string levelled);
        var locked = new ArMarkerCanvasLock
        {
            locked = true,
            position = panel.position,
            rotation = panel.rotation,
            sightings = (ushort)result.used,
            spreadMm = result.rmsMm,
            spreadDeg = result.rmsDeg,
            generation = canvasLock.generation,
        };
        int others = 0;
        if (IsSpawned)
        {
            foreach (var client in NetworkManager.ConnectedClientsIds)
                if (client != NetworkManager.LocalClientId)
                    others++;
            m_LinedUp.Clear();
            m_AlignUntil.Value = others > 0 ? now + k_AlignAfterLock : 0.0;
            m_SharedLock.Value = locked;
        }
        else
        {
            m_LocalLock = locked;
        }
        m_Finder.Begin(double.PositiveInfinity);
        var world = ToWorld(panel);
        // How far the levelling moved the canvas's far corner (the corner farthest from the marker).
        float corner = 0f;
        foreach (var c in new[] { new Vector3(-1f, -1f), new Vector3(-1f, 1f), new Vector3(1f, -1f), new Vector3(1f, 1f) })
        {
            var onPanel = Vector3.Scale(c, layout.size * 0.5f);
            corner = Mathf.Max(corner, Vector3.Distance(ArMarkerMath.Apply(measured, onPanel), ArMarkerMath.Apply(panel, onPanel)));
        }
        ArMarkerTracker.Get().Log($"canvas locked from {PlayerName(source)}'s {result}; panel centre {panel.position:F4} in the table frame " +
            $"(world {world.position:F4}), facing {(world.rotation * Vector3.back).ToString("F3")}, up {(world.rotation * Vector3.up).ToString("F3")}; " +
            (levelled != null ? $"made {levelled}: the sightings had it tilted {tilt:0.00} deg (far corner moved {corner * 1000f:0} mm)"
                : $"measured tilt kept (facing {(ToWorld(measured).rotation * Vector3.back).ToString("F3")})") +
            (others > 0 ? $"; tracking stays on up to {k_AlignAfterLock:0} s so the {others} other player(s) can line up their rooms by the marker" : ""));
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
        else
            m_HostSighting.OnValueChanged += OnHostSighting;
        // A cube placed before the session started was stamping its sightings on the app's own clock.
        if (m_Started)
        {
            ClockChanged($"this {shows} was placed before the session and is now shared, as {(IsServer ? "host" : "client")}");
            LogStart();
        }
        else
        {
            Begin();
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= OnClientLeft;
        m_HostSighting.OnValueChanged -= OnHostSighting;
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
        m_FindStart = Now;
        m_Finder.Begin(m_FindStart);
        LogStart();
        if (m_ShowCard)
            MakeCard();
    }

    void LogStart()
    {
        var tracker = ArMarkerTracker.Get();
        tracker.Log($"{shows} started on player {self} ({(IsSpawned ? (IsServer ? "host" : "client") : "not connected")}), clock {(IsSpawned ? "shared network" : "this app's own")} at {Now:0.000} s");
        tracker.Log($"canvas layout (from the host's settings.txt): {layout}");
        if (m_Canvas != null)
            m_Canvas.Configure(layout);
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
        m_Tracking = false;
        if (m_Card != null)
            Destroy(m_Card.gameObject);
    }

    void OnClientLeft(ulong client) => m_Arbiter.Forget(client);

    void OnHostSighting(ArMarkerSample previous, ArMarkerSample current)
    {
        if (current.valid)
            hostSighting?.Invoke(current);
    }

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
        ownSighting?.Invoke(sample);
        if (IsSpawned && IsServer)
            m_HostSighting.Value = sample;
        if (IsSpawned && !IsServer)
        {
            SightingRpc(sample);
            m_SightingsSent++;
        }
        else
        {
            m_Arbiter.Submit(sample);
            GatherForLock(sample);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void SightingRpc(ArMarkerSample sample, RpcParams rpcParams = default)
    {
        sample.source = rpcParams.Receive.SenderClientId;
        m_Arbiter.Submit(sample);
        GatherForLock(sample);
    }

    #endregion

    #region Every frame

    void Update()
    {
        if (!m_Started)
            return;
        double now = Now;
        bool locked = canvasLocked;
        if (canvasLock.generation != m_ShownGeneration || locked != m_ShownLocked)
        {
            m_ShownGeneration = canvasLock.generation;
            m_ShownLocked = locked;
            m_LockedAt = locked ? Time.unscaledTime : float.NegativeInfinity;
            m_LinedUpSent = m_AlignWaitLogged = false;
            if (locked)
                ArMarkerTracker.Get().Log($"canvas locked (search {canvasLock.generation}): from {canvasLock.sightings} sightings, " +
                    $"spread {canvasLock.spreadMm:0.0} mm, {canvasLock.spreadDeg:0.00} deg; panel centre {canvasLock.position:F4} in the table frame" + AlignmentNote());
        }
        FollowAlignmentAfterLock(now);
        SetTracking(WantTracking(now));
        if (locked)
        {
            ShowLocked(now);
            return;
        }

        // The host (or this headset alone) picks the shared pose.
        if (!IsSpawned || IsServer)
        {
            var published = m_Arbiter.Choose(now, out string change);
            if (shows == Shows.Canvas)
                TryLockCanvas(now);
            if (change != null)
                ArMarkerTracker.Get().Log($"shared pose: {change} (handoffs {m_Arbiter.handoffs})");
            if (m_Arbiter.fromAnotherClock != m_FromAnotherClockLogged)
            {
                ArMarkerTracker.Get().Log($"shared pose: dropped {m_Arbiter.fromAnotherClock - m_FromAnotherClockLogged} sighting(s) stamped on another clock");
                m_FromAnotherClockLogged = m_Arbiter.fromAnotherClock;
            }
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
                (m_Visual != null && m_Visual.gameObject.activeSelf ? $", drawn at world {m_Visual.position:F3}" : "") + PlayersLog(now) + CanvasLog());
            m_SightingsSent = 0;
        }
        UpdateCard(mode, sharedNow, now);
    }

    // The settled canvas: at its locked place, for everyone; no tracking, so no cube.
    void ShowLocked(double now)
    {
        SetActive(m_Visual, false);
        if (m_Canvas != null)
        {
            m_Canvas.PlaceCanvas(ToWorld(canvasLock.pose));
            m_Canvas.SetMarkerMaterial(m_SharedMaterial);
            SetActive(m_Canvas.transform, true);
        }
        if (Time.unscaledTime >= m_NextLog)
        {
            m_NextLog = Time.unscaledTime + 1f;
            ArMarkerTracker.Get().Log($"cube: canvas locked (search {canvasLock.generation}), " +
                (m_Tracking ? $"tracking still on for room alignment ({Mathf.Max(0f, (float)(m_AlignUntil.Value - now)):0} s left)" + PlayersLog(now) : "tracking off") + CanvasLog());
        }
        UpdateCard(ArMarkerRenderChooser.Mode.Hidden, shared, now);
    }

    static void SetActive(Transform t, bool on)
    {
        if (t != null && t.gameObject.activeSelf != on)
            t.gameObject.SetActive(on);
    }

    // On the host: each player's latest sighting, and how far the players' sightings of the same moment are from the
    // host's own (what the room alignment by the marker should bring to a few millimetres).
    string PlayersLog(double now)
    {
        if (!IsSpawned || !IsServer)
            return "";
        var latest = m_Arbiter.latest;
        if (latest.Count == 0)
            return "; players: no sightings";
        var text = new StringBuilder("; players:");
        bool hostFresh = latest.TryGetValue(self, out var host) && ArMarkerClock.Age(now, host.time) <= m_Arbiter.staleAfter;
        foreach (var pair in latest)
        {
            var sample = pair.Value;
            double age = ArMarkerClock.Age(now, sample.time);
            text.Append($" {pair.Key} {sample.CamerasText()} q {sample.quality:0.00} {(double.IsInfinity(age) ? "from another clock" : $"{age * 1000:0} ms old")}");
            if (pair.Key != self && hostFresh && age <= m_Arbiter.staleAfter && System.Math.Abs(sample.time - host.time) <= 0.1)
                text.Append($" ({Vector3.Distance(sample.position, host.position) * 1000f:0} mm from the host's, {System.Math.Abs(sample.time - host.time) * 1000:0} ms apart)");
            text.Append(',');
        }
        text.Length--;
        return text.ToString();
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
        if (shows == Shows.Canvas)
            text.Append('\n').Append(CanvasState());
        if (!string.IsNullOrEmpty(roomNote))
            text.Append("\n<size=80%>").Append(roomNote).Append("</size>");
        if (tracker != null && !canvasLocked && (!tracker.active || !tracker.status.StartsWith("Tracking:")))
            text.Append("\n<size=80%>").Append(tracker.status).Append("</size>");
        if (m_Card != null)
            m_Card.text = text.ToString();

        if (canvasLocked)
        {
            shortStatus = "AR canvas: " + CanvasState().Replace("<b>", "").Replace("</b>", "");
            if (m_Card != null)
                m_Card.text = "<b>AR canvas</b>\n" + CanvasState();
            return;
        }
        shortStatus = (shows == Shows.Canvas ? "Finding the canvas... " : "") + $"{(shows == Shows.Canvas ? "AR canvas" : "AR cube")}: sees {cameras}; shared from {(sharedNow.valid ? PlayerName(sharedNow.source) : "nobody")}; drawing {mode}." +
            (tracker != null && (!tracker.active || !tracker.status.StartsWith("Tracking:")) ? " " + tracker.status : "");
    }

    string CanvasState()
    {
        var state = canvasLock;
        if (state.locked)
            return $"Canvas <b>locked</b> from {state.sightings} sightings (spread {state.spreadMm:0.0} mm, {state.spreadDeg:0.0} deg); " +
                (m_Tracking ? "lining up the players' rooms by the marker: keep it in view. " : "tracking off. ") + "If the panel moves, press Re-find canvas.";
        bool host = !IsSpawned || IsServer;
        return "<b>Finding the canvas...</b> hold the panel still in view of both cameras" +
            (host ? $" ({m_Finder.count} of {m_Finder.needed} good sightings)" : "");
    }

    // With the player's id: two headsets signed in to the same account have the same name.
    string PlayerName(ulong client)
    {
        if (!IsSpawned)
            return "this headset";
        string who = client == NetworkManager.LocalClientId ? "you" : null;
        foreach (var player in FindObjectsByType<XRINetworkPlayer>())
        {
            if (player.OwnerClientId == client && !string.IsNullOrEmpty(player.playerName))
                return who != null ? $"you ({player.playerName}, player {client})" : $"{player.playerName} (player {client})";
        }
        return who != null ? $"you (player {client})" : $"player {client}";
    }

    #endregion
}
