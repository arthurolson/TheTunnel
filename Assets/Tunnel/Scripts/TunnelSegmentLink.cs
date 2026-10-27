using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Joins Tunnel Segments into one tunnel. A segment added with Add Segment is linked to the one before it and stays
/// snapped to that segment's far end: only the first segment of a tunnel can be moved (by its handles, or its height
/// button), and the rest follow it exactly, on every headset. Linked segments hide their handles, which would sit at
/// the joins. If a segment in the middle is removed, the ones after it become a tunnel of their own, where they are.
/// </summary>
public class TunnelSegmentLink : NetworkBehaviour
{
    [SerializeField]
    TunnelSegmentGeometry m_Geometry;

    [SerializeField]
    XRGrabInteractable m_Grab;

    [SerializeField]
    TunnelFloorAnchor m_FloorAnchor;

    [SerializeField]
    [Tooltip("Handle objects, hidden while this segment follows another.")]
    GameObject[] m_Handles;

    readonly NetworkVariable<NetworkObjectReference> m_Previous = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    static readonly List<TunnelSegmentLink> s_All = new();

    TunnelSegmentLink m_LocalPrevious; // when not connected
    bool? m_FollowingApplied;

    /// <summary>The segment this one is attached to the far end of, or null for the first segment of a tunnel.</summary>
    public TunnelSegmentLink previous
    {
        get
        {
            if (!IsSpawned)
                return m_LocalPrevious;
            return m_Previous.Value.TryGet(out var networkObject) ? networkObject.GetComponent<TunnelSegmentLink>() : null;
        }
    }

    /// <summary>The segment attached to this one's far end, if any.</summary>
    public TunnelSegmentLink next
    {
        get
        {
            foreach (var link in s_All)
            {
                if (link != this && link.previous == this)
                    return link;
            }

            return null;
        }
    }

    /// <summary>The last segment of the tunnel this one belongs to.</summary>
    public TunnelSegmentLink Tail()
    {
        var tail = this;
        for (int guard = 0; guard < 256; guard++)
        {
            var after = tail.next;
            if (after == null)
                break;
            tail = after;
        }

        return tail;
    }

    /// <summary>Where a segment attached to this one's far end goes.</summary>
    public Pose farEndPose => new(transform.TransformPoint(new Vector3(0f, 0f, m_Geometry.length)), transform.rotation);

    /// <summary>Server (or offline): attach this segment to the far end of <paramref name="before"/>.</summary>
    public void LinkTo(TunnelSegmentLink before)
    {
        if (IsSpawned)
        {
            if (IsServer)
                m_Previous.Value = before != null ? new NetworkObjectReference(before.NetworkObject) : default;
        }
        else
        {
            m_LocalPrevious = before;
        }
    }

    void OnEnable() => s_All.Add(this);

    void OnDisable() => s_All.Remove(this);

    void Update()
    {
        bool following = previous != null;
        if (following == m_FollowingApplied)
            return;
        m_FollowingApplied = following;

        // Followers are moved by the tunnel's first segment, not by hand or by the floor.
        if (m_Grab != null)
            m_Grab.enabled = !following;
        if (m_FloorAnchor != null)
            m_FloorAnchor.enabled = !following;
        foreach (var handle in m_Handles)
        {
            if (handle != null)
                handle.SetActive(!following);
        }
    }

    void LateUpdate()
    {
        // The first segment lays out the rest of its tunnel, in order, so nothing lags a frame behind.
        if (previous != null)
            return;
        var before = this;
        for (int guard = 0; guard < 256; guard++)
        {
            var after = before.next;
            if (after == null)
                break;
            var pose = before.farEndPose;
            after.transform.SetPositionAndRotation(pose.position, pose.rotation);
            before = after;
        }
    }
}
