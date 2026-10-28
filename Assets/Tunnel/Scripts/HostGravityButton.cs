using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

/// <summary>
/// The shared gravity switch: a "Gravity" button that floats over the host's right palm while the palm faces them
/// (poke it with the left index finger). Only the host sees it; it switches <see cref="Physics.gravity"/> on or off
/// for everyone, so all toys and molecules fall or float together. It doesn't depend on the game table, and works
/// locally when not connected. Gravity starts off, so everything floats until the host turns it on.
/// </summary>
public class HostGravityButton : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("Shown over the right palm; moved to the palm each frame.")]
    Transform m_PalmButtonRoot;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Label;

    [SerializeField]
    [Tooltip("How far the button floats out from the palm (meters).")]
    float m_PalmOffset = 0.05f;

    [SerializeField]
    [Tooltip("Palm must face the head at least this much (cosine) for the button to appear; it hides again below the lower value.")]
    float m_ShowFacing = 0.65f;

    [SerializeField]
    float m_HideFacing = 0.45f;

    readonly NetworkVariable<bool> m_GravityOn = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    static Vector3 s_DefaultGravity;
    static bool s_DefaultGravityCaptured;

    bool m_LocalGravityOn;
    bool m_ButtonShown;

    bool gravityOn => IsSpawned ? m_GravityOn.Value : m_LocalGravityOn;

    // The host, or the only player when not connected.
    bool canSwitch => !IsSpawned || IsServer;

    void Awake()
    {
        if (!s_DefaultGravityCaptured)
        {
            s_DefaultGravity = Physics.gravity;
            s_DefaultGravityCaptured = true;
        }

        if (m_Button != null)
            m_Button.onClick.AddListener(Toggle);
        if (m_PalmButtonRoot != null)
            m_PalmButtonRoot.gameObject.SetActive(false);
        Apply();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_GravityOn.OnValueChanged += OnGravityChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_GravityOn.OnValueChanged -= OnGravityChanged;
        Apply();
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (m_Button != null)
            m_Button.onClick.RemoveListener(Toggle);
        Physics.gravity = s_DefaultGravity;
    }

    public void Toggle()
    {
        if (!canSwitch)
            return;
        if (IsSpawned)
            m_GravityOn.Value = !m_GravityOn.Value;
        else
            m_LocalGravityOn = !m_LocalGravityOn;
        Apply();
    }

    void OnGravityChanged(bool previous, bool current) => Apply();

    void Apply()
    {
        Physics.gravity = gravityOn ? s_DefaultGravity : Vector3.zero;
        if (m_Label != null)
            m_Label.text = gravityOn ? "Gravity: On" : "Gravity: Off";
    }

    void Update()
    {
        if (canSwitch)
        {
            HandPalm.UpdateButton(Handedness.Right, m_PalmButtonRoot, m_PalmOffset, m_ShowFacing, m_HideFacing, ref m_ButtonShown);
        }
        else if (m_ButtonShown || (m_PalmButtonRoot != null && m_PalmButtonRoot.gameObject.activeSelf))
        {
            m_ButtonShown = false;
            m_PalmButtonRoot.gameObject.SetActive(false);
        }
    }
}
