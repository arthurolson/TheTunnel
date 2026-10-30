using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Builds the Viewer prefab (Assets/Tunnel/ViewerPanel.prefab) from the browser panel, so it moves, snaps, resizes
/// and closes exactly like it, then swaps the web page for a media screen, the address bar for the Viewer's menu
/// (borrowing the TS Menu's list), and adds the video controls. Also gives the browser's keyboard a move handle and
/// its bar a Copy button (for the Viewer's Paste), registers the prefab for spawning and adds a New Viewer button to the TS Menu. Safe to run again: the prefab keeps
/// its asset (and network) identity. Tunnel > Viewer > Rebuild Viewer Prefab.
/// </summary>
static class ViewerPrefabBuilder
{
    const string k_BrowserPath = "Assets/Tunnel/BrowserPanel.prefab";
    const string k_ViewerPath = "Assets/Tunnel/ViewerPanel.prefab";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_MaterialPath = "Assets/Tunnel/Materials/ViewerScreen.mat";
    const string k_HandleMaterialPath = "Assets/Tunnel/Materials/BP_CornerMarker.mat";
    const string k_NetworkPrefabListGuid = "7a0e675f74edc434e8c3591b070157ec";
    const float k_BarScale = 0.0006f;
    // Above the bar, left of the Close button.
    static readonly Vector2 k_TopButtonPosition = new(365f, 82f);

    [MenuItem("Tunnel/Viewer/Rebuild Viewer Prefab")]
    public static string BuildAll()
    {
        string log = UpdateBrowser();
        log += "\n" + BuildViewer();
        log += "\n" + Register();
        log += "\n" + AddMenuButton();
        AssetDatabase.SaveAssets();
        Debug.Log("[Viewer] " + log);
        return log;
    }

    #region Viewer prefab

    static string BuildViewer()
    {
        var root = PrefabUtility.LoadPrefabContents(k_BrowserPath);
        try
        {
            root.name = "ViewerPanel";
            foreach (var component in root.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name is "BrowserManager" or "BrowserSync"))
                Object.DestroyImmediate(component);

            var panel = root.GetComponent<BrowserPanel>();
            var screen = root.transform.Find("Screen");
            Object.DestroyImmediate(screen.Find("BrowserPanel_WebView").gameObject);
            screen.Find("Background").GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 1f);
            var hint = screen.Find("Background/Hint").GetComponent<TMP_Text>();
            hint.text = "Choose an image or video from the menu above";

            // The screen: a quad with the stereo-aware shader, a video player and its sound.
            var media = GameObject.CreatePrimitive(PrimitiveType.Quad);
            media.name = "Media";
            Object.DestroyImmediate(media.GetComponent<Collider>());
            media.layer = root.layer;
            media.transform.SetParent(root.transform, false);
            var renderer = media.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = ScreenMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var audio = media.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0.6f;
            audio.minDistance = 1.5f;
            audio.maxDistance = 20f;
            var video = media.AddComponent<VideoPlayer>();
            video.playOnAwake = false;

            // Bar and list templates are taken before the browser's own bits are removed.
            var bar = root.transform.Find("Address Bar");
            bar.name = "Menu Bar";
            var buttonTemplate = bar.Find("Back Button").gameObject;
            var controls = BuildControls(root.transform, bar, buttonTemplate);

            var viewerMenu = BuildMenu(bar, buttonTemplate, out var refs);
            foreach (string name in new[] { "Back Button", "Favorite Button", "Sync Button", "Saved Button", "Saved Pages", "Copy Button" })
            {
                var child = bar.Find(name);
                if (child != null)
                    Object.DestroyImmediate(child.gameObject);
            }

            // Keyboard: typed text goes to the Viewer's menu instead of a web page.
            var keyboardRoot = root.transform.Find("Keyboard");
            foreach (var component in keyboardRoot.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name == "SpatialKeyboardAdapter"))
                Object.DestroyImmediate(component);
            var keyboard = keyboardRoot.GetComponentInChildren<XRKeyboard>(true);
            AddKeyboardHandle(keyboardRoot, panel, keyboard.gameObject);
            AddBarHandle(bar, panel);

            // Wiring.
            var viewer = root.AddComponent<ViewerPanel>();
            Set(viewer, "m_Panel", panel);
            Set(viewer, "m_Screen", renderer);
            Set(viewer, "m_Video", video);
            Set(viewer, "m_Audio", audio);
            Set(viewer, "m_Hint", hint);
            Set(viewer, "m_Controls", controls.gameObject);

            Set(viewerMenu, "m_Viewer", viewer);
            Set(viewerMenu, "m_ScreenQuad", media.transform);
            Set(viewerMenu, "m_Keyboard", keyboard);
            Set(controls.GetComponent<ViewerControls>(), "m_Viewer", viewer);

            Set(panel, "m_ScreenQuad", media.transform);
            Set(panel, "m_Controls", controls);
            SetFloat(panel, "m_Width", 1.0f);
            SetFloat(panel, "m_ControlsDrop", 0.07f);
            SetFloat(panel, "m_ControlsSpace", 0.14f);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, k_ViewerPath);
            return $"Viewer prefab saved ({refs})" + (saved != null ? "" : " FAILED");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static Material ScreenMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_MaterialPath);
        if (material != null)
            return material;
        material = new Material(Shader.Find("Tunnel/ViewerScreen")) { name = "ViewerScreen" };
        AssetDatabase.CreateAsset(material, k_MaterialPath);
        return material;
    }

    /// <summary>The menu bar: kind buttons, name field, keyboard and close buttons, and the list beneath.</summary>
    static ViewerMenu BuildMenu(Transform bar, GameObject buttonTemplate, out string report)
    {
        foreach (var component in bar.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name == "BrowserBookmarks"))
            Object.DestroyImmediate(component);
        var menu = bar.gameObject.AddComponent<ViewerMenu>();

        var kindButtons = new Button[4];
        var kindLabels = new TMP_Text[4];
        float[] x = { -515f, -350f, -185f, -20f };
        for (int i = 0; i < 4; i++)
        {
            var button = CloneButton(buttonTemplate, bar, ViewerLibrary.KindFolders[i] + " Button", new Vector2(x[i], 0f), new Vector2(160f, 48.5f), 1f, ViewerLibrary.KindLabels[i], 20f);
            kindButtons[i] = button;
            kindLabels[i] = Label(button);
        }

        SetArray(menu, "m_KindButtons", kindButtons);
        SetArray(menu, "m_KindLabels", kindLabels);

        // Name field (the browser's address field without its web parts).
        var field = bar.Find("Address Field");
        field.name = "Name Field";
        foreach (var component in field.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name is "SharedSearchBar" or "AddressBarHelper"))
            Object.DestroyImmediate(component);
        var fieldRect = (RectTransform)field;
        fieldRect.anchoredPosition = new Vector2(247f, 0f);
        fieldRect.sizeDelta = new Vector2(330f, 72f);
        var fieldButton = field.Find("Focus Button").GetComponent<Button>();
        fieldButton.onClick = new Button.ButtonClickedEvent();
        Set(menu, "m_Field", fieldButton);
        Set(menu, "m_FieldText", field.Find("Text").GetComponent<TMP_Text>());

        var keyboardButton = bar.Find("Keyboard Button");
        foreach (var component in keyboardButton.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name == "BrowserKeyboardToggle"))
            Object.DestroyImmediate(component);
        var keyboardButtonComponent = keyboardButton.GetComponent<Button>();
        keyboardButtonComponent.onClick = new Button.ButtonClickedEvent();
        Set(menu, "m_KeyboardButton", keyboardButtonComponent);

        // Paste sits above the bar beside Close, where the browser has Copy.
        var paste = CloneButton(buttonTemplate, bar, "Paste Button", k_TopButtonPosition, new Vector2(130f, 48.5f), 1f, "Paste", 18f);
        Set(menu, "m_PasteButton", paste);
        var clear = CloneButton(buttonTemplate, bar, "Clear Button", k_TopButtonPosition - new Vector2(140f, 0f), new Vector2(130f, 48.5f), 1f, "Clear", 18f);
        Set(menu, "m_ClearButton", clear);

        // < and >: the menu places them on the screen's edges.
        var previous = CloneButton(buttonTemplate, bar, "Previous Button", new Vector2(-700f, -400f), new Vector2(70f, 120f), 1f, "<", 48f);
        var next = CloneButton(buttonTemplate, bar, "Next Button", new Vector2(700f, -400f), new Vector2(70f, 120f), 1f, ">", 48f);
        Set(menu, "m_PreviousButton", previous);
        Set(menu, "m_NextButton", next);

        // The list: the TS Menu's texture list, rescaled to the same physical size on this smaller-scaled canvas.
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var source = segment.GetComponentsInChildren<Transform>(true).First(t => t.name == "Texture List");
            var list = Object.Instantiate(source.gameObject, bar, false);
            list.name = "Media List";
            list.SetActive(false);
            var rect = (RectTransform)list.transform;
            float scale = 1.4f;
            rect.localScale = Vector3.one * scale;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition3D = new Vector3(-600f + 540f * scale * 0.5f, -45f - 12f - 560f * scale * 0.5f, -25f);
            var folder = list.transform.Find("Folder");
            if (folder != null)
                Object.DestroyImmediate(folder.gameObject);

            var refresh = list.transform.Find("Refresh Button").GetComponent<Button>();
            refresh.onClick = new Button.ButtonClickedEvent();
            ((RectTransform)refresh.transform).anchoredPosition = new Vector2(-110f, -228f);
            var delete = Object.Instantiate(refresh.gameObject, list.transform, false).GetComponent<Button>();
            delete.name = "Delete Button";
            ((RectTransform)delete.transform).anchoredPosition = new Vector2(110f, -228f);
            Label(delete).text = "Delete checked";
            delete.gameObject.SetActive(false);
            Set(menu, "m_DeleteButton", delete);

            var rowTemplate = list.transform.Find("Row Template").GetComponent<Button>();
            rowTemplate.onClick = new Button.ButtonClickedEvent();
            AddCheckBoxes(rowTemplate);
            var scroll = list.GetComponentInChildren<ScrollRect>(true);

            Set(menu, "m_ListPanel", list);
            Set(menu, "m_ListTitle", list.transform.Find("Title").GetComponent<TMP_Text>());
            Set(menu, "m_ListStatus", list.transform.Find("Status").GetComponent<TMP_Text>());
            Set(menu, "m_Scroll", scroll);
            Set(menu, "m_ListContent", scroll.content);
            Set(menu, "m_RowTemplate", rowTemplate);
            Set(menu, "m_RefreshButton", refresh);
            report = "list from TS Menu";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(segment);
        }

        return menu;
    }

    const string k_ToggleSprites = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/DemoAssets/Sprites/";

    /// <summary>Save and Delete check boxes at the right end of a list row (the name's label is shortened to fit).</summary>
    static void AddCheckBoxes(Button row)
    {
        var label = (RectTransform)row.transform.Find("Label");
        label.offsetMax = new Vector2(-176f, label.offsetMax.y);
        var text = label.GetComponent<TMP_Text>();
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        AddCheckBox(row.transform, "Save Toggle", "Save", -132f, new Color(0.133f, 0.588f, 0.953f), text);
        AddCheckBox(row.transform, "Delete Toggle", "Delete", -48f, new Color(0.9f, 0.25f, 0.25f), text);
    }

    static void AddCheckBox(Transform row, string name, string caption, float x, Color checkColor, TMP_Text style)
    {
        var outline = AssetDatabase.LoadAssetAtPath<Sprite>(k_ToggleSprites + "Round Radius 4 Outline.png");
        var filled = AssetDatabase.LoadAssetAtPath<Sprite>(k_ToggleSprites + "Round Radius 4.png");

        var root = NewUI(name, row);
        var rect = (RectTransform)root.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(84f, 46f);
        rect.anchoredPosition3D = new Vector3(x, 0f, -1f);
        var hit = root.AddComponent<Image>();
        hit.color = Color.clear; // the whole box and caption take the poke

        var box = NewUI("Box", root.transform).AddComponent<Image>();
        var boxRect = box.rectTransform;
        boxRect.anchorMin = boxRect.anchorMax = new Vector2(0f, 0.5f);
        boxRect.sizeDelta = new Vector2(26f, 26f);
        boxRect.anchoredPosition = new Vector2(15f, 0f);
        box.sprite = outline;
        box.type = Image.Type.Sliced;
        box.color = Color.white;

        var check = NewUI("Check", box.transform).AddComponent<Image>();
        var checkRect = check.rectTransform;
        checkRect.anchorMin = Vector2.zero;
        checkRect.anchorMax = Vector2.one;
        checkRect.sizeDelta = new Vector2(-8f, -8f);
        check.sprite = filled;
        check.type = Image.Type.Sliced;
        check.color = checkColor;
        check.raycastTarget = false;

        var label = NewUI("Caption", root.transform).AddComponent<TextMeshProUGUI>();
        var labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = new Vector2(32f, 0f);
        labelRect.offsetMax = Vector2.zero;
        label.font = style.font;
        label.color = style.color;
        label.fontSize = 17f;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.text = caption;
        label.raycastTarget = false;

        var toggle = root.AddComponent<Toggle>();
        toggle.targetGraphic = box;
        toggle.graphic = check;
        toggle.isOn = false;
    }

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        return go;
    }

    /// <summary>The video controls bar: a seek slider and time, then the transport buttons.</summary>
    static Transform BuildControls(Transform root, Transform bar, GameObject buttonTemplate)
    {
        var controlsObject = Object.Instantiate(bar.gameObject, root, false);
        controlsObject.name = "Video Controls";
        var controls = controlsObject.transform;
        for (int i = controls.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(controls.GetChild(i).gameObject);
        foreach (var bookmarks in controlsObject.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name == "BrowserBookmarks"))
            Object.DestroyImmediate(bookmarks);
        var rect = (RectTransform)controls;
        rect.sizeDelta = new Vector2(1100f, 150f);
        rect.localScale = Vector3.one * k_BarScale;

        // Background like the browser's saved-pages panel.
        var background = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        background.layer = root.gameObject.layer;
        background.transform.SetParent(controls, false);
        var backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.sizeDelta = Vector2.zero;
        backgroundRect.anchoredPosition3D = new Vector3(0f, 0f, 2f);
        var savedPages = bar.Find("Saved Pages").GetComponent<Image>();
        var image = background.GetComponent<Image>();
        image.sprite = savedPages.sprite;
        image.type = savedPages.type;
        image.color = new Color(0.106f, 0.106f, 0.106f, 0.92f);
        image.raycastTarget = true;

        var component = controlsObject.AddComponent<ViewerControls>();
        string[] names = { "Start", "Back", "Play", "Stop", "Forward", "Fast", "Loop" };
        string[] labels = { "|« Start", "« 10 s", "Play", "Stop", "10 s »", "Fast ×2", "Loop: Off" };
        for (int i = 0; i < names.Length; i++)
        {
            var button = CloneButton(buttonTemplate, controls, names[i] + " Button", new Vector2(-450f + i * 150f, -35f), new Vector2(140f, 48.5f), 1f, labels[i], 20f);
            Set(component, $"m_{names[i]}Button", button);
            if (names[i] is "Play" or "Fast" or "Loop")
                Set(component, $"m_{names[i]}Label", Label(button));
        }

        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var magnification = segment.GetComponentsInChildren<Transform>(true).First(t => t.name == "Magnification");
            var seek = Object.Instantiate(magnification.gameObject, controls, false);
            seek.name = "Seek";
            foreach (string child in new[] { "Ticks", "Magnification Text" })
            {
                var t = seek.transform.Find(child);
                if (t != null)
                    Object.DestroyImmediate(t.gameObject);
            }

            var seekRect = (RectTransform)seek.transform;
            seekRect.localScale = Vector3.one;
            seekRect.anchoredPosition3D = new Vector3(-115f, 35f, 0f);
            seekRect.sizeDelta = new Vector2(830f, 50f);
            var slider = seek.GetComponentInChildren<Slider>(true);
            slider.name = "Seek Slider";
            slider.onValueChanged = new Slider.SliderEvent();
            slider.wholeNumbers = false;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.SetValueWithoutNotify(0f);
            Set(component, "m_Seek", slider);

            var status = segment.GetComponentsInChildren<Transform>(true).First(t => t.name == "Texture List").Find("Status");
            var time = Object.Instantiate(status.gameObject, controls, false);
            time.name = "Time";
            var timeRect = (RectTransform)time.transform;
            timeRect.anchoredPosition3D = new Vector3(430f, 35f, 0f);
            timeRect.sizeDelta = new Vector2(210f, 50f);
            var timeText = time.GetComponent<TMP_Text>();
            timeText.text = "0:00 / 0:00";
            timeText.alignment = TextAlignmentOptions.Right;
            timeText.enableAutoSizing = false;
            timeText.fontSize = 24f;
            Set(component, "m_TimeText", timeText);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(segment);
        }

        return controls;
    }

    static Button CloneButton(GameObject template, Transform parent, string name, Vector2 position, Vector2 size, float scale, string label, float maxFontSize)
    {
        var clone = Object.Instantiate(template, parent, false);
        clone.name = name;
        var rect = (RectTransform)clone.transform;
        rect.anchoredPosition3D = new Vector3(position.x, position.y, 0f);
        rect.sizeDelta = size;
        rect.localScale = Vector3.one * scale;
        var button = clone.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        var text = Label(button);
        text.text = label;
        text.enableAutoSizing = true;
        text.fontSizeMin = 10f;
        text.fontSizeMax = maxFontSize;
        return button;
    }

    static TMP_Text Label(Button button) => button.GetComponentInChildren<TMP_Text>(true);

    #endregion

    #region Move handles

    /// <summary>A grab bar that moves its parent: a thin bar with a generous collider around it.</summary>
    static XRSimpleInteractable CreateHandle(Transform parent, Vector3 localPosition, Quaternion localRotation, float width, float parentScale)
    {
        var existing = parent.Find("Move Handle");
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        var handle = new GameObject("Move Handle");
        handle.layer = parent.gameObject.layer;
        handle.transform.SetParent(parent, false);
        handle.transform.localPosition = localPosition;
        handle.transform.localRotation = localRotation;
        handle.transform.localScale = Vector3.one / parentScale;
        var box = handle.AddComponent<BoxCollider>();
        box.size = new Vector3(width + 0.04f, 0.05f, 0.04f);

        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "Bar";
        Object.DestroyImmediate(bar.GetComponent<Collider>());
        bar.layer = parent.gameObject.layer;
        bar.transform.SetParent(handle.transform, false);
        bar.transform.localScale = new Vector3(width, 0.014f, 0.01f);
        var renderer = bar.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(k_HandleMaterialPath);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var interactable = handle.AddComponent<XRSimpleInteractable>();
        interactable.colliders.Clear();
        interactable.colliders.Add(box);
        return interactable;
    }

    static void AddKeyboardHandle(Transform keyboardRoot, BrowserPanel panel, GameObject keys)
    {
        // Just under the keyboard's bottom edge, in its tilted plane.
        var handle = CreateHandle(keyboardRoot, new Vector3(0f, -0.2f, 0f), Quaternion.identity, 0.16f, 1f);
        var attachment = keyboardRoot.GetComponent<PanelAttachment>();
        if (attachment == null)
            attachment = keyboardRoot.gameObject.AddComponent<PanelAttachment>();
        Set(attachment, "m_Panel", panel);
        Set(attachment, "m_Handle", handle);
        Set(attachment, "m_ShowHandleWith", keys);
    }

    static void AddBarHandle(Transform bar, BrowserPanel panel)
    {
        // Above the middle of the bar (the Close button sits above its right end).
        var handle = CreateHandle(bar, new Vector3(0f, 72f, 0f), Quaternion.identity, 0.16f, k_BarScale);
        var attachment = bar.GetComponent<PanelAttachment>();
        if (attachment == null)
            attachment = bar.gameObject.AddComponent<PanelAttachment>();
        Set(attachment, "m_Panel", panel);
        Set(attachment, "m_Handle", handle);
    }

    /// <summary>The browser's keyboard gets the same move handle as the Viewer's, and its bar a Copy button.</summary>
    static string UpdateBrowser()
    {
        var root = PrefabUtility.LoadPrefabContents(k_BrowserPath);
        try
        {
            var keyboardRoot = root.transform.Find("Keyboard");
            var keys = keyboardRoot.GetComponentInChildren<XRKeyboard>(true).gameObject;
            AddKeyboardHandle(keyboardRoot, root.GetComponent<BrowserPanel>(), keys);

            var bar = root.transform.Find("Address Bar");
            var old = bar.Find("Copy Button");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var copy = CloneButton(bar.Find("Favorite Button").gameObject, bar, "Copy Button", k_TopButtonPosition, new Vector2(130f, 48.5f), 1f, "Copy", 18f);
            var copier = copy.gameObject.AddComponent<BrowserCopyButton>();
            Set(copier, "m_Container", root.GetComponentInChildren<TLab.WebView.BrowserContainer>(true));
            Set(copier, "m_Button", copy);
            Set(copier, "m_Label", Label(copy));

            PrefabUtility.SaveAsPrefabAsset(root, k_BrowserPath);
            return "browser: keyboard handle, Copy button";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    #endregion

    #region Registration and the TS Menu button

    static string Register()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_ViewerPath);
        var browser = AssetDatabase.LoadAssetAtPath<GameObject>(k_BrowserPath);
        string log = $"hash viewer {prefab.GetComponent<NetworkObject>().PrefabIdHash} browser {browser.GetComponent<NetworkObject>().PrefabIdHash}";

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        registry.viewerPanel = prefab;
        EditorUtility.SetDirty(registry);

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(AssetDatabase.GUIDToAssetPath(k_NetworkPrefabListGuid));
        if (!list.PrefabList.Any(p => p.Prefab == prefab))
        {
            list.Add(new NetworkPrefab { Prefab = prefab });
            EditorUtility.SetDirty(list);
            log += ", added to network prefabs";
        }

        return log;
    }

    static string AddMenuButton()
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var menuPanel = root.GetComponentsInChildren<Transform>(true).First(t => t.name == "Menu Panel");
            var viewerButton = menuPanel.Find("New Viewer Button");
            if (viewerButton == null)
            {
                var browserButton = menuPanel.Find("New Browser Button");
                viewerButton = Object.Instantiate(browserButton.gameObject, menuPanel, false).transform;
                viewerButton.name = "New Viewer Button";
                viewerButton.SetSiblingIndex(browserButton.GetSiblingIndex() + 1);
            }

            var button = viewerButton.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            Label(button).text = "New Viewer";

            // Eight buttons now: a taller panel with the same top edge, the QR status still underneath.
            string[] order = { "Colors Button", "Textures Button", "Molecules Button", "New Browser Button", "New Viewer Button", "Add Segment Button", "Remove Segment Button", "Align Button" };
            var panelRect = (RectTransform)menuPanel;
            const float spacing = 68f, height = 570f;
            float top = panelRect.anchoredPosition.y + panelRect.sizeDelta.y * 0.5f;
            panelRect.sizeDelta = new Vector2(panelRect.sizeDelta.x, height);
            panelRect.anchoredPosition = new Vector2(panelRect.anchoredPosition.x, top - height * 0.5f);
            for (int i = 0; i < order.Length; i++)
            {
                var rect = (RectTransform)menuPanel.Find(order[i]);
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, (order.Length - 1) * 0.5f * spacing - i * spacing);
            }

            var status = (RectTransform)menuPanel.Find("QR Status");
            if (status != null)
                status.anchoredPosition = new Vector2(status.anchoredPosition.x, -height - 8f);

            Set(root.GetComponentInChildren<TunnelSegmentActions>(true), "m_NewViewerButton", button);
            PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath);
            return "TS Menu: New Viewer button";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    #endregion

    static void Set(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(field);
        if (property == null)
            throw new System.Exception($"{target.GetType().Name} has no field {field}");
        property.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetFloat(Object target, string field, float value)
    {
        var so = new SerializedObject(target);
        so.FindProperty(field).floatValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetArray(Object target, string field, Object[] values)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(field);
        property.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
