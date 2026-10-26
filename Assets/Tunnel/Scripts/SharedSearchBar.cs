using TLab.WebView;
using UnityEngine;

/// <summary>
/// The browser panel's address bar: Enter opens the typed address (or a Google search) for every player at once,
/// through <see cref="BrowserSync"/>.
/// </summary>
public class SharedSearchBar : SearchBar
{
    [SerializeField]
    BrowserSync m_Sync;

    protected override void HandlingOnEnterKey()
    {
        if (m_Sync == null)
        {
            base.HandlingOnEnterKey();
            return;
        }

        string text = m_text.Trim();
        if (text.Length == 0)
            return;
        bool isAddress = text.StartsWith("https://") || text.StartsWith("http://");
        m_Sync.LoadUrl(isAddress ? text : $"https://www.google.com/search?q={UnityEngine.Networking.UnityWebRequest.EscapeURL(text)}");
    }
}
