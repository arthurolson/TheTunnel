using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using UnityLabs.SmartUX.Network;

/// <summary>
/// Sets the Tunnel Segment down on the physical floor reported by the headset (the floor plane from the room's
/// Space Setup). Runs on every headset; only moves the tunnel while nobody is holding it, and stops once the user
/// has adjusted the height by hand with the handle's Height button.
/// </summary>
public class TunnelFloorAnchor : MonoBehaviour
{
    [SerializeField]
    XRGrabInteractable m_Grab;

    [SerializeField]
    FloorLockGrabTransformer m_FloorLock;

    [SerializeField]
    ClaimableNetworkBehaviour m_Claimable;

    [SerializeField]
    [Tooltip("Ignore floor changes smaller than this (meters).")]
    float m_Tolerance = 0.01f;

    static readonly int k_FloorYProperty = Shader.PropertyToID("_TunnelFloorY");

    ARPlaneManager m_Planes;
    float? m_FloorHeight;

    void OnEnable()
    {
        m_Planes = FindAnyObjectByType<ARPlaneManager>();
        if (m_Planes == null)
        {
            // Plane detection lives on the XR Origin; add it there if the rig doesn't have it yet.
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null)
            {
                m_Planes = origin.gameObject.AddComponent<ARPlaneManager>();
                m_Planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            }
        }

        if (m_Planes != null)
            m_Planes.trackablesChanged.AddListener(OnPlanesChanged);
    }

    void OnDisable()
    {
        if (m_Planes != null)
            m_Planes.trackablesChanged.RemoveListener(OnPlanesChanged);
    }

    void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
    {
        float? floor = null;
        foreach (var plane in m_Planes.trackables)
        {
            if ((plane.classifications & PlaneClassifications.Floor) == 0)
                continue;
            float y = plane.transform.position.y;
            floor = floor.HasValue ? Mathf.Min(floor.Value, y) : y;
        }

        if (floor.HasValue)
        {
            m_FloorHeight = floor;
            // The occlusion shader measures "real surfaces above 2 m" (the ceiling) from here.
            Shader.SetGlobalFloat(k_FloorYProperty, floor.Value);
        }
    }

    void LateUpdate()
    {
        if (!m_FloorHeight.HasValue)
            return;
        if (m_FloorLock != null && m_FloorLock.heightAdjustedByUser)
            return;
        if (m_Grab != null && m_Grab.isSelected)
            return;
        if (m_Claimable != null && m_Claimable.isOwnedRemotely)
            return;

        var position = transform.position;
        if (Mathf.Abs(position.y - m_FloorHeight.Value) < m_Tolerance)
            return;
        position.y = m_FloorHeight.Value;
        transform.position = position;
    }
}
