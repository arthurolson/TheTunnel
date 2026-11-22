using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tunnel > Menu > Add Saved States: puts the TS Menu's Saved states button under New figure in the tunnel segment
/// prefab (a copy of the Signage button; the menu's buttons re-spaced to fit), and the Saved states list beside the
/// menu (see <see cref="TunnelSaveStateMenu"/>): the Molecules list's panel, title, status and scroll view, with Save now,
/// the saved states, Restore and Delete. The list is given the prefabs a state is made from, taken from the segment's
/// other menus (molecules, containers and splat-box, spawn nozzle, New figure, sign). Run it again to rebuild them; run
/// it after Build Figure, Build Signage or Build Spawn Nozzle too, so it has their latest prefabs.
/// </summary>
static class SaveStateBuilder
{
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    [MenuItem("Tunnel/Menu/Add Saved States")]
    public static string Build()
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var control = root.transform.Find("Control Panel");
            var menuPanel = control.Find("Menu Panel");
            var signageButton = menuPanel.Find("Signage Button");
            var moleculeList = control.Find("Molecule List");
            var rowSource = control.Find("Container List/Box Row");
            var titleSource = control.Find("Container List/Title");
            if (signageButton == null || moleculeList == null || rowSource == null || titleSource == null)
                return Report("FAILED: the segment has no Signage button, Molecules list or Containers list to copy (run Build Signage first)");

            // The button, under New figure.
            var old = menuPanel.Find("Saved States Button");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var buttonObject = Object.Instantiate(signageButton.gameObject, menuPanel, false).transform;
            buttonObject.name = "Saved States Button";
            var button = buttonObject.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            var label = buttonObject.Find("Button Front/Text (TMP)").GetComponent<TMP_Text>();
            label.text = "Saved states";
            TunnelMenuLayout.Arrange(menuPanel);

            // The list, where the Molecules list is.
            var oldList = control.Find("Saved States List");
            if (oldList != null)
                Object.DestroyImmediate(oldList.gameObject);
            var list = Object.Instantiate(moleculeList.gameObject, control, false);
            list.name = "Saved States List";
            foreach (var unused in new[] { "Magnification", NozzleBuilder.k_RowName })
            {
                var child = list.transform.Find(unused);
                if (child != null)
                    Object.DestroyImmediate(child.gameObject);
            }
            var listRect = (RectTransform)list.transform;
            var moleculeRect = (RectTransform)moleculeList;
            listRect.sizeDelta = new Vector2(540f, 640f);
            float top = moleculeRect.anchoredPosition.y + moleculeRect.sizeDelta.y * 0.5f;
            listRect.anchoredPosition = new Vector2(moleculeRect.anchoredPosition.x, top - 320f);

            var title = (RectTransform)list.transform.Find("Title");
            title.anchoredPosition = new Vector2(-40f, 275f);
            title.GetComponent<TMP_Text>().text = "Saved states";
            var status = (RectTransform)list.transform.Find("Status");
            status.anchoredPosition = new Vector2(0f, 222f);
            status.sizeDelta = new Vector2(500f, 64f);
            var statusText = status.GetComponent<TMP_Text>();
            statusText.fontSize = 18f;
            statusText.textWrappingMode = TextWrappingModes.Normal;

            var save = Row(rowSource.gameObject, listRect, "Save Now Button", "Save now", new Vector2(0f, 155f), new Vector2(460f, 56f), true);
            save.GetComponentInChildren<TMP_Text>(true).fontStyle = FontStyles.Bold;
            var savedTitle = Text(titleSource.gameObject, listRect, "Saved Title", "Saved (newest first)", new Vector2(0f, 105f), new Vector2(460f, 36f), 22f);
            savedTitle.fontStyle = FontStyles.Bold;
            var scroll = (RectTransform)list.transform.Find("Scroll View");
            scroll.anchoredPosition = new Vector2(0f, -55f);
            scroll.sizeDelta = new Vector2(500f, 270f);
            var content = (RectTransform)scroll.Find("Viewport/Content");
            for (int i = content.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(content.GetChild(i).gameObject);
            var restore = Row(rowSource.gameObject, listRect, "Restore Button", "Restore", new Vector2(-118f, -250f), new Vector2(224f, 56f), true);
            restore.GetComponentInChildren<TMP_Text>(true).enableAutoSizing = true;
            var delete = Row(rowSource.gameObject, listRect, "Delete Button", "Delete", new Vector2(118f, -250f), new Vector2(224f, 56f), true);
            var rowTemplate = list.transform.Find("Row Template");
            var rowLabel = rowTemplate.GetComponentInChildren<TMP_Text>(true);
            rowLabel.richText = true;
            list.SetActive(false);

            // The list's component and what states are made of, from the segment's other menus.
            if (!root.TryGetComponent(out TunnelSaveStateMenu savedStates))
                savedStates = root.AddComponent<TunnelSaveStateMenu>();
            var moleculeMenu = root.GetComponentInChildren<TunnelMoleculeMenu>(true);
            var containerMenu = root.GetComponentInChildren<TunnelContainerMenu>(true);
            var signageMenu = root.GetComponentInChildren<TunnelSignageMenu>(true);
            var tunnelMenu = control.GetComponent<TunnelMenu>();
            Object From(Object owner, string field) => owner != null ? new SerializedObject(owner).FindProperty(field)?.objectReferenceValue : null;

            var so = new SerializedObject(savedStates);
            so.FindProperty("m_ListPanel").objectReferenceValue = list;
            so.FindProperty("m_SaveButton").objectReferenceValue = save;
            so.FindProperty("m_ListContent").objectReferenceValue = content;
            so.FindProperty("m_RowTemplate").objectReferenceValue = rowTemplate.GetComponent<Button>();
            so.FindProperty("m_RestoreButton").objectReferenceValue = restore;
            so.FindProperty("m_DeleteButton").objectReferenceValue = delete;
            so.FindProperty("m_StatusText").objectReferenceValue = statusText;
            so.FindProperty("m_Prefabs").objectReferenceValue = From(moleculeMenu, "m_Prefabs");
            so.FindProperty("m_SplatBoxPrefab").objectReferenceValue = From(containerMenu, "m_SplatBoxPrefab");
            so.FindProperty("m_NozzlePrefab").objectReferenceValue = From(moleculeMenu, "m_NozzlePrefab");
            so.FindProperty("m_FigurePrefab").objectReferenceValue = From(tunnelMenu, "m_FigurePrefab");
            so.FindProperty("m_SignPrefab").objectReferenceValue = From(signageMenu, "m_SignPrefab");
            so.ApplyModifiedPropertiesWithoutUndo();

            var menuSo = new SerializedObject(tunnelMenu);
            menuSo.FindProperty("m_SavedStatesButton").objectReferenceValue = button;
            menuSo.FindProperty("m_SavedStatesLabel").objectReferenceValue = label;
            menuSo.FindProperty("m_SavedStates").objectReferenceValue = savedStates;
            menuSo.ApplyModifiedPropertiesWithoutUndo();

            var missing = new System.Collections.Generic.List<string>();
            foreach (var field in new[] { "m_Prefabs", "m_SplatBoxPrefab", "m_NozzlePrefab", "m_FigurePrefab", "m_SignPrefab" })
                if (so.FindProperty(field).objectReferenceValue == null)
                    missing.Add(field);

            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return Report(saved
                ? "TS Menu: Saved states button under New figure, and its list" + (missing.Count > 0 ? $"; missing prefabs: {string.Join(", ", missing)}" : "; every prefab linked")
                : "FAILED to save the segment prefab");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static string Report(string text)
    {
        Debug.Log($"[SaveState] {text}");
        return text;
    }

    static Button Row(GameObject template, RectTransform parent, string name, string text, Vector2 position, Vector2 size, bool centred)
    {
        var row = Object.Instantiate(template, parent, false);
        row.name = name;
        row.SetActive(true);
        var rect = (RectTransform)row.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var button = row.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.interactable = true;
        var label = row.GetComponentInChildren<TMP_Text>(true);
        label.text = text;
        label.richText = true;
        if (centred)
            label.alignment = TextAlignmentOptions.Center;
        return button;
    }

    static TMP_Text Text(GameObject template, RectTransform parent, string name, string text, Vector2 position, Vector2 size, float fontSize)
    {
        var go = Object.Instantiate(template, parent, false);
        go.name = name;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var label = go.GetComponent<TMP_Text>();
        label.text = text;
        label.richText = true;
        label.enableAutoSizing = false;
        label.fontSize = fontSize;
        label.fontStyle = FontStyles.Normal;
        label.alignment = TextAlignmentOptions.Left;
        label.textWrappingMode = TextWrappingModes.Normal;
        return label;
    }
}
