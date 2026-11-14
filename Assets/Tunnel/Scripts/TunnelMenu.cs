using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// The Tunnel Segment's "TS Menu" button: opens and closes the panel holding the segment's buttons
/// (Colors, Textures, Molecules, Containers, Signage, Delete, New Browser, New Viewer, Add Segment, Remove Segment,
/// Align to QR). The colour chooser, the texture list, the molecule list, the container list and the signage list open
/// beside it, one at a time; closing the menu also closes them. Everyone sees
/// the same menu: whoever presses a button opens or closes it for all players (the colour chooser shares itself the
/// same way). Works locally when not connected.
/// Delete is one switch for everyone, like the rest of the menu: while it is on, any molecule or container anyone
/// grabs is deleted for everyone (see <see cref="Delete"/>).
/// </summary>
public class TunnelMenu : NetworkBehaviour
{
    enum SidePanel : byte
    {
        None,
        Textures,
        Molecules,
        Containers,
        Signage,
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
    Button m_SignageButton;

    [SerializeField]
    TMP_Text m_SignageLabel;

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

    [SerializeField]
    TunnelSignageMenu m_Signage;

    readonly NetworkVariable<bool> m_Open = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SidePanel> m_Panel = new(SidePanel.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    bool m_LocalOpen;
    SidePanel m_LocalPanel;

    // What the panels show now, so repeated updates don't rebuild the lists.
    SidePanel m_Shown = (SidePanel)255;
    bool m_ShownOpen;

    readonly NetworkVariable<bool> m_Delete = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // The menu whose switch everyone reads (every segment's menu holds the same value), or the local one when not
    // connected.
    static TunnelMenu s_Shared;
    static bool s_LocalDelete;

    /// <summary>While on, a molecule or container anyone grabs is deleted. Off when the app starts.</summary>
    public static bool deleteMode => s_Shared != null && s_Shared.IsSpawned ? s_Shared.m_Delete.Value : s_LocalDelete;

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
        if (m_SignageButton != null)
            m_SignageButton.onClick.AddListener(() => ToggleSidePanel(SidePanel.Signage));
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
        if (s_Shared == null || !s_Shared.IsSpawned)
        {
            s_Shared = this;
            m_Delete.OnValueChanged += OnDeleteChanged;
        }
        if (IsServer && s_Shared != this)
            m_Delete.Value = s_Shared.m_Delete.Value;
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        m_Open.OnValueChanged -= OnOpenChanged;
        m_Panel.OnValueChanged -= OnPanelChanged;
        if (s_Shared == this)
        {
            m_Delete.OnValueChanged -= OnDeleteChanged;
            s_Shared = null;
            foreach (var other in FindObjectsByType<TunnelMenu>(FindObjectsSortMode.None))
                if (other != this && other.IsSpawned)
                {
                    s_Shared = other;
                    other.m_Delete.OnValueChanged += other.OnDeleteChanged;
                    break;
                }
        }
    }

    void ToggleDeleteMode()
    {
        bool next = !deleteMode;
        if (s_Shared != null && s_Shared.IsSpawned)
            s_Shared.SetDeleteRpc(next);
        else
        {
            s_LocalDelete = next;
            ShowDeleteMode(next);
        }
    }

    // On every segment's menu, so whichever one a player reads has it.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetDeleteRpc(bool on)
    {
        foreach (var menu in FindObjectsByType<TunnelMenu>(FindObjectsSortMode.None))
            if (menu.IsSpawned)
                menu.m_Delete.Value = on;
    }

    void OnDeleteChanged(bool previous, bool current) => ShowDeleteMode(current);

    static void ShowDeleteMode(bool on) =>
        PlayerHudNotification.Instance?.ShowText(on ? "Delete on for everyone: grab a molecule or container to delete it" : "Delete off", 3f);

    void Update()
    {
        // Every segment's menu shows the one switch.
        if (m_DeleteLabel == null)
            return;
        string text = deleteMode ? "<color=#F55>Delete: ON</color>" : "Delete";
        if (m_DeleteLabel.text != text)
            m_DeleteLabel.text = text;
    }

    static readonly HashSet<GameObject> s_Deleting = new();

    /// <summary>
    /// Deletes a molecule or container its player has just grabbed with Delete on. Called from the grab event, but
    /// deletes it only on the next frame: the interaction toolkit finishes the grab after that event (it gives the
    /// hand focus on what it grabbed), and an object deleted in the middle of it leaves the hand focused on
    /// something that's gone, which breaks grabbing for the rest of the session. Next frame the hand lets go of it
    /// properly, and then it's deleted.
    /// </summary>
    public static void DeleteGrabbed(MonoBehaviour owner, XRBaseInteractable grab)
    {
        if (owner == null || !s_Deleting.Add(owner.gameObject))
            return;
        Debug.Log($"[TS Menu] {owner.name} grabbed with Delete on");
        owner.StartCoroutine(DeleteAfterGrab(owner.gameObject, grab));
    }

    static IEnumerator DeleteAfterGrab(GameObject target, XRBaseInteractable grab)
    {
        yield return null;
        s_Deleting.Remove(target);
        if (target == null)
            yield break;

        if (grab != null)
        {
            var manager = grab.interactionManager;
            if (manager != null)
            {
                manager.CancelInteractableSelection((IXRSelectInteractable)grab);
                manager.CancelInteractableFocus((IXRFocusInteractable)grab);
                manager.CancelInteractableHover((IXRHoverInteractable)grab);
            }
            grab.enabled = false; // nothing grabs it again before it's gone
        }

        Delete(target);
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
            if (networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer)
            {
                networkObject.Despawn(true);
                return;
            }

            // A player who isn't the host asks the host, through any segment's menu that is in the session.
            TunnelMenu menu = null;
            foreach (var candidate in FindObjectsByType<TunnelMenu>(FindObjectsSortMode.None))
                if (candidate.IsSpawned)
                {
                    menu = candidate;
                    break;
                }
            target.SetActive(false); // gone here at once; the host removes it for everyone
            if (menu != null)
                menu.DeleteRpc(networkObject);
            else
                Debug.LogWarning($"[TS Menu] no tunnel segment menu in the session to ask the host to delete {target.name}; hidden here only");
            return;
        }

        Destroy(target);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeleteRpc(NetworkObjectReference reference, RpcParams rpcParams = default)
    {
        bool found = reference.TryGet(out var networkObject) && networkObject.IsSpawned;
        Debug.Log($"[TS Menu] player {rpcParams.Receive.SenderClientId} asked to delete {(found ? networkObject.name : "an object that's already gone")}");
        if (found)
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
        ShowSignage(side == SidePanel.Signage);
    }

    void ShowSignage(bool show)
    {
        if (m_Signage != null)
            m_Signage.ShowList(show);
        if (m_SignageLabel != null)
            m_SignageLabel.text = show ? "Hide Signage" : "Signage";
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
