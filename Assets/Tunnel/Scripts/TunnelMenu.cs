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
/// (Colors, Textures, Molecules, Containers, Signage, Delete, Clear all, New figure, Saved states, New Browser, New Viewer, Add
/// Segment, Remove Segment, Align to QR). The colour chooser, the texture list, the molecule list, the container list, the signage
/// list and the saved states list open beside it, one at a time; closing the menu also closes them. Everyone sees
/// the same menu: whoever presses a button opens or closes it for all players (the colour chooser shares itself the
/// same way). Works locally when not connected.
/// Delete is one switch for everyone, like the rest of the menu: turning it on (the label reads Delete: ON in red)
/// calls up the eraser in front of whoever pressed it (see <see cref="TunnelEraser"/>); held in a hand, the eraser
/// deletes for everyone whatever deletable thing it touches (see <see cref="Delete"/>). Turning it off removes the
/// eraser. Grabbing things no longer deletes them, so people can go on holding things while Delete is on.
/// New figure calls up a little person in front of whoever pressed it, which they drive with the left thumbstick (see
/// <see cref="TunnelFigure"/>).
/// Clear all, pressed twice within a few seconds (the first press asks "press again"), takes away for everyone
/// everything placed in the tunnel that isn't on a tunnel wall (see <see cref="TunnelClearAll"/>).
/// Saved states opens the list that saves everything set up in every segment and brings a saved setup back for
/// everyone (see <see cref="TunnelSaveStateMenu"/>).
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
        SavedStates,
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
    Button m_ClearAllButton;

    [SerializeField]
    TMP_Text m_ClearAllLabel;

    [SerializeField]
    [Tooltip("Seconds the second press of Clear all has to come in.")]
    float m_ClearAllConfirmTime = 4f;

    [SerializeField]
    [Tooltip("The eraser that Delete calls up (made by Tunnel > Eraser > Build Eraser).")]
    TunnelEraser m_EraserPrefab;

    [SerializeField]
    [Tooltip("How far in front of whoever turns Delete on the eraser appears (meters).")]
    float m_EraserDistance = 0.4f;

    [SerializeField]
    [Tooltip("How far below their eyes it appears (meters).")]
    float m_EraserDrop = 0.25f;

    [SerializeField]
    Button m_NewFigureButton;

    [SerializeField]
    [Tooltip("The figure New figure calls up (made by Tunnel > Figure > Build Figure).")]
    TunnelFigure m_FigurePrefab;

    [SerializeField]
    [Tooltip("How far in front of whoever presses New figure it appears (meters).")]
    float m_FigureDistance = 0.6f;

    [SerializeField]
    Button m_SavedStatesButton;

    [SerializeField]
    TMP_Text m_SavedStatesLabel;

    [SerializeField]
    TunnelSaveStateMenu m_SavedStates;

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

    /// <summary>Whether Delete is on (for everyone): the eraser is out. Off when the app starts.</summary>
    public static bool eraserMode => s_Shared != null && s_Shared.IsSpawned ? s_Shared.m_Delete.Value : s_LocalDelete;

    /// <summary>
    /// Whether grabbing something deletes it: never now, since the eraser deletes instead (see <see cref="TunnelEraser"/>).
    /// Kept false for the grab handlers that still ask, so grabbing with Delete on just holds the thing.
    /// </summary>
    public static bool deleteMode => false;

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
        if (m_ClearAllButton != null)
            m_ClearAllButton.onClick.AddListener(OnClearAllPressed);
        if (m_NewFigureButton != null)
            m_NewFigureButton.onClick.AddListener(OnNewFigurePressed);
        if (m_SavedStatesButton != null)
            m_SavedStatesButton.onClick.AddListener(() => ToggleSidePanel(SidePanel.SavedStates));
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
        bool next = !eraserMode;
        var head = Camera.main != null ? Camera.main.transform : null;
        var pose = head != null ? TunnelMoleculeMenu.NozzlePose(head.position, head.forward, m_EraserDistance, m_EraserDrop)
            : new Pose(transform.position - transform.forward * m_EraserDistance, Quaternion.LookRotation(-transform.forward));
        Debug.Log($"[TS Menu] Delete {(next ? "on: calling up the eraser" : "off: removing the eraser")} " +
            $"({(s_Shared != null && s_Shared.IsSpawned ? "for everyone" : "not connected, here only")})");
        if (s_Shared != null && s_Shared.IsSpawned)
            s_Shared.SetDeleteRpc(next, pose.position, pose.rotation);
        else
        {
            s_LocalDelete = next;
            SetEraser(next, pose);
            ShowDeleteMode(next);
        }
    }

    // On every segment's menu, so whichever one a player reads has it; the eraser comes or goes with it.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetDeleteRpc(bool on, Vector3 position, Quaternion rotation)
    {
        foreach (var menu in FindObjectsByType<TunnelMenu>(FindObjectsSortMode.None))
            if (menu.IsSpawned)
                menu.m_Delete.Value = on;
        SetEraser(on, new Pose(position, rotation));
    }

    /// <summary>
    /// On the host (or here, not connected): puts the eraser at this pose, for everyone (one at most), or removes it.
    /// </summary>
    void SetEraser(bool on, Pose pose)
    {
        var current = TunnelEraser.current;
        if (!on)
        {
            if (current == null)
                return;
            Debug.Log("[Eraser] removed");
            if (current.TryGetComponent(out NetworkObject currentObject) && currentObject.IsSpawned)
                currentObject.Despawn(true);
            else
                Destroy(current.gameObject);
            return;
        }
        if (current != null)
        {
            Debug.Log("[Eraser] already out");
            return;
        }
        if (m_EraserPrefab == null)
        {
            Debug.LogWarning("[Eraser] no eraser to call up (run Tunnel > Eraser > Build Eraser)");
            return;
        }

        // Placed before it wakes up, as the molecules are (its network transform takes its pose when it's created).
        var holder = new GameObject("Eraser Holder");
        holder.SetActive(false);
        var eraser = Instantiate(m_EraserPrefab, holder.transform);
        eraser.transform.SetPositionAndRotation(pose.position, pose.rotation);
        eraser.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Eraser] placed at {pose.position:F2}");
        if (IsSpawned && eraser.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
    }

    // New figure: a figure stands in front of whoever pressed it, at the height of their hands, and they drive it.
    void OnNewFigurePressed()
    {
        var head = Camera.main != null ? Camera.main.transform : null;
        var pose = head != null ? TunnelMoleculeMenu.NozzlePose(head.position, head.forward, m_FigureDistance, 0.45f)
            : new Pose(transform.position - transform.forward * m_FigureDistance, Quaternion.LookRotation(-transform.forward));
        // It faces whoever called it up.
        pose.rotation = Quaternion.LookRotation(-(pose.rotation * Vector3.forward), Vector3.up);
        Debug.Log($"[Figure] New figure pressed ({(IsSpawned ? "for everyone" : "not connected, here only")})");
        if (IsSpawned)
            NewFigureRpc(pose.position, pose.rotation);
        else
            PlaceFigure(pose, null);
        PlayerHudNotification.Instance?.ShowText("New figure: walk it with the left stick (push all the way to run), X jumps; pick it up to move it", 4f);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NewFigureRpc(Vector3 position, Quaternion rotation, RpcParams rpcParams = default) =>
        PlaceFigure(new Pose(position, rotation), rpcParams.Receive.SenderClientId);

    /// <summary>On the host (or here, not connected): puts a new figure at this pose, owned by (driven by) the presser.</summary>
    void PlaceFigure(Pose pose, ulong? driver)
    {
        if (m_FigurePrefab == null)
        {
            Debug.LogWarning("[Figure] no figure to call up (run Tunnel > Figure > Build Figure)");
            return;
        }
        // Placed before it wakes up, so its network transform starts with this pose.
        var holder = new GameObject("Figure Holder");
        holder.SetActive(false);
        var figure = Instantiate(m_FigurePrefab, holder.transform);
        figure.transform.SetPositionAndRotation(pose.position, pose.rotation);
        figure.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Figure] placed at {pose.position:F2}");
        if (IsSpawned && driver.HasValue && figure.TryGetComponent(out NetworkObject networkObject))
            networkObject.SpawnWithOwnership(driver.Value, true);
    }

    void OnDeleteChanged(bool previous, bool current) => ShowDeleteMode(current);

    const string k_ClearAllText = "Clear all";
    const string k_ClearAllConfirmText = "<color=#FF6B5B>Clear all: press again</color>";
    Coroutine m_ClearAllConfirm;

    // First press: asks for a second within a few seconds. Second press: the host clears.
    void OnClearAllPressed()
    {
        if (m_ClearAllConfirm == null)
        {
            m_ClearAllConfirm = StartCoroutine(WaitForClearAllConfirm());
            PlayerHudNotification.Instance?.ShowText("Press Clear all again to take away everything not on a tunnel wall", 3f);
            return;
        }
        StopCoroutine(m_ClearAllConfirm);
        m_ClearAllConfirm = null;
        SetClearAllLabel(k_ClearAllText);
        Debug.Log($"[TS Menu] Clear all confirmed ({(IsSpawned ? (IsServer ? "clearing for everyone" : "asking the host") : "not connected, here only")})");
        if (IsSpawned)
            ClearAllRpc();
        else
            ShowCleared(TunnelClearAll.Clear());
    }

    IEnumerator WaitForClearAllConfirm()
    {
        SetClearAllLabel(k_ClearAllConfirmText);
        yield return new WaitForSeconds(m_ClearAllConfirmTime);
        SetClearAllLabel(k_ClearAllText);
        m_ClearAllConfirm = null;
    }

    void SetClearAllLabel(string text)
    {
        if (m_ClearAllLabel != null)
            m_ClearAllLabel.text = text;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ClearAllRpc() => ClearedRpc(TunnelClearAll.Clear());

    [Rpc(SendTo.Everyone)]
    void ClearedRpc(int count) => ShowCleared(count);

    static void ShowCleared(int count) =>
        PlayerHudNotification.Instance?.ShowText(count == 0
            ? "Clear all: nothing to clear (only things on the walls are left)"
            : $"Clear all: took away {count} {(count == 1 ? "thing" : "things")}; what's on the walls stays", 3f);

    static void ShowDeleteMode(bool on) =>
        PlayerHudNotification.Instance?.ShowText(on
            ? "Delete on for everyone: hold the eraser and touch something to delete it"
            : "Delete off: the eraser is gone", 3f);

    void Update()
    {
        // Every segment's menu shows the one switch.
        if (m_DeleteLabel == null)
            return;
        string text = eraserMode ? "<color=#F55>Delete: ON</color>" : "Delete";
        if (m_DeleteLabel.text != text)
            m_DeleteLabel.text = text;
    }

    static readonly HashSet<GameObject> s_Deleting = new();

    /// <summary>
    /// Deletes a molecule or container its player has just grabbed (no longer called with Delete on: the eraser
    /// deletes instead, see <see cref="TunnelEraser"/>). Called from the grab event, but
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
    /// Deletes a molecule, container or other deletable thing for everyone (the eraser calls it on what it touches):
    /// the host removes it from the session (asked through any tunnel segment's menu); when not connected it is simply
    /// destroyed.
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
        ShowSavedStates(side == SidePanel.SavedStates);
    }

    void ShowSavedStates(bool show)
    {
        if (m_SavedStates != null)
            m_SavedStates.ShowList(show);
        if (m_SavedStatesLabel != null)
            m_SavedStatesLabel.text = show ? "Hide Saved states" : "Saved states";
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
