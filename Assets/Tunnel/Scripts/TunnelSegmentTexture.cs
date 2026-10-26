using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The Tunnel Segment's wall texture and its "Textures" list. The list shows the textures built into the app and
/// the images in the host's Textures folder; choosing one puts it on this segment's walls and ceiling for everyone.
/// Built-in textures are on every headset. Folder images are only on the host, so other headsets receive them from
/// the host in small chunks the first time they need them.
/// Works locally (from this headset's own folder) when not connected.
/// </summary>
public class TunnelSegmentTexture : NetworkBehaviour
{
    const int k_ChunkSize = 4000;     // below the transport's 6144-byte payload limit
    const int k_ChunksPerFrame = 4;

    [SerializeField]
    TunnelSegmentAppearance m_Appearance;

    [Header("List UI")]
    [SerializeField]
    GameObject m_ListPanel;

    [SerializeField]
    RectTransform m_ListContent;

    [SerializeField]
    [Tooltip("Inactive row used as the template for list entries (a Button with a TMP label).")]
    Button m_RowTemplate;

    [SerializeField]
    TMP_Text m_StatusText;

    [SerializeField]
    Button m_RefreshButton;

    [SerializeField]
    [Tooltip("Shows where the Textures folder is on the host.")]
    TMP_Text m_FolderText;

    [SerializeField]
    float m_RowSpacing = 58f;

    readonly NetworkVariable<FixedString128Bytes> m_TextureName = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    string m_LocalName = string.Empty;
    readonly Dictionary<string, byte[][]> m_Incoming = new();
    static readonly HashSet<string> s_Requested = new();

    string currentName => IsSpawned ? m_TextureName.Value.ToString() : m_LocalName;

    public bool isListOpen => m_ListPanel != null && m_ListPanel.activeSelf;

    void Awake()
    {
        if (m_RowTemplate != null)
            m_RowTemplate.gameObject.SetActive(false);
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_RefreshButton != null)
            m_RefreshButton.onClick.AddListener(RequestList);
        if (m_FolderText != null)
            m_FolderText.text = "Host folder:\n" + TunnelTextureLibrary.folderPath.Replace("/storage/emulated/0/", "");
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_TextureName.OnValueChanged += OnTextureNameChanged;
        Apply(currentName);
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_TextureName.OnValueChanged -= OnTextureNameChanged;
    }

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        if (show)
            RequestList();
    }

    #region List

    public void RequestList()
    {
        SetStatus("Reading the host's Textures folder...");
        if (IsSpawned && !IsServer)
            RequestListRpc();
        else
            BuildList(TunnelTextureLibrary.ListFiles());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestListRpc(RpcParams rpcParams = default)
    {
        string names = string.Join("\n", TunnelTextureLibrary.ListFiles());
        ReceiveListRpc(names, RpcTarget.Single(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ReceiveListRpc(string names, RpcParams rpcParams) =>
        BuildList(new List<string>(string.IsNullOrEmpty(names) ? new string[0] : names.Split('\n')));

    /// <param name="folderNames">Images in the host's Textures folder; the built-in ones are added here.</param>
    void BuildList(List<string> folderNames)
    {
        if (m_ListContent == null || m_RowTemplate == null)
            return;

        for (int i = m_ListContent.childCount - 1; i >= 0; i--)
            Destroy(m_ListContent.GetChild(i).gameObject);

        var builtIn = TunnelTextureLibrary.ListBuiltIn();
        int row = 0;
        AddRow(row++, "None (plain colour)", string.Empty);
        foreach (var name in builtIn)
            AddRow(row++, name, name);
        foreach (var name in folderNames)
            AddRow(row++, $"{name}  (host folder)", name);
        m_ListContent.sizeDelta = new Vector2(m_ListContent.sizeDelta.x, row * m_RowSpacing);

        SetStatus(row == 1
            ? "No textures. Add .png or .jpg files to the host's Textures folder, then Refresh."
            : $"{builtIn.Count} built in, {folderNames.Count} from the host folder. Current: {(string.IsNullOrEmpty(currentName) ? "none" : currentName)}");
    }

    void AddRow(int index, string label, string textureName)
    {
        var row = Instantiate(m_RowTemplate, m_ListContent);
        row.gameObject.SetActive(true);
        ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -index * m_RowSpacing);
        row.GetComponentInChildren<TMP_Text>(true).text = label;
        row.onClick.AddListener(() => Choose(textureName));
    }

    void SetStatus(string text)
    {
        if (m_StatusText != null)
            m_StatusText.text = text;
    }

    #endregion

    #region Choosing and applying

    void Choose(string textureName)
    {
        if (IsSpawned)
        {
            ChooseRpc(textureName);
        }
        else
        {
            m_LocalName = textureName;
            Apply(textureName);
        }

        SetStatus(string.IsNullOrEmpty(textureName) ? "Texture removed" : $"Applying {textureName}...");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ChooseRpc(string textureName)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(textureName) > FixedString128Bytes.UTF8MaxLengthInBytes)
        {
            Debug.LogWarning($"[Textures] File name too long to share: {textureName}");
            return;
        }

        // Load first so a missing or unreadable file never gets assigned.
        if (!string.IsNullOrEmpty(textureName) && !TunnelTextureLibrary.TryGetCached(textureName, out _) &&
            !TunnelTextureLibrary.TryLoadFromFolder(textureName, out _, out _))
            return;
        m_TextureName.Value = new FixedString128Bytes(textureName);
    }

    void OnTextureNameChanged(FixedString128Bytes previous, FixedString128Bytes current) => Apply(current.ToString());

    void Apply(string textureName)
    {
        if (m_Appearance == null)
            return;

        if (string.IsNullOrEmpty(textureName))
        {
            m_Appearance.SetWallTexture(null);
            return;
        }

        if (TunnelTextureLibrary.TryGetCached(textureName, out var texture) ||
            ((!IsSpawned || IsServer) && TunnelTextureLibrary.TryLoadFromFolder(textureName, out texture, out _)))
        {
            m_Appearance.SetWallTexture(texture);
            SetStatus($"Current: {textureName}");
            return;
        }

        // A client without the image: ask the host for it (once per image, shared by all segments).
        if (IsSpawned && s_Requested.Add(textureName))
            RequestTextureRpc(textureName);
    }

    #endregion

    #region Sending images to clients

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestTextureRpc(string textureName, RpcParams rpcParams = default)
    {
        if (TunnelTextureLibrary.TryGetEncoded(textureName, out var bytes))
            StartCoroutine(SendChunks(textureName, bytes, rpcParams.Receive.SenderClientId));
    }

    IEnumerator SendChunks(string textureName, byte[] bytes, ulong clientId)
    {
        int count = (bytes.Length + k_ChunkSize - 1) / k_ChunkSize;
        for (int i = 0; i < count; i++)
        {
            int size = Mathf.Min(k_ChunkSize, bytes.Length - i * k_ChunkSize);
            var chunk = new byte[size];
            System.Buffer.BlockCopy(bytes, i * k_ChunkSize, chunk, 0, size);
            ReceiveChunkRpc(textureName, i, count, chunk, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            if ((i + 1) % k_ChunksPerFrame == 0)
                yield return null;
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ReceiveChunkRpc(string textureName, int index, int count, byte[] data, RpcParams rpcParams)
    {
        if (!m_Incoming.TryGetValue(textureName, out var chunks) || chunks.Length != count)
            m_Incoming[textureName] = chunks = new byte[count][];
        chunks[index] = data;

        foreach (var c in chunks)
        {
            if (c == null)
                return; // still waiting for more
        }

        m_Incoming.Remove(textureName);
        var all = new List<byte>();
        foreach (var c in chunks)
            all.AddRange(c);

        var texture = TunnelTextureLibrary.AddFromBytes(textureName, all.ToArray());
        s_Requested.Remove(textureName);
        if (texture == null)
            return;

        // Every segment showing this image can use it now.
        foreach (var segment in FindObjectsByType<TunnelSegmentTexture>(FindObjectsSortMode.None))
        {
            if (segment.currentName == textureName)
                segment.Apply(textureName);
        }
    }

    #endregion
}
