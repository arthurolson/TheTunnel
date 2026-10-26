using System;
using System.Collections.Generic;
using TLab.WebView;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Favorites and history for the browser panel, kept on this headset (PlayerPrefs).
/// Every page that finishes loading goes to the top of the history; "Favorite" adds or removes the current page;
/// "Saved" opens a list of both, and choosing an entry opens it.
/// </summary>
public class BrowserBookmarks : MonoBehaviour
{
    const string k_PrefsKey = "BrowserPanel.Bookmarks";

    [Serializable]
    class Data
    {
        public List<string> favorites = new();
        public List<string> history = new();
    }

    [SerializeField]
    BrowserContainer m_Container;

    [SerializeField]
    [Tooltip("Opens saved pages for every player (optional).")]
    BrowserSync m_Sync;

    [Header("Address bar buttons")]
    [SerializeField]
    Button m_FavoriteButton;

    [SerializeField]
    TMP_Text m_FavoriteLabel;

    [SerializeField]
    Button m_ListButton;

    [Header("Saved pages panel")]
    [SerializeField]
    GameObject m_ListPanel;

    [SerializeField]
    RectTransform m_FavoritesColumn;

    [SerializeField]
    RectTransform m_HistoryColumn;

    [SerializeField]
    [Tooltip("Inactive row used as the template for list entries (a Button with a TMP label).")]
    Button m_RowTemplate;

    [SerializeField]
    Button m_ClearHistoryButton;

    [SerializeField]
    int m_MaxHistory = 50;

    [SerializeField]
    int m_RowsShown = 7;

    [SerializeField]
    float m_RowSpacing = 58f;

    Data m_Data = new();
    string m_CurrentUrl = string.Empty;

    void Awake()
    {
        Load();
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_FavoriteButton != null)
            m_FavoriteButton.onClick.AddListener(ToggleFavorite);
        if (m_ListButton != null)
            m_ListButton.onClick.AddListener(ToggleList);
        if (m_ClearHistoryButton != null)
            m_ClearHistoryButton.onClick.AddListener(ClearHistory);
        UpdateFavoriteLabel();
    }

    /// <summary>Wire to the browser's page-finished event.</summary>
    public void OnPageFinished(string url)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith("about:") || url.StartsWith("data:"))
            return;

        m_CurrentUrl = url;
        m_Data.history.Remove(url);
        m_Data.history.Insert(0, url);
        if (m_Data.history.Count > m_MaxHistory)
            m_Data.history.RemoveRange(m_MaxHistory, m_Data.history.Count - m_MaxHistory);
        Save();
        UpdateFavoriteLabel();
        if (m_ListPanel != null && m_ListPanel.activeSelf)
            Rebuild();
    }

    public void ToggleFavorite()
    {
        if (string.IsNullOrEmpty(m_CurrentUrl))
            return;
        if (!m_Data.favorites.Remove(m_CurrentUrl))
            m_Data.favorites.Insert(0, m_CurrentUrl);
        Save();
        UpdateFavoriteLabel();
        if (m_ListPanel != null && m_ListPanel.activeSelf)
            Rebuild();
    }

    public void ToggleList()
    {
        if (m_ListPanel == null)
            return;
        bool open = !m_ListPanel.activeSelf;
        m_ListPanel.SetActive(open);
        if (open)
            Rebuild();
    }

    public void ClearHistory()
    {
        m_Data.history.Clear();
        Save();
        Rebuild();
    }

    void OpenUrl(string url)
    {
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_Sync != null)
            m_Sync.LoadUrl(url);
        else if (m_Container != null && m_Container.browser != null)
            m_Container.browser.LoadUrl(url);
    }

    void UpdateFavoriteLabel()
    {
        if (m_FavoriteLabel != null)
            m_FavoriteLabel.text = m_Data.favorites.Contains(m_CurrentUrl) ? "Favorite: On" : "Favorite";
    }

    void Rebuild()
    {
        Fill(m_FavoritesColumn, m_Data.favorites, "No favorites yet: press Favorite on a page");
        Fill(m_HistoryColumn, m_Data.history, "No pages visited yet");
    }

    void Fill(RectTransform column, List<string> urls, string emptyText)
    {
        if (column == null || m_RowTemplate == null)
            return;

        for (int i = column.childCount - 1; i >= 0; i--)
            Destroy(column.GetChild(i).gameObject);

        int rows = Mathf.Min(urls.Count, m_RowsShown);
        for (int i = 0; i < Mathf.Max(rows, 1); i++)
        {
            var row = Instantiate(m_RowTemplate, column);
            row.gameObject.SetActive(true);
            var rect = (RectTransform)row.transform;
            rect.anchoredPosition = new Vector2(0f, -i * m_RowSpacing);

            var label = row.GetComponentInChildren<TMP_Text>(true);
            if (rows == 0)
            {
                label.text = emptyText;
                row.interactable = false;
                continue;
            }

            string url = urls[i];
            label.text = Pretty(url);
            row.onClick.AddListener(() => OpenUrl(url));
        }
    }

    static string Pretty(string url)
    {
        string s = url;
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (s.StartsWith(prefix))
                s = s.Substring(prefix.Length);
        }

        if (s.StartsWith("www."))
            s = s.Substring(4);
        s = s.TrimEnd('/');
        return s.Length > 46 ? s.Substring(0, 43) + "..." : s;
    }

    void Load()
    {
        string json = PlayerPrefs.GetString(k_PrefsKey, string.Empty);
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                m_Data = JsonUtility.FromJson<Data>(json) ?? new Data();
            }
            catch (ArgumentException)
            {
                m_Data = new Data();
            }
        }
    }

    void Save()
    {
        PlayerPrefs.SetString(k_PrefsKey, JsonUtility.ToJson(m_Data));
        PlayerPrefs.Save();
    }
}
