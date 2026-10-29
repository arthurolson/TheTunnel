using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// The Viewer's menu bar: one button per kind of media (Images, Videos, Spatial Images, Spatial Videos), each opening
/// a scrollable list of what's in that kind's folder (built in and on this headset; see <see cref="ViewerLibrary"/>).
/// Tap a name to show it. The name field and Keyboard button bring up the keyboard: type a name (or part of one) or an
/// address and press Enter to show it; while typing, the open list only shows names containing the text.
/// The open list is shared, like the TS Menu: whoever opens or closes it does so for everyone.
/// </summary>
public class ViewerMenu : MonoBehaviour
{
    [SerializeField]
    ViewerPanel m_Viewer;

    [Header("Kinds of media (Images, Videos, Spatial Images, Spatial Videos)")]
    [SerializeField]
    Button[] m_KindButtons = new Button[4];

    [SerializeField]
    TMP_Text[] m_KindLabels = new TMP_Text[4];

    [Header("List")]
    [SerializeField]
    GameObject m_ListPanel;

    [SerializeField]
    TMP_Text m_ListTitle;

    [SerializeField]
    TMP_Text m_ListStatus;

    [SerializeField]
    ScrollRect m_Scroll;

    [SerializeField]
    RectTransform m_ListContent;

    [SerializeField]
    [Tooltip("Inactive row used as the template for list entries (a Button with a TMP label).")]
    Button m_RowTemplate;

    [SerializeField]
    Button m_RefreshButton;

    [SerializeField]
    float m_RowSpacing = 58f;

    [Header("Typing")]
    [SerializeField]
    Button m_Field;

    [SerializeField]
    TMP_Text m_FieldText;

    [SerializeField]
    Button m_KeyboardButton;

    [SerializeField]
    XRKeyboard m_Keyboard;

    const string k_FieldPlaceholder = "<color=#999>Name or address</color>";

    sbyte m_ShownList = -2;
    string m_Typed = string.Empty;
    string m_Message;
    Coroutine m_BuildRoutine;

    ViewerMediaKind? openKind => m_Viewer != null && m_Viewer.openList >= 0 ? (ViewerMediaKind)m_Viewer.openList : null;

    void Awake()
    {
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);

        for (int i = 0; i < m_KindButtons.Length; i++)
        {
            int kind = i;
            if (m_KindButtons[i] != null)
                m_KindButtons[i].onClick.AddListener(() => ToggleList((sbyte)kind));
        }

        if (m_RefreshButton != null)
            m_RefreshButton.onClick.AddListener(() => BuildList(true));
        if (m_Field != null)
            m_Field.onClick.AddListener(ToggleKeyboard);
        if (m_KeyboardButton != null)
            m_KeyboardButton.onClick.AddListener(ToggleKeyboard);
    }

    void OnEnable()
    {
        if (m_Viewer != null)
            m_Viewer.changed += OnViewerChanged;
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextUpdated.AddListener(OnTextUpdated);
            m_Keyboard.onTextSubmitted.AddListener(OnTextSubmitted);
            m_Keyboard.onClosed.AddListener(OnKeyboardClosed);
        }

        OnViewerChanged();
    }

    void OnDisable()
    {
        if (m_Viewer != null)
            m_Viewer.changed -= OnViewerChanged;
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextUpdated.RemoveListener(OnTextUpdated);
            m_Keyboard.onTextSubmitted.RemoveListener(OnTextSubmitted);
            m_Keyboard.onClosed.RemoveListener(OnKeyboardClosed);
        }
    }

    void ToggleList(sbyte kind)
    {
        if (m_Viewer != null)
            m_Viewer.SetOpenList(m_Viewer.openList == kind ? ViewerPanel.NoList : kind);
    }

    void OnViewerChanged()
    {
        if (m_Viewer == null)
            return;

        sbyte list = m_Viewer.openList;
        if (list != m_ShownList)
        {
            m_ShownList = list;
            m_Message = null;
            for (int i = 0; i < m_KindLabels.Length; i++)
            {
                if (m_KindLabels[i] != null)
                    m_KindLabels[i].text = i == list ? $"<u>{ViewerLibrary.KindLabels[i]}</u>" : ViewerLibrary.KindLabels[i];
            }

            if (m_ListPanel != null)
                m_ListPanel.SetActive(list >= 0);
            if (list >= 0)
                BuildList(true);
        }

        UpdateField();
    }

    void UpdateField()
    {
        if (m_FieldText == null)
            return;
        if (m_Typed.Length > 0)
            m_FieldText.text = m_Typed;
        else if (m_Viewer != null && m_Viewer.hasContent && m_Viewer.title.Length > 0)
            m_FieldText.text = m_Viewer.title;
        else
            m_FieldText.text = k_FieldPlaceholder;
    }

    #region List

    void BuildList(bool rescan)
    {
        if (m_BuildRoutine != null)
            StopCoroutine(m_BuildRoutine);
        if (isActiveAndEnabled)
            m_BuildRoutine = StartCoroutine(BuildListRoutine(rescan));
    }

    IEnumerator BuildListRoutine(bool rescan)
    {
        var kind = openKind;
        if (kind == null || m_ListContent == null || m_RowTemplate == null)
            yield break;

        if (m_ListTitle != null)
            m_ListTitle.text = ViewerLibrary.KindLabels[(int)kind.Value];
        if (rescan)
        {
            SetListStatus("Looking for media...");
            yield return ViewerLibrary.Refresh();
        }

        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);

        var all = ViewerLibrary.OfKind(kind.Value).ToList();
        var shown = m_Typed.Length == 0 ? all : all.Where(e => e.name.IndexOf(m_Typed, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            var entry = shown[i];
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            string note = entry.isLink ? "  <color=#8CF>(link)</color>" : entry.onHeadset ? "  <color=#AAA>(this headset)</color>" : string.Empty;
            row.GetComponentInChildren<TMP_Text>(true).text = entry.name + note;
            row.onClick.AddListener(() => Choose(entry));
        }

        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(1, shown.Count) * m_RowSpacing);
        if (m_Scroll != null)
            m_Scroll.verticalNormalizedPosition = 1f;

        string folder = ViewerLibrary.KindFolders[(int)kind.Value];
        if (m_Message != null)
            SetListStatus(m_Message);
        else if (all.Count == 0)
            SetListStatus($"Nothing here yet: add files or links to the {folder} folder.");
        else if (shown.Count == 0)
            SetListStatus($"No names containing \"{m_Typed}\". Enter opens it as typed.");
        else
            SetListStatus(m_Typed.Length > 0 ? $"{shown.Count} of {all.Count} contain \"{m_Typed}\"" : "Tap a name to show it.");
        m_BuildRoutine = null;
    }

    void Choose(ViewerLibrary.Entry entry)
    {
        m_Message = null;
        if (m_Viewer != null)
            m_Viewer.Open(entry);
        SetListStatus($"Showing {entry.name}");
    }

    void SetListStatus(string text)
    {
        if (m_ListStatus != null)
            m_ListStatus.text = text;
    }

    #endregion

    #region Keyboard

    void ToggleKeyboard()
    {
        if (m_Keyboard == null)
            return;
        if (m_Keyboard.isOpen)
            m_Keyboard.Close(false);
        else
            m_Keyboard.Open(m_Typed);
    }

    void OnTextUpdated(KeyboardTextEventArgs args)
    {
        string text = (args.keyboardText ?? string.Empty).Replace("\n", string.Empty);
        if (text == m_Typed)
            return;
        m_Typed = text;
        m_Message = null;
        UpdateField();
        if (openKind != null)
            BuildList(false);
    }

    void OnTextSubmitted(KeyboardTextEventArgs args)
    {
        string text = (args.keyboardText ?? string.Empty).Replace("\n", string.Empty).Trim();
        if (m_Viewer == null || text.Length == 0)
            return;

        // Typed while a list is open: a name matching one row opens that row's media.
        string result = m_Viewer.OpenTyped(text, openKind);
        bool opened = result.StartsWith("Opening");
        m_Message = result;
        SetListStatus(result);
        if (!opened)
            return;

        m_Typed = string.Empty;
        m_Keyboard.Close(true);
        UpdateField();
        if (openKind != null)
            BuildList(false);
    }

    void OnKeyboardClosed(KeyboardTextEventArgs args) => UpdateField();

    #endregion
}
