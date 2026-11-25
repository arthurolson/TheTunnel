using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Teleport along the tunnel: a "Teleport" check button under the Laser button over the left palm turns it on. While
/// it is on, point a hand (or a controller) at the tunnel's floor, in any segment: a line and a ring show where you
/// would land, and a pinch (or the trigger or grip) takes you there, still facing the way you were. Pointing at
/// anything else (a molecule, a panel, a wall) does what it always does. Only this player moves; everyone else sees
/// their avatar arrive. Works the same when not connected.
/// </summary>
public class TunnelTeleport : MonoBehaviour
{
    [SerializeField]
    [Tooltip("The hands' and controllers' Near-Far interactors whose aim is used.")]
    NearFarInteractor[] m_Interactors;

    [SerializeField]
    TeleportationProvider m_Provider;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    TMP_Text m_Label;

    [SerializeField]
    [Tooltip("The line and ring that show where a teleport lands.")]
    Material m_MarkerMaterial;

    [SerializeField]
    [Tooltip("How far a hand reaches when teleporting (meters).")]
    float m_Reach = 30f;

    [SerializeField]
    [Tooltip("The ring's diameter (meters).")]
    float m_RingSize = 0.35f;

    bool m_On;
    XROrigin m_Origin;
    LineRenderer m_Line;
    Transform m_Ring;
    float m_NextTeleport;

    /// <summary>Whether teleporting is on.</summary>
    public bool isOn => m_On;

    /// <summary>
    /// How far (in the world, sideways only) this player has teleported in all: where they are in the tunnel minus
    /// where the real room puts them. Things seen in the real room (the AR marker, the QR code) are shared without it,
    /// so a teleport never moves them, or the room alignment, for anyone else.
    /// </summary>
    public static Vector3 offset { get; private set; }

    /// <summary>Raised on this headset after each teleport, with the new <see cref="offset"/>.</summary>
    public static event System.Action<Vector3> offsetChanged;

    /// <summary><see cref="offset"/> in the shared tabletop frame (the frame AR marker sightings are given in).</summary>
    public static Vector3 OffsetIn(Transform table) =>
        table != null ? Quaternion.Inverse(table.rotation) * offset : offset;

    void Awake()
    {
        offset = Vector3.zero; // the scene (and the XR Origin) starts afresh
        if (m_Button != null)
            m_Button.onClick.AddListener(Toggle);
        MakeMarker();
        Set(false);
    }

    void OnDestroy()
    {
        if (m_Button != null)
            m_Button.onClick.RemoveListener(Toggle);
    }

    public void Toggle() => Set(!m_On);

    public void Set(bool on)
    {
        m_On = on;
        if (m_Label != null)
            m_Label.text = on ? "<b>[X]</b> Teleport" : "[  ] Teleport";
        ShowMarker(false);
        Debug.Log($"[Teleport] {(on ? "on: point at the tunnel floor and pinch (or pull the trigger) to go there" : "off")}");
    }

    void Update()
    {
        if (!m_On)
            return;
        bool shown = false;
        foreach (var interactor in m_Interactors)
        {
            if (interactor == null || !interactor.isActiveAndEnabled || shown)
                continue;
            if (!TryAim(interactor, out var from, out var to))
                continue;
            shown = true;
            ShowMarker(true, from, to);
            if (Time.unscaledTime >= m_NextTeleport && Pressed(interactor))
                Go(to);
        }
        if (!shown)
            ShowMarker(false);
    }

    /// <summary>Whether this hand points at the tunnel's floor with nothing else (a toy or a panel) in the way.</summary>
    bool TryAim(NearFarInteractor interactor, out Vector3 from, out Vector3 to)
    {
        from = to = default;
        var caster = interactor.farInteractionCaster;
        var origin = caster != null && caster.castOrigin != null ? caster.castOrigin : interactor.transform;
        if (interactor.interactablesHovered.Count > 0 || interactor.hasSelection)
            return false; // it's reaching for something
        if (interactor.TryGetCurrentUIRaycastResult(out var ui) && ui.isValid)
            return false; // it's on a panel
        from = origin.position;
        if (!Physics.Raycast(from, origin.forward, out var hit, m_Reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;
        if (!IsTunnelFloor(hit.collider) || hit.normal.y < 0.7f)
            return false;
        to = hit.point;
        return true;
    }

    // A segment's floor box (TunnelSegmentGeometry builds "Floor" under its "Colliders").
    static bool IsTunnelFloor(Collider c)
    {
        var parent = c.transform.parent;
        return c.name == "Floor" && parent != null && parent.name == "Colliders" && parent.parent != null
            && parent.parent.GetComponent<TunnelSegmentGeometry>() != null;
    }

    static bool Pressed(NearFarInteractor interactor) =>
        interactor.selectInput.ReadWasPerformedThisFrame() || interactor.activateInput.ReadWasPerformedThisFrame();

    // Slides the player along the floor so they stand where the ring was: the rig moves sideways only, never up or
    // down, so the tunnel's floor stays on the real floor (the rig's height is how the tunnel was lined up with it).
    void Go(Vector3 to)
    {
        m_NextTeleport = Time.unscaledTime + 0.5f;
        if (m_Origin == null)
            m_Origin = FindAnyObjectByType<XROrigin>();
        var head = m_Origin != null && m_Origin.Camera != null ? m_Origin.Camera.transform : Camera.main != null ? Camera.main.transform : null;
        if (m_Origin == null || head == null)
        {
            Debug.LogWarning("[Teleport] no XR Origin to move");
            return;
        }
        var rig = m_Origin.Origin.transform;
        var step = new Vector3(to.x - head.position.x, 0f, to.z - head.position.z);
        float rigY = rig.position.y, headY = head.position.y;
        rig.position += step;
        offset += step;
        offsetChanged?.Invoke(offset);
        Debug.Log($"[Teleport] to {to:F2}: moved {step.magnitude:F2} m along the floor; rig height {rigY:F2} → {rig.position.y:F2}, " +
            $"head height {headY:F2} → {head.position.y:F2}, tunnel floor at {to.y:F2}; " +
            $"now {offset.magnitude:F2} m from where the real room puts you");
    }

    void MakeMarker()
    {
        var line = new GameObject("Teleport Line");
        line.transform.SetParent(transform, false);
        m_Line = line.AddComponent<LineRenderer>();
        m_Line.positionCount = 2;
        m_Line.useWorldSpace = true;
        m_Line.widthMultiplier = 0.006f;
        m_Line.numCapVertices = 2;
        m_Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m_Line.receiveShadows = false;
        m_Line.sharedMaterial = m_MarkerMaterial;

        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "Teleport Ring";
        Destroy(ring.GetComponent<Collider>());
        ring.transform.SetParent(transform, false);
        ring.transform.localScale = new Vector3(m_RingSize, 0.002f, m_RingSize);
        var renderer = ring.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = m_MarkerMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m_Ring = ring.transform;
    }

    void ShowMarker(bool show, Vector3 from = default, Vector3 to = default)
    {
        if (m_Line == null || m_Ring == null)
            return;
        if (m_Line.gameObject.activeSelf != show)
            m_Line.gameObject.SetActive(show);
        if (m_Ring.gameObject.activeSelf != show)
            m_Ring.gameObject.SetActive(show);
        if (!show)
            return;
        m_Line.SetPosition(0, from);
        m_Line.SetPosition(1, to);
        m_Ring.SetPositionAndRotation(to + Vector3.up * 0.003f, Quaternion.identity);
    }
}
