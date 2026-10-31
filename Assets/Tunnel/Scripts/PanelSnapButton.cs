using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The browser's and the Viewer's Unsnap button: takes the panel off its tunnel wall, so it stays wherever it is let
/// go; pressed again (it then reads Snap) the panel goes back onto the nearest wall. Shared with everyone.
/// </summary>
public class PanelSnapButton : MonoBehaviour
{
    const string k_UnsnapText = "Unsnap";
    const string k_SnapText = "Snap";

    [SerializeField]
    BrowserPanel m_Panel;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Label;

    void Awake()
    {
        if (m_Button != null)
            m_Button.onClick.AddListener(OnPressed);
    }

    void OnEnable()
    {
        if (m_Panel != null)
            m_Panel.freeChanged += UpdateLabel;
        UpdateLabel();
    }

    void OnDisable()
    {
        if (m_Panel != null)
            m_Panel.freeChanged -= UpdateLabel;
    }

    void OnPressed()
    {
        if (m_Panel != null)
            m_Panel.SetFree(!m_Panel.isFree);
    }

    void UpdateLabel()
    {
        if (m_Label != null)
            m_Label.text = m_Panel != null && m_Panel.isFree ? k_SnapText : k_UnsnapText;
    }
}
