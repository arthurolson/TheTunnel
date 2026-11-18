using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A "Freeze" checkbox that appears just above a molecule while a hand on this headset holds it or a ray points at it,
/// and stays a few seconds after, facing the player. Ticking it freezes the molecule where it is (see
/// <see cref="MoleculeGravity.isFrozen"/>): it can still be grabbed and moved, and parks again wherever it is let go,
/// keeping its place in a container that moves. Unticking lets it move with the physics again. Shared with everyone.
/// One per headset, made by the TS Menu's Molecules list (prefab from Tunnel > Molecules > Build Freeze Checkbox).
/// </summary>
public class MoleculeFreezeCheckbox : MonoBehaviour
{
    [SerializeField]
    RectTransform m_Canvas;

    [SerializeField]
    Button m_Button;

    [SerializeField]
    [Tooltip("Seconds it stays after the molecule is let go of or no longer pointed at, to be ticked.")]
    float m_Linger = 6f;

    [SerializeField]
    [Tooltip("Gap between the molecule's top and the checkbox (meters).")]
    float m_Gap = 0.03f;

    static MoleculeFreezeCheckbox s_Instance;

    MoleculeGravity m_Target;
    float m_ShowUntil;
    TMP_Text m_Label;
    bool? m_ShownFrozen;

    /// <summary>The headset's checkbox, made from the prefab the first time.</summary>
    public static void EnsureFrom(MoleculeFreezeCheckbox prefab)
    {
        if (s_Instance != null || prefab == null)
            return;
        s_Instance = Instantiate(prefab);
        s_Instance.name = "Molecule Freeze Checkbox";
        DontDestroyOnLoad(s_Instance.gameObject);
    }

    void Awake()
    {
        m_Label = m_Button != null ? m_Button.GetComponentInChildren<TMP_Text>(true) : null;
        if (m_Button != null)
            m_Button.onClick.AddListener(Toggle);
        MoleculeGravity.attention += Show;
        SetVisible(false);
    }

    void OnDestroy()
    {
        MoleculeGravity.attention -= Show;
        if (s_Instance == this)
            s_Instance = null;
    }

    void Show(MoleculeGravity molecule)
    {
        if (molecule == null)
            return;
        m_Target = molecule;
        m_ShowUntil = Time.time + m_Linger;
        m_ShownFrozen = null;
        SetVisible(true);
    }

    void Toggle()
    {
        if (m_Target == null)
            return;
        m_Target.SetFrozen(!m_Target.isFrozen);
        m_ShowUntil = Time.time + m_Linger;
    }

    void SetVisible(bool visible)
    {
        if (m_Canvas != null && m_Canvas.gameObject.activeSelf != visible)
            m_Canvas.gameObject.SetActive(visible);
    }

    void LateUpdate()
    {
        if (m_Target == null)
        {
            SetVisible(false);
            return;
        }
        if (m_Target.isHeld || m_Target.isHovered)
            m_ShowUntil = Time.time + m_Linger;
        if (Time.time > m_ShowUntil)
        {
            SetVisible(false);
            m_Target = null;
            return;
        }

        bool frozen = m_Target.isFrozen;
        if (frozen != m_ShownFrozen && m_Label != null)
        {
            m_ShownFrozen = frozen;
            m_Label.text = (frozen ? "■" : "□") + "  Freeze";
        }

        // Just above the molecule, facing the player.
        var head = Camera.main != null ? Camera.main.transform : null;
        float radius = TunnelMoleculeMenu.WorldRadius(m_Target.gameObject);
        var position = m_Target.transform.position + Vector3.up * (radius + m_Gap);
        var facing = head != null ? position - head.position : Vector3.forward;
        facing.y = 0f;
        transform.SetPositionAndRotation(position, Quaternion.LookRotation(facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward, Vector3.up));
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Molecules > Build Freeze Checkbox).</summary>
    public void Configure(RectTransform canvas, Button button)
    {
        m_Canvas = canvas;
        m_Button = button;
    }
#endif
}
