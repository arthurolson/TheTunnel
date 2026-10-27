using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Shows a button over the palm of a tracked hand while that palm faces the viewer, facing the viewer, and hides it
/// when the palm turns away (with a little hysteresis so it doesn't flicker). Used by the palm buttons.
/// </summary>
public static class HandPalm
{
    static readonly List<XRHandSubsystem> s_Subsystems = new();
    static XRHandSubsystem s_Hands;
    static XROrigin s_Origin;

    /// <summary>
    /// Moves <paramref name="buttonRoot"/> over the palm and shows it, or hides it. <paramref name="shown"/> carries
    /// whether it was showing last time. <paramref name="showFacing"/> and <paramref name="hideFacing"/> are how much
    /// (cosine) the palm must face the head for the button to appear and to stay.
    /// </summary>
    public static void UpdateButton(Handedness handedness, Transform buttonRoot, float offset, float showFacing, float hideFacing, ref bool shown)
    {
        if (buttonRoot == null)
            return;

        bool show = TryGetPalm(handedness, out var palmPosition, out var palmNormal, out var head);
        if (show)
        {
            float facing = Vector3.Dot(palmNormal, (head - palmPosition).normalized);
            show = shown ? facing > hideFacing : facing > showFacing;
        }

        if (shown != show || buttonRoot.gameObject.activeSelf != show)
        {
            shown = show;
            buttonRoot.gameObject.SetActive(show);
        }

        if (!show)
            return;

        Vector3 buttonPosition = palmPosition + palmNormal * offset;
        buttonRoot.SetPositionAndRotation(buttonPosition, Quaternion.LookRotation(buttonPosition - head, Vector3.up));
    }

    /// <summary>The palm's centre and the direction it faces (out of the palm), and the head, in the world.</summary>
    public static bool TryGetPalm(Handedness handedness, out Vector3 position, out Vector3 normal, out Vector3 head)
    {
        position = normal = head = default;
        if (s_Origin == null)
            s_Origin = Object.FindAnyObjectByType<XROrigin>();
        if (s_Hands == null || !s_Hands.running)
        {
            s_Hands = null;
            SubsystemManager.GetSubsystems(s_Subsystems);
            foreach (var subsystem in s_Subsystems)
            {
                if (subsystem.running)
                {
                    s_Hands = subsystem;
                    break;
                }
            }
        }

        if (s_Origin == null || s_Hands == null)
            return false;
        var hand = handedness == Handedness.Left ? s_Hands.leftHand : s_Hands.rightHand;
        if (!hand.isTracked || !hand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palm))
            return false;

        // Hand joints are in tracking space, under the origin's camera floor offset.
        Transform space = s_Origin.CameraFloorOffsetObject.transform;
        position = space.TransformPoint(palm.position);
        normal = space.rotation * palm.rotation * Vector3.down; // the palm joint's up points out of the back of the hand
        head = s_Origin.Camera.transform.position;
        return true;
    }
}
