using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using XRMultiplayer;

/// <summary>
/// The TS Menu's "Saved states" list: Save now keeps everything set up in the tunnel, in every segment, as a numbered
/// state (State 1, State 2, ...; see <see cref="TunnelSavedState"/>), on the host first and then in the asset repository
/// for every headset (see <see cref="TunnelSharedStates"/>); one not uploaded yet is marked so. Each saved state is
/// listed with when it was saved and what it holds, those shared by other headsets too; tap one to check it, then
/// Restore (pressed twice within a few seconds, as it takes away what's there now) brings back exactly what was saved,
/// for everyone, or Delete removes it (a shared one from every headset). The line under the title says how sharing
/// stands (no token on this headset, offline...). In a session everyone sees the host's list and the same messages, and
/// saving, restoring and deleting from any headset is done by the host; which state is checked is each player's own.
/// Works locally when not connected.
/// </summary>
public class TunnelSaveStateMenu : NetworkBehaviour
{
    [SerializeField] GameObject m_ListPanel;
    [SerializeField] Button m_SaveButton;
    [SerializeField] RectTransform m_ListContent;
    [SerializeField] Button m_RowTemplate;
    [SerializeField] Button m_RestoreButton;
    [SerializeField] Button m_DeleteButton;
    [SerializeField] TMP_Text m_StatusText;
    [SerializeField] float m_RowSpacing = 58f;

    [SerializeField]
    [Tooltip("Seconds the second press of Restore has to come in.")]
    float m_RestoreConfirmTime = 4f;

    [Header("What states are made of (filled in by Tunnel > Menu > Add Saved States)")]
    [SerializeField] TunnelPrefabRegistry m_Prefabs;
    [SerializeField] GameObject m_SplatBoxPrefab;
    [SerializeField] SpawnNozzle m_NozzlePrefab;
    [SerializeField] TunnelFigure m_FigurePrefab;
    [SerializeField] TunnelSign m_SignPrefab;

    static readonly List<TunnelSaveStateMenu> s_Menus = new();

    // The host's sharing status, then one line per saved state: id, name, date and summary, and 1 if not uploaded yet
    // (tab-separated).
    readonly NetworkVariable<FixedString4096Bytes> m_Saved = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    string m_Checked = ""; // a state's id
    string m_Shown;
    Coroutine m_RestoreConfirm;
    bool m_Fetching;

    bool isListOpen => m_ListPanel != null && m_ListPanel.activeSelf;

    /// <summary>The prefabs a state is made from.</summary>
    public TunnelSaveState.Prefabs prefabs => new()
    {
        registry = m_Prefabs,
        splatBox = m_SplatBoxPrefab,
        nozzle = m_NozzlePrefab,
        figure = m_FigurePrefab,
        sign = m_SignPrefab,
    };

    /// <summary>
    /// The saved states as this player sees them (the host's in a session), oldest first: shared ones and those saved
    /// on the host and not uploaded yet.
    /// </summary>
    public List<TunnelSharedStates.Item> saved
    {
        get
        {
            if (!IsSpawned)
                return TunnelSharedStates.Everything();
            var lines = m_Saved.Value.ToString().Split('\n');
            var items = new List<TunnelSharedStates.Item>();
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3)
                    items.Add(new TunnelSharedStates.Item { id = parts[0], name = parts[1], info = parts[2], local = parts.Length > 3 && parts[3] == "1" });
            }
            return items;
        }
    }

    /// <summary>How sharing stands, as this player sees it (the host's in a session).</summary>
    public string sharing => IsSpawned ? m_Saved.Value.ToString().Split('\n')[0] : TunnelSharedStates.status;

    /// <summary>The name of a state in the list, or its id.</summary>
    string NameOf(string id) => saved.Where(i => i.id == id).Select(i => i.name).FirstOrDefault() ?? TunnelSharedStates.NameOf(id);

    /// <summary>The id of the state checked in the list ("" for none).</summary>
    public string checkedState => m_Checked;

    void Awake()
    {
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_SaveButton != null)
            m_SaveButton.onClick.AddListener(SaveNow);
        if (m_RestoreButton != null)
            m_RestoreButton.onClick.AddListener(OnRestorePressed);
        if (m_DeleteButton != null)
            m_DeleteButton.onClick.AddListener(DeleteChecked);
    }

    void OnEnable()
    {
        s_Menus.Add(this);
        TunnelStateStore.changed += OnStoreChanged;
        TunnelSharedStates.changed += OnStoreChanged;
    }

    void OnDisable()
    {
        s_Menus.Remove(this);
        TunnelStateStore.changed -= OnStoreChanged;
        TunnelSharedStates.changed -= OnStoreChanged;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Saved.OnValueChanged += OnSavedChanged;
        if (IsServer)
            ShareSaved();
    }

    public override void OnNetworkDespawn()
    {
        m_Saved.OnValueChanged -= OnSavedChanged;
        base.OnNetworkDespawn();
    }

    void OnSavedChanged(FixedString4096Bytes previous, FixedString4096Bytes current)
    {
        if (!isListOpen)
            return;
        BuildRows();
        ShowSharing();
    }

    // On the host or when not connected: the saved states or how sharing stands changed.
    void OnStoreChanged()
    {
        if (IsSpawned && IsServer)
            ShareSaved();
        else if (!IsSpawned && isListOpen)
        {
            BuildRows();
            ShowSharing();
        }
    }

    // The host's sharing status and saved states, for everyone (as many as one shared text holds, the newest kept).
    void ShareSaved()
    {
        string status = TunnelSharedStates.status.Replace("\n", " ").Replace("\t", " ");
        var lines = TunnelSharedStates.Everything().Select(i => $"{i.id}\t{i.name}\t{i.info.Replace("\t", " ")}\t{(i.local ? "1" : "0")}").ToList();
        while (lines.Count > 0 && Encoding.UTF8.GetByteCount(status + "\n" + string.Join("\n", lines)) > 4000)
            lines.RemoveAt(0);
        var text = status + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
        if (m_Saved.Value.ToString() != text)
            m_Saved.Value = new FixedString4096Bytes(text);
    }

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        if (!show)
            return;
        m_Shown = null;
        BuildRows();
        SetStatus("Save now keeps everything in every segment. Tap a saved state, then Restore (twice) to bring it back for everyone.");
        ShowSharing();
        // Saves from other headsets, and uploads that failed before: the host (or this headset) looks again.
        if (IsSpawned && !IsServer)
            RefreshRpc();
        else
            _ = TunnelSharedStates.Refresh();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RefreshRpc() => _ = TunnelSharedStates.Refresh();

    // How sharing stands, under the list's message.
    void ShowSharing() => ShowStatus();

    #region The list

    void BuildRows()
    {
        if (m_ListContent == null || m_RowTemplate == null)
            return;
        var states = saved;
        string joined = string.Join("\n", states.Select(s => $"{s.id}\t{s.name}\t{s.info}\t{s.local}"));
        if (joined == m_Shown && m_ListContent.childCount == states.Count)
        {
            ShowChecks();
            return;
        }
        m_Shown = joined;
        // Out of the list at once (Destroy waits for the frame's end), so the new rows are counted and labelled alone.
        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
        {
            var old = m_ListContent.GetChild(i);
            old.gameObject.SetActive(false);
            old.SetParent(null, false);
            Destroy(old.gameObject);
        }
        if (!states.Any(s => s.id == m_Checked))
            m_Checked = "";
        // Newest at the top.
        for (int i = 0; i < states.Count; i++)
        {
            string id = states[states.Count - 1 - i].id;
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.name = $"Saved {id}";
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            row.onClick.AddListener(() => Check(id));
        }
        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(1, states.Count) * m_RowSpacing);
        ShowChecks();
    }

    void Check(string id)
    {
        m_Checked = m_Checked == id ? "" : id;
        CancelRestoreConfirm();
        ShowChecks();
    }

    /// <summary>Checks a saved state by its id (as tapping its row does); "" unchecks.</summary>
    public void CheckSaved(string id)
    {
        m_Checked = id ?? "";
        CancelRestoreConfirm();
        ShowChecks();
    }

    void ShowChecks()
    {
        if (m_ListContent == null)
            return;
        var states = saved;
        for (int i = 0; i < m_ListContent.childCount && i < states.Count; i++)
        {
            var item = states[states.Count - 1 - i];
            var label = m_ListContent.GetChild(i).GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = $"{(item.id == m_Checked ? "■" : "□")}  {item.name}  <size=75%><color=#AAA>{item.info}</color>"
                    + (item.local ? " <color=#FFB347>· not uploaded</color>" : "") + "</size>";
        }
        bool any = !string.IsNullOrEmpty(m_Checked);
        if (m_RestoreButton != null)
            m_RestoreButton.interactable = any;
        if (m_DeleteButton != null)
            m_DeleteButton.interactable = any;
        if (m_RestoreConfirm == null)
            SetRestoreLabel(any ? $"Restore {NameOf(m_Checked)}" : "Restore");
        if (states.Count == 0)
            SetStatus("No saved states yet: set things up in the tunnel, then press Save now.");
    }

    #endregion

    #region Saving

    void SaveNow()
    {
        Debug.Log($"[SaveState] Save now pressed: {(IsSpawned ? (IsServer ? "saving on this headset, the host" : "asking the host") : "not connected, saving here")}");
        if (IsSpawned)
            SaveRpc();
        else
            SaveHere();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SaveRpc() => SaveHere();

    // On the host (or here when not connected).
    void SaveHere()
    {
        if (TunnelSaveState.busy)
        {
            ShareStatus("A saved state is being put back: save again when it's done.");
            return;
        }
        string name = TunnelStateStore.NextName(TunnelSharedStates.Everything().Select(i => i.name));
        var state = TunnelSaveState.Capture(name);
        if (!TunnelStateStore.Save(state))
        {
            ShareStatus($"Couldn't save {name} (see the log).");
            return;
        }
        if (!TunnelSharedStates.hasToken)
        {
            ShareStatus($"Saved {state.name} on this headset: {state.Summary()}. (No GitHub token here to share it.)");
            return;
        }
        ShareStatus($"Saved {state.name}: {state.Summary()}. Uploading it for every headset...");
        UploadThenReport(state.id, state.name);
    }

    // After a save on the host: whether every headset can have it now.
    async void UploadThenReport(string id, string name)
    {
        await TunnelSharedStates.UploadPending();
        if (this == null || !isActiveAndEnabled)
            return;
        ShareStatus(TunnelSharedStates.IsShared(id) && !TunnelStateStore.Exists(id)
            ? $"{name} is shared: any headset can restore it."
            : $"{name} is kept on this headset for now. {TunnelSharedStates.status}");
    }

    #endregion

    #region Restoring

    const string k_ConfirmColor = "#FF6B5B";

    // First press: asks for a second within a few seconds. Second press: the host puts the state back.
    void OnRestorePressed()
    {
        if (string.IsNullOrEmpty(m_Checked))
        {
            ShareStatus("Tap a saved state first, then Restore.");
            return;
        }
        if (m_RestoreConfirm == null)
        {
            m_RestoreConfirm = StartCoroutine(WaitForRestoreConfirm());
            SetStatus($"Restore {NameOf(m_Checked)} takes away everything in the tunnel now (on the walls too). Press Restore again to go ahead.");
            return;
        }
        CancelRestoreConfirm();
        Debug.Log($"[SaveState] restore {m_Checked} confirmed: {(IsSpawned ? (IsServer ? "putting it back for everyone" : "asking the host") : "not connected, here only")}");
        if (IsSpawned)
            RestoreRpc(m_Checked);
        else
            RestoreHere(m_Checked, 0);
    }

    IEnumerator WaitForRestoreConfirm()
    {
        SetRestoreLabel($"<color={k_ConfirmColor}>Restore: press again</color>");
        yield return new WaitForSeconds(m_RestoreConfirmTime);
        m_RestoreConfirm = null;
        ShowChecks();
    }

    void CancelRestoreConfirm()
    {
        if (m_RestoreConfirm != null)
            StopCoroutine(m_RestoreConfirm);
        m_RestoreConfirm = null;
    }

    void SetRestoreLabel(string text)
    {
        var label = m_RestoreButton != null ? m_RestoreButton.GetComponentInChildren<TMP_Text>(true) : null;
        if (label != null && label.text != text)
            label.text = text;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RestoreRpc(string id, RpcParams rpcParams = default) => RestoreHere(id, rpcParams.Receive.SenderClientId);

    /// <summary>
    /// On the host (or here when not connected): puts a saved state back for everyone, replacing what's in the tunnel,
    /// downloading it first if it's a shared one not kept on this headset yet. <paramref name="requester"/> drives any
    /// New figure in it. False if it isn't saved or another is being put back.
    /// </summary>
    public bool RestoreHere(string id, ulong requester)
    {
        if (TunnelSaveState.busy || m_Fetching)
        {
            ShareStatus("A saved state is already being put back: wait for it to finish.");
            return false;
        }
        var state = TunnelSharedStates.LoadNow(id);
        if (state != null)
            return Put(state, requester);
        if (!TunnelSharedStates.IsShared(id))
        {
            ShareStatus($"{NameOf(id)} isn't saved any more.");
            return false;
        }
        DownloadThenRestore(id, requester);
        return true;
    }

    async void DownloadThenRestore(string id, ulong requester)
    {
        m_Fetching = true;
        ShareStatus($"Downloading {NameOf(id)}...");
        TunnelSavedState state = null;
        string error;
        try
        {
            (state, error) = await TunnelSharedStates.Load(id);
        }
        catch (System.Exception e)
        {
            error = $"Couldn't download {NameOf(id)}: {e.Message}";
        }
        m_Fetching = false;
        if (this == null)
            return;
        if (state == null)
            ShareStatus(error);
        else
            Put(state, requester);
    }

    bool Put(TunnelSavedState state, ulong requester)
    {
        ShareStatus($"Putting back {state.name} ({state.Summary()})...");
        // This segment may be among those a state with fewer segments removes: the report goes to whichever list remains.
        return TunnelSaveState.Restore(state, prefabs, requester, report =>
        {
            var menu = this != null && isActiveAndEnabled ? this : s_Menus.FirstOrDefault(m => m != null && m.isActiveAndEnabled);
            if (menu != null)
                menu.ShareStatus(report);
            menu?.HudRpcOrLocal(report);
        });
    }

    #endregion

    #region Deleting

    void DeleteChecked()
    {
        if (string.IsNullOrEmpty(m_Checked))
        {
            ShareStatus("Tap a saved state first, then Delete.");
            return;
        }
        string id = m_Checked;
        m_Checked = "";
        CancelRestoreConfirm();
        Debug.Log($"[SaveState] delete {id}: {(IsSpawned ? (IsServer ? "here, the host" : "asking the host") : "not connected, here")}");
        if (IsSpawned)
            DeleteRpc(id);
        else
            DeleteHere(id);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeleteRpc(string id) => DeleteHere(id);

    // On the host (or here): one saved only here goes from here; a shared one from the repository (needs the token).
    async void DeleteHere(string id)
    {
        if (TunnelSharedStates.IsShared(id) && !TunnelStateStore.Exists(id))
            ShareStatus($"Deleting {NameOf(id)} from every headset...");
        string message;
        try
        {
            (_, message) = await TunnelSharedStates.Delete(id);
        }
        catch (System.Exception e)
        {
            message = $"Couldn't delete {NameOf(id)}: {e.Message}";
        }
        if (this != null)
            ShareStatus(message);
    }

    #endregion

    #region Messages

    /// <summary>A message on everyone's copy of the list.</summary>
    void ShareStatus(string text)
    {
        if (IsSpawned)
            StatusRpc(text);
        else
            SetStatus(text);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void StatusRpc(string text) => SetStatus(text);

    string m_Message = "";

    void SetStatus(string text)
    {
        m_Message = text ?? "";
        ShowStatus();
    }

    // The last message, and under it in blue how sharing stands.
    void ShowStatus()
    {
        if (m_StatusText == null)
            return;
        string line = sharing;
        string text = string.IsNullOrEmpty(line) ? m_Message : $"{m_Message}\n<color=#9CF>{line}</color>";
        if (m_StatusText.text != text)
            m_StatusText.text = text;
    }

    // The outcome of a restore also shows in front of everyone, whether or not their list is open.
    void HudRpcOrLocal(string text)
    {
        if (IsSpawned)
            HudRpc(text);
        else
            PlayerHudNotification.Instance?.ShowText(text, 4f);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void HudRpc(string text) => PlayerHudNotification.Instance?.ShowText(text, 4f);

    #endregion
}
