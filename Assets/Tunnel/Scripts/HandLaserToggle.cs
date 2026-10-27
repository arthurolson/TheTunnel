using TMPro;
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

    bool m_LaserOn;
    bool m_ButtonShown;

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

    void Update() => HandPalm.UpdateButton(Handedness.Left, m_PalmButtonRoot, m_PalmOffset, m_ShowFacing, m_HideFacing, ref m_ButtonShown);

    void ShowButton(bool show)
    {
        m_ButtonShown = show;
        if (m_PalmButtonRoot != null)
            m_PalmButtonRoot.gameObject.SetActive(show);
    }
}
