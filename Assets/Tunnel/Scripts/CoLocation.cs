using System;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// Puts players who share a real room into one shared space, using a QR code on the floor. The host's sighting of
/// the code defines where it is in the shared world. Every other headset that sees the code moves its XR Origin so
/// its own sighting lands on the host's, after which tunnels, panels and avatars line up with the real room for
/// everyone. Once two players are both aligned, each hides the other's avatar (keeping the name tag over their head),
/// so the other player's real body shows through passthrough, cut out of the tunnel by the headset's depth.
/// One scene object holds the shared anchor; aligning is started from the TS Menu's "Align to QR" button.
/// Then the AR marker fine-tunes it: whenever this (joining) headset and the host both see the AR marker, their
/// sightings at the same moment are compared, and once they have differed steadily for about a second this headset's
/// XR Origin glides by the turn (about the vertical only) and shift that bring its sighting onto the host's
/// (<see cref="ArMarkerRoomAlignment"/>). The host never moves; players who haven't aligned can be aligned this way too.
/// Players aligned to the code teleport together (<see cref="TunnelTeleport"/>): when one of them teleports, the host
/// sends the same step to every aligned player at the same place in the tunnel, so their real bodies keep standing
/// where their places in the tunnel are. A teleport moves players through the tunnel but not through the real room:
/// the code and the marker are measured without it, so nobody's alignment changes, and aligning puts a player at the
/// host's place in the tunnel. Two aligned players who are somehow at different places in the tunnel (for at least
/// a second and a half) see each other's avatars again, since their real bodies no longer stand where they are.
/// </summary>
public class CoLocation : NetworkBehaviour
{
    [SerializeField]
    QrFloorScanner m_Scanner;

    [SerializeField]
    [Tooltip("Hide the avatars of other players aligned to the same QR code (they're visible through passthrough).")]
    bool m_HideColocatedAvatars = true;

    // xyz: the code's centre on the floor in the shared world; w: its yaw in degrees.
    readonly NetworkVariable<Vector4> m_Anchor = new(Vector4.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_HasAnchor = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    NetworkList<ulong> m_AlignedClients;
    // How far each player has teleported (TunnelTeleport.offset), as they report it.
    NetworkList<PlayerOffset> m_Offsets;

    /// <summary>Two aligned players whose teleports differ by more than this see each other's avatars.</summary>
    const float k_SamePlace = 0.3f;
    /// <summary>How long two aligned players must be apart before their avatars show (a group teleport reaches each
    /// headset at a slightly different moment, and shouldn't flash them).</summary>
    const float k_ApartFor = 1.5f;
    readonly System.Collections.Generic.Dictionary<ulong, bool> m_AvatarHidden = new();
    readonly System.Collections.Generic.Dictionary<ulong, float> m_ApartSince = new();

    QrFloorScanner.Measurement? m_Pending;
    float m_NextAvatarCheck;

    // Fine alignment from the AR marker (joining players only).
    readonly ArMarkerRoomAlignment m_MarkerAlignment = new();
    float m_NextMarkerCorrection, m_NextMarkerLog, m_NextAlignedLog, m_LastHostMarker = float.NegativeInfinity;
    bool m_Gliding;
    int m_MarkerCorrections;
    float m_MarkerMovedMm, m_MarkerTurnedDeg;
    readonly System.Text.StringBuilder m_MarkerSteps = new();
    const float k_MarkerGlide = 0.5f, k_MarkerCooldown = 2f, k_MaxMarkerShiftWhenAligned = 1f, k_MarkerLogEvery = 2f;

    public static CoLocation instance { get; private set; }

    /// <summary>
    /// On a joining headset: whether the AR marker alignment has run this session and by how much (for the logs,
    /// e.g. when the canvas locks and tracking stops); null on the host or before the marker was seen by both.
    /// </summary>
    public static string markerAlignmentSummary { get; private set; }

    /// <summary>When (unscaled time) the AR marker last showed this joining headset's room lined up with the host's.</summary>
    public static float markerLinedUpAt { get; private set; } = float.NegativeInfinity;

    /// <summary>Progress and results, for the menu's status line.</summary>
    public event Action<string> statusChanged;

    public string status { get; private set; } = "Not aligned. Tape the QR code to the floor and tap Align to QR.";

    void Awake()
    {
        instance = this;
        m_AlignedClients = new NetworkList<ulong>(); // NetworkLists must be created in Awake
        m_Offsets = new NetworkList<PlayerOffset>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Anchor.OnValueChanged += OnAnchorChanged;
        TunnelTeleport.offsetChanged += ShareOffset;
        if (TunnelTeleport.offset != Vector3.zero)
            ShareOffset(TunnelTeleport.offset); // teleported before joining
        if (!IsServer)
        {
            ArMarkerCube.ownSighting += OnOwnMarker;
            ArMarkerCube.hostSighting += OnHostMarker;
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_Anchor.OnValueChanged -= OnAnchorChanged;
        TunnelTeleport.offsetChanged -= ShareOffset;
        ArMarkerCube.ownSighting -= OnOwnMarker;
        ArMarkerCube.hostSighting -= OnHostMarker;
        m_MarkerAlignment.Clear();
        m_MarkerAlignment.ResetCounts();
        m_MarkerCorrections = 0;
        m_MarkerMovedMm = m_MarkerTurnedDeg = 0f;
        m_MarkerSteps.Clear();
        markerAlignmentSummary = null;
        markerLinedUpAt = float.NegativeInfinity;
        ArMarkerCube.roomNote = null;
        // A new session starts unaligned.
        CoLocationState.isAligned = false;
        m_Pending = null;
        SetStatus("Not aligned.");
    }

    /// <summary>Look for the QR code and line this headset up with the shared room.</summary>
    public void Align()
    {
        if (m_Scanner == null || m_Scanner.isScanning)
            return;
        if (!IsSpawned)
        {
            SetStatus("Join or host a session first; aligning lines you up with the other players.");
            return;
        }

        m_Scanner.Scan(SetStatus, OnMeasured);
    }

    void OnMeasured(QrFloorScanner.Measurement? measurement)
    {
        if (measurement == null)
            return;

        if (!m_HasAnchor.Value)
        {
            if (IsServer)
            {
                // The host defines the shared room as it sees it; it doesn't move.
                var m = measurement.Value;
                var room = m.position - TunnelTeleport.offset; // where the code is with the host's teleports taken off
                m_Anchor.Value = new Vector4(room.x, room.y, room.z, m.yaw);
                m_HasAnchor.Value = true;
                MarkAligned();
                int players = NetworkManager.ConnectedClientsIds.Count;
                SetStatus($"Aligned: this headset sets the shared room ({m.samples} sightings, within {m.spread * 100f:0} cm). " +
                    (players > 1
                        ? $"{players} players are in this session; the others can align now."
                        : "You're the only player in this session: other headsets must join it before they align."));
                return;
            }

            m_Pending = measurement;
            SetStatus("Seen the code. Waiting for the host to align to it...");
            return;
        }

        ApplyAlignment(measurement.Value);
    }

    void OnAnchorChanged(Vector4 previous, Vector4 current)
    {
        if (m_Pending != null && m_HasAnchor.Value)
        {
            var pending = m_Pending.Value;
            m_Pending = null;
            ApplyAlignment(pending);
        }
    }

    /// <summary>Moves the XR Origin so this headset's sighting of the code coincides with the shared one.</summary>
    void ApplyAlignment(QrFloorScanner.Measurement measured)
    {
        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
            return;

        Vector4 anchor = m_Anchor.Value;
        var sharedPosition = new Vector3(anchor.x, anchor.y, anchor.z);
        float turn = Mathf.DeltaAngle(measured.yaw, anchor.w);
        var rotation = Quaternion.Euler(0f, turn, 0f);

        // Turn the origin about the measured point, then slide that point onto the shared one, plus however far the
        // host has teleported: everyone in the room is at the host's place in the tunnel, and teleports together.
        TunnelTeleport.Adopt(OffsetOf(NetworkManager.ServerClientId));
        var originTransform = origin.transform;
        Vector3 newPosition = sharedPosition + TunnelTeleport.offset + rotation * (originTransform.position - measured.position);
        originTransform.SetPositionAndRotation(newPosition, rotation * originTransform.rotation);

        MarkAligned();
        Debug.Log($"[QR] aligned: moved {(sharedPosition - measured.position).magnitude * 100f:0} cm, turned {turn:0.0} deg " +
            "(the AR marker, seen by this headset and the host together, fine-tunes this; its lines start \"marker alignment\")");
        SetStatus($"Aligned with the shared room ({measured.samples} sightings, within {measured.spread * 100f:0} cm), " +
            $"{NetworkManager.ConnectedClientsIds.Count} players in this session.");
    }

    #region Fine alignment from the AR marker

    void OnOwnMarker(ArMarkerSample sample)
    {
        if (m_Gliding)
            return;
        m_MarkerAlignment.AddOwn(sample);
        if (markerAlignmentSummary == null)
            markerAlignmentSummary = "marker alignment: not run yet (this headset has seen the marker, the host's sightings have not matched yet)";
        // Say so when this headset sees the marker but the host doesn't (nothing to align to).
        if (IsSpawned && !IsServer && Time.unscaledTime >= m_NextMarkerLog && Time.unscaledTime - m_LastHostMarker > 2f)
            LogMarkerWaiting(m_LastHostMarker < 0f ? "the host hasn't seen the marker yet" : $"the host hasn't seen the marker for {Time.unscaledTime - m_LastHostMarker:0} s");
    }

    void OnHostMarker(ArMarkerSample sample)
    {
        if (m_Gliding || !IsSpawned || IsServer)
            return;
        m_LastHostMarker = Time.unscaledTime;
        m_MarkerAlignment.AddHost(sample);
        if (Time.unscaledTime < m_NextMarkerCorrection)
            return;
        if (m_MarkerAlignment.TryCorrection(out var correction, out string why))
        {
            ApplyMarkerCorrection(correction);
        }
        else if (why != null && why.StartsWith("already aligned"))
        {
            markerLinedUpAt = Time.unscaledTime;
            if (Time.unscaledTime >= m_NextAlignedLog)
            {
                m_NextAlignedLog = Time.unscaledTime + 5f;
                string line = $"marker alignment: checked, nothing to correct: {why} (corrections this session {m_MarkerCorrections}, {MarkerTotals()})";
                markerAlignmentSummary = $"marker alignment: {m_MarkerCorrections} correction(s) this session ({MarkerTotals()}); last check {why}";
                Debug.Log("[QR] " + line);
                ArMarkerTracker.instance?.Log("room alignment: " + line);
            }
        }
        else if (Time.unscaledTime >= m_NextMarkerLog)
        {
            LogMarkerWaiting(why);
        }
    }

    // Every couple of seconds while waiting: why, and how the host's and this headset's sightings are pairing up.
    void LogMarkerWaiting(string why)
    {
        m_NextMarkerLog = Time.unscaledTime + k_MarkerLogEvery;
        var a = m_MarkerAlignment;
        string line = $"marker alignment: waiting ({why}); in the last {k_MarkerLogEvery:0} s: host sightings {a.hostReceived}, own {a.ownReceived}, " +
            $"paired {a.paired}, host's with no own sighting at that moment {a.unpaired}; corrections this session {m_MarkerCorrections} ({MarkerTotals()})";
        a.ResetCounts();
        Debug.Log("[QR] " + line);
        ArMarkerTracker.instance?.Log("room alignment: " + line);
    }

    string MarkerTotals() => m_MarkerCorrections == 0 ? "none yet" :
        $"moved {m_MarkerMovedMm:0} mm and turned {m_MarkerTurnedDeg:0.0} deg in all: {m_MarkerSteps}";

    // Moves this headset's XR Origin so its sighting of the marker lands on the host's: a turn about the vertical
    // through the sighting, then a shift; glided over half a second.
    void ApplyMarkerCorrection(ArMarkerRoomAlignment.Correction correction)
    {
        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
            return;
        var tableTop = FindAnyObjectByType<TableTop>();
        var table = tableTop != null ? new Pose(tableTop.transform.position, tableTop.transform.rotation) : Pose.identity;
        // The sightings have this player's teleport taken off (see ArMarkerCube), so it goes back on here.
        var pivot = table.position + table.rotation * correction.pivot + TunnelTeleport.offset;
        var target = table.position + table.rotation * correction.target + TunnelTeleport.offset;
        float shift = Vector3.Distance(pivot, target);
        if (shift > k_MaxMarkerShiftWhenAligned && CoLocationState.isAligned)
        {
            Debug.Log($"[QR] marker alignment: ignored a {shift * 100f:0} cm correction (aligned by the QR code already): {correction}");
            m_MarkerAlignment.Clear();
            m_NextMarkerCorrection = Time.unscaledTime + k_MarkerCooldown;
            return;
        }
        StartCoroutine(GlideOrigin(origin.transform, Quaternion.AngleAxis(correction.turn, Vector3.up), pivot, target, correction, shift));
    }

    System.Collections.IEnumerator GlideOrigin(Transform origin, Quaternion turn, Vector3 pivot, Vector3 target,
        ArMarkerRoomAlignment.Correction correction, float shift)
    {
        m_Gliding = true;
        Vector3 fromPosition = origin.position, toPosition = turn * (origin.position - pivot) + target;
        Quaternion fromRotation = origin.rotation, toRotation = turn * origin.rotation;
        for (float t = 0f; t < k_MarkerGlide; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / k_MarkerGlide);
            origin.SetPositionAndRotation(Vector3.Lerp(fromPosition, toPosition, k), Quaternion.Slerp(fromRotation, toRotation, k));
            yield return null;
        }
        origin.SetPositionAndRotation(toPosition, toRotation);
        m_Gliding = false;
        m_MarkerAlignment.Clear(); // earlier sightings are in the old frame
        m_NextMarkerCorrection = Time.unscaledTime + k_MarkerCooldown;
        bool wasAligned = CoLocationState.isAligned;
        MarkAligned();
        m_MarkerCorrections++;
        m_MarkerMovedMm += shift * 1000f;
        m_MarkerTurnedDeg += Mathf.Abs(correction.turn);
        m_MarkerSteps.Append(m_MarkerSteps.Length > 0 ? ", " : "").Append($"{shift * 1000f:0} mm / {correction.turn:0.0} deg");
        string note = $"Aligned to the host via the marker: moved {shift * 1000f:0} mm, turned {correction.turn:0.0} deg";
        markerAlignmentSummary = $"marker alignment: {m_MarkerCorrections} correction(s) this session ({MarkerTotals()})";
        ArMarkerCube.roomNote = note;
        Debug.Log($"[QR] {note} ({correction}; {(wasAligned ? "after the QR alignment" : "without a QR alignment")}); correction {m_MarkerCorrections} this session, " +
            $"{MarkerTotals()}; origin now {toPosition:F3}, yaw {toRotation.eulerAngles.y:0.0}");
        ArMarkerTracker.instance?.Log($"room alignment: {note} ({correction}); correction {m_MarkerCorrections} this session, {MarkerTotals()}");
        SetStatus(note + ".");
    }

    #endregion

    void MarkAligned()
    {
        CoLocationState.isAligned = true;
        SetAlignedRpc(true);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetAlignedRpc(bool aligned, RpcParams rpcParams = default)
    {
        ulong id = rpcParams.Receive.SenderClientId;
        if (aligned && !m_AlignedClients.Contains(id))
            m_AlignedClients.Add(id);
        else if (!aligned)
            m_AlignedClients.Remove(id);
    }

    void Update()
    {
        if (!IsSpawned || Time.unscaledTime < m_NextAvatarCheck)
            return;
        m_NextAvatarCheck = Time.unscaledTime + 0.5f;

        if (IsServer)
        {
            // Players who left are no longer aligned.
            for (int i = m_AlignedClients.Count - 1; i >= 0; i--)
            {
                if (!NetworkManager.ConnectedClients.ContainsKey(m_AlignedClients[i]))
                    m_AlignedClients.RemoveAt(i);
            }
            for (int i = m_Offsets.Count - 1; i >= 0; i--)
            {
                if (!NetworkManager.ConnectedClients.ContainsKey(m_Offsets[i].client))
                    m_Offsets.RemoveAt(i);
            }
        }

        foreach (var player in FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None))
        {
            if (player.IsLocalPlayer)
                continue;
            // An aligned player's real body shows through passthrough, so their avatar is hidden, unless one of you has
            // teleported away from the other: then the avatar shows where they are in the tunnel.
            bool samePlace = (OffsetOf(player.OwnerClientId) - TunnelTeleport.offset).magnitude < k_SamePlace;
            if (samePlace)
                m_ApartSince.Remove(player.OwnerClientId);
            else if (!m_ApartSince.TryGetValue(player.OwnerClientId, out float since))
                m_ApartSince[player.OwnerClientId] = Time.unscaledTime;
            else
                samePlace = Time.unscaledTime - since < k_ApartFor; // not apart for long enough yet
            bool hide = m_HideColocatedAvatars && CoLocationState.isAligned && m_AlignedClients.Contains(player.OwnerClientId) && samePlace;
            if (m_AvatarHidden.TryGetValue(player.OwnerClientId, out bool wasHidden) ? wasHidden != hide : hide)
                Debug.Log($"[Teleport] player {player.OwnerClientId}'s avatar {(hide ? "hidden: their real body is where they are in the tunnel" : "shown")} " +
                    $"(they have teleported {OffsetOf(player.OwnerClientId).magnitude:F2} m, this player {TunnelTeleport.offset.magnitude:F2} m)");
            m_AvatarHidden[player.OwnerClientId] = hide;
            var nameTag = player.GetComponentInChildren<PlayerNameTag>(true);
            foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
            {
                if (nameTag == null || !renderer.transform.IsChildOf(nameTag.transform))
                    renderer.forceRenderingOff = hide;
            }
        }
    }

    #region Teleports

    /// <summary>
    /// Called when this player teleports by <paramref name="step"/>: if other players aligned to the same code are at
    /// the same place in the tunnel, asks the host to move all of them (this player too) and returns true; otherwise
    /// returns false and this player moves alone.
    /// </summary>
    public bool TryTeleportGroup(Vector3 step)
    {
        if (!IsSpawned || !CoLocationState.isAligned)
            return false;
        foreach (var id in m_AlignedClients)
        {
            if (id != NetworkManager.LocalClientId && (OffsetOf(id) - TunnelTeleport.offset).magnitude < k_SamePlace)
            {
                TeleportGroupRpc(step);
                return true;
            }
        }
        return false;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TeleportGroupRpc(Vector3 step, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        var from = OffsetOf(sender);
        var group = new System.Collections.Generic.List<ulong> { sender };
        foreach (var id in m_AlignedClients)
        {
            if (id != sender && NetworkManager.ConnectedClients.ContainsKey(id) && (OffsetOf(id) - from).magnitude < k_SamePlace)
                group.Add(id);
        }
        Debug.Log($"[Teleport] player {sender} teleported {step.magnitude:F2} m: moving players {string.Join(", ", group)} together");
        MoveGroupRpc(step, sender, group.Count, RpcTarget.Group(group.ToArray(), RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void MoveGroupRpc(Vector3 step, ulong by, int players, RpcParams rpcParams = default) =>
        TunnelTeleport.MoveBy(step, by == NetworkManager.LocalClientId
            ? $"teleported with the {players - 1} other player{(players == 2 ? "" : "s")} in this room"
            : $"player {by} teleported, so this player, in the same room, goes too ({players} players)");

    void ShareOffset(Vector3 offset)
    {
        if (IsSpawned)
            SetOffsetRpc(offset);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetOffsetRpc(Vector3 offset, RpcParams rpcParams = default)
    {
        var entry = new PlayerOffset { client = rpcParams.Receive.SenderClientId, offset = offset };
        for (int i = 0; i < m_Offsets.Count; i++)
        {
            if (m_Offsets[i].client == entry.client)
            {
                m_Offsets[i] = entry;
                return;
            }
        }
        m_Offsets.Add(entry);
    }

    /// <summary>How far a player has teleported, as they last reported it (zero if they haven't).</summary>
    public Vector3 OffsetOf(ulong client)
    {
        if (client == NetworkManager.LocalClientId)
            return TunnelTeleport.offset;
        foreach (var entry in m_Offsets)
        {
            if (entry.client == client)
                return entry.offset;
        }
        return Vector3.zero;
    }

    public struct PlayerOffset : INetworkSerializable, IEquatable<PlayerOffset>
    {
        public ulong client;
        public Vector3 offset;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref client);
            serializer.SerializeValue(ref offset);
        }

        public bool Equals(PlayerOffset other) => client == other.client && offset == other.offset;
    }

    #endregion

    void SetStatus(string text)
    {
        status = text;
        statusChanged?.Invoke(text);
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (instance == this)
            instance = null;
    }
}
