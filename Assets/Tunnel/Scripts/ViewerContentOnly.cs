using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// The Viewer's content only mode, as everyone sees it: while <see cref="ViewerPanel.chromeHidden"/>, the frame, its
/// resize corners, the menu bar (with its list, &lt; and &gt;, and move handle) and the keyboard are hidden, leaving
/// only the picture or video floating where it was, at the same size. (The video controls and the dark backdrop are
/// hidden by <see cref="ViewerPanel"/> itself.) One small "show frame" button sits just off the media's lower right
/// corner, always the same size, and follows the corner when the media changes shape; pressing it brings everything
/// back as it was, the keyboard open again if it was open.
/// </summary>
public class ViewerContentOnly : MonoBehaviour
{
    [SerializeField]
    ViewerPanel m_Viewer;

    [SerializeField]
    [Tooltip("The quad showing the media: the button sits at its lower right corner.")]
    Transform m_ScreenQuad;

    [SerializeField]
    [Tooltip("Everything hidden in content only: frame bars, corner markers, the menu bar and the keyboard.")]
    GameObject[] m_Chrome = new GameObject[0];

    [SerializeField]
    [Tooltip("The keyboard's keys, reopened on the way back if they were open.")]
    XRKeyboard m_Keyboard;

    [SerializeField]
    [Tooltip("The small canvas holding the show frame button; shown only in content only.")]
    RectTransform m_ShowFrame;

    [SerializeField]
    Button m_ShowFrameButton;

    [SerializeField]
    [Tooltip("Gap between the media's corner and the button (meters).")]
    float m_Gap = 0.008f;

    bool m_Hidden;
    bool m_KeyboardWasOpen;
    // Which chrome was showing when it was hidden, so only that comes back.
    bool[] m_WasActive = new bool[0];

    void Awake()
    {
        if (m_ShowFrame != null)
            m_ShowFrame.gameObject.SetActive(false);
        if (m_ShowFrameButton != null)
            m_ShowFrameButton.onClick.AddListener(() =>
            {
                if (m_Viewer != null)
                    m_Viewer.SetContentOnly(false);
            });
    }

    void OnEnable()
    {
        if (m_Viewer != null)
            m_Viewer.changed += Apply;
        Apply();
    }

    void OnDisable()
    {
        if (m_Viewer != null)
            m_Viewer.changed -= Apply;
    }

    void Apply()
    {
        bool hide = m_Viewer != null && m_Viewer.chromeHidden;
        if (hide == m_Hidden)
            return;
        m_Hidden = hide;

        if (hide)
        {
            m_KeyboardWasOpen = m_Keyboard != null && m_Keyboard.isOpen;
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

            // Hiding the keys forgets they were open (they'd show but not type); open them again with their text.
            if (m_KeyboardWasOpen && m_Keyboard != null && m_Keyboard.gameObject.activeInHierarchy && !m_Keyboard.isOpen)
                m_Keyboard.Open(m_Keyboard.text);
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

    /// <summary>Just outside the media's lower right corner, a little in front of it; the button's size never changes.</summary>
    void Place()
    {
        if (m_ShowFrame == null || m_ScreenQuad == null)
            return;
        Vector3 right = m_ScreenQuad.right, up = m_ScreenQuad.up, toViewer = -m_ScreenQuad.forward;
        Vector2 half = new Vector2(m_ScreenQuad.lossyScale.x, m_ScreenQuad.lossyScale.y) * 0.5f;
        Vector2 buttonHalf = Vector2.Scale(m_ShowFrame.rect.size, m_ShowFrame.lossyScale) * 0.5f;
        m_ShowFrame.SetPositionAndRotation(
            m_ScreenQuad.position + right * (half.x + m_Gap + buttonHalf.x) - up * (half.y + m_Gap + buttonHalf.y) + toViewer * 0.005f,
            m_ScreenQuad.rotation);
    }
}
