using TLab.VKeyborad;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The browser's content only mode, like the Viewer's: the bar's Content only button hides the frame, its resize
/// corners, the address bar (with its buttons, bookmarks and history) and the keyboard for everyone, leaving only the
/// web page floating where it was, at the same size. The page itself still works: it can be scrolled and tapped, and
/// its videos keep playing. One small "show frame" button just off the page's lower right corner, always the same
/// size, brings everything back as it was, the keyboard open again if it was open. The panel can't be moved or resized
/// until the frame is back. Shared with everyone, players who join later included; works locally when not connected.
/// </summary>
public class BrowserContentOnly : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("The canvas showing the web page: the show frame button sits at its lower right corner.")]
    RectTransform m_Screen;

    [SerializeField]
    [Tooltip("Everything hidden in content only: frame bars, corner markers, the address bar and the keyboard.")]
    GameObject[] m_Chrome = new GameObject[0];

    [SerializeField]
    [Tooltip("The keyboard, reopened on the way back if it was open.")]
    BaseVKeyborad m_Keyboard;

    [SerializeField]
    [Tooltip("The bar's Content only button.")]
    Button m_ContentOnlyButton;

    [SerializeField]
    [Tooltip("The small canvas holding the show frame button; shown only in content only.")]
    RectTransform m_ShowFrame;

    [SerializeField]
    Button m_ShowFrameButton;

    [SerializeField]
    [Tooltip("Gap between the page's corner and the button (meters).")]
    float m_Gap = 0.008f;

    readonly NetworkVariable<bool> m_ContentOnly = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    bool m_LocalContentOnly;
    bool m_Hidden;
    bool m_KeyboardWasOpen;
    // Which chrome was showing when it was hidden, so only that comes back.
    bool[] m_WasActive = new bool[0];
    readonly Vector3[] m_Corners = new Vector3[4];

    /// <summary>Only the page is showing (for everyone).</summary>
    public bool contentOnly => IsSpawned ? m_ContentOnly.Value : m_LocalContentOnly;

    void Awake()
    {
        if (m_ShowFrame != null)
            m_ShowFrame.gameObject.SetActive(false);
        if (m_ContentOnlyButton != null)
            m_ContentOnlyButton.onClick.AddListener(() => SetContentOnly(true));
        if (m_ShowFrameButton != null)
            m_ShowFrameButton.onClick.AddListener(() => SetContentOnly(false));
    }

    ///<inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_ContentOnly.OnValueChanged += OnContentOnlyChanged;
        Apply();
    }

    ///<inheritdoc/>
    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_ContentOnly.OnValueChanged -= OnContentOnlyChanged;
    }

    /// <summary>
    /// Content only (true): the frame, address bar and keyboard go for everyone, and only the page stays, at the same
    /// place and size, with a small button at its lower right corner to bring them back (false).
    /// </summary>
    public void SetContentOnly(bool on)
    {
        Debug.Log($"[Browser] {name}: content only {(on ? "on" : "off")}");
        if (IsSpawned)
        {
            SetContentOnlyRpc(on);
            return;
        }

        m_LocalContentOnly = on;
        Apply();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetContentOnlyRpc(bool on) => m_ContentOnly.Value = on;

    void OnContentOnlyChanged(bool previous, bool current) => Apply();

    void Apply()
    {
        bool hide = contentOnly;
        if (hide == m_Hidden)
            return;
        m_Hidden = hide;

        if (hide)
        {
            m_KeyboardWasOpen = m_Keyboard != null && m_Keyboard.isVisible;
            m_WasActive = new bool[m_Chrome.Length];
            for (int i = 0; i < m_Chrome.Length; i++)
            {
                if (m_Chrome[i] == null)
                    continue;
                m_WasActive[i] = m_Chrome[i].activeSelf;
                m_Chrome[i].SetActive(false);
            }
        }
        else
        {
            for (int i = 0; i < m_Chrome.Length; i++)
            {
                if (m_Chrome[i] != null && (i >= m_WasActive.Length || m_WasActive[i]))
                    m_Chrome[i].SetActive(true);
            }

            // Hiding the keys forgets they were open (they'd show but not type); open them again.
            if (m_KeyboardWasOpen && m_Keyboard != null && m_Keyboard.isActiveAndEnabled && !m_Keyboard.isVisible)
                m_Keyboard.SetVisibility(true);
            m_KeyboardWasOpen = false;
        }

        if (m_ShowFrame != null)
        {
            m_ShowFrame.gameObject.SetActive(hide);
            if (hide)
                Place();
        }
    }

    void LateUpdate()
    {
        if (m_Hidden)
            Place();
    }

    /// <summary>Just outside the page's lower right corner, a little in front of it; the button's size never changes.</summary>
    void Place()
    {
        if (m_ShowFrame == null || m_Screen == null)
            return;
        m_Screen.GetWorldCorners(m_Corners); // bottom-left, top-left, top-right, bottom-right
        Vector3 right = m_Screen.right, up = m_Screen.up, toViewer = -m_Screen.forward;
        Vector2 buttonHalf = Vector2.Scale(m_ShowFrame.rect.size, m_ShowFrame.lossyScale) * 0.5f;
        m_ShowFrame.SetPositionAndRotation(
            m_Corners[3] + right * (m_Gap + buttonHalf.x) - up * (m_Gap + buttonHalf.y) + toViewer * 0.005f,
            m_Screen.rotation);
    }
}
