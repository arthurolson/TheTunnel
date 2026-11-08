using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Logs why a grab doesn't happen, to find what stops grabbing on the headset. Every grab and release by a hand or
/// laser is logged with what it held. When a hand or laser presses select and nothing is grabbed within a moment,
/// it logs that interactor's state: its interaction group (which member is blocking it), what it hovers, its valid
/// targets and whether each could be selected, and the interaction manager. At most one report every 2 seconds.
/// Created by itself at start; needs nothing in the scene.
/// </summary>
public class TunnelGrabDiagnostics : MonoBehaviour
{
    const float k_RescanInterval = 2f;
    const float k_FailDelay = 0.35f;
    const float k_ReportInterval = 2f;

    readonly List<XRBaseInteractor> m_Interactors = new();
    readonly HashSet<XRBaseInteractor> m_Hooked = new();
    readonly Dictionary<XRBaseInteractor, float> m_PressedAt = new();
    readonly List<IXRInteractable> m_Targets = new();
    float m_NextScan;
    float m_NextReport;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var go = new GameObject("Tunnel Grab Diagnostics");
        go.hideFlags = HideFlags.HideInHierarchy;
        DontDestroyOnLoad(go);
        go.AddComponent<TunnelGrabDiagnostics>();
    }

    void Update()
    {
        if (Time.unscaledTime >= m_NextScan)
        {
            m_NextScan = Time.unscaledTime + k_RescanInterval;
            Rescan();
        }

        for (int i = m_Interactors.Count - 1; i >= 0; i--)
        {
            var interactor = m_Interactors[i];
            if (interactor == null)
            {
                m_Interactors.RemoveAt(i);
                continue;
            }
            if (interactor is XRSocketInteractor || !interactor.isActiveAndEnabled)
                continue;

            bool pressed = interactor.isSelectActive;
            if (!pressed || interactor.hasSelection)
            {
                m_PressedAt.Remove(interactor);
                continue;
            }
            if (!m_PressedAt.TryGetValue(interactor, out float since))
            {
                m_PressedAt[interactor] = Time.unscaledTime;
                continue;
            }
            if (since < 0f || Time.unscaledTime - since < k_FailDelay || Time.unscaledTime < m_NextReport)
                continue;
            m_PressedAt[interactor] = -1f; // once per press
            m_NextReport = Time.unscaledTime + k_ReportInterval;
            Report(interactor);
        }
    }

    void Rescan()
    {
        m_Interactors.Clear();
        foreach (var interactor in FindObjectsByType<XRBaseInteractor>(FindObjectsInactive.Include))
        {
            if (interactor is XRSocketInteractor)
                continue;
            m_Interactors.Add(interactor);
            if (m_Hooked.Add(interactor))
            {
                var hooked = interactor;
                interactor.selectEntered.AddListener(args => Debug.Log($"[Grab] {Name(hooked)} grabbed {Name(args.interactableObject)}"));
                interactor.selectExited.AddListener(args => Debug.Log($"[Grab] {Name(hooked)} let go of {Name(args.interactableObject)}{(args.isCanceled ? " (canceled)" : "")}"));
            }
        }
    }

    void Report(XRBaseInteractor interactor)
    {
        var text = new StringBuilder();
        var manager = interactor.interactionManager;
        text.Append($"[Grab] {Name(interactor)} pressed select but grabbed nothing. ");
        text.Append($"manager {(manager == null ? "none" : manager.isActiveAndEnabled ? "on" : "OFF")}");
        if (manager != null)
            text.Append($", interactor registered {manager.IsRegistered((IXRInteractor)interactor)}");
        text.Append($", layers {interactor.interactionLayers.value}, allowSelect {interactor.allowSelect}, allowHover {interactor.allowHover}");

        if (interactor.containingGroup is XRInteractionGroup group)
        {
            text.Append($"\n  group {group.name}: active {Name(group.activeInteractor)}, focus {Name(group.focusInteractor)}");
            var members = new List<IXRGroupMember>();
            group.GetGroupMembers(members);
            foreach (var member in members)
            {
                if (member is not XRBaseInteractor m)
                    continue;
                text.Append($"\n    {Name(m)}: enabled {m.isActiveAndEnabled}, select pressed {m.isSelectActive}, ");
                text.Append($"hovers [{Names(m.interactablesHovered)}], holds [{Names(m.interactablesSelected)}]");
            }
        }
        else
        {
            text.Append("\n  no interaction group");
        }

        text.Append($"\n  hovers [{Names(interactor.interactablesHovered)}]");
        m_Targets.Clear();
        try
        {
            interactor.GetValidTargets(m_Targets);
        }
        catch (System.Exception e)
        {
            text.Append($"\n  valid targets threw {e.GetType().Name}: {e.Message}");
        }
        text.Append($"\n  valid targets {m_Targets.Count}");
        int shown = 0;
        foreach (var target in m_Targets)
        {
            if (shown++ >= 6)
                break;
            text.Append($"\n    {Name(target)}");
            if (target is XRBaseInteractable interactable)
            {
                text.Append($": enabled {interactable.isActiveAndEnabled}, layers {interactable.interactionLayers.value}, ");
                text.Append($"selectable {SafeSelectable(interactable, interactor)}, held by [{Names(interactable.interactorsSelecting)}], ");
                text.Append($"filters {interactable.selectFilters.count}");
                if (manager != null)
                    text.Append($", registered {manager.IsRegistered((IXRInteractable)interactable)}, can select {SafeCanSelect(manager, interactor, interactable)}");
                if (interactable.TryGetComponent(out XRMultiplayer.NetworkBaseInteractable network))
                    text.Append($", network spawned {network.IsSpawned} owner {network.IsOwner} interacting {network.isInteracting}");
            }
        }
        Debug.Log(text.ToString());
    }

    static string SafeSelectable(XRBaseInteractable interactable, XRBaseInteractor interactor)
    {
        try { return interactable.IsSelectableBy((IXRSelectInteractor)interactor).ToString(); }
        catch (System.Exception e) { return e.GetType().Name; }
    }

    static string SafeCanSelect(XRInteractionManager manager, XRBaseInteractor interactor, XRBaseInteractable interactable)
    {
        try { return manager.CanSelect((IXRSelectInteractor)interactor, (IXRSelectInteractable)interactable).ToString(); }
        catch (System.Exception e) { return e.GetType().Name; }
    }

    static string Names<T>(IReadOnlyList<T> items)
    {
        if (items == null || items.Count == 0)
            return "";
        var text = new StringBuilder();
        for (int i = 0; i < items.Count && i < 4; i++)
        {
            if (i > 0)
                text.Append(", ");
            text.Append(Name(items[i]));
        }
        if (items.Count > 4)
            text.Append($" +{items.Count - 4}");
        return text.ToString();
    }

    static string Name(object o)
    {
        if (o == null)
            return "none";
        if (o is Object unityObject)
        {
            if (unityObject == null)
                return "DESTROYED";
            if (unityObject is Component component)
            {
                var parent = component.transform.parent;
                return parent != null ? $"{parent.name}/{component.name}" : component.name;
            }
            return unityObject.name;
        }
        return o.ToString();
    }
}
