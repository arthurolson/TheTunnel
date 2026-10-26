using TMPro;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Keeps a grabbed Tunnel Segment on the floor: whatever the earlier transformers compute, the height stays where
    /// it was when the grab started and only rotation about the vertical axis is kept. While height adjustment is
    /// switched on (the handle's Height button), moving the gripping hand up or down raises or lowers the tunnel at
    /// <see cref="m_HeightSensitivity"/> of the hand's motion, for fine floor alignment. Put this last in the
    /// grab interactable's transformer lists.
    /// </summary>
    public class FloorLockGrabTransformer : XRBaseGrabTransformer
    {
        [SerializeField]
        [Range(0.01f, 1f)]
        [Tooltip("Fraction of the hand's vertical movement applied to the tunnel while height adjustment is on.")]
        float m_HeightSensitivity = 0.2f;

        [SerializeField]
        [Tooltip("Optional button that switches height adjustment on and off.")]
        Button m_HeightButton;

        [SerializeField]
        TMP_Text m_HeightLabel;

        float m_GrabStartHeight;
        float m_HandStartHeight;
        bool m_WasAdjusting;

        /// <summary>Whether vertical hand movement currently adjusts the tunnel's height.</summary>
        public bool heightAdjustEnabled { get; private set; }

        /// <summary>True once the user has changed the height by hand; automatic floor placement then stays out of the way.</summary>
        public bool heightAdjustedByUser { get; private set; }

        protected override RegistrationMode registrationMode => RegistrationMode.SingleAndMultiple;

        protected override void Start()
        {
            base.Start();
            if (m_HeightButton != null)
                m_HeightButton.onClick.AddListener(ToggleHeightAdjust);
            UpdateLabel();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (m_HeightButton != null)
                m_HeightButton.onClick.RemoveListener(ToggleHeightAdjust);
        }

        public void ToggleHeightAdjust()
        {
            heightAdjustEnabled = !heightAdjustEnabled;
            UpdateLabel();
        }

        public override void OnGrab(XRGrabInteractable grabInteractable)
        {
            base.OnGrab(grabInteractable);
            m_WasAdjusting = heightAdjustEnabled;
            Rebase(grabInteractable);
        }

        public override void OnGrabCountChanged(XRGrabInteractable grabInteractable, Pose targetPose, Vector3 localScale)
        {
            base.OnGrabCountChanged(grabInteractable, targetPose, localScale);
            Rebase(grabInteractable);
        }

        public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase, ref Pose targetPose, ref Vector3 localScale)
        {
            if (updatePhase is not (XRInteractionUpdateOrder.UpdatePhase.Dynamic or XRInteractionUpdateOrder.UpdatePhase.OnBeforeRender))
                return;

            // Switched mid-grab (e.g. with the other hand): continue from where things are now, without a jump.
            if (heightAdjustEnabled != m_WasAdjusting)
            {
                m_WasAdjusting = heightAdjustEnabled;
                Rebase(grabInteractable);
            }

            float height = m_GrabStartHeight;
            if (heightAdjustEnabled && grabInteractable.interactorsSelecting.Count > 0)
            {
                float hand = HandHeight(grabInteractable);
                height += (hand - m_HandStartHeight) * m_HeightSensitivity;
                if (!Mathf.Approximately(height, m_GrabStartHeight))
                    heightAdjustedByUser = true;
            }

            var position = targetPose.position;
            position.y = height;
            targetPose.position = position;

            Vector3 forward = Vector3.ProjectOnPlane(targetPose.forward, Vector3.up);
            if (forward.sqrMagnitude > 1e-6f)
                targetPose.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        void Rebase(XRGrabInteractable grabInteractable)
        {
            m_GrabStartHeight = grabInteractable.transform.position.y;
            if (grabInteractable.interactorsSelecting.Count > 0)
                m_HandStartHeight = HandHeight(grabInteractable);
        }

        static float HandHeight(XRGrabInteractable grabInteractable)
        {
            var interactors = grabInteractable.interactorsSelecting;
            float sum = 0f;
            foreach (var interactor in interactors)
                sum += interactor.GetAttachTransform(grabInteractable).position.y;
            return sum / interactors.Count;
        }

        void UpdateLabel()
        {
            if (m_HeightLabel != null)
                m_HeightLabel.text = heightAdjustEnabled ? "Height: Adjust" : "Height: Locked";
        }
    }
}
