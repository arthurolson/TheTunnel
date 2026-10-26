using System.Collections.Generic;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// A "Laser" button that floats over the palm of the left hand while the palm faces the viewer. Pressing it
/// (e.g. poking it with the right index finger) turns the hands' far rays on or off. With the laser on you point
/// and pinch to select, press UI and grab/move objects from a distance.
/// </summary>
public class HandLaserToggle : MonoBehaviour
{
    [SerializeField]
    [Tooltip("The hands' Near-Far interactors whose far casting (the laser) is switched.")]
    NearFarInteractor[] m_HandInteractors;

    [SerializeField]
    [Tooltip("Shown over the left palm; moved to the palm each frame.")]
    Transform m_PalmButtonRoot;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Label;

    [SerializeField]
    bool m_LaserOnAtStart;

    [SerializeField]
    [Tooltip("How far the button floats out from the palm (meters).")]
    float m_PalmOffset = 0.05f;

    [SerializeField]
    [Tooltip("Palm must face the head at least this much (cosine) for the button to appear; it hides again below the lower value.")]
    float m_ShowFacing = 0.65f;

    [SerializeField]
    float m_HideFacing = 0.45f;

    XRHandSubsystem m_Hands;
    XROrigin m_Origin;
    bool m_LaserOn;
    bool m_ButtonShown;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    void Awake()
    {
        if (m_Button != null)
            m_Button.onClick.AddListener(Toggle);
        SetLaser(m_LaserOnAtStart);
        ShowButton(false);
    }

    void OnDestroy()
    {
        if (m_Button != null)
            m_Button.onClick.RemoveListener(Toggle);
    }

    public void Toggle() => SetLaser(!m_LaserOn);

    public void SetLaser(bool on)
    {
        m_LaserOn = on;
        foreach (var interactor in m_HandInteractors)
        {
            if (interactor != null)
                interactor.enableFarCasting = on;
        }

        if (m_Label != null)
            m_Label.text = on ? "Laser: On" : "Laser: Off";
    }

    void Update()
    {
        if (m_PalmButtonRoot == null)
            return;

        if (m_Origin == null)
            m_Origin = FindAnyObjectByType<XROrigin>();
        if (m_Hands == null || !m_Hands.running)
        {
            m_Hands = null;
            SubsystemManager.GetSubsystems(s_Subsystems);
            foreach (var subsystem in s_Subsystems)
            {
                if (subsystem.running)
                {
                    m_Hands = subsystem;
                    break;
                }
            }
        }

        if (m_Origin == null || m_Hands == null || !m_Hands.leftHand.isTracked ||
            !m_Hands.leftHand.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palm))
        {
            ShowButton(false);
            return;
        }

        // Hand joints are in tracking space, under the origin's camera floor offset.
        Transform space = m_Origin.CameraFloorOffsetObject.transform;
        Vector3 palmPosition = space.TransformPoint(palm.position);
        Vector3 palmNormal = space.rotation * palm.rotation * Vector3.down; // the palm joint's up points out of the back of the hand
        Vector3 head = m_Origin.Camera.transform.position;
        Vector3 toHead = (head - palmPosition).normalized;

        float facing = Vector3.Dot(palmNormal, toHead);
        bool show = m_ButtonShown ? facing > m_HideFacing : facing > m_ShowFacing;
        ShowButton(show);
        if (!show)
            return;

        Vector3 buttonPosition = palmPosition + palmNormal * m_PalmOffset;
        m_PalmButtonRoot.SetPositionAndRotation(buttonPosition, Quaternion.LookRotation(buttonPosition - head, Vector3.up));
    }

    void ShowButton(bool show)
    {
        if (m_ButtonShown == show && m_PalmButtonRoot != null && m_PalmButtonRoot.gameObject.activeSelf == show)
            return;
        m_ButtonShown = show;
        if (m_PalmButtonRoot != null)
            m_PalmButtonRoot.gameObject.SetActive(show);
    }
}
