using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The browser panel's Close button: tap it, then tap again within a few seconds to remove the panel for everyone
/// (the second tap guards against closing a page by accident).
/// </summary>
public class BrowserCloseButton : MonoBehaviour
{
    const string k_CloseText = "Close";
    const string k_ConfirmText = "Tap again";

    [SerializeField]
    BrowserPanel m_Panel;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Label;

    [SerializeField]
    [Tooltip("Seconds the button waits for its confirming second tap.")]
    float m_ConfirmTime = 3f;

    Coroutine m_ConfirmRoutine;

    void Awake()
    {
        if (m_Button != null)
            m_Button.onClick.AddListener(OnPressed);
        SetLabel(k_CloseText);
    }

    void OnDisable()
    {
        m_ConfirmRoutine = null;
        SetLabel(k_CloseText);
    }

    void OnPressed()
    {
        if (m_ConfirmRoutine == null)
        {
            m_ConfirmRoutine = StartCoroutine(WaitForConfirm());
            return;
        }

        StopCoroutine(m_ConfirmRoutine);
        m_ConfirmRoutine = null;
        SetLabel(k_CloseText);
        if (m_Panel != null)
            m_Panel.Close();
    }

    IEnumerator WaitForConfirm()
    {
        SetLabel(k_ConfirmText);
        yield return new WaitForSeconds(m_ConfirmTime);
        SetLabel(k_CloseText);
        m_ConfirmRoutine = null;
    }

    void SetLabel(string text)
    {
        if (m_Label != null)
            m_Label.text = text;
    }
}
