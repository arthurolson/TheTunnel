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
/// state (State 1, State 2, ...; see <see cref="TunnelSavedState"/>), stored on the host so it is still there after the
/// app restarts. Each saved state is listed with when it was saved and what it holds; tap one to check it, then Restore
/// (pressed twice within a few seconds, as it takes away what's there now) brings back exactly what was saved, for
/// everyone, or Delete removes it from the list. In a session everyone sees the host's saved states and the same
/// messages, and saving or restoring from any headset is done by the host; which state is checked is each player's
/// own. Works locally when not connected.
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

    // One line per saved state: its name, a tab, then its date and summary.
    readonly NetworkVariable<FixedString4096Bytes> m_Saved = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    string m_Checked = "";
    string m_Shown;
    Coroutine m_RestoreConfirm;

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

    /// <summary>The saved states as this player sees them (the host's in a session): name and its date and summary.</summary>
    public List<(string name, string info)> saved
    {
        get
        {
            if (!IsSpawned)
                return TunnelStateStore.Names().Select(n => (n, TunnelStateStore.Info(n))).ToList();
            string joined = m_Saved.Value.ToString();
            if (joined.Length == 0)
                return new List<(string, string)>();
            return joined.Split('\n').Select(line =>
            {
                int tab = line.IndexOf('\t');
                return tab < 0 ? (line, "") : (line.Substring(0, tab), line.Substring(tab + 1));
            }).ToList();
        }
    }

    /// <summary>The state checked in the list ("" for none).</summary>
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
    }

    void OnDisable()
    {
        s_Menus.Remove(this);
        TunnelStateStore.changed -= OnStoreChanged;
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
        if (isListOpen)
            BuildRows();
    }

    // On the host or when not connected: the store changed.
    void OnStoreChanged()
    {
        if (IsSpawned && IsServer)
            ShareSaved();
        else if (!IsSpawned && isListOpen)
            BuildRows();
    }

    // The host's saved states, for everyone (as many as one shared text holds, the newest kept).
    void ShareSaved()
    {
        var lines = TunnelStateStore.Names().Select(n => $"{n}\t{TunnelStateStore.Info(n)}").ToList();
        while (lines.Count > 0 && Encoding.UTF8.GetByteCount(string.Join("\n", lines)) > 4000)
            lines.RemoveAt(0);
        m_Saved.Value = new FixedString4096Bytes(string.Join("\n", lines));
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
        SetStatus("Save now keeps everything in every segment as it is. Tap a saved state, then Restore (twice) to bring it back for everyone.");
    }

    #region The list

    void BuildRows()
    {
        if (m_ListContent == null || m_RowTemplate == null)
            return;
        var states = saved;
        string joined = string.Join("\n", states.Select(s => s.name + "\t" + s.info));
        if (joined == m_Shown && m_ListContent.childCount == states.Count)
        {
            ShowChecks();
            return;
        }
        m_Shown = joined;
        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);
        if (!states.Any(s => s.name == m_Checked))
            m_Checked = "";
        // Newest at the top.
        for (int i = 0; i < states.Count; i++)
        {
            string name = states[states.Count - 1 - i].name;
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.name = $"Saved {name}";
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            row.onClick.AddListener(() => Check(name));
        }
        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(1, states.Count) * m_RowSpacing);
        ShowChecks();
    }

    void Check(string name)
    {
        m_Checked = m_Checked == name ? "" : name;
        CancelRestoreConfirm();
        ShowChecks();
    }

    /// <summary>Checks a saved state (as tapping its row does); "" unchecks.</summary>
    public void CheckSaved(string name)
    {
        m_Checked = name ?? "";
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
            var (name, info) = states[states.Count - 1 - i];
            var label = m_ListContent.GetChild(i).GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = $"{(name == m_Checked ? "■" : "□")}  {name}  <size=75%><color=#AAA>{info}</color></size>";
        }
        bool any = !string.IsNullOrEmpty(m_Checked);
        if (m_RestoreButton != null)
            m_RestoreButton.interactable = any;
        if (m_DeleteButton != null)
            m_DeleteButton.interactable = any;
        if (m_RestoreConfirm == null)
            SetRestoreLabel(any ? $"Restore {m_Checked}" : "Restore");
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
        string name = TunnelStateStore.NextName();
        var state = TunnelSaveState.Capture(name);
        ShareStatus(TunnelStateStore.Save(state)
            ? $"Saved {state.name}: {state.Summary()}."
            : $"Couldn't save {name} (see the log).");
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
            SetStatus($"Restore {m_Checked} takes away everything in the tunnel now (on the walls too). Press Restore again to go ahead.");
            return;
        }
        CancelRestoreConfirm();
        Debug.Log($"[SaveState] restore \"{m_Checked}\" confirmed: {(IsSpawned ? (IsServer ? "putting it back for everyone" : "asking the host") : "not connected, here only")}");
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
    void RestoreRpc(string name, RpcParams rpcParams = default) => RestoreHere(name, rpcParams.Receive.SenderClientId);

    /// <summary>
    /// On the host (or here when not connected): puts a saved state back for everyone, replacing what's in the tunnel.
    /// <paramref name="requester"/> drives any New figure in it. False if it isn't saved or another is being put back.
    /// </summary>
    public bool RestoreHere(string name, ulong requester)
    {
        if (TunnelSaveState.busy)
        {
            ShareStatus("A saved state is already being put back: wait for it to finish.");
            return false;
        }
        var state = TunnelStateStore.Load(name);
        if (state == null)
        {
            ShareStatus($"{name} isn't saved any more.");
            return false;
        }
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
        string name = m_Checked;
        m_Checked = "";
        CancelRestoreConfirm();
        Debug.Log($"[SaveState] delete \"{name}\": {(IsSpawned ? (IsServer ? "here, the host" : "asking the host") : "not connected, here")}");
        if (IsSpawned)
            DeleteRpc(name);
        else
            DeleteHere(name);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeleteRpc(string name) => DeleteHere(name);

    void DeleteHere(string name) =>
        ShareStatus(TunnelStateStore.Delete(name) ? $"Deleted {name} from the saved states." : $"{name} wasn't saved.");

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

    void SetStatus(string text)
    {
        if (m_StatusText != null)
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
