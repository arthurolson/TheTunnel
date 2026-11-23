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
/// Space Setup). A headset with no Space Setup floor (the room was never scanned, or it hasn't loaded after a few
/// seconds) uses the floor the headset itself tracks instead, and switches to the Space Setup floor if one appears.
/// Runs on every headset; only moves the tunnel while nobody is holding it, and stops once the user has adjusted
/// the height by hand with the handle's Height button.
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

    [SerializeField]
    [Tooltip("Seconds to wait for the Space Setup floor before using the headset's own tracked floor.")]
    float m_SpaceSetupWait = 5f;

    ARPlaneManager m_Planes;
    XROrigin m_Origin;
    float? m_FloorHeight;
    float m_EnabledAt;
    string m_LoggedSource;

    void OnEnable()
    {
        m_EnabledAt = Time.time;
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
            m_FloorHeight = floor;
    }

    // The floor to stand the tunnel on: the Space Setup floor, or after a few seconds without one, the headset's own
    // tracked floor (the XR Origin sits on it).
    bool TryFloor(out float floor, out string source)
    {
        if (m_FloorHeight.HasValue)
        {
            floor = m_FloorHeight.Value;
            source = "the Space Setup floor";
            return true;
        }
        floor = 0f;
        source = null;
        if (Time.time - m_EnabledAt < m_SpaceSetupWait)
            return false;
        if (m_Origin == null)
            m_Origin = FindAnyObjectByType<XROrigin>();
        // Only in a headset tracking from the floor (in the Editor, or tracking from the head, the rig isn't on it).
        if (m_Origin == null || m_Origin.Origin == null || !UnityEngine.XR.XRSettings.isDeviceActive
            || m_Origin.CurrentTrackingOriginMode != UnityEngine.XR.TrackingOriginModeFlags.Floor)
        {
            if (m_LoggedSource == null)
            {
                m_LoggedSource = "none";
                Debug.Log($"[Floor] {name}: no Space Setup floor after {m_SpaceSetupWait:F0} s, and the headset's tracked floor isn't known " +
                    $"(device active {UnityEngine.XR.XRSettings.isDeviceActive}, tracking {(m_Origin != null ? m_Origin.CurrentTrackingOriginMode.ToString() : "no XR Origin")}); the tunnel stays at {transform.position.y:F2}");
            }
            return false;
        }
        floor = m_Origin.Origin.transform.position.y;
        source = $"the headset's tracked floor (no Space Setup floor)";
        return true;
    }

    void LateUpdate()
    {
        if (!TryFloor(out float floor, out string source))
            return;
        if (source != m_LoggedSource)
        {
            m_LoggedSource = source;
            Debug.Log($"[Floor] {name} stands on {source} at {floor:F2}");
        }
        if (m_FloorLock != null && m_FloorLock.heightAdjustedByUser)
            return;
        if (m_Grab != null && m_Grab.isSelected)
            return;
        if (m_Claimable != null && m_Claimable.isOwnedRemotely)
            return;

        var position = transform.position;
        if (Mathf.Abs(position.y - floor) < m_Tolerance)
            return;
        position.y = floor;
        transform.position = position;
    }
}
