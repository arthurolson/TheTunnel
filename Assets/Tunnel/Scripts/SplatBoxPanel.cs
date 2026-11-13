using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The small menu beside a splat-box (see <see cref="SplatBox"/>): "Splats", then one row per splat in its library
/// (see <see cref="SplatLibrary"/>), the one shown checked. Pressing a row shows that splat in the box, for everyone.
/// Its rows are made from the library when it appears, so a splat added to the library needs no new menu. Each headset
/// has its own copy, facing its player. Made by Tunnel > Containers > Build Containers.
/// </summary>
public class SplatBoxPanel : MonoBehaviour
{
    [SerializeField]
    RectTransform m_Canvas;

    [SerializeField]
    [Tooltip("A row, hidden; one copy is made per splat.")]
    Button m_RowTemplate;

    [SerializeField]
    TMP_Text m_Title;

    [SerializeField]
    [Tooltip("Height of the title above the rows (canvas units).")]
    float m_Top = 80f;

    [SerializeField]
    [Tooltip("Height of each row (canvas units).")]
    float m_RowHeight = 64f;

    [SerializeField]
    [Tooltip("Room under the last row (canvas units).")]
    float m_Bottom = 16f;

    SplatBox m_Box;
    readonly List<Button> m_Rows = new();
    int m_ShownFor = int.MinValue;

    /// <summary>Its width (meters).</summary>
    public float width => m_Canvas != null ? m_Canvas.sizeDelta.x * m_Canvas.lossyScale.x : 0.16f;

    /// <summary>Makes a row for each splat in the box's library.</summary>
    public void Bind(SplatBox box)
    {
        m_Box = box;
        foreach (var row in m_Rows)
            if (row != null)
                Destroy(row.gameObject);
        m_Rows.Clear();

        var library = box.library;
        int count = library != null ? library.count : 0;
        if (m_RowTemplate != null)
        {
            m_RowTemplate.gameObject.SetActive(false);
            for (int i = 0; i < count; i++)
            {
                var row = Instantiate(m_RowTemplate, m_RowTemplate.transform.parent, false);
                row.name = $"Splat Row {i}";
                row.gameObject.SetActive(true);
                var rect = (RectTransform)row.transform;
                rect.anchoredPosition = new Vector2(0f, -(m_Top + m_RowHeight * (i + 0.5f)));
                int index = i;
                row.onClick.AddListener(() => m_Box?.Choose(index));
                m_Rows.Add(row);
            }
        }
        if (m_Canvas != null)
            m_Canvas.sizeDelta = new Vector2(m_Canvas.sizeDelta.x, m_Top + m_RowHeight * Mathf.Max(count, 1) + m_Bottom);
        if (m_Title != null)
            m_Title.text = count == 0 ? "Splats  <color=#AAA>(none in the library)</color>" : "Splats";
        m_ShownFor = int.MinValue;
        Refresh();
    }

    void Update() => Refresh();

    // ■ for the splat shown, □ for the others.
    void Refresh()
    {
        if (m_Box == null || m_Box.shown == m_ShownFor)
            return;
        m_ShownFor = m_Box.shown;
        var library = m_Box.library;
        for (int i = 0; i < m_Rows.Count; i++)
        {
            var label = m_Rows[i].GetComponentInChildren<TMP_Text>(true);
            var splat = library.Get(i);
            label.text = (i == m_ShownFor ? "■  " : "□  ") + (splat != null ? splat.name : "?");
        }
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Containers > Build Containers).</summary>
    public void Configure(RectTransform canvas, Button rowTemplate, TMP_Text title)
    {
        m_Canvas = canvas;
        m_RowTemplate = rowTemplate;
        m_Title = title;
    }
#endif
}
