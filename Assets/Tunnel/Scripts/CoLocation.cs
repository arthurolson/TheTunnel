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

    QrFloorScanner.Measurement? m_Pending;
    float m_NextAvatarCheck;

    // Fine alignment from the AR marker (joining players only).
    readonly ArMarkerRoomAlignment m_MarkerAlignment = new();
    float m_NextMarkerCorrection, m_NextMarkerLog;
    bool m_Gliding;
    const float k_MarkerGlide = 0.5f, k_MarkerCooldown = 2f, k_MaxMarkerShiftWhenAligned = 1f;

    public static CoLocation instance { get; private set; }

    /// <summary>Progress and results, for the menu's status line.</summary>
    public event Action<string> statusChanged;

    public string status { get; private set; } = "Not aligned. Tape the QR code to the floor and tap Align to QR.";

    void Awake()
    {
        instance = this;
        m_AlignedClients = new NetworkList<ulong>(); // NetworkLists must be created in Awake
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Anchor.OnValueChanged += OnAnchorChanged;
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
        ArMarkerCube.ownSighting -= OnOwnMarker;
        ArMarkerCube.hostSighting -= OnHostMarker;
        m_MarkerAlignment.Clear();
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
                m_Anchor.Value = new Vector4(m.position.x, m.position.y, m.position.z, m.yaw);
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

        // Turn the origin about the measured point, then slide that point onto the shared one.
        var originTransform = origin.transform;
        Vector3 newPosition = sharedPosition + rotation * (originTransform.position - measured.position);
        originTransform.SetPositionAndRotation(newPosition, rotation * originTransform.rotation);

        MarkAligned();
        Debug.Log($"[QR] aligned: moved {(sharedPosition - measured.position).magnitude * 100f:0} cm, turned {turn:0.0} deg");
        SetStatus($"Aligned with the shared room ({measured.samples} sightings, within {measured.spread * 100f:0} cm), " +
            $"{NetworkManager.ConnectedClientsIds.Count} players in this session.");
    }

    #region Fine alignment from the AR marker

    void OnOwnMarker(ArMarkerSample sample)
    {
        if (!m_Gliding)
            m_MarkerAlignment.AddOwn(sample);
    }

    void OnHostMarker(ArMarkerSample sample)
    {
        if (m_Gliding || !IsSpawned || IsServer)
            return;
        m_MarkerAlignment.AddHost(sample);
        if (Time.unscaledTime < m_NextMarkerCorrection)
            return;
        if (m_MarkerAlignment.TryCorrection(out var correction, out string why))
        {
            ApplyMarkerCorrection(correction);
        }
        else if (Time.unscaledTime >= m_NextMarkerLog && m_MarkerAlignment.pairs > 0)
        {
            m_NextMarkerLog = Time.unscaledTime + 2f;
            Debug.Log($"[QR] marker alignment: waiting ({why})");
        }
    }

    // Moves this headset's XR Origin so its sighting of the marker lands on the host's: a turn about the vertical
    // through the sighting, then a shift; glided over half a second.
    void ApplyMarkerCorrection(ArMarkerRoomAlignment.Correction correction)
    {
        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
            return;
        var tableTop = FindAnyObjectByType<TableTop>();
        var table = tableTop != null ? new Pose(tableTop.transform.position, tableTop.transform.rotation) : Pose.identity;
        var pivot = table.position + table.rotation * correction.pivot;
        var target = table.position + table.rotation * correction.target;
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
        string note = $"Aligned to the host via the marker: moved {shift * 1000f:0} mm, turned {correction.turn:0.0} deg";
        ArMarkerCube.roomNote = note;
        Debug.Log($"[QR] {note} ({correction}; {(wasAligned ? "after the QR alignment" : "without a QR alignment")}); origin now {toPosition:F3}, yaw {toRotation.eulerAngles.y:0.0}");
        ArMarkerTracker.instance?.Log($"room alignment: {note} ({correction})");
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
        }

        foreach (var player in FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None))
        {
            if (player.IsLocalPlayer)
                continue;
            bool hide = m_HideColocatedAvatars && CoLocationState.isAligned && m_AlignedClients.Contains(player.OwnerClientId);
            var nameTag = player.GetComponentInChildren<PlayerNameTag>(true);
            foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
            {
                if (nameTag == null || !renderer.transform.IsChildOf(nameTag.transform))
                    renderer.forceRenderingOff = hide;
            }
        }
    }

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
