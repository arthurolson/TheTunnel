using TMPro;
using Unity.Netcode;
using UnityEngine.UI;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Networked gravity switch for the Sandbox game mode. Any player can press the button; the server flips
    /// one shared value and every client applies it to <see cref="Physics.gravity"/>, so all toys (including
    /// the reduced gravity of <see cref="SmallScalePhysicsModifier"/>) float or fall together for everyone.
    /// Gravity is only changed while the Sandbox UI is active; other game modes always get normal gravity.
    /// </summary>
    public class SandboxGravityToggle : NetworkBehaviour
    {
        [SerializeField]
        Button m_Button;

        [SerializeField]
        TMP_Text m_Label;

        /// <summary>
        /// The Sandbox UI root. Gravity is switched off only while this is active in the hierarchy.
        /// </summary>
        [SerializeField]
        GameObject m_SandboxUIRoot;

        readonly NetworkVariable<bool> m_GravityOn = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        static Vector3 s_DefaultGravity;
        static bool s_DefaultGravityCaptured;

        bool m_LocalGravityOn = true;
        bool m_AppliedGravityOn = true;

        bool gravityOn => IsSpawned ? m_GravityOn.Value : m_LocalGravityOn;

        void Awake()
        {
            if (!s_DefaultGravityCaptured)
            {
                s_DefaultGravity = Physics.gravity;
                s_DefaultGravityCaptured = true;
            }

            if (m_Button != null)
                m_Button.onClick.AddListener(OnButtonPressed);
        }

        ///<inheritdoc/>
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_GravityOn.OnValueChanged += OnGravityChanged;
            UpdateLabel();
        }

        ///<inheritdoc/>
        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            m_GravityOn.OnValueChanged -= OnGravityChanged;
            ApplyGravity(true);
        }

        ///<inheritdoc/>
        public override void OnDestroy()
        {
            base.OnDestroy();
            if (m_Button != null)
                m_Button.onClick.RemoveListener(OnButtonPressed);
            ApplyGravity(true);
        }

        void Update()
        {
            // Off only while the Sandbox is showing, so switching game modes always restores normal gravity.
            bool sandboxActive = m_SandboxUIRoot == null || m_SandboxUIRoot.activeInHierarchy;
            bool wantGravityOn = gravityOn || !sandboxActive;
            if (wantGravityOn != m_AppliedGravityOn)
                ApplyGravity(wantGravityOn);
        }

        void OnButtonPressed()
        {
            if (IsSpawned)
            {
                ToggleGravityRpc();
            }
            else
            {
                // Not connected (e.g. testing in the Editor): toggle locally.
                m_LocalGravityOn = !m_LocalGravityOn;
                UpdateLabel();
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ToggleGravityRpc()
        {
            m_GravityOn.Value = !m_GravityOn.Value;
        }

        void OnGravityChanged(bool previous, bool current)
        {
            UpdateLabel();
        }

        void ApplyGravity(bool on)
        {
            m_AppliedGravityOn = on;
            Physics.gravity = on ? s_DefaultGravity : Vector3.zero;
        }

        void UpdateLabel()
        {
            if (m_Label != null)
                m_Label.text = gravityOn ? "Gravity: On" : "Gravity: Off";
        }
    }
}
