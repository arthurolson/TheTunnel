using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The TS Menu's eraser: a hand-held block eraser (a felt pad under a wooden block) that appears in front of whoever
/// turns the menu's Delete on, for everyone, and goes when Delete is turned off again (see <see cref="TunnelMenu"/>).
/// It deletes only while a player holds it: whatever deletable thing its felt touches (a molecule, a virus with its
/// bound antibodies, a container by its edges, a palette and its marker, a painting copy, the spawn nozzle or a sign;
/// see <see cref="IsDeletable"/>) is deleted for everyone, with a tick in the hand that holds it, a soft rub sound
/// and a flash of the felt. It never deletes itself, the TS Menu, the tunnel, the movie or container menus, panels or
/// people. Lying where it was left, it deletes nothing. There is one at a time, and it stays where it's left (it isn't
/// pushed, and pushes nothing). Works locally when not connected.
/// </summary>
public class TunnelEraser : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("The felt pad: a box whose transform is the felt's size; what it touches is deleted.")]
    Transform m_Felt;

    [SerializeField]
    [Tooltip("The felt's renderer, which flashes when it deletes something.")]
    Renderer m_FeltRenderer;

    [SerializeField]
    [Tooltip("How far past the felt it reaches (meters): anything this close counts as touched.")]
    float m_Reach = 0.01f;

    [SerializeField]
    Color m_FeltColour = new(0.16f, 0.17f, 0.2f);

    [SerializeField]
    Color m_FlashColour = new(1f, 0.45f, 0.35f);

    [SerializeField]
    [Tooltip("Seconds the flash takes to fade.")]
    float m_FlashTime = 0.25f;

    /// <summary>The kinds of thing it deletes (a GameObject with one of these on it).</summary>
    public static readonly Type[] deletableTypes =
    {
        typeof(MoleculeGravity), typeof(TunnelContainer), typeof(TunnelPalette), typeof(TunnelMarker),
        typeof(PaintingCopy), typeof(SpawnNozzle), typeof(TunnelSign),
    };

    /// <summary>The eraser in the tunnel, if there is one.</summary>
    public static TunnelEraser current { get; private set; }

    const int k_IgnoreRaycastLayer = 2; // containers' walls and binding sites: never touched by it

    static AudioClip s_Rub;
    readonly Collider[] m_Hits = new Collider[64];
    readonly Dictionary<GameObject, float> m_Erased = new();
    XRGrabInteractable m_Grab;
    AudioSource m_Audio;
    MaterialPropertyBlock m_Block;
    float m_FlashedAt = -10f;
    bool m_Flashing;

    /// <summary>Whether a player holds it here (in a hand, not a socket): only then does it delete.</summary>
    public bool held
    {
        get
        {
            if (m_Grab == null || !m_Grab.isSelected)
                return false;
            foreach (var interactor in m_Grab.interactorsSelecting)
                if (!(interactor is XRSocketInteractor))
                    return true;
            return false;
        }
    }

    /// <summary>How many things it has deleted since it appeared (on this headset).</summary>
    public int erased { get; private set; }

    void Awake()
    {
        TryGetComponent(out m_Grab);
        m_Block = new MaterialPropertyBlock();
        if (!TryGetComponent(out m_Audio))
            m_Audio = gameObject.AddComponent<AudioSource>();
        m_Audio.playOnAwake = false;
        m_Audio.spatialBlend = 1f;
        m_Audio.minDistance = 0.3f;
        m_Audio.maxDistance = 6f;
        m_Audio.volume = 0.7f;
        if (s_Rub == null)
            s_Rub = MakeRub();
    }

    void OnEnable() => current = this;

    void OnDisable()
    {
        if (current == this)
            current = null;
    }

    void Start() => Debug.Log($"[Eraser] appeared at {transform.position:F2} ({(IsSpawned ? "for everyone" : "not connected, here only")})");

    void Update()
    {
        if (held)
            EraseTouched();
        ShowFlash();
    }

    /// <summary>
    /// Deletes, for everyone, every deletable thing the felt touches now, if a player holds it here. Returns how
    /// many it deleted.
    /// </summary>
    public int EraseTouched()
    {
        if (!held || m_Felt == null)
            return 0;
        int count = Physics.OverlapBoxNonAlloc(m_Felt.position, Vector3.Scale(m_Felt.lossyScale, Vector3.one * 0.5f) + Vector3.one * m_Reach,
            m_Hits, m_Felt.rotation, Physics.AllLayers, QueryTriggerInteraction.Ignore);
        int deleted = 0;
        for (int i = 0; i < count; i++)
        {
            var hit = m_Hits[i];
            if (hit == null || hit.gameObject.layer == k_IgnoreRaycastLayer || hit.transform.IsChildOf(transform))
                continue;
            var target = DeletableAt(hit);
            if (target == null || target == gameObject || m_Erased.ContainsKey(target))
                continue;
            Erase(target);
            deleted++;
        }
        if (m_Erased.Count > 0)
        {
            // Forget those long gone (a deleted one may linger a frame before it's destroyed).
            List<GameObject> old = null;
            foreach (var (go, at) in m_Erased)
                if (go == null || Time.unscaledTime - at > 2f)
                    (old ??= new List<GameObject>()).Add(go);
            if (old != null)
                foreach (var go in old)
                    m_Erased.Remove(go);
        }
        return deleted;
    }

    // Deletes it (and what goes with it) for everyone, with a tick, a rub and a flash.
    void Erase(GameObject target)
    {
        m_Erased[target] = Time.unscaledTime;
        erased++;
        Debug.Log($"[Eraser] touched {target.name}: deleting it for everyone");
        // A palette and its marker go together, as they always have.
        if (target.TryGetComponent(out TunnelPalette palette))
        {
            foreach (var marker in TunnelMarker.all.ToArray())
                if (marker != null && marker.palette == palette && !m_Erased.ContainsKey(marker.gameObject))
                {
                    m_Erased[marker.gameObject] = Time.unscaledTime;
                    TunnelMenu.Delete(marker.gameObject);
                }
        }
        else if (target.TryGetComponent(out TunnelMarker ownMarker) && ownMarker.palette != null && !m_Erased.ContainsKey(ownMarker.palette.gameObject))
        {
            m_Erased[ownMarker.palette.gameObject] = Time.unscaledTime;
            TunnelMenu.Delete(ownMarker.palette.gameObject);
        }
        TunnelMenu.Delete(target);

        if (m_Grab != null)
            foreach (var interactor in m_Grab.interactorsSelecting)
                if (interactor is XRBaseInputInteractor input)
                    input.SendHapticImpulse(0.6f, 0.06f);
        Feedback();
        if (IsSpawned)
            FeedbackRpc();
    }

    [Rpc(SendTo.NotMe, InvokePermission = RpcInvokePermission.Everyone)]
    void FeedbackRpc() => Feedback();

    void Feedback()
    {
        m_FlashedAt = Time.unscaledTime;
        m_Flashing = true;
        if (m_Audio != null && s_Rub != null && m_Audio.isActiveAndEnabled)
            m_Audio.PlayOneShot(s_Rub);
    }

    void ShowFlash()
    {
        if (!m_Flashing || m_FeltRenderer == null)
            return;
        float t = m_FlashTime > 0f ? Mathf.Clamp01((Time.unscaledTime - m_FlashedAt) / m_FlashTime) : 1f;
        m_FeltRenderer.GetPropertyBlock(m_Block);
        m_Block.SetColor("_BaseColor", Color.Lerp(m_FlashColour, m_FeltColour, t));
        m_FeltRenderer.SetPropertyBlock(m_Block);
        if (t >= 1f)
            m_Flashing = false;
    }

    /// <summary>
    /// Whether the eraser deletes this: a molecule, container, palette, marker, painting copy, spawn nozzle or sign
    /// (see <see cref="deletableTypes"/>), never the eraser itself.
    /// </summary>
    public static bool IsDeletable(GameObject go)
    {
        if (go == null || go.GetComponent<TunnelEraser>() != null)
            return false;
        foreach (var type in deletableTypes)
            if (go.GetComponent(type) != null)
                return true;
        return false;
    }

    /// <summary>The deletable thing a collider belongs to (the nearest one up from it), or null.</summary>
    public static GameObject DeletableAt(Collider collider)
    {
        for (var t = collider != null ? collider.transform : null; t != null; t = t.parent)
        {
            if (t.GetComponent<TunnelEraser>() != null)
                return null;
            if (IsDeletable(t.gameObject))
                return t.gameObject;
        }
        return null;
    }

    // A short soft rub: noise, lowered, rising and fading over a tenth of a second.
    static AudioClip MakeRub()
    {
        const int rate = 44100;
        int length = rate / 9;
        var samples = new float[length];
        var random = new System.Random(7);
        float low = 0f;
        for (int i = 0; i < length; i++)
        {
            float t = i / (float)length;
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            low += (noise - low) * 0.25f; // softer, felt-like
            float envelope = Mathf.Sin(Mathf.PI * Mathf.Min(1f, t * 3f) * 0.5f) * (1f - t) * (1f - t);
            samples[i] = low * envelope * 0.9f;
        }
        var clip = AudioClip.Create("Eraser Rub", length, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

#if UNITY_EDITOR
    /// <summary>Set by Tunnel > Eraser > Build Eraser.</summary>
    public void Configure(Transform felt, Renderer feltRenderer, Color feltColour)
    {
        m_Felt = felt;
        m_FeltRenderer = feltRenderer;
        m_FeltColour = feltColour;
    }
#endif
}
