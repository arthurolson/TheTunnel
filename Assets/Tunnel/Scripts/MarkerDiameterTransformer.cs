using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Two hands on a marker (<see cref="TunnelMarker"/>) change its diameter, the size of the marks it makes, and never
/// its length: after the general grab transformer has scaled it evenly, this puts its length (local Z) back and keeps
/// the diameter within range.
/// </summary>
[RequireComponent(typeof(XRGeneralGrabTransformer))]
public class MarkerDiameterTransformer : XRBaseGrabTransformer
{
    [SerializeField]
    float m_Length = 0.15f;

    [SerializeField]
    Vector2 m_DiameterRange = new(0.002f, 0.1f);

    protected override RegistrationMode registrationMode => RegistrationMode.SingleAndMultiple;

    protected override void Start()
    {
        base.Start();
        // After the general transformer, so it has scaled the marker first.
        if (TryGetComponent(out XRGrabInteractable grab))
        {
            grab.RemoveSingleGrabTransformer(this);
            grab.RemoveMultipleGrabTransformer(this);
            grab.AddSingleGrabTransformer(this);
            grab.AddMultipleGrabTransformer(this);
        }
    }

    public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
    {
        float diameter = Mathf.Clamp(Mathf.Abs(localScale.x), m_DiameterRange.x, m_DiameterRange.y);
        localScale = new Vector3(diameter, diameter, m_Length);
    }
}
