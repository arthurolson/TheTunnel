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
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_Anchor.OnValueChanged -= OnAnchorChanged;
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
                SetStatus($"Aligned: this headset sets the shared room ({m.samples} sightings, within {m.spread * 100f:0} cm). Others can align now.");
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
        SetStatus($"Aligned with the shared room ({measured.samples} sightings, within {measured.spread * 100f:0} cm).");
    }

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
