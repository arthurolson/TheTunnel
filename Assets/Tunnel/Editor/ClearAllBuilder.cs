using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tunnel > Menu > Add Clear All Button: puts the TS Menu's Clear all button (see <see cref="TunnelClearAll"/>) under
/// Delete in the tunnel segment prefab, a copy of the Delete button, and hooks it to the segment's menu. Run it again
/// to rebuild the button; the menu's other buttons are re-spaced to fit it.
/// </summary>
static class ClearAllBuilder
{
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    [MenuItem("Tunnel/Menu/Add Clear All Button")]
    public static void Build()
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var menuPanel = root.transform.Find("Control Panel/Menu Panel");
            var deleteButton = menuPanel.Find("Delete Button");
            var old = menuPanel.Find("Clear All Button");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var clear = Object.Instantiate(deleteButton.gameObject, menuPanel, false).transform;
            clear.name = "Clear All Button";
            clear.SetSiblingIndex(deleteButton.GetSiblingIndex() + 1);
            var button = clear.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            var label = clear.Find("Button Front/Text (TMP)").GetComponent<TMP_Text>();
            label.text = "Clear all";
            label.richText = true;
            TunnelMenuLayout.Arrange(menuPanel);

            var menu = root.GetComponentInChildren<TunnelMenu>(true);
            var so = new SerializedObject(menu);
            so.FindProperty("m_ClearAllButton").objectReferenceValue = button;
            so.FindProperty("m_ClearAllLabel").objectReferenceValue = label;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath);
            Debug.Log("[TS Menu] Clear all button added under Delete");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
