using UnityEngine;

/// <summary>
/// Spaces the TS Menu's buttons in its menu panel, in a fixed order, one every 56 units from the top, and sizes the
/// panel to hold them (its top stays where it is). Buttons not in the prefab are skipped. Used by the builders that
/// add buttons (Containers, Delete, Paint, Signage).
/// </summary>
static class TunnelMenuLayout
{
    static readonly string[] k_Order =
    {
        "Colors Button", "Textures Button", "Molecules Button", "Containers Button", "Signage Button", "Paint Button", "Delete Button",
        "New Browser Button", "New Viewer Button", "Add Segment Button", "Remove Segment Button", "Align Button",
    };

    const float k_Spacing = 56f;
    const float k_PanelTop = 135f; // the panel's top edge in the control panel (just under the TS Menu button)

    public static void Arrange(Transform menuPanel)
    {
        var buttons = new System.Collections.Generic.List<RectTransform>();
        foreach (var name in k_Order)
            if (menuPanel.Find(name) is RectTransform button)
                buttons.Add(button);

        var panel = (RectTransform)menuPanel;
        float height = k_Spacing * buttons.Count + 10f;
        panel.sizeDelta = new Vector2(panel.sizeDelta.x, height);
        panel.anchoredPosition = new Vector2(panel.anchoredPosition.x, k_PanelTop - height * 0.5f);
        float top = k_Spacing * (buttons.Count - 1) * 0.5f;
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].anchoredPosition = new Vector2(buttons[i].anchoredPosition.x, top - k_Spacing * i);
            buttons[i].SetSiblingIndex(i);
        }

        // The Align to QR status line stays just under the panel (it hangs from the panel's top edge).
        if (menuPanel.Find("QR Status") is RectTransform status)
            status.anchoredPosition = new Vector2(status.anchoredPosition.x, -height - 8f);
    }
}
