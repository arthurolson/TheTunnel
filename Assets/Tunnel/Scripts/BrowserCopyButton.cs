using System.Collections;
using TLab.WebView;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The browser panel's Copy button: copies the address of the page it shows, to paste into a Viewer (its Paste
/// button) or anywhere else on the headset. Open an image or video on its own in the browser to copy its direct
/// address; for an ordinary page the Viewer looks for the page's main picture or video itself.
/// </summary>
public class BrowserCopyButton : MonoBehaviour
{
    const string k_CopyText = "Copy";

    [SerializeField]
    BrowserContainer m_Container;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Label;

    Coroutine m_FlashRoutine;

    void Awake()
    {
        if (m_Button != null)
            m_Button.onClick.AddListener(Copy);
        SetLabel(k_CopyText);
    }

    void OnDisable()
    {
        m_FlashRoutine = null;
        SetLabel(k_CopyText);
    }

    void Copy()
    {
        string url = m_Container != null && m_Container.browser != null ? m_Container.browser.GetUrl() : null;
        if (string.IsNullOrEmpty(url))
        {
            Flash("No page");
            return;
        }

        TunnelClipboard.Copy(url);
        Debug.Log($"[Browser] copied {url}");
        Flash("Copied");
    }

    void Flash(string text)
    {
        if (m_FlashRoutine != null)
            StopCoroutine(m_FlashRoutine);
        m_FlashRoutine = StartCoroutine(FlashRoutine(text));
    }

    IEnumerator FlashRoutine(string text)
    {
        SetLabel(text);
        yield return new WaitForSeconds(1.5f);
        SetLabel(k_CopyText);
        m_FlashRoutine = null;
    }

    void SetLabel(string text)
    {
        if (m_Label != null)
            m_Label.text = text;
    }
}
