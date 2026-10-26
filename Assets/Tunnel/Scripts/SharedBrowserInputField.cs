using TLab.WebView;
using UnityEngine;

/// <summary>
/// The browser page's text input (keys typed on the keyboard go into the focused field on the page), sent through
/// <see cref="BrowserSync"/> so the other players' copies of the page receive the same typing.
/// </summary>
public class SharedBrowserInputField : BrowserInputField
{
    const int k_BackspaceKeyCode = 67;

    [SerializeField]
    BrowserSync m_Sync;

    public override void OnBackSpaceKey()
    {
        if (m_Sync == null)
        {
            base.OnBackSpaceKey();
            return;
        }

        m_Sync.PressKey(k_BackspaceKeyCode);
        AfterOnBackSpaceKey();
    }

    public override void AddKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (m_Sync == null)
            base.AddKey(key);
        else
            m_Sync.TypeCharacter(key[0]);
    }
}
