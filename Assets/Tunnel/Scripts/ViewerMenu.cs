using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// The Viewer's menu bar: one button per kind of media (Images, Videos, Spatial Images, Spatial Videos), each opening
/// a scrollable list of what's in that kind's folder (built in and on this headset; see <see cref="ViewerLibrary"/>)
/// followed by the Quest's own photos and videos, newest first. Tap a name to show it; Clear empties the screen. The name field and Keyboard button bring up the keyboard: type a name (or part of one) or an
/// address and press Enter to show it; while typing, the open list only shows names containing the text.
/// Choosing something closes the list; the list opens beside the screen, on its left, so it doesn't cover it.
/// Each row has three check boxes: Save (build this headset's file into the app at the next build, so every headset
/// has it; checked and greyed for media already built in), Rename (brings up the keyboard with the name; Enter renames
/// the file) and Delete (marks the file; "Delete checked" removes the marked ones from this headset). The Quest's own
/// photos and videos are renamed or deleted only once the wearer allows it in the headset's own prompt.
/// &lt; and &gt; on the screen's edges show the previous or next item of the shown kind.
/// Paste shows an address copied in the browser (its Copy button); with the keyboard open it adds the address to
/// the typed text instead, to edit before pressing Enter.
/// The open list is shared, like the TS Menu: whoever opens or closes it does so for everyone.
/// </summary>
public class ViewerMenu : MonoBehaviour
{
    [SerializeField]
    ViewerPanel m_Viewer;

    [SerializeField]
    [Tooltip("The screen the list sits beside and the < and > buttons sit on.")]
    Transform m_ScreenQuad;

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

    [SerializeField]
    [Tooltip("Gap between the list and the screen's left edge (meters).")]
    float m_ListGap = 0.03f;

    [SerializeField]
    Button m_DeleteButton;

    [Header("Previous and next")]
    [SerializeField]
    Button m_PreviousButton;

    [SerializeField]
    Button m_NextButton;

    [Header("Typing")]
    [SerializeField]
    Button m_Field;

    [SerializeField]
    TMP_Text m_FieldText;

    [SerializeField]
    Button m_KeyboardButton;

    [SerializeField]
    Button m_PasteButton;

    [SerializeField]
    Button m_ClearButton;

    [SerializeField]
    XRKeyboard m_Keyboard;

    const string k_FieldPlaceholder = "<color=#999>Name or address</color>";

    sbyte m_ShownList = -2;
    string m_Typed = string.Empty;
    string m_Message;
    Coroutine m_BuildRoutine;
    string m_Notice;
    float m_NoticeUntil;
    readonly HashSet<string> m_ToDelete = new();
    ViewerLibrary.Entry m_Renaming;
    Toggle m_RenameToggle;
    string m_RenameText = string.Empty;

    /// <summary>How long to wait for the wearer to answer the headset's delete or rename prompt.</summary>
    const float k_PromptWait = 180f;
    float m_NextStepCheck;

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
        if (m_PasteButton != null)
            m_PasteButton.onClick.AddListener(Paste);
        if (m_ClearButton != null)
            m_ClearButton.onClick.AddListener(() =>
            {
                if (m_Viewer != null)
                    m_Viewer.Clear();
            });
        if (m_DeleteButton != null)
        {
            m_DeleteButton.onClick.AddListener(DeleteChecked);
            m_DeleteButton.gameObject.SetActive(false);
        }

        if (m_PreviousButton != null)
            m_PreviousButton.onClick.AddListener(() => Step(-1));
        if (m_NextButton != null)
            m_NextButton.onClick.AddListener(() => Step(1));
        UpdateStepButtons();
    }

    IEnumerator Start()
    {
        // The library is read when a list opens or a named item loads; < and > need it before that.
        yield return ViewerLibrary.Refresh();
        UpdateStepButtons();
    }

    void OnEnable()
    {
        if (m_Viewer != null)
        {
            m_Viewer.changed += OnViewerChanged;
            m_Viewer.notice += ShowNotice;
        }

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
        {
            m_Viewer.changed -= OnViewerChanged;
            m_Viewer.notice -= ShowNotice;
        }

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
        if (m_Notice != null && Time.unscaledTime < m_NoticeUntil)
            m_FieldText.text = $"<size=70%>{m_Notice}</size>";
        else if (m_Renaming != null)
            m_FieldText.text = $"<size=70%>Rename to:</size> {m_RenameText}";
        else if (m_Typed.Length > 0)
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
            yield return ViewerLibrary.Refresh(true);
        }

        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);

        var all = ViewerLibrary.OfKind(kind.Value).ToList();
        var marked = ViewerLibrary.ReadSaveList().Select(line => line.path).ToHashSet();
        var shown = m_Typed.Length == 0 ? all : all.Where(e => e.name.IndexOf(m_Typed, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            var entry = shown[i];
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            string note = entry.isLink ? "  <color=#8CF>(link)</color>"
                : entry.isDeviceMedia ? "  <color=#AAA>(Quest)</color>"
                : entry.onHeadset ? "  <color=#AAA>(this headset)</color>" : string.Empty;
            var label = row.transform.Find("Label").GetComponent<TMP_Text>();
            string text = entry.name + note;
            row.onClick.AddListener(() => Choose(entry));
            SetUpCheckBoxes(row, entry, label, text, marked.Contains(entry.file ?? string.Empty));
        }

        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(1, shown.Count) * m_RowSpacing);
        if (m_Scroll != null)
            m_Scroll.verticalNormalizedPosition = 1f;

        string folder = ViewerLibrary.KindFolders[(int)kind.Value];
        if (m_Message != null)
            SetListStatus(m_Message);
        else if (all.Count == 0)
            SetListStatus($"Nothing here yet: add files or links to the {folder} folder, or allow photos and videos.");
        else if (shown.Count == 0)
            SetListStatus($"No names containing \"{m_Typed}\". Enter opens it as typed.");
        else
            SetListStatus(m_Typed.Length > 0 ? $"{shown.Count} of {all.Count} contain \"{m_Typed}\"" : "Tap a name to show it.");
        m_BuildRoutine = null;
        UpdateDeleteButton();
        UpdateStepButtons();
    }

    void SetUpCheckBoxes(Button row, ViewerLibrary.Entry entry, TMP_Text label, string text, bool marked)
    {
        var save = row.transform.Find("Save Toggle")?.GetComponent<Toggle>();
        if (save != null)
        {
            bool builtIn = ViewerLibrary.IsBuiltIn(entry);
            save.SetIsOnWithoutNotify(builtIn || marked);
            save.interactable = !builtIn && entry.file != null;
            save.onValueChanged.AddListener(on =>
            {
                ViewerLibrary.MarkToSave(entry, on);
                ShowNotice(on ? $"{entry.name} goes into the app at the next build." : $"{entry.name} won't go into the app.");
            });
        }

        var rename = row.transform.Find("Rename Toggle")?.GetComponent<Toggle>();
        if (rename != null)
        {
            rename.gameObject.SetActive(entry.canDelete);
            rename.SetIsOnWithoutNotify(m_Renaming != null && m_Renaming.file == entry.file);
            if (rename.isOn)
                m_RenameToggle = rename;
            rename.onValueChanged.AddListener(on =>
            {
                if (on)
                    StartRename(entry, rename);
                else if (m_Renaming != null && m_Renaming.file == entry.file)
                    CancelRename();
            });
        }

        var delete = row.transform.Find("Delete Toggle")?.GetComponent<Toggle>();
        bool toDelete = entry.canDelete && m_ToDelete.Contains(entry.file);
        label.text = toDelete ? DeleteLook(text) : text;
        if (delete == null)
            return;
        delete.gameObject.SetActive(entry.canDelete);
        delete.SetIsOnWithoutNotify(toDelete);
        delete.onValueChanged.AddListener(on =>
        {
            if (on)
                m_ToDelete.Add(entry.file);
            else
                m_ToDelete.Remove(entry.file);
            label.text = on ? DeleteLook(text) : text;
            UpdateDeleteButton();
        });
    }

    static string DeleteLook(string text) => $"<color=#F88><s>{text}</s></color>";

    void UpdateDeleteButton()
    {
        int count = m_ToDelete.Count(file => ViewerLibrary.all.Any(e => e.file == file));
        if (m_DeleteButton == null)
            return;
        m_DeleteButton.gameObject.SetActive(count > 0);
        var text = m_DeleteButton.GetComponentInChildren<TMP_Text>(true);
        if (text != null)
            text.text = $"Delete {count} checked";
    }

    void DeleteChecked()
    {
        var entries = ViewerLibrary.all.Where(e => e.canDelete && m_ToDelete.Contains(e.file)).ToList();
        m_ToDelete.Clear();
        if (entries.Count == 0)
            return;

        var (deleted, asked) = ViewerLibrary.Delete(entries);
        if (entries.Any(e => IsShown(e) && !File.Exists(e.file)))
            m_Viewer.Clear();
        if (asked > 0)
            StartCoroutine(WaitForHeadsetDelete(entries.Where(e => e.isDeviceMedia).ToList()));
        int failed = entries.Count - deleted - asked;
        m_Message = (deleted > 0 ? $"Deleted {deleted}. " : string.Empty)
            + (asked > 0 ? $"Answer the headset's prompt to delete {asked} of its own. " : string.Empty)
            + (failed > 0 ? $"Couldn't delete {failed}." : string.Empty);
        BuildList(true);
    }

    /// <summary>
    /// The headset's delete prompt floats over the app without pausing it, so the files are watched until they're gone
    /// (or the wearer has had time to answer), then the list is read again.
    /// </summary>
    IEnumerator WaitForHeadsetDelete(List<ViewerLibrary.Entry> entries)
    {
        float giveUp = Time.unscaledTime + k_PromptWait;
        while (Time.unscaledTime < giveUp && entries.Any(e => File.Exists(e.file)))
            yield return new WaitForSecondsRealtime(0.5f);

        var gone = entries.Where(e => !File.Exists(e.file)).ToList();
        if (gone.Any(IsShown))
            m_Viewer.Clear();
        ShowNotice(gone.Count == entries.Count ? $"Deleted {gone.Count} of the headset's photos and videos."
            : gone.Count > 0 ? $"Deleted {gone.Count} of {entries.Count}; the headset kept the rest."
            : "The headset didn't delete them.");
        if (openKind != null)
            BuildList(true);
    }

    #region Renaming

    void StartRename(ViewerLibrary.Entry entry, Toggle toggle)
    {
        if (m_RenameToggle != null && m_RenameToggle != toggle)
            m_RenameToggle.SetIsOnWithoutNotify(false);
        m_Renaming = entry;
        m_RenameToggle = toggle;
        m_RenameText = entry.name;
        if (m_Keyboard != null)
            m_Keyboard.Open(entry.name);
        ShowNotice($"Type the new name for {entry.name}, then press Enter.");
    }

    void CancelRename()
    {
        m_Renaming = null;
        if (m_RenameToggle != null)
            m_RenameToggle.SetIsOnWithoutNotify(false);
        m_RenameToggle = null;
        UpdateField();
    }

    void FinishRename(string newName)
    {
        var entry = m_Renaming;
        m_Renaming = null;
        if (m_Keyboard != null && m_Keyboard.isOpen)
            m_Keyboard.Close(true);
        CancelRename();

        newName = ViewerLibrary.CleanName(newName);
        if (newName.Length == 0 || newName == entry.name)
        {
            ShowNotice("Name unchanged.");
            return;
        }

        if (entry.isDeviceMedia && !ViewerLibrary.MayChangeDeviceMedia(entry))
        {
            if (!ViewerLibrary.AskToChangeDeviceMedia(entry))
            {
                ShowNotice($"The headset won't let the app rename {entry.name}.");
                return;
            }

            ShowNotice($"Allow the change in the headset's prompt to rename {entry.name}.");
            StartCoroutine(RenameWhenAllowed(entry, newName));
            return;
        }

        StartCoroutine(Rename(entry, newName));
    }

    IEnumerator RenameWhenAllowed(ViewerLibrary.Entry entry, string newName)
    {
        float giveUp = Time.unscaledTime + k_PromptWait;
        while (Time.unscaledTime < giveUp && !ViewerLibrary.MayChangeDeviceMedia(entry))
            yield return new WaitForSecondsRealtime(0.5f);
        if (!ViewerLibrary.MayChangeDeviceMedia(entry))
        {
            ShowNotice($"The headset didn't allow renaming {entry.name}.");
            yield break;
        }

        yield return Rename(entry, newName);
    }

    IEnumerator Rename(ViewerLibrary.Entry entry, string newName)
    {
        bool shown = IsShown(entry);
        string error = ViewerLibrary.Rename(entry, newName);
        ShowNotice(error ?? $"Renamed {entry.name} to {newName}.");
        if (error != null)
            yield break;

        yield return ViewerLibrary.Refresh();
        var renamed = ViewerLibrary.Find(entry.kind, newName);
        if (shown && renamed != null)
            m_Viewer.Open(renamed);
        if (openKind != null)
            BuildList(false);
    }

    #endregion

    void Choose(ViewerLibrary.Entry entry)
    {
        m_Message = null;
        if (m_Viewer == null)
            return;
        m_Viewer.Open(entry);
        CloseList();
    }

    /// <summary>Once something is chosen the list gets out of the way of the screen (for everyone).</summary>
    void CloseList()
    {
        if (m_Viewer != null && m_Viewer.openList != ViewerPanel.NoList)
            m_Viewer.SetOpenList(ViewerPanel.NoList);
    }

    void SetListStatus(string text)
    {
        if (m_ListStatus != null)
            m_ListStatus.text = text;
    }

    #endregion

    #region Placement, previous and next

    void LateUpdate()
    {
        if (m_ScreenQuad == null)
            return;
        Vector3 right = m_ScreenQuad.right, up = m_ScreenQuad.up, toViewer = -m_ScreenQuad.forward;
        Vector3 center = m_ScreenQuad.position;
        Vector2 half = new Vector2(m_ScreenQuad.lossyScale.x, m_ScreenQuad.lossyScale.y) * 0.5f;

        // The list: left of the screen, tops level.
        if (m_ListPanel != null && m_ListPanel.activeSelf)
        {
            var list = (RectTransform)m_ListPanel.transform;
            Vector2 listHalf = Vector2.Scale(list.rect.size, list.lossyScale) * 0.5f;
            list.position = center - right * (half.x + m_ListGap + listHalf.x) + up * (half.y - listHalf.y) + toViewer * 0.005f;
        }

        // < and >: just inside the screen's left and right edges, halfway up.
        foreach (var (button, side) in new[] { (m_PreviousButton, -1f), (m_NextButton, 1f) })
        {
            if (button == null || !button.gameObject.activeSelf)
                continue;
            var rect = (RectTransform)button.transform;
            float buttonHalf = rect.rect.width * rect.lossyScale.x * 0.5f;
            rect.position = center + right * side * (half.x - buttonHalf - 0.01f) + toViewer * 0.008f;
        }
    }

    /// <summary>The shown kind's list without repeated names (one file can be both built in and on the Quest).</summary>
    List<ViewerLibrary.Entry> StepList() => m_Viewer == null ? new List<ViewerLibrary.Entry>()
        : ViewerLibrary.OfKind(m_Viewer.kind).GroupBy(e => (e.isLink, e.name.ToLowerInvariant())).Select(g => g.First()).ToList();

    bool IsShown(ViewerLibrary.Entry entry) =>
        m_Viewer != null && m_Viewer.hasContent && m_Viewer.kind == entry.kind && (m_Viewer.isUrl
            ? entry.isLink && entry.location == m_Viewer.source
            : !entry.isLink && string.Equals(entry.name, m_Viewer.source, System.StringComparison.OrdinalIgnoreCase));

    void Step(int direction)
    {
        if (m_Viewer == null || !m_Viewer.hasContent)
            return;
        var list = StepList();
        if (list.Count == 0)
            return;
        int at = list.FindIndex(IsShown);
        int next = at < 0 ? (direction > 0 ? 0 : list.Count - 1) : (at + direction + list.Count) % list.Count;
        m_Viewer.Open(list[next]);
    }

    /// <summary>&lt; and &gt; show when the shown kind has more than one item.</summary>
    void UpdateStepButtons()
    {
        bool show = m_Viewer != null && m_Viewer.hasContent && StepList().Count > 1;
        foreach (var button in new[] { m_PreviousButton, m_NextButton })
        {
            if (button != null && button.gameObject.activeSelf != show)
                button.gameObject.SetActive(show);
        }
    }

    #endregion

    #region Paste and notices

    void Paste()
    {
        string text = TunnelClipboard.Paste().Replace("\n", " ").Trim();
        if (text.Length == 0)
        {
            ShowNotice("Nothing to paste: press Copy in a browser first.");
            return;
        }

        if (m_Keyboard != null && m_Keyboard.isOpen)
        {
            m_Keyboard.UpdateText(m_Keyboard.text + text);
            return;
        }

        if (m_Viewer == null)
            return;
        string result = m_Viewer.OpenTyped(text, openKind);
        ShowNotice(result);
        if (result.StartsWith("Opening"))
            CloseList();
    }

    /// <summary>Shown in the name field for a few seconds (and in the open list's status line).</summary>
    void ShowNotice(string text)
    {
        m_Notice = text;
        m_NoticeUntil = Time.unscaledTime + 5f;
        if (m_Viewer != null && m_Viewer.openList >= 0)
        {
            m_Message = text;
            SetListStatus(text);
        }

        UpdateField();
    }

    void Update()
    {
        if (m_Notice != null && Time.unscaledTime >= m_NoticeUntil)
        {
            m_Notice = null;
            UpdateField();
        }

        if (Time.unscaledTime >= m_NextStepCheck)
        {
            m_NextStepCheck = Time.unscaledTime + 0.5f;
            UpdateStepButtons();
        }
    }

    #endregion

    #region Keyboard

    void ToggleKeyboard()
    {
        if (m_Keyboard == null)
            return;
        if (m_Keyboard.isOpen)
        {
            m_Keyboard.Close(false);
        }
        else
        {
            CancelRename();
            m_Keyboard.Open(m_Typed);
        }
    }

    void OnTextUpdated(KeyboardTextEventArgs args)
    {
        string text = (args.keyboardText ?? string.Empty).Replace("\n", string.Empty);
        if (m_Renaming != null)
        {
            m_RenameText = text;
            UpdateField();
            return;
        }

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
        if (m_Renaming != null)
        {
            FinishRename(text);
            return;
        }

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
        CloseList();
    }

    void OnKeyboardClosed(KeyboardTextEventArgs args)
    {
        // Closed without Enter: no rename.
        if (m_Renaming != null)
            CancelRename();
        UpdateField();
    }

    #endregion
}
