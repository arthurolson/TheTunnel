using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityLabs.SmartUX.Network;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// A 16:9 browser panel that is moved by its frame, resized from its corners and, when released, snapped
    /// onto the nearest Tunnel Segment wall (and then follows that tunnel when it moves).
    /// Corner markers only appear while a hand or controller is near that corner. The panel always stays upright.
    /// Size and wall attachment are replicated; the pose is synced while someone holds it (ClaimableNetworkTransform).
    /// </summary>
    public class BrowserPanel : NetworkBehaviour
    {
        const float k_Aspect = 16f / 9f;
        const float k_WidthSendThreshold = 0.002f;

        [SerializeField]
        XRGrabInteractable m_Grab;

        [SerializeField]
        ClaimableNetworkBehaviour m_Claimable;

        [SerializeField]
        [Tooltip("World-space canvas holding the browser view, authored at a fixed 16:9 pixel size.")]
        RectTransform m_Screen;

        [SerializeField]
        Transform m_FrameTop;

        [SerializeField]
        Transform m_FrameBottom;

        [SerializeField]
        Transform m_FrameLeft;

        [SerializeField]
        Transform m_FrameRight;

        [SerializeField]
        [Tooltip("Corner markers in order: top-left, top-right, bottom-left, bottom-right.")]
        XRSimpleInteractable[] m_Corners = new XRSimpleInteractable[4];

        [Header("Address bar and keyboard (constant size, follow the panel's edges)")]
        [SerializeField]
        Transform m_AddressBar;

        [SerializeField]
        [Tooltip("Gap between the top of the frame and the centre of the address bar (meters).")]
        float m_AddressBarGap = 0.04f;

        [SerializeField]
        Transform m_Keyboard;

        [SerializeField]
        [Tooltip("Drop from the bottom of the frame to the centre of the keyboard (meters).")]
        float m_KeyboardDrop = 0.13f;

        [SerializeField]
        [Tooltip("How far the keyboard sits out in front of the screen, towards the viewer (meters).")]
        float m_KeyboardForward = 0.12f;

        [SerializeField]
        [Tooltip("Keyboard tilt, top edge leaning away from the viewer like a desk keyboard (degrees).")]
        float m_KeyboardTilt = 40f;

        [Header("Size (meters)")]
        [SerializeField]
        float m_Width = 1.2f;

        [SerializeField]
        float m_MinWidth = 0.3f;

        [SerializeField]
        float m_MaxWidth = 3.0f;

        [SerializeField]
        float m_FrameThickness = 0.025f;

        [SerializeField]
        float m_FrameDepth = 0.015f;

        [Header("Behaviour")]
        [SerializeField]
        [Tooltip("A corner marker appears when a hand or controller comes this close to it (meters).")]
        float m_CornerRevealDistance = 0.15f;

        [SerializeField]
        [Tooltip("A corner marker also appears when a laser's end point lands this close to it (meters).")]
        float m_CornerRayRevealDistance = 0.12f;

        [SerializeField]
        [Tooltip("Gap left between the panel and the wall it snaps to (meters).")]
        float m_WallOffset = 0.012f;

        [SerializeField]
        [Tooltip("Walls farther than this from the released panel are ignored (meters).")]
        float m_MaxSnapDistance = 10f;

        [SerializeField]
        float m_SnapDuration = 0.2f;

        // Width < 0 means "not set yet"; the server fills in the authored width on spawn.
        readonly NetworkVariable<float> m_NetWidth = new(-1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<NetworkObjectReference> m_NetTunnel = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        // xyz = position in the tunnel's local frame, w = yaw in the tunnel's local frame (degrees).
        readonly NetworkVariable<Vector4> m_NetLocalPose = new(Vector4.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<bool> m_NetAttached = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        TunnelSegmentGeometry m_AttachedTunnel;
        Vector3 m_AttachedLocalPosition;
        float m_AttachedLocalYaw;

        bool m_Moving;
        bool m_Snapping;
        float m_LastSentWidth = -1f;

        // corner drag state
        int m_DragCorner = -1;
        IXRSelectInteractor m_DragInteractor;
        Vector3 m_DragOppositeCorner;
        Vector3 m_DragRight;
        Vector3 m_DragUp;

        readonly List<XRBaseInteractor> m_Interactors = new();
        float m_NextInteractorRefresh;
        readonly bool[] m_CornerVisible = new bool[4];

        float height => m_Width / k_Aspect;

        static int s_PanelCount;

        void Awake()
        {
            // Page scripts call back into Unity by object name, so every panel's browser view needs its own.
            if (m_Screen != null)
            {
                var view = m_Screen.Find("BrowserPanel_WebView");
                if (view != null)
                    view.name = $"BrowserPanel_WebView_{++s_PanelCount}";
            }

            ApplyLayout();
            for (int i = 0; i < m_Corners.Length; i++)
                SetCornerVisible(i, false);

            if (m_Grab != null)
            {
                m_Grab.firstSelectEntered.AddListener(OnGrabStarted);
                m_Grab.lastSelectExited.AddListener(OnGrabEnded);
            }

            for (int i = 0; i < m_Corners.Length; i++)
            {
                if (m_Corners[i] == null)
                    continue;
                int corner = i;
                m_Corners[i].selectEntered.AddListener(args => OnCornerSelected(corner, args));
                m_Corners[i].selectExited.AddListener(args => OnCornerReleased(corner, args));
            }
        }

        ///<inheritdoc/>
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_NetWidth.OnValueChanged += OnNetWidthChanged;
            m_NetAttached.OnValueChanged += OnNetAttachmentChanged;
            m_NetLocalPose.OnValueChanged += OnNetLocalPoseChanged;

            if (IsServer && m_NetWidth.Value < 0f)
                m_NetWidth.Value = m_Width;

            if (m_NetWidth.Value > 0f)
                SetWidthLocal(m_NetWidth.Value);

            // A replicated attachment wins; otherwise the server publishes the one adopted from the scene layout.
            if (m_NetAttached.Value)
                ReadAttachmentFromNetwork();
            else if (IsServer && m_AttachedTunnel != null)
                StartCoroutine(PublishAttachmentWhenTunnelSpawned());
        }

        IEnumerator PublishAttachmentWhenTunnelSpawned()
        {
            var tunnelObject = m_AttachedTunnel.GetComponentInParent<NetworkObject>();
            while (tunnelObject != null && !tunnelObject.IsSpawned)
                yield return null;
            if (m_AttachedTunnel != null && !m_NetAttached.Value)
                Attach(m_AttachedTunnel, m_AttachedLocalPosition, m_AttachedLocalYaw);
        }

        ///<inheritdoc/>
        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            m_NetWidth.OnValueChanged -= OnNetWidthChanged;
            m_NetAttached.OnValueChanged -= OnNetAttachmentChanged;
            m_NetLocalPose.OnValueChanged -= OnNetLocalPoseChanged;
        }

        ///<inheritdoc/>
        public override void OnDestroy()
        {
            base.OnDestroy();
            if (m_Grab != null)
            {
                m_Grab.firstSelectEntered.RemoveListener(OnGrabStarted);
                m_Grab.lastSelectExited.RemoveListener(OnGrabEnded);
            }
        }

        void Start()
        {
            // Placed in the scene already on a wall: adopt it, so the panel follows that tunnel from the start.
            if (!IsSpawned || IsServer)
                AttachToClosestWallAtCurrentPose();
        }

        void Update()
        {
            UpdateCornerVisibility();

            if (m_DragCorner >= 0)
                UpdateCornerDrag();
        }

        void LateUpdate()
        {
            if (m_Moving || m_Snapping || m_DragCorner >= 0 || m_AttachedTunnel == null)
                return;
            // Someone else is holding it: their synced transform is authoritative.
            if (m_Claimable != null && m_Claimable.isOwnedRemotely)
                return;

            var tunnel = m_AttachedTunnel.transform;
            transform.SetPositionAndRotation(
                tunnel.TransformPoint(m_AttachedLocalPosition),
                Quaternion.Euler(0f, tunnel.eulerAngles.y + m_AttachedLocalYaw, 0f));
        }

        #region Moving

        void OnGrabStarted(SelectEnterEventArgs args)
        {
            m_Moving = true;
            StopSnap();
            Detach();
        }

        void OnGrabEnded(SelectExitEventArgs args)
        {
            m_Moving = false;
            SnapToClosestWall();
        }

        #endregion

        #region Closing

        /// <summary>Removes this panel for every player.</summary>
        public void Close()
        {
            if (IsSpawned)
                CloseRpc();
            else
                Destroy(gameObject);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void CloseRpc()
        {
            if (NetworkObject != null && NetworkObject.IsSpawned)
                NetworkObject.Despawn(true);
        }

        #endregion

        #region Resizing

        void OnCornerSelected(int corner, SelectEnterEventArgs args)
        {
            if (m_DragCorner >= 0)
                return;

            StopSnap();
            Detach();
            HoldOwnership(true);

            // The opposite corner of the screen stays where it is; signs say which way the dragged corner lies.
            var (sx, sy) = CornerSigns(corner);
            m_DragRight = transform.right;
            m_DragUp = transform.up;
            m_DragOppositeCorner = transform.position - sx * m_DragRight * (m_Width * 0.5f) - sy * m_DragUp * (height * 0.5f);
            m_DragCorner = corner;
            m_DragInteractor = args.interactorObject;
        }

        void OnCornerReleased(int corner, SelectExitEventArgs args)
        {
            if (corner != m_DragCorner)
                return;

            m_DragCorner = -1;
            m_DragInteractor = null;
            SendWidth(true);
            HoldOwnership(false);
            SnapToClosestWall();
        }

        void UpdateCornerDrag()
        {
            if (m_DragInteractor == null)
                return;

            var (sx, sy) = CornerSigns(m_DragCorner);
            Vector3 hand;
            if (m_DragInteractor is NearFarInteractor nearFar && nearFar.selectionRegion.Value == NearFarInteractor.Region.Far)
            {
                // Laser drag: the corner goes where the laser crosses the panel's plane.
                var origin = nearFar.curveOrigin;
                var ray = new Ray(origin.position, origin.forward);
                if (!new Plane(Vector3.Cross(m_DragRight, m_DragUp), m_DragOppositeCorner).Raycast(ray, out float distance))
                    return;
                hand = ray.GetPoint(distance);
            }
            else
            {
                hand = m_DragInteractor.GetAttachTransform(m_Corners[m_DragCorner]).position;
            }

            Vector3 fromOpposite = hand - m_DragOppositeCorner;

            // Fixed aspect: the larger of the two pulls decides the width.
            float alongWidth = Vector3.Dot(fromOpposite, m_DragRight) * sx;
            float alongHeight = Vector3.Dot(fromOpposite, m_DragUp) * sy;
            float width = Mathf.Clamp(Mathf.Max(alongWidth, alongHeight * k_Aspect), m_MinWidth, m_MaxWidth);

            transform.position = m_DragOppositeCorner + sx * m_DragRight * (width * 0.5f) + sy * m_DragUp * (width / k_Aspect * 0.5f);
            SetWidthLocal(width);
            SendWidth(false);
        }

        static (float, float) CornerSigns(int corner) => corner switch
        {
            0 => (-1f, 1f),   // top-left
            1 => (1f, 1f),    // top-right
            2 => (-1f, -1f),  // bottom-left
            _ => (1f, -1f),   // bottom-right
        };

        void UpdateCornerVisibility()
        {
            if (Time.unscaledTime >= m_NextInteractorRefresh)
            {
                m_NextInteractorRefresh = Time.unscaledTime + 2f;
                m_Interactors.Clear();
                m_Interactors.AddRange(FindObjectsByType<XRBaseInteractor>(FindObjectsSortMode.None));
            }

            for (int i = 0; i < m_Corners.Length; i++)
            {
                if (m_Corners[i] == null)
                    continue;

                bool visible = i == m_DragCorner || m_Corners[i].isHovered;
                if (!visible)
                {
                    Vector3 corner = m_Corners[i].transform.position;
                    float maxSqr = m_CornerRevealDistance * m_CornerRevealDistance;
                    float rayMaxSqr = m_CornerRayRevealDistance * m_CornerRayRevealDistance;
                    foreach (var interactor in m_Interactors)
                    {
                        if (interactor == null || !interactor.isActiveAndEnabled)
                            continue;
                        if ((interactor.GetAttachTransform(m_Corners[i]).position - corner).sqrMagnitude < maxSqr)
                        {
                            visible = true;
                            break;
                        }

                        // A laser pointing at (or next to) the corner reveals it, so it can then be grabbed from afar.
                        if (interactor is NearFarInteractor nearFar && nearFar.enableFarCasting)
                        {
                            var endType = nearFar.TryGetCurveEndPoint(out Vector3 end);
                            bool hit = endType is EndPointType.ValidCastHit or EndPointType.UI or EndPointType.AttachPoint;
                            if (hit && (end - corner).sqrMagnitude < rayMaxSqr)
                            {
                                visible = true;
                                break;
                            }
                        }
                    }
                }

                if (visible != m_CornerVisible[i])
                    SetCornerVisible(i, visible);
            }
        }

        void SetCornerVisible(int corner, bool visible)
        {
            m_CornerVisible[corner] = visible;
            if (m_Corners[corner] == null)
                return;
            foreach (var r in m_Corners[corner].GetComponentsInChildren<Renderer>(true))
                r.enabled = visible;
            foreach (var c in m_Corners[corner].colliders)
                c.enabled = visible;
        }

        void SetWidthLocal(float width)
        {
            m_Width = Mathf.Clamp(width, m_MinWidth, m_MaxWidth);
            ApplyLayout();
        }

        void SendWidth(bool force)
        {
            if (!IsSpawned || (!force && Mathf.Abs(m_Width - m_LastSentWidth) < k_WidthSendThreshold))
                return;
            m_LastSentWidth = m_Width;
            SetWidthRpc(m_Width);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SetWidthRpc(float width) => m_NetWidth.Value = width;

        void OnNetWidthChanged(float previous, float current)
        {
            if (m_DragCorner < 0 && current > 0f)
                SetWidthLocal(current);
        }

        /// <summary>
        /// Sizes the screen and places the frame bars and corner markers around it. The frame keeps a constant
        /// thickness whatever the panel size.
        /// </summary>
        void ApplyLayout()
        {
            float w = m_Width, h = height, t = m_FrameThickness, d = m_FrameDepth;
            if (m_Screen != null && m_Screen.rect.width > 0f)
                m_Screen.localScale = Vector3.one * (w / m_Screen.rect.width);

            // The frame sits just behind the screen (+Z is towards the wall when snapped).
            float z = d * 0.5f;
            SetBar(m_FrameTop, new Vector3(0f, (h + t) * 0.5f, z), new Vector3(w, t, d));
            SetBar(m_FrameBottom, new Vector3(0f, -(h + t) * 0.5f, z), new Vector3(w, t, d));
            SetBar(m_FrameLeft, new Vector3(-(w + t) * 0.5f, 0f, z), new Vector3(t, h, d));
            SetBar(m_FrameRight, new Vector3((w + t) * 0.5f, 0f, z), new Vector3(t, h, d));

            for (int i = 0; i < m_Corners.Length; i++)
            {
                if (m_Corners[i] == null)
                    continue;
                var (sx, sy) = CornerSigns(i);
                m_Corners[i].transform.localPosition = new Vector3(sx * (w + t) * 0.5f, sy * (h + t) * 0.5f, 0f);
                m_Corners[i].transform.localRotation = Quaternion.identity;
            }

            if (m_AddressBar != null)
            {
                m_AddressBar.localPosition = new Vector3(0f, h * 0.5f + t + m_AddressBarGap, 0f);
                m_AddressBar.localRotation = Quaternion.identity;
            }

            if (m_Keyboard != null)
            {
                // -Z is towards the viewer; tilting about +X leans the top edge back.
                m_Keyboard.localPosition = new Vector3(0f, -(h * 0.5f + t) - m_KeyboardDrop, -m_KeyboardForward);
                m_Keyboard.localRotation = Quaternion.Euler(m_KeyboardTilt, 0f, 0f);
            }
        }

        static void SetBar(Transform bar, Vector3 position, Vector3 size)
        {
            if (bar == null)
                return;
            bar.localPosition = position;
            bar.localRotation = Quaternion.identity;
            bar.localScale = size;
        }

        #endregion

        #region Wall snapping

        struct WallTarget
        {
            public TunnelSegmentGeometry tunnel;
            public Vector3 localPosition;
            public float localYaw;
            public float distance;
        }

        bool TryFindClosestWall(out WallTarget best)
        {
            best = new WallTarget { distance = float.MaxValue };
            float halfPanelW = m_Width * 0.5f + m_FrameThickness;
            float halfPanelH = height * 0.5f + m_FrameThickness;
            // The keyboard hangs below the frame; keep it above the floor. (The address bar may rise into the arch.)
            float below = halfPanelH + (m_Keyboard != null ? m_KeyboardDrop + 0.08f : 0f);

            var head = Camera.main != null ? Camera.main.transform : null;
            foreach (var tunnel in FindObjectsByType<TunnelSegmentGeometry>(FindObjectsSortMode.None))
            {
                Vector3 local = tunnel.transform.InverseTransformPoint(transform.position);
                // Which face to use is decided by where the viewer stands, not where the panel happens to be:
                // a panel pushed slightly through the wall must not flip to the outside and read backwards.
                Vector3 viewer = head != null ? tunnel.transform.InverseTransformPoint(head.position) : local;
                float halfWidth = tunnel.width * 0.5f;
                float halfLength = tunnel.length * 0.5f;

                foreach (float side in new[] { -1f, 1f })
                {
                    float dx = Mathf.Abs(local.x - side * halfWidth);
                    float dz = Mathf.Max(0f, Mathf.Abs(local.z) - halfLength);
                    float dy = Mathf.Max(0f, Mathf.Max(local.y - tunnel.wallHeight, -local.y));
                    float distance = Mathf.Sqrt(dx * dx + dz * dz + dy * dy);
                    if (distance >= best.distance || distance > m_MaxSnapDistance)
                        continue;

                    // Viewer on the inside of this wall -> inner face; otherwise the outer face.
                    bool inside = viewer.x * side < halfWidth;
                    float x = side * (halfWidth + (inside ? -m_WallOffset : m_WallOffset));
                    float z = halfPanelW <= halfLength ? Mathf.Clamp(local.z, -halfLength + halfPanelW, halfLength - halfPanelW) : 0f;
                    float y = below + halfPanelH <= tunnel.wallHeight
                        ? Mathf.Clamp(local.y, below, tunnel.wallHeight - halfPanelH)
                        : tunnel.wallHeight * 0.5f;

                    // The viewer looks along the panel's forward, into the wall.
                    bool facesPlusX = inside ? side > 0f : side < 0f;
                    best = new WallTarget
                    {
                        tunnel = tunnel,
                        localPosition = new Vector3(x, y, z),
                        localYaw = facesPlusX ? 90f : -90f,
                        distance = distance,
                    };
                }
            }

            return best.tunnel != null;
        }

        void SnapToClosestWall()
        {
            if (!TryFindClosestWall(out var target))
                return;

            StopSnap();
            m_SnapRoutine = StartCoroutine(SnapRoutine(target));
        }

        Coroutine m_SnapRoutine;

        IEnumerator SnapRoutine(WallTarget target)
        {
            m_Snapping = true;
            HoldOwnership(true);

            var tunnel = target.tunnel.transform;
            Vector3 startPos = transform.position;
            Quaternion startRot = transform.rotation;
            for (float t = 0f; t < m_SnapDuration; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / m_SnapDuration);
                Vector3 endPos = tunnel.TransformPoint(target.localPosition);
                Quaternion endRot = Quaternion.Euler(0f, tunnel.eulerAngles.y + target.localYaw, 0f);
                transform.SetPositionAndRotation(Vector3.Lerp(startPos, endPos, k), Quaternion.Slerp(startRot, endRot, k));
                yield return null;
            }

            Attach(target.tunnel, target.localPosition, target.localYaw);
            m_Snapping = false;
            m_SnapRoutine = null;
            HoldOwnership(false);
        }

        void StopSnap()
        {
            if (m_SnapRoutine == null)
                return;
            StopCoroutine(m_SnapRoutine);
            m_SnapRoutine = null;
            m_Snapping = false;
            HoldOwnership(false);
        }

        void AttachToClosestWallAtCurrentPose()
        {
            if (!TryFindClosestWall(out var target) || target.distance > m_WallOffset * 4f)
                return;
            Attach(target.tunnel, target.localPosition, target.localYaw);
        }

        void Attach(TunnelSegmentGeometry tunnel, Vector3 localPosition, float localYaw)
        {
            m_AttachedTunnel = tunnel;
            m_AttachedLocalPosition = localPosition;
            m_AttachedLocalYaw = localYaw;

            var tunnelObject = tunnel.GetComponentInParent<NetworkObject>();
            if (IsSpawned && tunnelObject != null && tunnelObject.IsSpawned)
                SetAttachmentRpc(new NetworkObjectReference(tunnelObject), new Vector4(localPosition.x, localPosition.y, localPosition.z, localYaw));
        }

        void Detach()
        {
            m_AttachedTunnel = null;
            if (IsSpawned)
                ClearAttachmentRpc();
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SetAttachmentRpc(NetworkObjectReference tunnel, Vector4 localPose)
        {
            m_NetTunnel.Value = tunnel;
            m_NetLocalPose.Value = localPose;
            m_NetAttached.Value = true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ClearAttachmentRpc() => m_NetAttached.Value = false;

        void OnNetAttachmentChanged(bool previous, bool current) => ReadAttachmentFromNetwork();

        void OnNetLocalPoseChanged(Vector4 previous, Vector4 current) => ReadAttachmentFromNetwork();

        void ReadAttachmentFromNetwork()
        {
            if (m_Moving || m_Snapping || m_DragCorner >= 0)
                return;

            if (!m_NetAttached.Value || !m_NetTunnel.Value.TryGet(out NetworkObject tunnelObject))
            {
                m_AttachedTunnel = null;
                return;
            }

            m_AttachedTunnel = tunnelObject.GetComponentInChildren<TunnelSegmentGeometry>();
            Vector4 pose = m_NetLocalPose.Value;
            m_AttachedLocalPosition = pose;
            m_AttachedLocalYaw = pose.w;
        }

        #endregion

        void HoldOwnership(bool hold)
        {
            if (m_Claimable == null || !IsSpawned)
                return;
            m_Claimable.ShouldHoldOwnership = hold;
            if (hold)
                m_Claimable.ClaimOwnership();
        }
    }
}
