using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Stretches a box Container along its own axes with two hands. With both hands on its corners it scales as a whole,
/// like a molecule (the general grab transformer does that); otherwise (a hand on an edge) each of the box's axes
/// along which the hands are apart stretches by how much further apart, along that axis, they get. The general grab
/// transformer still moves and turns it. On a mixed reality box's front corner balls the hands stretch its front only
/// (width and height), never its depth.
/// </summary>
[RequireComponent(typeof(XRGeneralGrabTransformer))]
public class ContainerStretchTransformer : XRBaseGrabTransformer
{
    [SerializeField]
    [Tooltip("A grab within this fraction of the box's half-size from a corner, on every axis, is on the corner.")]
    float m_CornerZone = 0.35f;

    [SerializeField]
    [Tooltip("Axes along which the hands are at least this fraction of their distance apart are stretched.")]
    float m_AxisShare = 0.35f;

    [SerializeField]
    Vector2 m_SizeRange = new(0.05f, 5f);

    protected override RegistrationMode registrationMode => RegistrationMode.Multiple;

    XRGeneralGrabTransformer m_General;
    TunnelContainer m_Container;
    bool m_Stretching;
    Vector3 m_StartScale;
    Vector3 m_StartApart; // the hands' separation along the box's axes when the stretch began
    readonly bool[] m_Axes = new bool[3];

    void Awake()
    {
        m_General = GetComponent<XRGeneralGrabTransformer>();
        m_Container = GetComponent<TunnelContainer>();
    }

    protected override void Start()
    {
        base.Start();
        // After the general transformer, so it moves and turns the box first and this only changes its scale.
        if (TryGetComponent(out XRGrabInteractable grab))
        {
            grab.RemoveMultipleGrabTransformer(this);
            grab.AddMultipleGrabTransformer(this);
        }
    }

    public override void OnGrabCountChanged(XRGrabInteractable grabInteractable, Pose targetPose, Vector3 localScale)
    {
        base.OnGrabCountChanged(grabInteractable, targetPose, localScale);
        m_Stretching = false;
        if (grabInteractable.interactorsSelecting.Count != 2)
        {
            m_General.allowTwoHandedScaling = true;
            return;
        }

        var a = HandPosition(grabInteractable, 0);
        var b = HandPosition(grabInteractable, 1);
        // On two corners it scales as a whole, except on a mixed reality box's front corner balls: there the hands
        // are apart only across the front, so the stretch below changes its width and height and not its depth.
        bool onFront = m_Container != null && m_Container.OnFrontCorner(a) && m_Container.OnFrontCorner(b);
        if (OnCorner(a) && OnCorner(b) && !onFront)
        {
            m_General.allowTwoHandedScaling = true;
            return;
        }

        m_StartApart = Quaternion.Inverse(targetPose.rotation) * (b - a);
        float apart = m_StartApart.magnitude;
        bool any = false;
        for (int i = 0; i < 3; i++)
        {
            m_Axes[i] = apart > 1e-3f && Mathf.Abs(m_StartApart[i]) >= m_AxisShare * apart;
            any |= m_Axes[i];
        }

        if (onFront)
            m_Axes[m_Container.frontFace / 2] = false; // the depth stays as it is
        any = m_Axes[0] || m_Axes[1] || m_Axes[2];
        m_Stretching = any;
        m_StartScale = localScale;
        m_General.allowTwoHandedScaling = !any;
    }

    public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
    {
        if (!m_Stretching || grabInteractable.interactorsSelecting.Count != 2)
            return;

        var apart = Quaternion.Inverse(targetPose.rotation) * (HandPosition(grabInteractable, 1) - HandPosition(grabInteractable, 0));
        var scale = localScale;
        for (int i = 0; i < 3; i++)
        {
            if (!m_Axes[i])
                continue;
            float ratio = Mathf.Abs(apart[i]) / Mathf.Max(Mathf.Abs(m_StartApart[i]), 1e-4f);
            scale[i] = Mathf.Clamp(m_StartScale[i] * ratio, m_SizeRange.x, m_SizeRange.y);
        }

        localScale = scale;
    }

    static Vector3 HandPosition(XRGrabInteractable grab, int index)
    {
        var interactor = grab.interactorsSelecting[index];
        return interactor.GetAttachTransform(grab).position;
    }

    bool OnCorner(Vector3 world)
    {
        // In the box's own coordinates its corners are at ±0.5.
        var local = transform.InverseTransformPoint(world) * 2f;
        float edge = 1f - m_CornerZone;
        return Mathf.Abs(local.x) >= edge && Mathf.Abs(local.y) >= edge && Mathf.Abs(local.z) >= edge;
    }
}
