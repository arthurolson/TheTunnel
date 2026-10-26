using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The Tunnel Segment's "TS Menu" button: opens and closes the panel holding the segment's buttons
/// (Colors, Textures, New Browser, Add Segment, Remove Segment). The colour chooser and the texture list
/// open beside it, one at a time. The menu itself is per-headset; closing it also closes both side panels.
/// </summary>
public class TunnelMenu : MonoBehaviour
{
    [SerializeField]
    Button m_MenuButton;

    [SerializeField]
    TMP_Text m_MenuLabel;

    [SerializeField]
    GameObject m_MenuPanel;

    [SerializeField]
    Button m_ColorsButton;

    [SerializeField]
    Button m_TexturesButton;

    [SerializeField]
    TMP_Text m_TexturesLabel;

    [SerializeField]
    TunnelSegmentAppearance m_Appearance;

    [SerializeField]
    TunnelSegmentTexture m_Texture;

    bool m_Open;

    void Awake()
    {
        if (m_MenuButton != null)
            m_MenuButton.onClick.AddListener(ToggleMenu);
        if (m_TexturesButton != null)
            m_TexturesButton.onClick.AddListener(ToggleTextures);
        // The appearance component toggles the chooser itself; just keep the texture list out of its way.
        if (m_ColorsButton != null)
            m_ColorsButton.onClick.AddListener(() => ShowTextures(false));
        SetOpen(false);
    }

    void ToggleMenu() => SetOpen(!m_Open);

    void SetOpen(bool open)
    {
        m_Open = open;
        if (m_MenuPanel != null)
            m_MenuPanel.SetActive(open);
        if (m_MenuLabel != null)
            m_MenuLabel.text = open ? "Close Menu" : "TS Menu";
        if (!open)
        {
            ShowTextures(false);
            if (m_Appearance != null && m_Appearance.isActiveAndEnabled)
                m_Appearance.SetChooserOpen(false);
        }
    }

    void ToggleTextures()
    {
        bool show = m_Texture == null || !m_Texture.isListOpen;
        if (show && m_Appearance != null)
            m_Appearance.SetChooserOpen(false);
        ShowTextures(show);
    }

    void ShowTextures(bool show)
    {
        if (m_Texture != null)
            m_Texture.ShowList(show);
        if (m_TexturesLabel != null)
            m_TexturesLabel.text = show ? "Hide Textures" : "Textures";
    }
}
