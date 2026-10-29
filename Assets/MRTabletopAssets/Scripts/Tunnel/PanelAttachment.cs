using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Something that hangs off a <see cref="BrowserPanel"/> (its bar or keyboard) and can be moved by its handle:
    /// pinch or grab the handle and drag (or point a laser at it, select and swing), and it stays where it is let go,
    /// relative to the panel, so it follows the panel from then on. The panel decides its usual spot; this keeps the
    /// offset from it, and the panel shares that offset with everyone. Let go close to the usual spot and it snaps back.
    /// </summary>
    public class PanelAttachment : MonoBehaviour
    {
        [SerializeField]
        BrowserPanel m_Panel;

        [SerializeField]
        XRSimpleInteractable m_Handle;

        [SerializeField]
        [Tooltip("The handle only shows while this is active (the keyboard's keys). Empty = always shown.")]
        GameObject m_ShowHandleWith;

        [SerializeField]
        [Tooltip("Let go this close to the usual spot (meters) and it snaps back to it.")]
        float m_SnapBackDistance = 0.04f;

        [SerializeField]
        [Tooltip("While dragging, where it is gets shared this often (seconds).")]
        float m_SendInterval = 0.1f;

        Vector3 m_DefaultPosition;
        Quaternion m_DefaultRotation = Quaternion.identity;
        Vector3 m_Offset;

        IXRSelectInteractor m_Interactor;
        Vector3 m_GrabDelta;
        float m_RayDistance;
        float m_NextSend;
        bool m_HandleShown = true;
        Vector3 m_HandleScale;

        /// <summary>How far it was moved from its usual spot, in the panel's space.</summary>
        public Vector3 offset => m_Offset;

        public bool isDragging => m_Interactor != null;

        void Awake()
        {
            if (m_Panel == null)
                m_Panel = GetComponentInParent<BrowserPanel>();
            if (m_Handle != null)
            {
                m_HandleScale = m_Handle.transform.localScale;
                m_Handle.selectEntered.AddListener(OnGrabbed);
                m_Handle.selectExited.AddListener(OnReleased);
                m_Handle.hoverEntered.AddListener(_ => Highlight(true));
                m_Handle.hoverExited.AddListener(_ => Highlight(m_Handle.isHovered));
            }

            UpdateHandleVisibility();
        }

        void OnDestroy()
        {
            if (m_Handle == null)
                return;
            m_Handle.selectEntered.RemoveListener(OnGrabbed);
            m_Handle.selectExited.RemoveListener(OnReleased);
            m_Handle.hoverEntered.RemoveAllListeners();
            m_Handle.hoverExited.RemoveAllListeners();
        }

        /// <summary>The panel's usual spot for it (panel-local); it sits there plus its offset.</summary>
        public void SetDefaultPose(Vector3 localPosition, Quaternion localRotation)
        {
            m_DefaultPosition = localPosition;
            m_DefaultRotation = localRotation;
            if (!isDragging)
                ApplyPose();
        }

        /// <summary>Someone moved it (or the panel just spawned): ignored while this player is dragging it.</summary>
        public void SetOffsetFromNetwork(Vector3 newOffset)
        {
            if (isDragging)
                return;
            m_Offset = newOffset;
            ApplyPose();
        }

        void ApplyPose()
        {
            transform.localPosition = m_DefaultPosition + m_Offset;
            transform.localRotation = m_DefaultRotation;
        }

        void Update()
        {
            UpdateHandleVisibility();
            if (!isDragging)
                return;

            transform.position = GrabPoint() + m_GrabDelta;
            m_Offset = transform.localPosition - m_DefaultPosition;
            if (Time.unscaledTime >= m_NextSend)
            {
                m_NextSend = Time.unscaledTime + m_SendInterval;
                if (m_Panel != null)
                    m_Panel.OnAttachmentMoved(this);
            }
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            if (isDragging)
                return;
            m_Interactor = args.interactorObject;
            if (IsFar(m_Interactor, out var origin))
                m_RayDistance = Mathf.Max(0.1f, Vector3.Dot(m_Handle.transform.position - origin.position, origin.forward));
            m_GrabDelta = transform.position - GrabPoint();
        }

        void OnReleased(SelectExitEventArgs args)
        {
            if (args.interactorObject != m_Interactor)
                return;
            m_Interactor = null;
            if (m_Offset.magnitude < m_SnapBackDistance)
                m_Offset = Vector3.zero;
            ApplyPose();
            if (m_Panel != null)
                m_Panel.OnAttachmentMoved(this);
        }

        /// <summary>The hand's grab point, or for a laser the point that far along it.</summary>
        Vector3 GrabPoint()
        {
            if (IsFar(m_Interactor, out var origin))
                return origin.position + origin.forward * m_RayDistance;
            return m_Interactor.GetAttachTransform(m_Handle).position;
        }

        static bool IsFar(IXRSelectInteractor interactor, out Transform origin)
        {
            origin = null;
            if (interactor is NearFarInteractor nearFar && nearFar.selectionRegion.Value == NearFarInteractor.Region.Far)
                origin = nearFar.curveOrigin;
            return origin != null;
        }

        void UpdateHandleVisibility()
        {
            if (m_Handle == null)
                return;
            bool show = isDragging || m_ShowHandleWith == null || m_ShowHandleWith.activeInHierarchy;
            if (show == m_HandleShown)
                return;
            m_HandleShown = show;
            foreach (var r in m_Handle.GetComponentsInChildren<Renderer>(true))
                r.enabled = show;
            foreach (var c in m_Handle.colliders)
                c.enabled = show;
        }

        void Highlight(bool on)
        {
            if (m_Handle != null)
                m_Handle.transform.localScale = on ? m_HandleScale * 1.25f : m_HandleScale;
        }
    }
}
