using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// The Tunnel Segment's "TS Menu" button: opens and closes the panel holding the segment's buttons
/// (Colors, Textures, Molecules, Containers, Delete, New Browser, New Viewer, Add Segment, Remove Segment, Align to
/// QR). The colour chooser, the texture list, the molecule list and the container list open beside it, one at a time; closing the menu also closes them. Everyone sees
/// the same menu: whoever presses a button opens or closes it for all players (the colour chooser shares itself the
/// same way). Works locally when not connected.
/// Delete is each player's own switch: while it is on, any molecule or container that player grabs is deleted for
/// everyone (see <see cref="Delete"/>).
/// </summary>
public class TunnelMenu : NetworkBehaviour
{
    enum SidePanel : byte
    {
        None,
        Textures,
        Molecules,
        Containers,
    }

    [SerializeField]
    Button m_MenuButton;

    [SerializeField]
    TMP_Text m_MenuLabel;

    [SerializeField]
    GameObject m_MenuPanel;

    [SerializeField]
    Button m_ColorsButton;

    [SerializeField]
    Button m_TexturesButton;

    [SerializeField]
    TMP_Text m_TexturesLabel;

    [SerializeField]
    Button m_MoleculesButton;

    [SerializeField]
    TMP_Text m_MoleculesLabel;

    [SerializeField]
    Button m_ContainersButton;

    [SerializeField]
    TMP_Text m_ContainersLabel;

    [SerializeField]
    Button m_DeleteButton;

    [SerializeField]
    TMP_Text m_DeleteLabel;

    [SerializeField]
    TunnelSegmentAppearance m_Appearance;

    [SerializeField]
    TunnelSegmentTexture m_Texture;

    [SerializeField]
    TunnelMoleculeMenu m_Molecules;

    [SerializeField]
    TunnelContainerMenu m_Containers;

    readonly NetworkVariable<bool> m_Open = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SidePanel> m_Panel = new(SidePanel.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    bool m_LocalOpen;
    SidePanel m_LocalPanel;

    // What the panels show now, so repeated updates don't rebuild the lists.
    SidePanel m_Shown = (SidePanel)255;
    bool m_ShownOpen;

    /// <summary>While on, a molecule or container this player grabs is deleted. Off when the app starts.</summary>
    public static bool deleteMode { get; private set; }

    bool isOpen => IsSpawned ? m_Open.Value : m_LocalOpen;
    SidePanel panel => IsSpawned ? m_Panel.Value : m_LocalPanel;

    void Awake()
    {
        if (m_MenuButton != null)
            m_MenuButton.onClick.AddListener(ToggleMenu);
        if (m_TexturesButton != null)
            m_TexturesButton.onClick.AddListener(ToggleTextures);
        if (m_MoleculesButton != null)
            m_MoleculesButton.onClick.AddListener(ToggleMolecules);
        if (m_ContainersButton != null)
            m_ContainersButton.onClick.AddListener(() => ToggleSidePanel(SidePanel.Containers));
        if (m_DeleteButton != null)
            m_DeleteButton.onClick.AddListener(ToggleDeleteMode);
        // The appearance component toggles the chooser itself; just keep the lists out of its way.
        if (m_ColorsButton != null)
            m_ColorsButton.onClick.AddListener(() => SetState(isOpen, SidePanel.None));
        Apply(false, SidePanel.None);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Open.OnValueChanged += OnOpenChanged;
        m_Panel.OnValueChanged += OnPanelChanged;
        Apply(m_Open.Value, m_Panel.Value);
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_Open.OnValueChanged -= OnOpenChanged;
        m_Panel.OnValueChanged -= OnPanelChanged;
    }

    void ToggleDeleteMode()
    {
        deleteMode = !deleteMode;
        PlayerHudNotification.Instance?.ShowText(deleteMode ? "Delete on: grab a molecule or container to delete it" : "Delete off", 3f);
    }

    void Update()
    {
        // Every segment's menu shows the one switch.
        if (m_DeleteLabel == null)
            return;
        string text = deleteMode ? "<color=#F55>Delete: ON</color>" : "Delete";
        if (m_DeleteLabel.text != text)
            m_DeleteLabel.text = text;
    }

    /// <summary>
    /// Deletes a molecule or container for everyone: the host removes it from the session (asked through any
    /// tunnel segment's menu); when not connected it is simply destroyed.
    /// </summary>
    public static void Delete(GameObject target)
    {
        if (target == null)
            return;
        Debug.Log($"[TS Menu] deleting {target.name}");
        PlayerHudNotification.Instance?.ShowText($"Deleted {target.name.Replace("(Clone)", "").Replace("_Networked", "").Replace("Toy_", "").Replace('_', ' ').Trim()}", 2f);
        if (target.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned)
        {
            var menu = FindAnyObjectByType<TunnelMenu>();
            if (menu != null && menu.IsSpawned)
            {
                target.SetActive(false); // gone here at once; the host removes it for everyone
                menu.DeleteRpc(networkObject);
                return;
            }
        }

        Destroy(target);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeleteRpc(NetworkObjectReference reference)
    {
        if (reference.TryGet(out var networkObject) && networkObject.IsSpawned)
            networkObject.Despawn(true);
    }

    void ToggleMenu()
    {
        bool open = !isOpen;
        if (!open)
            CloseChooser();
        SetState(open, SidePanel.None);
    }

    void ToggleTextures() => ToggleSidePanel(SidePanel.Textures);

    void ToggleMolecules() => ToggleSidePanel(SidePanel.Molecules);

    void ToggleSidePanel(SidePanel side)
    {
        bool show = panel != side;
        if (show)
            CloseChooser();
        SetState(true, show ? side : SidePanel.None);
    }

    // Only the player who pressed a button does this: the chooser shares its own open state.
    void CloseChooser()
    {
        if (m_Appearance != null && m_Appearance.isActiveAndEnabled)
            m_Appearance.SetChooserOpen(false);
    }

    void SetState(bool open, SidePanel side)
    {
        if (!open)
            side = SidePanel.None;
        if (IsSpawned)
        {
            SetStateRpc(open, side);
        }
        else
        {
            m_LocalOpen = open;
            m_LocalPanel = side;
            Apply(open, side);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetStateRpc(bool open, SidePanel side)
    {
        m_Open.Value = open;
        m_Panel.Value = open ? side : SidePanel.None;
    }

    void OnOpenChanged(bool previous, bool current) => Apply(current, m_Panel.Value);

    void OnPanelChanged(SidePanel previous, SidePanel current) => Apply(m_Open.Value, current);

    void Apply(bool open, SidePanel side)
    {
        if (!open)
            side = SidePanel.None;
        if (m_Shown == side && m_ShownOpen == open)
            return;
        m_Shown = side;
        m_ShownOpen = open;

        if (m_MenuPanel != null)
            m_MenuPanel.SetActive(open);
        if (m_MenuLabel != null)
            m_MenuLabel.text = open ? "Close Menu" : "TS Menu";
        ShowTextures(side == SidePanel.Textures);
        ShowMolecules(side == SidePanel.Molecules);
        ShowContainers(side == SidePanel.Containers);
    }

    void ShowContainers(bool show)
    {
        if (m_Containers != null)
            m_Containers.ShowList(show);
        if (m_ContainersLabel != null)
            m_ContainersLabel.text = show ? "Hide Containers" : "Containers";
    }

    void ShowMolecules(bool show)
    {
        if (m_Molecules != null)
            m_Molecules.ShowList(show);
        if (m_MoleculesLabel != null)
            m_MoleculesLabel.text = show ? "Hide Molecules" : "Molecules";
    }

    void ShowTextures(bool show)
    {
        if (m_Texture != null)
            m_Texture.ShowList(show);
        if (m_TexturesLabel != null)
            m_TexturesLabel.text = show ? "Hide Textures" : "Textures";
    }
}
