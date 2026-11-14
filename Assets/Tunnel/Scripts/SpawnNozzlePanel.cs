using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The small menu beside the spawn nozzle (see <see cref="SpawnNozzle"/>): what's attached to it, a Copies slider
/// (1 to 100, shared by everyone), a Spawn button that sprays that many copies of the attached molecule (Stop while
/// it sprays), and the nozzle's latest message. Each headset has its own copy, facing its player. Made by
/// Tunnel > Molecules > Build Spawn Nozzle.
/// </summary>
public class SpawnNozzlePanel : MonoBehaviour
{
    [SerializeField]
    RectTransform m_Canvas;

    [SerializeField]
    TMP_Text m_Title;

    [SerializeField]
    [Tooltip("What's attached (or how to attach something).")]
    TMP_Text m_Loaded;

    [SerializeField]
    Slider m_CountSlider;

    [SerializeField]
    TMP_Text m_CountText;

    [SerializeField]
    Button m_SpawnButton;

    [SerializeField]
    TMP_Text m_Status;

    SpawnNozzle m_Nozzle;
    float m_NextRefresh;

    /// <summary>Its width (meters).</summary>
    public float width => m_Canvas != null ? m_Canvas.sizeDelta.x * m_Canvas.lossyScale.x : 0.25f;

    public void Bind(SpawnNozzle nozzle)
    {
        m_Nozzle = nozzle;
        Refresh(true);
    }

    void Awake()
    {
        if (m_CountSlider != null)
        {
            m_CountSlider.minValue = SpawnNozzle.k_MinCount;
            m_CountSlider.maxValue = SpawnNozzle.k_MaxCount;
            m_CountSlider.wholeNumbers = true;
            m_CountSlider.SetValueWithoutNotify(SpawnNozzle.k_DefaultCount);
            m_CountSlider.onValueChanged.AddListener(OnCountMoved);
        }
        if (m_SpawnButton != null)
            m_SpawnButton.onClick.AddListener(() =>
            {
                if (m_Nozzle != null)
                    m_Nozzle.SpawnOrStop();
            });
    }

    void OnDestroy()
    {
        if (m_CountSlider != null)
            m_CountSlider.onValueChanged.RemoveAllListeners();
        if (m_SpawnButton != null)
            m_SpawnButton.onClick.RemoveAllListeners();
    }

    void OnCountMoved(float value)
    {
        if (m_Nozzle != null)
            m_Nozzle.SetCount(Mathf.RoundToInt(value));
        ShowCount(Mathf.RoundToInt(value));
    }

    void Update()
    {
        // A few times a second is plenty for text that follows the nozzle's state.
        if (Time.unscaledTime < m_NextRefresh)
            return;
        m_NextRefresh = Time.unscaledTime + 0.2f;
        Refresh();
    }

    /// <summary>
    /// Shows the nozzle's state: what's attached, the count, the button and the latest message. With
    /// <paramref name="moveSlider"/> (when the shared count changes) the slider is moved to the count too; otherwise it
    /// is left alone, so it doesn't jump back while it's being dragged.
    /// </summary>
    public void Refresh(bool moveSlider = false)
    {
        if (m_Nozzle == null)
            return;

        int count = m_Nozzle.count;
        if (moveSlider && m_CountSlider != null && Mathf.RoundToInt(m_CountSlider.value) != count)
            m_CountSlider.SetValueWithoutNotify(count);
        ShowCount(m_CountSlider != null ? Mathf.RoundToInt(m_CountSlider.value) : count);

        var molecule = m_Nozzle.loaded;
        int most = m_Nozzle.maxCopies;
        SetText(m_Loaded, molecule == null
            ? "<color=#AAA>Empty: let go of a molecule at the cup on its back.</color>"
            : $"Attached: <b>{m_Nozzle.loadedName}</b> ×{m_Nozzle.loadedMagnification:0.#}" +
              (most < SpawnNozzle.k_MaxCount ? $"  <color=#FC6>at most {most} of these</color>" : ""));

        if (m_SpawnButton != null)
        {
            bool spraying = m_Nozzle.spraying;
            m_SpawnButton.interactable = spraying || molecule != null;
            int n = Mathf.Min(count, most);
            SetText(m_SpawnButton.GetComponentInChildren<TMP_Text>(true), spraying
                ? $"Stop  <color=#AAA>{m_Nozzle.made} of {m_Nozzle.target}</color>"
                : molecule == null ? "Spawn  <color=#AAA>(attach a molecule)</color>" : $"Spawn {n}");
        }

        SetText(m_Status, m_Nozzle.message);
    }

    void ShowCount(int count) => SetText(m_CountText, $"Copies: {count}");

    static void SetText(TMP_Text label, string text)
    {
        if (label != null && label.text != text)
            label.text = text;
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Molecules > Build Spawn Nozzle).</summary>
    public void Configure(RectTransform canvas, TMP_Text title, TMP_Text loaded, Slider countSlider, TMP_Text countText, Button spawnButton, TMP_Text status)
    {
        m_Canvas = canvas;
        m_Title = title;
        m_Loaded = loaded;
        m_CountSlider = countSlider;
        m_CountText = countText;
        m_SpawnButton = spawnButton;
        m_Status = status;
    }
#endif
}
