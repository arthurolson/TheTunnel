using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Keeps a grabbed object upright: whatever earlier transformers compute, only rotation about the world
    /// vertical axis survives, so the object's horizontal edges stay level. Put it after the transformer that moves the object.
    /// With <see cref="m_FaceViewer"/> the object instead turns to face the viewer while held, so a panel can never be
    /// carried round to show its back.
    /// </summary>
    public class UprightGrabTransformer : XRBaseGrabTransformer
    {
        [SerializeField]
        [Tooltip("While held, turn to face the viewer (front towards the head) instead of following the hand's rotation.")]
        bool m_FaceViewer;

        Transform m_Head;

        protected override RegistrationMode registrationMode => RegistrationMode.SingleAndMultiple;

        public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
            if (updatePhase is not (XRInteractionUpdateOrder.UpdatePhase.Dynamic or XRInteractionUpdateOrder.UpdatePhase.OnBeforeRender))
                return;

            Vector3 forward;
            if (m_FaceViewer && TryGetHead(out var head))
                forward = targetPose.position - head.position; // the viewer looks along the panel's forward
            else
                forward = targetPose.forward;

            forward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(targetPose.up, Vector3.up); // looking straight up/down: fall back to up
            if (forward.sqrMagnitude < 1e-6f)
                return;
            targetPose.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        bool TryGetHead(out Transform head)
        {
            if (m_Head == null && Camera.main != null)
                m_Head = Camera.main.transform;
            head = m_Head;
            return head != null;
        }
    }
}
