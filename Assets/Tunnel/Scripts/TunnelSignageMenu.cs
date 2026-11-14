using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The TS Menu's "Signage" list: New sign puts a sign (see <see cref="TunnelSign"/>) flat on the wall beside the list,
/// for everyone, with its menu open for whoever pressed it. Below, the signs stored on the host (see
/// <see cref="SignStore"/>): tap one to check it, then Place puts a copy of it on the wall beside the list, and Delete
/// removes it from the store (signs already in the tunnel stay). The Edit buttons on signs check box shows or hides the
/// small Edit button on every sign, for a clean look. In a session everyone sees the host's stored signs, the same
/// check box and the same messages; which stored sign is checked is each player's own. Works locally when not connected.
/// </summary>
public class TunnelSignageMenu : NetworkBehaviour
{
    [SerializeField] TunnelSegmentGeometry m_Geometry;
    [SerializeField] GameObject m_ListPanel;
    [SerializeField] TunnelSign m_SignPrefab;
    [SerializeField] Button m_NewButton;
    [SerializeField] RectTransform m_ListContent;
    [SerializeField] Button m_RowTemplate;
    [SerializeField] Button m_PlaceButton;
    [SerializeField] Button m_DeleteButton;
    [SerializeField] Button m_EditButtonsRow;
    [SerializeField] TMP_Text m_StatusText;
    [SerializeField] float m_RowSpacing = 58f;

    [SerializeField]
    [Tooltip("Gap between the list's far edge and a new sign (meters).")]
    float m_GapFromList = 0.08f;

    const string k_NewText = "New sign";

    static readonly List<TunnelSignageMenu> s_Menus = new();
    static bool s_LocalEditButtons = true;

    readonly NetworkVariable<FixedString4096Bytes> m_StoredNames = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_EditButtons = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    string m_Checked = "";
    string m_ShownNames;

    /// <summary>Whether signs show their small Edit button (one switch for everyone; on when the app starts).</summary>
    public static bool editButtonsShown
    {
        get
        {
            foreach (var menu in s_Menus)
                if (menu != null && menu.IsSpawned)
                    return menu.m_EditButtons.Value;
            return s_LocalEditButtons;
        }
    }

    bool isListOpen => m_ListPanel != null && m_ListPanel.activeSelf;

    /// <summary>The stored signs' names as this player sees them (the host's in a session).</summary>
    public List<string> storedNames
    {
        get
        {
            if (!IsSpawned)
                return SignStore.Names();
            string joined = m_StoredNames.Value.ToString();
            return joined.Length == 0 ? new List<string>() : joined.Split('\n').ToList();
        }
    }

    void Awake()
    {
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_NewButton != null)
            m_NewButton.onClick.AddListener(NewSign);
        if (m_PlaceButton != null)
            m_PlaceButton.onClick.AddListener(PlaceChecked);
        if (m_DeleteButton != null)
            m_DeleteButton.onClick.AddListener(DeleteChecked);
        if (m_EditButtonsRow != null)
            m_EditButtonsRow.onClick.AddListener(ToggleEditButtons);
    }

    void OnEnable()
    {
        s_Menus.Add(this);
        SignStore.changed += OnStoreChanged;
    }

    void OnDisable()
    {
        s_Menus.Remove(this);
        SignStore.changed -= OnStoreChanged;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_StoredNames.OnValueChanged += OnNamesChanged;
        if (IsServer)
        {
            ShareNames();
            // Every segment's menu holds the one switch; a new segment takes the others' value.
            foreach (var other in s_Menus)
                if (other != this && other.IsSpawned)
                {
                    m_EditButtons.Value = other.m_EditButtons.Value;
                    break;
                }
        }
    }

    public override void OnNetworkDespawn()
    {
        m_StoredNames.OnValueChanged -= OnNamesChanged;
        base.OnNetworkDespawn();
    }

    void OnNamesChanged(FixedString4096Bytes previous, FixedString4096Bytes current)
    {
        if (isListOpen)
            BuildRows();
    }

    // On the host or when not connected: the store changed.
    void OnStoreChanged()
    {
        if (IsSpawned && IsServer)
            ShareNames();
        else if (!IsSpawned && isListOpen)
            BuildRows();
    }

    // The host's stored names, for everyone (as many as one shared text holds).
    void ShareNames()
    {
        var sb = new StringBuilder();
        foreach (var name in SignStore.Names())
        {
            if (Encoding.UTF8.GetByteCount(sb.ToString()) + Encoding.UTF8.GetByteCount(name) + 1 > 4000)
                break;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(name);
        }
        m_StoredNames.Value = new FixedString4096Bytes(sb.ToString());
    }

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        if (!show)
            return;
        m_ShownNames = null;
        BuildRows();
        SetStatus("New sign puts a sign on the wall beside this list; its Edit button opens its menu. Saved signs are listed below.");
    }

    void Update()
    {
        if (isListOpen)
            ShowEditButtonsRow();
    }

    void ShowEditButtonsRow()
    {
        if (m_EditButtonsRow == null)
            return;
        string text = (editButtonsShown ? "■" : "□") + "  Edit buttons on signs  <color=#AAA>untick for a clean look</color>";
        var label = m_EditButtonsRow.GetComponentInChildren<TMP_Text>(true);
        if (label != null && label.text != text)
            label.text = text;
    }

    #region Stored signs

    void BuildRows()
    {
        if (m_ListContent == null || m_RowTemplate == null)
            return;
        var names = storedNames;
        string joined = string.Join("\n", names);
        if (joined == m_ShownNames && m_ListContent.childCount == names.Count)
        {
            ShowChecks();
            return;
        }
        m_ShownNames = joined;
        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);
        if (!names.Contains(m_Checked))
            m_Checked = "";
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            var row = Instantiate(m_RowTemplate, m_ListContent);
            row.name = $"Stored {name}";
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            row.onClick.AddListener(() => Check(name));
        }
        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, Mathf.Max(1, names.Count) * m_RowSpacing);
        ShowChecks();
    }

    void Check(string name)
    {
        m_Checked = m_Checked == name ? "" : name;
        ShowChecks();
    }

    void ShowChecks()
    {
        if (m_ListContent == null)
            return;
        var names = storedNames;
        for (int i = 0; i < m_ListContent.childCount && i < names.Count; i++)
        {
            var label = m_ListContent.GetChild(i).GetComponentInChildren<TMP_Text>(true);
            if (label != null)
                label.text = (names[i] == m_Checked ? "■  " : "□  ") + names[i];
        }
        bool any = !string.IsNullOrEmpty(m_Checked);
        if (m_PlaceButton != null)
            m_PlaceButton.interactable = any;
        if (m_DeleteButton != null)
            m_DeleteButton.interactable = any;
        if (names.Count == 0)
            SetStatus("No saved signs yet: press Save on a sign's menu to keep it here.");
    }

    /// <summary>Checks a stored sign (as tapping its row does); "" unchecks.</summary>
    public void CheckStored(string name)
    {
        m_Checked = name ?? "";
        ShowChecks();
    }

    void PlaceChecked()
    {
        if (string.IsNullOrEmpty(m_Checked))
        {
            ShareStatus("Tap a saved sign first, then Place.");
            return;
        }
        Debug.Log($"[Sign] place stored \"{m_Checked}\": {(IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only")}");
        if (IsSpawned)
            PlaceStoredRpc(m_Checked);
        else
            PlaceStored(m_Checked, ulong.MaxValue);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PlaceStoredRpc(string name, RpcParams rpcParams = default) => PlaceStored(name, rpcParams.Receive.SenderClientId);

    // On the host (or here when not connected).
    TunnelSign PlaceStored(string name, ulong forPlayer)
    {
        var record = SignStore.Load(name);
        if (record == null)
        {
            ShareStatus($"\"{name}\" isn't stored any more.");
            return null;
        }
        var fonts = m_SignPrefab != null ? m_SignPrefab.fonts : null;
        var sign = Place(record.text, record.ToStyle(fonts), record.name, record.scale, forPlayer);
        if (sign != null)
            ShareStatus($"\"{record.name}\" placed on the wall beside this list.");
        return sign;
    }

    void DeleteChecked()
    {
        if (string.IsNullOrEmpty(m_Checked))
        {
            ShareStatus("Tap a saved sign first, then Delete.");
            return;
        }
        string name = m_Checked;
        m_Checked = "";
        Debug.Log($"[Sign] delete stored \"{name}\": {(IsSpawned ? (IsServer ? "here, the host" : "asking the host") : "not connected, here")}");
        if (IsSpawned)
            DeleteStoredRpc(name);
        else
            DeleteStored(name);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeleteStoredRpc(string name) => DeleteStored(name);

    void DeleteStored(string name) =>
        ShareStatus(SignStore.Delete(name) ? $"Deleted \"{name}\" from the saved signs (any copy in the tunnel stays)." : $"\"{name}\" wasn't stored.");

    #endregion

    #region New signs

    void NewSign()
    {
        Debug.Log($"[Sign] New sign pressed: {(IsSpawned ? (IsServer ? "for everyone" : "asking the host") : "not connected, here only")}");
        if (IsSpawned)
            NewSignRpc();
        else
            Place(k_NewText, SignStyle.Default, "", 1f, ulong.MaxValue);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NewSignRpc(RpcParams rpcParams = default) => Place(k_NewText, SignStyle.Default, "", 1f, rpcParams.Receive.SenderClientId);

    /// <summary>
    /// A sign for everyone (on the host, or here when not connected), set up before it appears and placed flat on the
    /// wall beside the list; the menu opens for <paramref name="forPlayer"/> (here when not connected).
    /// </summary>
    public TunnelSign Place(string text, SignStyle style, string saveName, float scale, ulong forPlayer)
    {
        if (m_SignPrefab == null || m_Geometry == null)
        {
            Debug.LogWarning("[Sign] no sign to place (run Tunnel > Signage > Build Signage)");
            return null;
        }

        // Set up and placed before it wakes up: its network transform takes its pose when it's created.
        var holder = new GameObject("Sign Holder");
        holder.SetActive(false);
        var sign = Instantiate(m_SignPrefab, holder.transform);
        sign.Configure(text, style, saveName, scale, forPlayer);
        var pose = WallSpot(0.25f * scale, 0.1f * scale, scale);
        sign.transform.SetPositionAndRotation(pose.position, pose.rotation);
        sign.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Sign] placed \"{text.Split('\n')[0]}\" at {pose.position:F2}");

        if (IsSpawned && sign.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
        else
            sign.OpenMenu();
        return sign;
    }

    /// <summary>
    /// Flat on the list's wall, at the list's height, just past its far edge, facing into the tunnel; if a sign is
    /// already there, the first free spot further along.
    /// </summary>
    public Pose WallSpot(float halfWidth, float halfHeight, float scale)
    {
        var frame = m_Geometry.transform;
        var list = (RectTransform)m_ListPanel.transform;
        var listCenter = frame.InverseTransformPoint(list.position);
        int side = listCenter.x >= 0f ? 1 : -1;
        var corners = new Vector3[4];
        list.GetWorldCorners(corners);
        float farZ = corners.Max(c => frame.InverseTransformPoint(c).z);
        float halfLength = m_Geometry.length * 0.5f;
        float back = 0.008f * scale + 0.002f; // its back just off the wall (see TunnelSign.TrySnapToWall)
        float x = side * (m_Geometry.width * 0.5f - back);
        float y = listCenter.y;
        float first = Mathf.Min(farZ + m_GapFromList + halfWidth, halfLength - halfWidth);
        float z = first;
        for (float tryZ = first; tryZ <= halfLength - halfWidth + 1e-3f; tryZ += 0.1f)
        {
            if (Free(tryZ))
            {
                z = tryZ;
                break;
            }
        }

        bool Free(float atZ)
        {
            foreach (var other in TunnelSign.all)
            {
                if (other == null)
                    continue;
                var p = frame.InverseTransformPoint(other.transform.position);
                var h = other.worldHalfSize;
                if (Mathf.Sign(p.x) == side && Mathf.Abs(p.z - atZ) < halfWidth + h.x && Mathf.Abs(p.y - y) < halfHeight + h.y)
                    return false;
            }
            return true;
        }

        return new Pose(frame.TransformPoint(new Vector3(x, y, z)), Quaternion.LookRotation(frame.TransformDirection(Vector3.right * side), frame.up));
    }

    #endregion

    #region Edit buttons

    void ToggleEditButtons()
    {
        bool next = !editButtonsShown;
        if (IsSpawned)
            SetEditButtonsRpc(next);
        else
            s_LocalEditButtons = next;
        ShowEditButtonsRow();
    }

    // On every segment's menu, so whichever one a player reads has it.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetEditButtonsRpc(bool on)
    {
        foreach (var menu in s_Menus)
            if (menu != null && menu.IsSpawned)
                menu.m_EditButtons.Value = on;
    }

    #endregion

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
}
