using TLab.WebView;
using UnityEngine;

/// <summary>
/// Makes the browser panel's address bar behave like a normal browser's: it shows the current page address after
/// each load, and tapping it clears the field and gives it the keyboard so a new address can be typed straight away.
/// </summary>
public class AddressBarHelper : MonoBehaviour
{
    [SerializeField]
    SearchBar m_SearchBar;

    /// <summary>Wire to the browser's page-finished event.</summary>
    public void ShowUrl(string url)
    {
        if (m_SearchBar == null || m_SearchBar.inputFieldIsActive)
            return; // don't overwrite what the user is typing
        m_SearchBar.m_text = url;
        m_SearchBar.Display();
    }

    /// <summary>Wire to the address bar's tap target.</summary>
    public void BeginEditing()
    {
        if (m_SearchBar == null)
            return;
        m_SearchBar.m_text = string.Empty;
        m_SearchBar.Display();
        m_SearchBar.OnFocus(true);

        // Already focused but the keyboard was hidden (e.g. with its hide key): bring it back.
        var keyboard = m_SearchBar.keyborad;
        if (keyboard != null && !keyboard.isVisible)
            keyboard.SetVisibility(true);
    }
}
